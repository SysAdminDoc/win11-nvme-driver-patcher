using System.Text.Json.Nodes;
using System.IO.Compression;
using Microsoft.Data.Sqlite;
using NVMeDriverPatcher.Models;
using NVMeDriverPatcher.Services;

namespace NVMeDriverPatcher.Tests;

public sealed class DiagnosticsServiceTests : IDisposable
{
    private readonly string _tempRoot = Path.Combine(Path.GetTempPath(), $"NVMeDriverPatcher.Tests.{Guid.NewGuid():N}");

    public DiagnosticsServiceTests()
    {
        Directory.CreateDirectory(_tempRoot);
    }

    [Fact]
    public void TryCreateShareableConfigText_RedactsPathLikePropertiesAndPreservesOtherSettings()
    {
        var configPath = Path.Combine(_tempRoot, "config.json");
        File.WriteAllText(configPath, """
            {
              "PatchProfile": "Safe",
              "LastDiagnosticsPath": "C:\\Users\\alice\\AppData\\Local\\NVMePatcher\\NVMe_Diagnostics_20260417.txt",
              "WorkingDir": "C:\\Users\\alice\\AppData\\Local\\NVMePatcher",
              "LastRun": "2026-04-17T18:30:00Z"
            }
            """);

        var sanitized = DiagnosticsService.TryCreateShareableConfigText(configPath);

        Assert.NotNull(sanitized);
        var root = JsonNode.Parse(sanitized!);
        Assert.NotNull(root);
        Assert.Equal("Safe", root!["PatchProfile"]?.GetValue<string>());
        Assert.Equal("NVMe_Diagnostics_20260417.txt", root["LastDiagnosticsPath"]?.GetValue<string>());
        Assert.Equal("NVMePatcher", root["WorkingDir"]?.GetValue<string>());
        Assert.Equal("2026-04-17T18:30:00Z", root["LastRun"]?.GetValue<string>());
    }

    [Fact]
    public void TryCreateShareableConfigText_ReturnsNullForMalformedJson()
    {
        var configPath = Path.Combine(_tempRoot, "broken-config.json");
        File.WriteAllText(configPath, "{ \"PatchProfile\": ");

        var sanitized = DiagnosticsService.TryCreateShareableConfigText(configPath);

        Assert.Null(sanitized);
    }

    [Fact]
    public void TryCreateShareableDiagnosticsText_RedactsIdentityAndHardwareIdentifierLines()
    {
        var reportPath = Path.Combine(_tempRoot, "diagnostics.txt");
        File.WriteAllText(reportPath, """
            NVMe Driver Patcher - System Diagnostics Report
            Computer Name: DESKTOP-ALICE
            User: alice
            OS: Windows 11 Pro
            STORAGE DRIVES
            Disk 1: Test NVMe (1 TB) [NVMe]
              PNP ID: SCSI\DISK&VEN_NVME&PROD_TEST\SERIAL-123456
            """);

        var sanitized = DiagnosticsService.TryCreateShareableDiagnosticsText(reportPath);

        Assert.NotNull(sanitized);
        Assert.Contains("Computer Name: [redacted]", sanitized, StringComparison.Ordinal);
        Assert.Contains("User: [redacted]", sanitized, StringComparison.Ordinal);
        Assert.Contains("  PNP ID: [redacted]", sanitized, StringComparison.Ordinal);
        Assert.Contains("OS: Windows 11 Pro", sanitized, StringComparison.Ordinal);
        Assert.DoesNotContain("DESKTOP-ALICE", sanitized, StringComparison.Ordinal);
        Assert.DoesNotContain("User: alice", sanitized, StringComparison.Ordinal);
        Assert.DoesNotContain("SERIAL-123456", sanitized, StringComparison.Ordinal);
    }

