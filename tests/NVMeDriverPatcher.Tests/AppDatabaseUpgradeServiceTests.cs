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
        Assert.Equal("desktop-qd1", Scalar(path, "SELECT DesktopProfileId FROM Benchmarks WHERE Id=1"));
        Assert.Equal(0d, Convert.ToDouble(Scalar(path, "SELECT DesktopReadIOPS FROM Benchmarks WHERE Id=1")));
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
    public void InjectedUpgradeFailure_LeavesEveryOlderBackupInPlace()
    {
        // Pruning is for a committed upgrade. After a failed one every copy is still a recovery
        // path, and the failure reported is the upgrade's.
        var path = Path.Combine(_root, "rollback-keep.db");
        CreateV1Fixture(path);
        var backups = Directory.CreateDirectory(Path.Combine(_root, "database-backups")).FullName;
        var older = Enumerable.Range(1, 4)
            .Select(i => Path.Combine(backups, $"nvmepatcher-preupgrade-v1-2026010{i}-000000-{new string((char)('a' + i), 32)}.db"))
            .ToList();
        foreach (var file in older) File.WriteAllText(file, "old copy");

        var result = AppDatabaseUpgradeService.Upgrade(
            path,
            beforeCommit: (_, _) => throw new IOException("simulated commit barrier failure"),
            mutexName: MutexName());

        Assert.Equal(AppDatabaseAvailability.Unavailable, result.Availability);
        Assert.All(older, file => Assert.True(File.Exists(file), file));
        Assert.Equal(5, Directory.GetFiles(backups, "nvmepatcher-preupgrade-*.db").Length);
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

    [Fact]
    public void Upgrade_LeavesTheThreeNewestPreUpgradeBackups()
    {
        // Every upgrade took a full copy of the database into database-backups and nothing
        // removed any of them.
        var path = Path.Combine(_root, "v1.db");
        CreateV1Fixture(path);
        var backups = Directory.CreateDirectory(Path.Combine(_root, "database-backups")).FullName;
        var older = Enumerable.Range(1, 4)
            .Select(i => Path.Combine(backups, $"nvmepatcher-preupgrade-v1-2026010{i}-000000-{new string((char)('a' + i), 32)}.db"))
            .ToList();
        foreach (var file in older) File.WriteAllText(file, "old copy");
        var unrelated = Path.Combine(backups, "copy-i-made-myself.db");
        File.WriteAllText(unrelated, "mine");

        var result = Upgrade(path);

        Assert.True(result.IsAvailable, result.Summary);
        Assert.True(File.Exists(result.BackupPath));
        var remaining = Directory.GetFiles(backups, "nvmepatcher-preupgrade-*.db").Select(Path.GetFileName).Order().ToList();
        Assert.Equal(AppDatabaseUpgradeService.UpgradeBackupRetention, remaining.Count);
        Assert.Contains(Path.GetFileName(result.BackupPath!), remaining);
        Assert.Contains(Path.GetFileName(older[3]), remaining);
        Assert.Contains(Path.GetFileName(older[2]), remaining);
        Assert.False(File.Exists(older[0]));
        Assert.False(File.Exists(older[1]));
        Assert.True(File.Exists(unrelated));
    }

    [Fact]
    public void PruneUpgradeBackups_KeepsTheCopyJustMade_EvenWhenItsStampSortsOldest()
    {
        var backups = Directory.CreateDirectory(Path.Combine(_root, "database-backups")).FullName;
        string Name(string stamp, char fill) => Path.Combine(backups, $"nvmepatcher-preupgrade-v2-{stamp}-{new string(fill, 32)}.db");
        var justMade = Name("20250101-000000", 'a');   // clock went backwards
        var newer = new[] { Name("20260301-000000", 'b'), Name("20260302-000000", 'c'), Name("20260303-000000", 'd'), Name("20260304-000000", 'e') };
        foreach (var file in newer.Append(justMade)) File.WriteAllText(file, "x");

        var removed = AppDatabaseUpgradeService.PruneUpgradeBackups(backups, 3, justMade);

        Assert.Equal(2, removed);
        Assert.True(File.Exists(justMade));
        Assert.True(File.Exists(newer[3]));
        Assert.True(File.Exists(newer[2]));
        Assert.False(File.Exists(newer[1]));
        Assert.False(File.Exists(newer[0]));
        Assert.Equal(0, AppDatabaseUpgradeService.PruneUpgradeBackups(Path.Combine(_root, "missing"), 3, null));
    }

    [Fact]
    public void PruneBypassIoHistory_TrimsAnOversizedTableToTheNewestRows()
    {
        // BypassIoHistory was the one table with no prune at all.
        var path = Path.Combine(_root, "history.db");
        Assert.True(Upgrade(path).IsAvailable);
        using (var seed = new AppDbContext(path))
        {
            for (var i = 0; i < 130; i++)
            {
                seed.BypassIoHistory.Add(new BypassIoHistoryRecord
                {
                    Timestamp = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc).AddMinutes(i),
                    VolumeLetter = "C:",
                    Stack = "stornvme.sys",
                    Description = "row " + i,
                    IsPrePatch = i % 2 == 0,
                });
            }
            seed.SaveChanges();
        }

        using var db = new AppDbContext(path);
        var removed = NVMeDriverPatcher.Services.DataService.PruneBypassIoHistory(db, 100);

        Assert.Equal(30, removed);
        var left = db.BypassIoHistory.OrderBy(b => b.Timestamp).Select(b => b.Description).ToList();
        Assert.Equal(100, left.Count);
        Assert.Equal("row 30", left[0]);
        Assert.Equal("row 129", left[^1]);
        Assert.Equal(0, NVMeDriverPatcher.Services.DataService.PruneBypassIoHistory(db, 100));
    }

    [Fact]
    public void SaveBypassIoSnapshot_TrimsTheTableAsItWrites()
    {
        // Only the GUI pruned, at startup. A machine that applied and removed from the CLI or the
        // scheduled task for years grew this table without bound.
        var path = Path.Combine(_root, "history-save.db");
        Assert.True(Upgrade(path).IsAvailable);
        using (var seed = new AppDbContext(path))
        {
            for (var i = 0; i < NVMeDriverPatcher.Services.DataService.BypassIoHistoryRetention - 1; i++)
            {
                seed.BypassIoHistory.Add(new BypassIoHistoryRecord
                {
                    Timestamp = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc).AddMinutes(i),
                    VolumeLetter = "C:",
                    Stack = "stornvme.sys",
                    Description = "row " + i,
                });
            }
            seed.SaveChanges();
        }

        using var db = new AppDbContext(path);
        var volumes = new[]
        {
            new NVMeDriverPatcher.Services.BypassIoVolumeInfo { Letter = "C:", Enabled = true, Stack = "nvmedisk.sys" },
            new NVMeDriverPatcher.Services.BypassIoVolumeInfo { Letter = "D:", Enabled = false, Stack = "stornvme.sys" },
            new NVMeDriverPatcher.Services.BypassIoVolumeInfo { Letter = "E:", Enabled = true, Stack = "nvmedisk.sys" },
        };
        var removed = NVMeDriverPatcher.Services.DataService.SaveBypassIoSnapshot(
            db, volumes, "After patch install", isPrePatch: false, new DateTime(2026, 6, 1, 0, 0, 0, DateTimeKind.Utc));

        Assert.Equal(2, removed);
        var rows = db.BypassIoHistory.OrderBy(b => b.Timestamp).ThenBy(b => b.Id).ToList();
        Assert.Equal(NVMeDriverPatcher.Services.DataService.BypassIoHistoryRetention, rows.Count);
        Assert.Equal("row 2", rows[0].Description);
        Assert.Equal(new[] { "C:", "D:", "E:" }, rows.TakeLast(3).Select(r => r.VolumeLetter));
        Assert.All(rows.TakeLast(3), r => Assert.Equal("After patch install", r.Description));
    }

    [Fact]
    public void BuildBackupPath_StampsTheGregorianDate_WhateverCalendarTheRegionalFormatUses()
    {
        // The stamp is sorted as text and parsed by the prune. Formatted in the user's culture, a
        // Thai regional format wrote the Buddhist year (2569 for 2026), which sorted every copy
        // made there after copies from a Gregorian machine.
        var thai = System.Globalization.CultureInfo.GetCultureInfo("th-TH");
        Assert.IsType<System.Globalization.ThaiBuddhistCalendar>(thai.DateTimeFormat.Calendar);
        var saved = System.Globalization.CultureInfo.CurrentCulture;
        try
        {
            System.Globalization.CultureInfo.CurrentCulture = thai;
            // Positive control: the culture is in effect, and the old interpolation gave the wrong year.
            Assert.NotEqual(DateTime.UtcNow.Year.ToString(System.Globalization.CultureInfo.InvariantCulture), $"{DateTime.UtcNow:yyyy}");

            var path = AppDatabaseUpgradeService.BuildBackupPath(Path.Combine(_root, "nvmepatcher.db"), 2);

            var match = System.Text.RegularExpressions.Regex.Match(
                Path.GetFileName(path), @"^nvmepatcher-preupgrade-v2-(\d{4})\d{4}-\d{6}-[0-9a-f]{32}\.db$");
            Assert.True(match.Success, path);
            Assert.Equal(DateTime.UtcNow.Year, int.Parse(match.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture));
            Assert.Equal(Path.Combine(_root, "database-backups"), Path.GetDirectoryName(path));
        }
        finally
        {
            System.Globalization.CultureInfo.CurrentCulture = saved;
        }
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
        foreach (var column in new[]
                 {
                     "DesktopProfileId", "DesktopProfileName", "DesktopThreads", "DesktopOutstandingIo",
                     "DesktopDurationSeconds", "DesktopReadIOPS", "DesktopReadThroughputMBs",
                     "DesktopReadLatencyMs", "DesktopWriteIOPS", "DesktopWriteThroughputMBs",
                     "DesktopWriteLatencyMs",
                     "DiskSpdVersion", "DiskSpdSha256", "DiskSpdArguments"
                 })
            Assert.True(ColumnExists(path, "Benchmarks", column), $"Missing benchmark column {column}");
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

    private static bool ColumnExists(string path, string table, string column)
    {
        using var connection = Open(path);
        using var command = connection.CreateCommand();
        command.CommandText = $"SELECT COUNT(*) FROM pragma_table_info('{table}') WHERE name=$name";
        command.Parameters.AddWithValue("$name", column);
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

    [Fact]
    public void DeclaredV3Fixture_GainsDiskSpdColumnsAndKeepsRows()
    {
        var path = Path.Combine(_root, "v3.db");
        CreateV1Fixture(path);
        AddV2Schema(path);
        Execute(path,
            """
            ALTER TABLE Benchmarks ADD COLUMN DesktopProfileId TEXT NOT NULL DEFAULT 'desktop-qd1';
            ALTER TABLE Benchmarks ADD COLUMN DesktopProfileName TEXT NOT NULL DEFAULT 'Desktop QD1';
            ALTER TABLE Benchmarks ADD COLUMN DesktopThreads INTEGER NOT NULL DEFAULT 1;
            ALTER TABLE Benchmarks ADD COLUMN DesktopOutstandingIo INTEGER NOT NULL DEFAULT 1;
            ALTER TABLE Benchmarks ADD COLUMN DesktopDurationSeconds INTEGER NOT NULL DEFAULT 30;
            ALTER TABLE Benchmarks ADD COLUMN DesktopReadIOPS REAL NOT NULL DEFAULT 0;
            ALTER TABLE Benchmarks ADD COLUMN DesktopReadThroughputMBs REAL NOT NULL DEFAULT 0;
            ALTER TABLE Benchmarks ADD COLUMN DesktopReadLatencyMs REAL NOT NULL DEFAULT 0;
            ALTER TABLE Benchmarks ADD COLUMN DesktopWriteIOPS REAL NOT NULL DEFAULT 0;
            ALTER TABLE Benchmarks ADD COLUMN DesktopWriteThroughputMBs REAL NOT NULL DEFAULT 0;
            ALTER TABLE Benchmarks ADD COLUMN DesktopWriteLatencyMs REAL NOT NULL DEFAULT 0;
            PRAGMA user_version=3;
            """);

        var result = Upgrade(path);

        Assert.True(result.IsAvailable, result.Summary);
        Assert.NotNull(result.BackupPath);
        AssertCurrentSchema(path);
        Assert.Equal("legacy benchmark", Scalar(path, "SELECT Label FROM Benchmarks WHERE Id=1"));
        Assert.True(Scalar(path, "SELECT DiskSpdVersion FROM Benchmarks WHERE Id=1") is null or DBNull);
        Assert.False(ColumnExists(result.BackupPath!, "Benchmarks", "DiskSpdVersion"));
    }

    [Fact]
    public void DiskSpdProvenance_RoundTripsThroughTheContext()
    {
        var path = Path.Combine(_root, "roundtrip.db");
        Assert.True(Upgrade(path).IsAvailable);

        using (var db = new AppDbContext(path))
        {
            db.Benchmarks.Add(new BenchmarkRecord
            {
                Label = "x",
                Timestamp = DateTime.UtcNow,
                DiskSpdVersion = "2.2",
                DiskSpdSha256 = "abc123",
                DiskSpdArguments = "-c128M -d30"
            });
            db.SaveChanges();
        }

        Assert.Equal("2.2", Scalar(path, "SELECT DiskSpdVersion FROM Benchmarks WHERE Label='x'"));
        Assert.Equal("-c128M -d30", Scalar(path, "SELECT DiskSpdArguments FROM Benchmarks WHERE Label='x'"));
    }
}
