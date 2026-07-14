using Microsoft.Data.Sqlite;

namespace NVMeDriverPatcher.Services;

internal sealed record SqliteSnapshotResult(
    bool Success,
    string Summary,
    string? SnapshotPath = null);

/// <summary>
/// Creates a one-file, point-in-time SQLite snapshot through the Online Backup API. Copying a
/// WAL database and its sidecars as independent files cannot establish that all three came from
/// the same transaction boundary.
/// </summary>
internal static class SqliteSnapshotService
{
    internal static SqliteSnapshotResult CreateValidatedSnapshot(
        string sourcePath,
        string destinationPath)
    {
        if (string.IsNullOrWhiteSpace(sourcePath) || !File.Exists(sourcePath))
            return new(false, "Source database is missing.");
        if (string.IsNullOrWhiteSpace(destinationPath))
            return new(false, "Snapshot destination is missing.");

        var destination = Path.GetFullPath(destinationPath);
        try
        {
            var directory = Path.GetDirectoryName(destination)
                            ?? throw new InvalidDataException("Snapshot destination directory is unavailable.");
            Directory.CreateDirectory(directory);
            DeleteSnapshotFiles(destination);

            var sourceConnectionString = new SqliteConnectionStringBuilder
            {
                DataSource = Path.GetFullPath(sourcePath),
                Mode = SqliteOpenMode.ReadOnly,
                Cache = SqliteCacheMode.Private,
                DefaultTimeout = 10
            }.ToString();
            var destinationConnectionString = new SqliteConnectionStringBuilder
            {
                DataSource = destination,
                Mode = SqliteOpenMode.ReadWriteCreate,
                Cache = SqliteCacheMode.Private,
                DefaultTimeout = 10
            }.ToString();

            using (var source = new SqliteConnection(sourceConnectionString))
            using (var target = new SqliteConnection(destinationConnectionString))
            {
                source.Open();
                ExecutePragma(source, "PRAGMA trusted_schema=OFF;");
                ExecutePragma(source, "PRAGMA cell_size_check=ON;");
                ExecutePragma(source, "PRAGMA query_only=ON;");
                ExecutePragma(source, "PRAGMA busy_timeout=10000;");
                ValidateQuickCheck(source);

                target.Open();
                ExecutePragma(target, "PRAGMA trusted_schema=OFF;");
                ExecutePragma(target, "PRAGMA cell_size_check=ON;");
                ExecutePragma(target, "PRAGMA busy_timeout=10000;");

                source.BackupDatabase(target);

                // A standalone bundle should not require WAL/SHM companions. Convert the copied
                // database back to the self-contained delete journal mode before validation.
                using var journal = target.CreateCommand();
                journal.CommandText = "PRAGMA journal_mode=DELETE;";
                var mode = journal.ExecuteScalar()?.ToString();
                if (!string.Equals(mode, "delete", StringComparison.OrdinalIgnoreCase))
                    throw new InvalidDataException($"Snapshot journal mode remained '{mode ?? "unknown"}'.");
            }

            ValidateQuickCheck(destination);
            if (!File.Exists(destination) || new FileInfo(destination).Length <= 0)
                throw new InvalidDataException("Validated snapshot is empty.");
            if (File.Exists(destination + "-wal") || File.Exists(destination + "-shm"))
                throw new InvalidDataException("Snapshot unexpectedly requires WAL sidecars.");

            return new(true, "SQLite Online Backup snapshot passed PRAGMA quick_check.", destination);
        }
        catch (Exception ex)
        {
            DeleteSnapshotFiles(destination);
            return new(false, $"SQLite snapshot failed validation ({ex.GetType().Name}).");
        }
    }

    private static void ValidateQuickCheck(string path)
    {
        var connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = path,
            Mode = SqliteOpenMode.ReadOnly,
            Cache = SqliteCacheMode.Private,
            DefaultTimeout = 10
        }.ToString();
        using var connection = new SqliteConnection(connectionString);
        connection.Open();
        ExecutePragma(connection, "PRAGMA trusted_schema=OFF;");
        ExecutePragma(connection, "PRAGMA cell_size_check=ON;");
        ValidateQuickCheck(connection);
    }

    private static void ValidateQuickCheck(SqliteConnection connection)
    {
        using var command = connection.CreateCommand();
        command.CommandText = "PRAGMA quick_check;";
        using var reader = command.ExecuteReader();
        var results = new List<string>();
        while (reader.Read())
            results.Add(reader.GetString(0));
        if (results.Count != 1 || !results[0].Equals("ok", StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("PRAGMA quick_check did not return exactly one 'ok' row.");
    }

    private static void ExecutePragma(SqliteConnection connection, string commandText)
    {
        using var command = connection.CreateCommand();
        command.CommandText = commandText;
        command.ExecuteNonQuery();
    }

    internal static void DeleteSnapshotFiles(string path)
    {
        foreach (var candidate in new[] { path, path + "-wal", path + "-shm" })
        {
            try { if (File.Exists(candidate)) File.Delete(candidate); }
            catch { }
        }
    }
}