    [Fact]
    public void TryCreateShareableLogText_RedactsUserProfilePaths()
    {
        var logPath = Path.Combine(_tempRoot, "crash.log");
        File.WriteAllText(logPath, """
            [2026-04-19T10:00:00Z] [Dispatcher] IOException: denied
               at NVMeDriverPatcher.Services.ConfigService.Load()
               path: C:\Users\alice\AppData\Local\NVMePatcher\config.json
            """);

        var sanitized = DiagnosticsService.TryCreateShareableLogText(logPath);

        Assert.NotNull(sanitized);
        Assert.Contains(@"C:\Users\[redacted]\AppData\Local\NVMePatcher\config.json", sanitized, StringComparison.Ordinal);
        Assert.DoesNotContain(@"\alice\", sanitized, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Export_IncludesCodeIntegrityBlockedBackupDriverEvidence()
    {
        var ev = CodeIntegrityEventService.TryCreateBackupDriverEvent(
            3077,
            new DateTime(2026, 4, 15, 12, 0, 0, DateTimeKind.Utc),
            @"CodeIntegrity blocked C:\Users\alice\Downloads\psmounterex.sys");
        var preflight = new PreflightResult
        {
            CodeIntegrityBlockedDrivers = [ev!]
        };

        var reportPath = DiagnosticsService.Export(_tempRoot, preflight, []);

        Assert.NotNull(reportPath);
        var report = File.ReadAllText(reportPath!);
        Assert.Contains("CODEINTEGRITY BLOCKED BACKUP DRIVERS", report, StringComparison.Ordinal);
        Assert.Contains("psmounterex.sys", report, StringComparison.Ordinal);
        Assert.Contains("Macrium Reflect", report, StringComparison.Ordinal);
        Assert.DoesNotContain(@"C:\Users\alice", report, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Export_IncludesCustomNativeWorkaroundEvidenceFromPreflight()
    {
        var preflight = new PreflightResult
        {
            TestSigningEnabled = true,
            ControllerAudit = new PerControllerAuditReport
            {
                Controllers =
                {
                    new ControllerAudit
                    {
                        FriendlyName = "WD SN850X",
                        InstanceId = "SCSI\\DISK&VEN_NVME&PROD_WD",
                        BoundDriver = "nvmedisk.sys",
                        IsNative = true,
                        InfName = "oem42.inf",
                        DriverProvider = "Community Test",
                        HardwareId = "SCSI\\DiskNVMe____Custom_Model",
                        CompatibleId = "GenNvmeDisk"
                    }
                }
            }
        };

        var reportPath = DiagnosticsService.Export(_tempRoot, preflight, []);

        Assert.NotNull(reportPath);
        var report = File.ReadAllText(reportPath!);
        Assert.Contains("CUSTOM / TEST-SIGNED NVMe WORKAROUND EVIDENCE", report, StringComparison.Ordinal);
        Assert.Contains("BCD TESTSIGNING: Yes", report, StringComparison.Ordinal);
        Assert.Contains("WD SN850X", report, StringComparison.Ordinal);
        Assert.Contains("oem42.inf", report, StringComparison.Ordinal);
        Assert.Contains("pnputil /enum-drivers /files", report, StringComparison.Ordinal);
    }

    [Fact]
    public void Export_IncludesTypedCriticalProbeEvidence()
    {
        var probeReport = new CriticalProbeReport { Scope = MutationProbeScope.RegistryPatch };
        probeReport.Items.Add(new CriticalProbeResult
        {
            Id = "VeraCrypt",
            Label = "VeraCrypt system encryption",
            Verdict = CriticalProbeVerdict.Unknown,
            ReasonCode = CriticalProbeReasonCode.AccessDenied,
            Detail = "Registry evidence unavailable.",
            NativeError = "HRESULT=0x80070005",
            Evidence = ["exception=UnauthorizedAccessException"],
            ObservedAtUtc = new DateTimeOffset(2026, 7, 14, 12, 0, 0, TimeSpan.Zero)
        });
        var bitLocker = new BitLockerRecoveryProof(
            new BitLockerVolumeEvidence
            {
                ProbeSucceeded = true,
                SystemVolumePresent = true,
                MountPoint = "C:",
                ConversionStatus = 0,
                ProtectionStatus = 0
            },
            new DirectoryJoinEvidence(true, DirectoryJoinKind.None));
        var preflight = new PreflightResult
        {
            CriticalProbes = probeReport,
            BitLockerRecovery = bitLocker
        };

        var reportPath = DiagnosticsService.Export(_tempRoot, preflight, []);

        Assert.NotNull(reportPath);
        var report = File.ReadAllText(reportPath!);
        Assert.Contains("CRITICAL ENVIRONMENT PROBES", report, StringComparison.Ordinal);
        Assert.Contains("[Unknown] VeraCrypt: AccessDenied", report, StringComparison.Ordinal);
        Assert.Contains("Native Error: HRESULT=0x80070005", report, StringComparison.Ordinal);
        Assert.Contains("Observed UTC: 2026-07-14T12:00:00.0000000+00:00", report, StringComparison.Ordinal);
    }

    [Fact]
    public void ExportBundle_IncludesFinalIntegrityManifestAndVerifiesAsZipPayload()
    {
        var configPath = Path.Combine(_tempRoot, "config.json");
        File.WriteAllText(configPath, "{ \"PatchProfile\": \"Safe\" }");
        var outputPath = Path.Combine(_tempRoot, "support.zip");

        var bundle = DiagnosticsService.ExportBundle(
            _tempRoot,
            preflight: new PreflightResult(),
            logHistory: ["[INFO] test"],
            configPath,
            outputPath);

        Assert.Equal(outputPath, bundle);
        using (var zip = ZipFile.OpenRead(outputPath))
            Assert.NotNull(zip.GetEntry(GeneratedArtifactManifestService.ManifestFileName));
        var integrity = GeneratedArtifactManifestService.VerifyZip(outputPath);
        Assert.True(integrity.Success, integrity.Summary + Environment.NewLine +
            string.Join(Environment.NewLine, integrity.Issues.Select(i => i.Detail)));
        Assert.Equal("support-bundle", integrity.PayloadType);
    }

    [Fact]
    public async Task ExportBundle_UsesOneValidatedSnapshotDuringConcurrentWalWrites()
    {
        var databasePath = Path.Combine(_tempRoot, "nvmepatcher.db");
        var connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = databasePath,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Cache = SqliteCacheMode.Private,
            DefaultTimeout = 10
        }.ToString();
        using var keeper = new SqliteConnection(connectionString);
        keeper.Open();
        using (var setup = keeper.CreateCommand())
        {
            setup.CommandText = """
                PRAGMA journal_mode=WAL;
                CREATE TABLE evidence(id INTEGER PRIMARY KEY, value TEXT NOT NULL);
                INSERT INTO evidence(value) VALUES ('baseline');
                """;
            setup.ExecuteNonQuery();
        }

        using var cancellation = new CancellationTokenSource();
        int committedWrites = 0;
        var writer = Task.Run(() =>
        {
            using var connection = new SqliteConnection(connectionString);
            connection.Open();
            while (!cancellation.IsCancellationRequested)
            {
                using var command = connection.CreateCommand();
                command.CommandText = "INSERT INTO evidence(value) VALUES ($value);";
                command.Parameters.AddWithValue("$value", $"writer-{Volatile.Read(ref committedWrites)}");
                command.ExecuteNonQuery();
                Interlocked.Increment(ref committedWrites);
                Thread.Sleep(2);
            }
        });

        try
        {
            Assert.True(SpinWait.SpinUntil(() => Volatile.Read(ref committedWrites) >= 5, TimeSpan.FromSeconds(5)));
            var outputPath = Path.Combine(_tempRoot, "concurrent-support.zip");

            var bundle = DiagnosticsService.ExportBundle(
                _tempRoot,
                preflight: new PreflightResult(),
                logHistory: ["[INFO] concurrent snapshot test"],
                outputPath: outputPath);

            Assert.Equal(outputPath, bundle);
            var extracted = Path.Combine(_tempRoot, "extracted-snapshot.db");
            using (var zip = ZipFile.OpenRead(outputPath))
            {
                var dbEntries = zip.Entries
                    .Where(entry => entry.FullName.StartsWith("data/nvmepatcher.db", StringComparison.OrdinalIgnoreCase))
                    .ToArray();
                var snapshotEntry = Assert.Single(dbEntries);
                Assert.Equal("data/nvmepatcher.db", snapshotEntry.FullName);
                Assert.DoesNotContain(zip.Entries, entry =>
                    entry.FullName.EndsWith("-wal", StringComparison.OrdinalIgnoreCase) ||
                    entry.FullName.EndsWith("-shm", StringComparison.OrdinalIgnoreCase));
                snapshotEntry.ExtractToFile(extracted);
            }

            using var snapshot = new SqliteConnection(new SqliteConnectionStringBuilder
            {
                DataSource = extracted,
                Mode = SqliteOpenMode.ReadOnly,
                Cache = SqliteCacheMode.Private
            }.ToString());
            snapshot.Open();
            using var quickCheck = snapshot.CreateCommand();
            quickCheck.CommandText = "PRAGMA quick_check;";
            Assert.Equal("ok", quickCheck.ExecuteScalar()?.ToString());
            using var count = snapshot.CreateCommand();
            count.CommandText = "SELECT COUNT(*) FROM evidence;";
            Assert.True(Convert.ToInt64(count.ExecuteScalar()) >= 1);
        }
        finally
        {
            cancellation.Cancel();
            await writer.WaitAsync(TimeSpan.FromSeconds(10));
        }
    }

    [Fact]
    public void CreateValidatedSnapshot_RejectsCorruptSourceAndDeletesPartialOutput()
    {
        var source = Path.Combine(_tempRoot, "corrupt.db");
        var destination = Path.Combine(_tempRoot, "snapshot.db");
        File.WriteAllText(source, "not a sqlite database");

        var result = SqliteSnapshotService.CreateValidatedSnapshot(source, destination);

        Assert.False(result.Success);
        Assert.Contains("failed validation", result.Summary, StringComparison.OrdinalIgnoreCase);
        Assert.False(File.Exists(destination));
        Assert.False(File.Exists(destination + "-wal"));
        Assert.False(File.Exists(destination + "-shm"));
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_tempRoot))
                Directory.Delete(_tempRoot, recursive: true);
        }
        catch
        {
            // Best-effort temp cleanup only.
        }
    }
}
