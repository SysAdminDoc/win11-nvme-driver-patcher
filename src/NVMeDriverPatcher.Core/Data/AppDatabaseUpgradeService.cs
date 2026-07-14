using Microsoft.Data.Sqlite;
using NVMeDriverPatcher.Services;

namespace NVMeDriverPatcher.Data;

public enum AppDatabaseAvailability
{
    NotInitialized,
    Available,
    Unavailable,
    NewerSchema
}

public sealed record AppDatabaseState(
    AppDatabaseAvailability Availability,
    int SchemaVersion,
    string Summary,
    string RecoveryAction,
    string? BackupPath = null)
{
    public bool IsAvailable => Availability == AppDatabaseAvailability.Available;

    public static AppDatabaseState NotInitialized { get; } = new(
        AppDatabaseAvailability.NotInitialized,
        0,
        "History database has not been initialized.",
        "Initialize the data service before reading history.");
}

/// <summary>
/// Adopts the two historical EnsureCreated schemas into an explicit PRAGMA user_version chain.
/// Every existing database is quick-checked first; every upgrade gets a validated SQLite Online
/// Backup snapshot before transactional DDL. Unknown/newer schemas fail closed without mutation.
/// </summary>
internal static class AppDatabaseUpgradeService
{
    internal const int CurrentSchemaVersion = 2;
    private const string UpgradeMutexName = @"Global\NVMeDriverPatcher.DatabaseUpgrade";
    private static readonly TimeSpan UpgradeMutexTimeout = TimeSpan.FromSeconds(30);

    private static readonly string[] BaseTables = ["Benchmarks", "Snapshots", "Telemetry"];
    private static readonly string[] CurrentTables = [.. BaseTables, "BypassIoHistory"];
    private static readonly string[] CurrentIndexes =
    [
        "IX_Benchmarks_Timestamp",
        "IX_Snapshots_Timestamp",
        "IX_Telemetry_Timestamp",
        "IX_Telemetry_DriveNumber_Timestamp",
        "IX_BypassIoHistory_Timestamp",
        "IX_BypassIoHistory_VolumeLetter_Timestamp"
    ];
    private static readonly IReadOnlyDictionary<string, string[]> RequiredColumns =
        new Dictionary<string, string[]>(StringComparer.OrdinalIgnoreCase)
        {
            ["Benchmarks"] =
            [
                "Id", "Label", "Timestamp", "ReadIOPS", "ReadThroughputMBs", "ReadLatencyMs",
                "WriteIOPS", "WriteThroughputMBs", "WriteLatencyMs", "Notes"
            ],
            ["Snapshots"] =
                ["Id", "Timestamp", "Description", "RegistryStateJson", "PatchStatusJson", "IsPrePatch"],
            ["Telemetry"] =
            [
                "Id", "DriveNumber", "Timestamp", "TemperatureCelsius", "AvailableSparePercent",
                "PercentageUsed", "DataUnitsRead", "DataUnitsWritten", "PowerOnHours", "MediaErrors",
                "UnsafeShutdowns"
            ],
            ["BypassIoHistory"] =
                ["Id", "Timestamp", "VolumeLetter", "Enabled", "Stack", "Description", "IsPrePatch"]
        };

