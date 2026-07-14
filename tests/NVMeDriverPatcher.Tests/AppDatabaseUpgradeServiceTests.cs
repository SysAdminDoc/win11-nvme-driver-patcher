using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using NVMeDriverPatcher.Data;

namespace NVMeDriverPatcher.Tests;

public sealed class AppDatabaseUpgradeServiceTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(),
        $"NVMePatcher.DbUpgradeTests.{Guid.NewGuid():N}");

    public AppDatabaseUpgradeServiceTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { }
    }

    [Fact]
    public void NewDatabase_CreatesVersionedCurrentSchema()
    {
        var path = Path.Combine(_root, "new.db");

        var result = Upgrade(path);

        Assert.True(result.IsAvailable, result.Summary);
        Assert.Equal(AppDatabaseUpgradeService.CurrentSchemaVersion, result.SchemaVersion);
        Assert.Null(result.BackupPath);
        AssertCurrentSchema(path);
    }

    [Fact]
    public void HistoricalV1Fixture_UpgradesTransactionallyAndPreservesEveryRow()
    {
        var path = Path.Combine(_root, "v1.db");
        CreateV1Fixture(path);

        var result = Upgrade(path);

        Assert.True(result.IsAvailable, result.Summary);
        Assert.NotNull(result.BackupPath);
        Assert.True(File.Exists(result.BackupPath));
        AssertCurrentSchema(path);
        Assert.Equal("legacy benchmark", Scalar(path, "SELECT Label FROM Benchmarks WHERE Id=1"));
        Assert.Equal("legacy snapshot", Scalar(path, "SELECT Description FROM Snapshots WHERE Id=1"));
        Assert.Equal(7L, Convert.ToInt64(Scalar(path, "SELECT DriveNumber FROM Telemetry WHERE Id=1")));

        AssertQuickCheck(result.BackupPath!);
        Assert.Equal("legacy benchmark", Scalar(result.BackupPath!, "SELECT Label FROM Benchmarks WHERE Id=1"));
        Assert.False(ObjectExists(result.BackupPath!, "table", "BypassIoHistory"));

        var backupCount = Directory.GetFiles(Path.GetDirectoryName(result.BackupPath!)!, "*.db").Length;
        var second = Upgrade(path);
        Assert.True(second.IsAvailable, second.Summary);
        Assert.Equal(backupCount, Directory.GetFiles(Path.GetDirectoryName(result.BackupPath!)!, "*.db").Length);
    }

    [Fact]
    public void FormerUnversionedV2Fixture_IsAdoptedWithoutLosingBypassHistory()
    {
        var path = Path.Combine(_root, "legacy-v2.db");
        CreateV1Fixture(path);
        AddV2Schema(path);
        Execute(path,
            "INSERT INTO BypassIoHistory (Timestamp, VolumeLetter, Enabled, Stack, Description, IsPrePatch) " +
            "VALUES ('2026-07-14T00:00:00Z', 'D:', 1, 'stornvme.sys', 'legacy bypass', 1);");

        var result = Upgrade(path);

        Assert.True(result.IsAvailable, result.Summary);
        Assert.NotNull(result.BackupPath);
        AssertCurrentSchema(path);
        Assert.Equal("legacy bypass", Scalar(path, "SELECT Description FROM BypassIoHistory WHERE Id=1"));
        Assert.Equal("legacy bypass", Scalar(result.BackupPath!, "SELECT Description FROM BypassIoHistory WHERE Id=1"));
    }

    [Fact]
    public void InjectedUpgradeFailure_RollsBackDdlAndReturnsVerifiedRecoveryPath()
    {
        var path = Path.Combine(_root, "rollback.db");
        CreateV1Fixture(path);

        var result = AppDatabaseUpgradeService.Upgrade(
            path,
            beforeCommit: (_, _) => throw new IOException("simulated commit barrier failure"),
            mutexName: MutexName());

        Assert.Equal(AppDatabaseAvailability.Unavailable, result.Availability);
        Assert.Contains("simulated commit barrier failure", result.Summary, StringComparison.Ordinal);
        Assert.NotNull(result.BackupPath);
        Assert.True(File.Exists(result.BackupPath));
        Assert.Contains(result.BackupPath!, result.RecoveryAction, StringComparison.Ordinal);
        Assert.Equal(0L, Convert.ToInt64(Scalar(path, "PRAGMA user_version")));
        Assert.False(ObjectExists(path, "table", "BypassIoHistory"));
        Assert.Equal("legacy benchmark", Scalar(path, "SELECT Label FROM Benchmarks WHERE Id=1"));
        AssertQuickCheck(result.BackupPath!);
    }

    [Fact]
    public void CorruptDatabase_IsUnavailableAndNeverReplacedWithEmptyHistory()
    {
        var path = Path.Combine(_root, "corrupt.db");
        var bytes = System.Text.Encoding.UTF8.GetBytes("not a sqlite database");
        File.WriteAllBytes(path, bytes);

        var result = Upgrade(path);

        Assert.Equal(AppDatabaseAvailability.Unavailable, result.Availability);
        Assert.False(result.IsAvailable);
        Assert.Contains("preserve nvmepatcher.db", result.RecoveryAction, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(bytes, File.ReadAllBytes(path));
    }

    [Fact]
    public void NewerSchema_IsTypedAndLeftUntouched()
    {
        var path = Path.Combine(_root, "newer.db");
        CreateV1Fixture(path);
        AddV2Schema(path);
        Execute(path, "PRAGMA user_version=99;");

        var result = Upgrade(path);

        Assert.Equal(AppDatabaseAvailability.NewerSchema, result.Availability);
        Assert.Equal(99, result.SchemaVersion);
        Assert.Contains("do not downgrade", result.RecoveryAction, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(99L, Convert.ToInt64(Scalar(path, "PRAGMA user_version")));
    }

    private AppDatabaseState Upgrade(string path) =>
        AppDatabaseUpgradeService.Upgrade(path, mutexName: MutexName());

    private static string MutexName() =>
        @"Local\NVMeDriverPatcher.Tests.DatabaseUpgrade." + Guid.NewGuid().ToString("N");

    private static void CreateV1Fixture(string path)
    {
        Execute(path,
            """
            CREATE TABLE Benchmarks (
              Id INTEGER NOT NULL PRIMARY KEY AUTOINCREMENT,
              Label TEXT NOT NULL,
              Timestamp TEXT NOT NULL,
              ReadIOPS REAL NOT NULL,
              ReadThroughputMBs REAL NOT NULL,
              ReadLatencyMs REAL NOT NULL,
              WriteIOPS REAL NOT NULL,
              WriteThroughputMBs REAL NOT NULL,
              WriteLatencyMs REAL NOT NULL,
              Notes TEXT NULL
            );
            CREATE INDEX IX_Benchmarks_Timestamp ON Benchmarks (Timestamp);
            CREATE TABLE Snapshots (
              Id INTEGER NOT NULL PRIMARY KEY AUTOINCREMENT,
              Timestamp TEXT NOT NULL,
              Description TEXT NOT NULL,
              RegistryStateJson TEXT NOT NULL,
              PatchStatusJson TEXT NOT NULL,
              IsPrePatch INTEGER NOT NULL
            );
            CREATE INDEX IX_Snapshots_Timestamp ON Snapshots (Timestamp);
            CREATE TABLE Telemetry (
              Id INTEGER NOT NULL PRIMARY KEY AUTOINCREMENT,
              DriveNumber INTEGER NOT NULL,
              Timestamp TEXT NOT NULL,
              TemperatureCelsius INTEGER NOT NULL,
              AvailableSparePercent INTEGER NOT NULL,
              PercentageUsed INTEGER NOT NULL,
              DataUnitsRead INTEGER NOT NULL,
              DataUnitsWritten INTEGER NOT NULL,
              PowerOnHours INTEGER NOT NULL,
              MediaErrors INTEGER NOT NULL,
              UnsafeShutdowns INTEGER NOT NULL
            );
            CREATE INDEX IX_Telemetry_Timestamp ON Telemetry (Timestamp);
            CREATE INDEX IX_Telemetry_DriveNumber_Timestamp ON Telemetry (DriveNumber, Timestamp);
            INSERT INTO Benchmarks (Label, Timestamp, ReadIOPS, ReadThroughputMBs, ReadLatencyMs, WriteIOPS, WriteThroughputMBs, WriteLatencyMs)
              VALUES ('legacy benchmark', '2026-07-14T00:00:00Z', 1, 2, 3, 4, 5, 6);
            INSERT INTO Snapshots (Timestamp, Description, RegistryStateJson, PatchStatusJson, IsPrePatch)
              VALUES ('2026-07-14T00:00:00Z', 'legacy snapshot', '{}', '{}', 1);
            INSERT INTO Telemetry (DriveNumber, Timestamp, TemperatureCelsius, AvailableSparePercent, PercentageUsed, DataUnitsRead, DataUnitsWritten, PowerOnHours, MediaErrors, UnsafeShutdowns)
              VALUES (7, '2026-07-14T00:00:00Z', 40, 100, 1, 2, 3, 4, 0, 0);
            """);
    }

    private static void AddV2Schema(string path)
    {
        Execute(path,
            """
            CREATE TABLE BypassIoHistory (
              Id INTEGER NOT NULL PRIMARY KEY AUTOINCREMENT,
              Timestamp TEXT NOT NULL,
              VolumeLetter TEXT NOT NULL,
              Enabled INTEGER NOT NULL,
              Stack TEXT NOT NULL,
              Description TEXT NOT NULL,
              IsPrePatch INTEGER NOT NULL
            );
            CREATE INDEX IX_BypassIoHistory_Timestamp ON BypassIoHistory (Timestamp);
            CREATE INDEX IX_BypassIoHistory_VolumeLetter_Timestamp ON BypassIoHistory (VolumeLetter, Timestamp);
            """);
    }

    private static void AssertCurrentSchema(string path)
    {
        Assert.Equal(AppDatabaseUpgradeService.CurrentSchemaVersion,
            Convert.ToInt32(Scalar(path, "PRAGMA user_version")));
        foreach (var table in new[] { "Benchmarks", "Snapshots", "Telemetry", "BypassIoHistory" })
            Assert.True(ObjectExists(path, "table", table), $"Missing table {table}");
        foreach (var index in new[]
                 {
                     "IX_Benchmarks_Timestamp", "IX_Snapshots_Timestamp",
                     "IX_Telemetry_Timestamp", "IX_Telemetry_DriveNumber_Timestamp",
                     "IX_BypassIoHistory_Timestamp", "IX_BypassIoHistory_VolumeLetter_Timestamp"
                 })
            Assert.True(ObjectExists(path, "index", index), $"Missing index {index}");
        AssertQuickCheck(path);
    }

    private static void AssertQuickCheck(string path) =>
        Assert.Equal("ok", Scalar(path, "PRAGMA quick_check"));

    private static bool ObjectExists(string path, string type, string name)
    {
        using var connection = Open(path);
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM sqlite_master WHERE type=$type AND name=$name";
        command.Parameters.AddWithValue("$type", type);
        command.Parameters.AddWithValue("$name", name);
        return Convert.ToInt64(command.ExecuteScalar()) == 1;
    }

    private static object? Scalar(string path, string sql)
    {
        using var connection = Open(path);
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        return command.ExecuteScalar();
    }

    private static void Execute(string path, string sql)
    {
        using var connection = Open(path);
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.ExecuteNonQuery();
    }

    private static SqliteConnection Open(string path)
    {
        var connection = new SqliteConnection($"Data Source={path}");
        connection.Open();
        return connection;
    }
}