    internal static AppDatabaseState Upgrade(
        string databasePath,
        Action<SqliteConnection, SqliteTransaction>? beforeCommit = null,
        string? mutexName = null)
    {
        if (string.IsNullOrWhiteSpace(databasePath))
            return Unavailable(0, "Database path is missing.", null);

        var path = Path.GetFullPath(databasePath);
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)
                                      ?? throw new InvalidDataException("Database directory is unavailable."));
        }
        catch (Exception ex)
        {
            return Unavailable(0, $"Database directory could not be prepared ({ex.GetType().Name}).", null);
        }

        using var lease = AcquireMutex(mutexName ?? UpgradeMutexName);
        if (!lease.Held)
            return Unavailable(0, "Database upgrade lock could not be acquired: " + lease.Error, null);

        try
        {
            if (!File.Exists(path) || new FileInfo(path).Length == 0 || HasNoApplicationTables(path))
                return CreateCurrent(path);

            int declaredVersion;
            int detectedVersion;
            using (var inspection = Open(path, SqliteOpenMode.ReadOnly))
            {
                ValidateQuickCheck(inspection);
                declaredVersion = ReadUserVersion(inspection);
                detectedVersion = DetectSchemaVersion(inspection, declaredVersion);

                if (detectedVersion > CurrentSchemaVersion)
                {
                    return new AppDatabaseState(
                        AppDatabaseAvailability.NewerSchema,
                        detectedVersion,
                        $"History database schema v{detectedVersion} is newer than supported v{CurrentSchemaVersion}.",
                        "Use the newer NVMe Driver Patcher version that created this database; do not downgrade or replace the file.");
                }

                if (detectedVersion < 1)
                    return Unavailable(declaredVersion, "History database has an unknown or incomplete schema.", null);

                if (detectedVersion == CurrentSchemaVersion && declaredVersion == CurrentSchemaVersion)
                    ValidateCurrentSchema(inspection);
            }

            if (detectedVersion == CurrentSchemaVersion && declaredVersion == CurrentSchemaVersion)
            {
                using var current = Open(path, SqliteOpenMode.ReadWrite);
                ApplyPersistentPragmas(current);
                ValidateQuickCheck(current);
                ValidateCurrentSchema(current);
                return Available(CurrentSchemaVersion, "History database schema and integrity checks passed.");
            }

            // Either a real v1→v2 migration or adoption of the formerly-unversioned v2
            // layout. Both change schema metadata, so both require a validated backup.
            return UpgradeExisting(path, detectedVersion, beforeCommit);
        }
        catch (Exception ex)
        {
            return Unavailable(0, $"History database initialization failed ({ex.GetType().Name}: {ex.Message}).", null);
        }
    }

    private static AppDatabaseState CreateCurrent(string path)
    {
        try
        {
            using (var context = new AppDbContext(path))
                context.Database.EnsureCreated();

            using var connection = Open(path, SqliteOpenMode.ReadWrite);
            using (var transaction = connection.BeginTransaction())
            {
                SetUserVersion(connection, transaction, CurrentSchemaVersion);
                transaction.Commit();
            }
            ApplyPersistentPragmas(connection);
            ValidateQuickCheck(connection);
            ValidateCurrentSchema(connection);
            return Available(CurrentSchemaVersion, "Created history database schema v2; integrity check passed.");
        }
        catch (Exception ex)
        {
            return Unavailable(0, $"History database creation failed ({ex.GetType().Name}: {ex.Message}).", null);
        }
    }

    private static AppDatabaseState UpgradeExisting(
        string path,
        int detectedVersion,
        Action<SqliteConnection, SqliteTransaction>? beforeCommit)
    {
        var backupPath = BuildBackupPath(path, detectedVersion);
        var backup = SqliteSnapshotService.CreateValidatedSnapshot(path, backupPath);
        if (!backup.Success || backup.SnapshotPath is null)
            return Unavailable(detectedVersion, "Pre-upgrade database backup failed validation.", null);

        try
        {
            using var connection = Open(path, SqliteOpenMode.ReadWrite);
            using var transaction = connection.BeginTransaction(deferred: false);

            var currentDetected = DetectSchemaVersion(connection, ReadUserVersion(connection, transaction), transaction);
            if (currentDetected != detectedVersion)
                throw new InvalidOperationException("Database schema changed while the upgrade lock was held.");

            if (detectedVersion == 1)
                UpgradeV1ToV2(connection, transaction);
            else if (detectedVersion != CurrentSchemaVersion)
                throw new InvalidDataException($"No migration path exists from schema v{detectedVersion}.");

            SetUserVersion(connection, transaction, CurrentSchemaVersion);
            ValidateCurrentSchema(connection, transaction);
            beforeCommit?.Invoke(connection, transaction);
            transaction.Commit();

            ApplyPersistentPragmas(connection);
            ValidateQuickCheck(connection);
            ValidateCurrentSchema(connection);
            return new AppDatabaseState(
                AppDatabaseAvailability.Available,
                CurrentSchemaVersion,
                $"Upgraded history database from v{detectedVersion} to v{CurrentSchemaVersion}; backup and integrity checks passed.",
                "No recovery action is required.",
                backup.SnapshotPath);
        }
        catch (Exception ex)
        {
            return Unavailable(
                detectedVersion,
                $"History database upgrade failed ({ex.GetType().Name}: {ex.Message}).",
                backup.SnapshotPath);
        }
    }

    private static void UpgradeV1ToV2(SqliteConnection connection, SqliteTransaction transaction)
    {
        Execute(connection, transaction,
            """
            CREATE TABLE "BypassIoHistory" (
                "Id" INTEGER NOT NULL CONSTRAINT "PK_BypassIoHistory" PRIMARY KEY AUTOINCREMENT,
                "Timestamp" TEXT NOT NULL,
                "VolumeLetter" TEXT NOT NULL,
                "Enabled" INTEGER NOT NULL,
                "Stack" TEXT NOT NULL,
                "Description" TEXT NOT NULL,
                "IsPrePatch" INTEGER NOT NULL
            );
            """);
        Execute(connection, transaction,
            "CREATE INDEX \"IX_BypassIoHistory_Timestamp\" ON \"BypassIoHistory\" (\"Timestamp\");");
        Execute(connection, transaction,
            "CREATE INDEX \"IX_BypassIoHistory_VolumeLetter_Timestamp\" ON \"BypassIoHistory\" (\"VolumeLetter\", \"Timestamp\");");
    }

    private static int DetectSchemaVersion(
        SqliteConnection connection,
        int declaredVersion,
        SqliteTransaction? transaction = null)
    {
        if (declaredVersion > 0)
            return declaredVersion;

        var tables = ReadObjectNames(connection, "table", transaction);
        if (CurrentTables.All(tables.Contains))
            return CurrentSchemaVersion;
        if (BaseTables.All(tables.Contains) && !tables.Contains("BypassIoHistory"))
            return 1;
        return 0;
    }

    private static bool HasNoApplicationTables(string path)
    {
        try
        {
            using var connection = Open(path, SqliteOpenMode.ReadOnly);
            var tables = ReadObjectNames(connection, "table");
            tables.Remove("sqlite_sequence");
            return tables.Count == 0;
        }
        catch
        {
            // Let the normal open/quick_check path return a typed corruption failure.
            return false;
        }
    }

    private static void ValidateCurrentSchema(
        SqliteConnection connection,
        SqliteTransaction? transaction = null)
    {
        var tables = ReadObjectNames(connection, "table", transaction);
        var indexes = ReadObjectNames(connection, "index", transaction);
        var missingTables = CurrentTables.Where(table => !tables.Contains(table)).ToArray();
        var missingIndexes = CurrentIndexes.Where(index => !indexes.Contains(index)).ToArray();
        if (missingTables.Length > 0 || missingIndexes.Length > 0)
        {
            throw new InvalidDataException(
                "Current schema validation failed. Missing: " +
                string.Join(", ", missingTables.Concat(missingIndexes)));
        }

        foreach (var (table, requiredColumns) in RequiredColumns)
        {
            var columns = ReadColumnNames(connection, table, transaction);
            var missingColumns = requiredColumns.Where(column => !columns.Contains(column)).ToArray();
            if (missingColumns.Length > 0)
                throw new InvalidDataException($"Table {table} is missing columns: {string.Join(", ", missingColumns)}");
        }
    }

    private static HashSet<string> ReadColumnNames(
        SqliteConnection connection,
        string table,
        SqliteTransaction? transaction)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = $"PRAGMA table_info(\"{table.Replace("\"", "\"\"")}\");";
        using var reader = command.ExecuteReader();
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        while (reader.Read())
            names.Add(reader.GetString(1));
        return names;
    }

    private static HashSet<string> ReadObjectNames(
        SqliteConnection connection,
        string type,
        SqliteTransaction? transaction = null)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "SELECT name FROM sqlite_master WHERE type = $type";
        command.Parameters.AddWithValue("$type", type);
        using var reader = command.ExecuteReader();
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        while (reader.Read())
            names.Add(reader.GetString(0));
        return names;
    }

    private static int ReadUserVersion(
        SqliteConnection connection,
        SqliteTransaction? transaction = null)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "PRAGMA user_version;";
        return Convert.ToInt32(command.ExecuteScalar(), System.Globalization.CultureInfo.InvariantCulture);
    }

    private static void SetUserVersion(
        SqliteConnection connection,
        SqliteTransaction transaction,
        int version) =>
        Execute(connection, transaction, $"PRAGMA user_version={version};");

    private static void ValidateQuickCheck(SqliteConnection connection)
    {
        using var command = connection.CreateCommand();
        command.CommandText = "PRAGMA quick_check;";
        using var reader = command.ExecuteReader();
        var rows = new List<string>();
        while (reader.Read())
            rows.Add(reader.GetString(0));
        if (rows.Count != 1 || !rows[0].Equals("ok", StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("PRAGMA quick_check did not return exactly one 'ok' row.");
    }

    private static void ApplyPersistentPragmas(SqliteConnection connection)
    {
        Execute(connection, null, "PRAGMA journal_mode=WAL;");
        Execute(connection, null, "PRAGMA busy_timeout=5000;");
        Execute(connection, null, "PRAGMA synchronous=NORMAL;");
    }

    private static void Execute(
        SqliteConnection connection,
        SqliteTransaction? transaction,
        string sql)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = sql;
        command.ExecuteNonQuery();
    }

    private static SqliteConnection Open(string path, SqliteOpenMode mode)
    {
        var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = path,
            Mode = mode,
            Cache = SqliteCacheMode.Private,
            DefaultTimeout = 10
        }.ToString());
        connection.Open();
        SqliteDefensiveConnectionInterceptor.HardenConnection(connection);
        Execute(connection, null, "PRAGMA busy_timeout=10000;");
        return connection;
    }

    private static string BuildBackupPath(string path, int sourceVersion)
    {
        var directory = Path.Combine(Path.GetDirectoryName(path)!, "database-backups");
        var file = $"nvmepatcher-preupgrade-v{sourceVersion}-{DateTime.UtcNow:yyyyMMdd-HHmmss}-{Guid.NewGuid():N}.db";
        return Path.Combine(directory, file);
    }

    private static AppDatabaseState Available(int version, string summary) => new(
        AppDatabaseAvailability.Available,
        version,
        summary,
        "No recovery action is required.");

    private static AppDatabaseState Unavailable(int version, string summary, string? backupPath) => new(
        AppDatabaseAvailability.Unavailable,
        version,
        summary,
        backupPath is null
            ? "Close the app, preserve nvmepatcher.db for support, then move it aside to let the app create a fresh history database."
            : $"Close the app and restore the validated pre-upgrade backup '{backupPath}' over nvmepatcher.db, or preserve both files for support.",
        backupPath);

    private static MutexLease AcquireMutex(string name)
    {
        Mutex? mutex = null;
        try
        {
            mutex = new Mutex(initiallyOwned: false, name);
            bool held;
            try { held = mutex.WaitOne(UpgradeMutexTimeout); }
            catch (AbandonedMutexException) { held = true; }
            return new MutexLease(mutex, held, held ? null : "timed out after 30 seconds");
        }
        catch (Exception ex)
        {
            mutex?.Dispose();
            return new MutexLease(null, false, $"{ex.GetType().Name}: {ex.Message}");
        }
    }

    private sealed class MutexLease(Mutex? mutex, bool held, string? error) : IDisposable
    {
        public bool Held { get; } = held;
        public string? Error { get; } = error;

        public void Dispose()
        {
            if (Held)
            {
                try { mutex?.ReleaseMutex(); } catch { }
            }
            mutex?.Dispose();
        }
    }
}
