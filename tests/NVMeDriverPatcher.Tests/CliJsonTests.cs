using System.Text.Json;
using NVMeDriverPatcher.Data;
using NVMeDriverPatcher.Models;
using NVMeDriverPatcher.Services;

namespace NVMeDriverPatcher.Tests;

// Pins the machine-readable CLI JSON contract: stable camelCase field names and a versioned
// envelope. The PowerShell module (and other automation) reads these exact names, so a rename
// here is a breaking change that must bump CliJson.SchemaVersion — these tests force that choice.
public sealed class CliJsonTests
{
    private static JsonElement Parse(string command, object data)
    {
        var json = CliJson.Serialize(command, data);
        return JsonDocument.Parse(json).RootElement;
    }

    [Fact]
    public void Envelope_HasVersionedShape()
    {
        var root = Parse("status", CliJson.BuildStatus(new PatchStatus(), null, EnablementSource.None, null));
        Assert.Equal(CliJson.SchemaVersion, root.GetProperty("schemaVersion").GetInt32());
        Assert.Equal("status", root.GetProperty("command").GetString());
        Assert.True(root.TryGetProperty("data", out _));
    }

    [Fact]
    public void Status_FieldNamesAreStable()
    {
        var status = new PatchStatus { Applied = true, Count = 5, Total = 5, Keys = { "735209102" } };
        var native = new NativeNVMeStatus { IsActive = true, ActiveDriver = "nvmedisk.sys" };
        var data = Parse("status", CliJson.BuildStatus(status, native, EnablementSource.RegistryPatch, null))
            .GetProperty("data");

        Assert.Equal("applied", data.GetProperty("status").GetString());
        Assert.True(data.GetProperty("applied").GetBoolean());
        Assert.False(data.GetProperty("partial").GetBoolean());
        Assert.Equal(5, data.GetProperty("componentsApplied").GetInt32());
        Assert.Equal(5, data.GetProperty("componentsTotal").GetInt32());
        Assert.Equal("735209102", data.GetProperty("appliedKeys")[0].GetString());
        Assert.True(data.GetProperty("nativeActive").GetBoolean());
        Assert.Equal("nvmedisk.sys", data.GetProperty("activeDriver").GetString());
        Assert.Equal("RegistryPatch", data.GetProperty("enablementSource").GetString());
    }

    [Fact]
    public void Status_NotApplied_ReportsNotAppliedString()
    {
        var data = Parse("status", CliJson.BuildStatus(new PatchStatus { Applied = false, Partial = false }, null, EnablementSource.None, null))
            .GetProperty("data");
        Assert.Equal("not-applied", data.GetProperty("status").GetString());
    }

    [Fact]
    public void Status_ReportsRegistryOverrideCompatibility()
    {
        var assessment = FallbackFeatureCatalog.AssessRegistryOverrides(
            new WindowsBuildDetails { BuildNumber = 26100, UBR = 8687 },
            AppConfig.FeatureIDs);
        var data = Parse("status", CliJson.BuildStatus(
                new PatchStatus(), null, EnablementSource.None, null, assessment))
            .GetProperty("data");

        var registry = data.GetProperty("registryOverride");
        Assert.Equal(26100, registry.GetProperty("buildNumber").GetInt32());
        Assert.Equal("pre-26200 sampled branch", registry.GetProperty("branch").GetString());
        Assert.False(registry.TryGetProperty("hasMismatch", out _));
        Assert.Equal("735209102", registry.GetProperty("features")[0].GetProperty("registryId").GetString());
        Assert.Equal(60786016, registry.GetProperty("features")[0].GetProperty("knownBranchId").GetInt32());
        Assert.False(registry.GetProperty("features")[0].TryGetProperty("matchesKnownFeature", out _));
        Assert.DoesNotContain("MISMATCH", registry.GetProperty("summary").GetString(), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Status_AlwaysCarriesScheduledTaskWarnings()
    {
        // Always present, so a script can test its length without checking that it exists first.
        var clean = Parse("status", CliJson.BuildStatus(new PatchStatus(), null, EnablementSource.None, null))
            .GetProperty("data");
        Assert.Equal(0, clean.GetProperty("scheduledTaskWarnings").GetArrayLength());

        const string warning = @"SysAdminDoc\NVMePatcher\BootVerify runs a program anyone can replace.";
        var flagged = Parse("status", CliJson.BuildStatus(
                new PatchStatus(), null, EnablementSource.None, null, null, null, new[] { warning }))
            .GetProperty("data");
        Assert.Equal(warning, flagged.GetProperty("scheduledTaskWarnings")[0].GetString());
    }

    [Fact]
    public void Watchdog_FieldNamesAreStable()
    {
        var report = new WatchdogReport
        {
            Verdict = WatchdogVerdict.Unavailable,
            ObservedVerdict = WatchdogVerdict.Warning,
            FailureCode = "StatePersistenceFailed",
            TotalEvents = 2
        };
        report.Counts.Add(new WatchdogEventCount { Source = "disk", Id = 51, Description = "paging error", Count = 2 });
        var data = Parse("watchdog", CliJson.BuildWatchdog(report)).GetProperty("data");

        Assert.Equal("Unavailable", data.GetProperty("verdict").GetString());
        Assert.False(data.GetProperty("dataAvailable").GetBoolean());
        Assert.Equal("StatePersistenceFailed", data.GetProperty("failureCode").GetString());
        Assert.Equal("Warning", data.GetProperty("observedVerdict").GetString());
        Assert.Equal(2, data.GetProperty("totalEvents").GetInt32());
        var first = data.GetProperty("eventCounts")[0];
        Assert.Equal("disk", first.GetProperty("source").GetString());
        Assert.Equal(51, first.GetProperty("id").GetInt32());
        Assert.Equal(2, first.GetProperty("count").GetInt32());
    }

    [Fact]
    public void RecoveryProof_FieldNamesAreStable()
    {
        var report = new RecoveryProofReport();
        report.BitLockerRecovery = new BitLockerRecoveryProof(
            new BitLockerVolumeEvidence
            {
                ProbeSucceeded = true,
                SystemVolumePresent = true,
                MountPoint = "C:",
                ConversionStatus = 1,
                ProtectionStatus = 0,
                SuspendCount = 1,
                RecoveryProtectorIds = ["{11111111-1111-1111-1111-111111111111}"]
            },
            new DirectoryJoinEvidence(true, DirectoryJoinKind.MicrosoftEntra));
        report.Items.Add(new RecoveryProofItem { Label = "System Restore", Passed = false, Detail = "off" });
        report.Items.Add(new RecoveryProofItem { Label = "Recovery kit", Passed = true, Detail = "fresh" });
        var data = Parse("recovery-proof", CliJson.BuildRecoveryProof(report)).GetProperty("data");

        Assert.False(data.GetProperty("allPassed").GetBoolean());
        Assert.Equal(1, data.GetProperty("passedCount").GetInt32());
        Assert.Equal(2, data.GetProperty("totalCount").GetInt32());
        var item = data.GetProperty("items")[0];
        Assert.Equal("System Restore", item.GetProperty("label").GetString());
        Assert.False(item.GetProperty("passed").GetBoolean());
        Assert.Equal("off", item.GetProperty("detail").GetString());
        var bitLocker = data.GetProperty("bitLocker");
        Assert.True(bitLocker.GetProperty("probeSucceeded").GetBoolean());
        Assert.True(bitLocker.GetProperty("encrypted").GetBoolean());
        Assert.True(bitLocker.GetProperty("readyForMutation").GetBoolean());
        Assert.Equal("C:", bitLocker.GetProperty("mountPoint").GetString());
        Assert.Equal(1u, bitLocker.GetProperty("suspendCount").GetUInt32());
        Assert.Equal("MicrosoftEntra", bitLocker.GetProperty("directoryJoin").GetString());
        Assert.Equal("{11111111-1111-1111-1111-111111111111}", bitLocker.GetProperty("protectorIds")[0].GetString());
    }

    [Fact]
    public void CriticalProbes_FieldNamesAndReasonCodesAreStable()
    {
        var report = new CriticalProbeReport { Scope = MutationProbeScope.RegistryPatch };
        report.Items.Add(new CriticalProbeResult
        {
            Id = "IntelStorage",
            Label = "Intel RST/VMD",
            Verdict = CriticalProbeVerdict.Unknown,
            ReasonCode = CriticalProbeReasonCode.Timeout,
            Detail = "query timed out",
            NativeError = "WMI=Timedout; HRESULT=0x80041069",
            Evidence = ["exception=ManagementException"],
            ObservedAtUtc = new DateTimeOffset(2026, 7, 14, 12, 0, 0, TimeSpan.Zero)
        });

        var data = Parse("preflight", CliJson.BuildCriticalProbes(report)).GetProperty("data");

        Assert.Equal("RegistryPatch", data.GetProperty("scope").GetString());
        Assert.False(data.GetProperty("allPassed").GetBoolean());
        Assert.True(data.GetProperty("hasUnknown").GetBoolean());
        Assert.Equal(2, data.GetProperty("exitCode").GetInt32());
        var item = data.GetProperty("items")[0];
        Assert.Equal("IntelStorage", item.GetProperty("id").GetString());
        Assert.Equal("Unknown", item.GetProperty("verdict").GetString());
        Assert.Equal("Timeout", item.GetProperty("reasonCode").GetString());
        Assert.Equal("WMI=Timedout; HRESULT=0x80041069", item.GetProperty("nativeError").GetString());
        Assert.Equal("exception=ManagementException", item.GetProperty("evidence")[0].GetString());
        Assert.Equal("2026-07-14T12:00:00+00:00", item.GetProperty("observedAtUtc").GetString());
    }

    [Fact]
    public void RecoveryEvidence_FieldNamesAndAdvisoryStateAreStable()
    {
        var evidence = new OsRecoveryEvidence
        {
            PointInTimeRestoreSupported = true,
            PointInTimeRestoreEnabled = null,
            RestorePointQuerySucceeded = false,
            QuickMachineRecoverySupported = true,
            QuickMachineRecoveryEnabled = true,
            QuickMachineRecoveryAutoRemediationEnabled = false,
            QuickMachineRecoveryQuerySucceeded = true,
        };
        var report = new RecoveryProofReport { OsRecovery = evidence };
        report.Items.Add(new RecoveryProofItem { Label = "Recovery kit", Passed = true, Detail = "fresh" });

        var proof = Parse("recovery-proof", CliJson.BuildRecoveryProof(report)).GetProperty("data");
        var os = proof.GetProperty("osRecovery");
        Assert.True(os.GetProperty("pointInTimeRestoreSupported").GetBoolean());
        Assert.False(os.GetProperty("restorePointQuerySucceeded").GetBoolean());
        Assert.True(os.GetProperty("quickMachineRecoveryEnabled").GetBoolean());
        Assert.False(os.GetProperty("quickMachineRecoveryAutoRemediationEnabled").GetBoolean());
        Assert.Equal(evidence.Summary, os.GetProperty("summary").GetString());
        Assert.Equal(evidence.PointInTimeRestoreSummary, os.GetProperty("pointInTimeRestoreSummary").GetString());
        Assert.Equal(evidence.QuickMachineRecoverySummary, os.GetProperty("quickMachineRecoverySummary").GetString());

        var probes = Parse("preflight", CliJson.BuildCriticalProbes(new CriticalProbeReport(), evidence))
            .GetProperty("data");
        Assert.True(probes.GetProperty("osRecovery").GetProperty("quickMachineRecoverySupported").GetBoolean());
    }

    [Fact]
    public void BypassIo_FieldNamesAreStable()
    {
        var result = new BypassIOResult
        {
            Supported = false, StorageType = "NVMe", DriverCompat = "nvmedisk.sys",
            BlockedBy = "native stack", Warning = "DirectStorage slower",
            GamingImpact = "Ratchet impact",
        };
        var data = Parse("bypassio", CliJson.BuildBypassIo(result)).GetProperty("data");

        Assert.False(data.GetProperty("supported").GetBoolean());
        Assert.Equal("NVMe", data.GetProperty("storageType").GetString());
        Assert.Equal("nvmedisk.sys", data.GetProperty("driverCompat").GetString());
        Assert.Equal("native stack", data.GetProperty("blockedBy").GetString());
        Assert.Equal("DirectStorage slower", data.GetProperty("warning").GetString());
        Assert.Equal("Ratchet impact", data.GetProperty("gamingImpact").GetString());
        Assert.False(data.TryGetProperty("history", out _));   // only with --history
    }

    [Fact]
    public void BypassIo_WithHistory_CarriesThePrePostDiff()
    {
        var before = new DateTime(2026, 10, 1, 12, 0, 0, DateTimeKind.Utc);
        var after = before.AddHours(1);
        List<BypassIoHistoryRecord> pre =
        [
            new() { Timestamp = before, VolumeLetter = "C:", Enabled = true, Stack = "stornvme", IsPrePatch = true },
            new() { Timestamp = before, VolumeLetter = "D:", Enabled = true, Stack = "stornvme", IsPrePatch = true },
        ];
        List<BypassIoHistoryRecord> post =
        [
            new() { Timestamp = after, VolumeLetter = "C:", Enabled = true, Stack = "nvmedisk" },
            new() { Timestamp = after, VolumeLetter = "D:", Enabled = false, Stack = "nvmedisk" },
        ];
        var diff = BypassIoInspectorService.DiffLatestPair(pre, post);

        var history = Parse("bypassio", CliJson.BuildBypassIo(new BypassIOResult(), diff))
            .GetProperty("data").GetProperty("history");

        Assert.True(history.GetProperty("recorded").GetBoolean());
        Assert.Equal(before, history.GetProperty("pre").GetProperty("takenAt").GetDateTime());
        Assert.Equal(2, history.GetProperty("pre").GetProperty("volumes").GetArrayLength());
        var postD = history.GetProperty("post").GetProperty("volumes")[1];
        Assert.Equal("D:", postD.GetProperty("volume").GetString());
        Assert.False(postD.GetProperty("enabled").GetBoolean());
        Assert.Equal("nvmedisk", postD.GetProperty("stack").GetString());
        var lost = Assert.Single(history.GetProperty("lostAfterPatch").EnumerateArray());
        Assert.Equal("D:", lost.GetString());
    }

    [Fact]
    public void BypassIo_WithHistoryButNoSnapshots_SaysNotRecorded()
    {
        var history = Parse("bypassio", CliJson.BuildBypassIo(new BypassIOResult(), BypassIoInspectorService.DiffLatestPair([], [])))
            .GetProperty("data").GetProperty("history");

        Assert.False(history.GetProperty("recorded").GetBoolean());
        Assert.False(history.TryGetProperty("pre", out _));
        Assert.False(history.TryGetProperty("post", out _));
        Assert.Empty(history.GetProperty("lostAfterPatch").EnumerateArray());
    }

    [Fact]
    public void VerifyPayload_UsesTheEnvelope_AndFieldNamesAreStable()
    {
        var result = new ArtifactIntegrityResult
        {
            PayloadPath = @"C:\kit",
            PayloadType = "directory",
            SchemaVersion = 2,
            Issues = [new(ArtifactIntegrityIssueKind.HashMismatch, "recovery.reg", "sha256 differs")]
        };

        var root = Parse("verify-payload", CliJson.BuildPayloadVerification(result));

        Assert.Equal(CliJson.SchemaVersion, root.GetProperty("schemaVersion").GetInt32());
        Assert.Equal("verify-payload", root.GetProperty("command").GetString());
        var data = root.GetProperty("data");
        Assert.False(data.GetProperty("success").GetBoolean());
        Assert.Equal(@"C:\kit", data.GetProperty("payloadPath").GetString());
        Assert.Equal("directory", data.GetProperty("payloadType").GetString());
        Assert.Equal(2, data.GetProperty("manifestSchemaVersion").GetInt32());
        Assert.Contains("1 issue", data.GetProperty("summary").GetString(), StringComparison.Ordinal);
        var issue = Assert.Single(data.GetProperty("issues").EnumerateArray());
        Assert.Equal("HashMismatch", issue.GetProperty("kind").GetString());
        Assert.Equal("recovery.reg", issue.GetProperty("relativePath").GetString());
        Assert.Equal("sha256 differs", issue.GetProperty("detail").GetString());
    }

    [Fact]
    public void VerifyPayload_CleanResult_HasNoIssuesAndSucceeds()
    {
        var data = Parse("verify-payload", CliJson.BuildPayloadVerification(new ArtifactIntegrityResult
        {
            PayloadPath = @"D:\kit.zip", PayloadType = "zip", SchemaVersion = 1
        })).GetProperty("data");

        Assert.True(data.GetProperty("success").GetBoolean());
        Assert.Equal(0, data.GetProperty("issues").GetArrayLength());
        Assert.StartsWith("Payload integrity verified", data.GetProperty("summary").GetString(), StringComparison.Ordinal);
    }

    [Fact]
    public void Controllers_FieldNamesAreStable()
    {
        var report = new PerControllerAuditReport();
        report.ObservedAtUtc = new DateTimeOffset(2026, 7, 14, 15, 0, 0, TimeSpan.Zero);
        report.Controllers.Add(new ControllerAudit
        {
            IsNative = true, FriendlyName = "WD SN850X", BoundDriver = "nvmedisk.sys",
            BoundDriverVersion = "10.0.26100.8521",
            InstanceId = "SCSI\\...", InfName = "nvmedisk.inf", DriverProvider = "Microsoft",
            DeviceClass = "DiskDrive", HardwareId = "SCSI\\DiskNVMe", CompatibleId = "GenNvmeDisk",
            DriverCandidateProbeSucceeded = true,
            DriverCandidateCommand = "pnputil.exe /enum-devices ... /drivers /format xml",
            DriverCandidates =
            {
                new ControllerDriverCandidate
                {
                    InfName = "nvmedisk.inf", Provider = "Microsoft", Rank = "00FF2006",
                    Status = "BestRanked/Installed", DriverVersion = "06/21/2006 10.0.26100.8521"
                }
            }
        });
        var data = Parse("controllers", CliJson.BuildControllers(report)).GetProperty("data");

        Assert.Equal(1, data.GetProperty("nativeCount").GetInt32());
        Assert.Equal(0, data.GetProperty("legacyCount").GetInt32());
        Assert.Equal(0, data.GetProperty("candidateProbeFailureCount").GetInt32());
        var c = data.GetProperty("controllers")[0];
        Assert.True(c.GetProperty("isNative").GetBoolean());
        Assert.Equal("WD SN850X", c.GetProperty("friendlyName").GetString());
        Assert.Equal("nvmedisk.sys", c.GetProperty("boundDriver").GetString());
        Assert.Equal("nvmedisk.inf", c.GetProperty("infName").GetString());
        Assert.Equal("Microsoft", c.GetProperty("driverProvider").GetString());
        Assert.Equal("10.0.26100.8521", c.GetProperty("boundDriverVersion").GetString());
        Assert.Equal("GenNvmeDisk", c.GetProperty("compatibleId").GetString());
        Assert.True(c.GetProperty("driverCandidateProbeSucceeded").GetBoolean());
        Assert.Equal("00FF2006", c.GetProperty("driverCandidates")[0].GetProperty("rank").GetString());
        Assert.True(c.GetProperty("driverCandidates")[0].GetProperty("isInstalled").GetBoolean());
        Assert.Equal("2026-07-14T15:00:00+00:00", data.GetProperty("observedAtUtc").GetString());
    }

    [Fact]
    public void Reliability_FieldNamesAreStable()
    {
        var report = new ReliabilityCorrelationReport
        {
            DataAvailable = true,
            PrePatchAverage = 8.5,
            PostPatchAverage = 7.2,
            Summary = "Stability dropped after patch",
        };
        report.Series.Add(new ReliabilityPoint { Timestamp = new DateTime(2026, 6, 1, 0, 0, 0, DateTimeKind.Utc), Index = 8.5 });
        var data = Parse("reliability", CliJson.BuildReliability(report)).GetProperty("data");

        Assert.True(data.GetProperty("dataAvailable").GetBoolean());
        Assert.Equal(8.5, data.GetProperty("prePatchAverage").GetDouble());
        Assert.Equal(7.2, data.GetProperty("postPatchAverage").GetDouble());
        Assert.Equal(-1.3, data.GetProperty("delta").GetDouble(), 2);
        var point = data.GetProperty("series")[0];
        Assert.True(point.TryGetProperty("timestamp", out _));
        Assert.Equal(8.5, point.GetProperty("index").GetDouble());
    }

    [Fact]
    public void Minidump_FieldNamesAreStable()
    {
        var report = new MinidumpTriageReport
        {
            TotalFound = 3, NewerThanPatch = 1, NVMeRelated = 1, ScanCompleted = true,
            Summary = "1 NVMe-related dump found",
        };
        report.Dumps.Add(new MinidumpSummary
        {
            FilePath = @"C:\Windows\Minidump\061626-1234.dmp",
            SizeBytes = 262144,
            CreatedUtc = new DateTime(2026, 6, 16, 12, 0, 0, DateTimeKind.Utc),
            MentionsNVMeStack = true,
            MatchedModules = { "nvmedisk.sys" },
        });
        var data = Parse("minidump", CliJson.BuildMinidump(report)).GetProperty("data");

        Assert.Equal(3, data.GetProperty("totalFound").GetInt32());
        Assert.Equal(1, data.GetProperty("newerThanPatch").GetInt32());
        Assert.Equal(1, data.GetProperty("nvMeRelated").GetInt32());
        Assert.True(data.GetProperty("scanCompleted").GetBoolean());
        var dump = data.GetProperty("dumps")[0];
        Assert.True(dump.GetProperty("mentionsNVMeStack").GetBoolean());
        Assert.Equal(262144, dump.GetProperty("sizeBytes").GetInt64());
        Assert.Equal("nvmedisk.sys", dump.GetProperty("matchedModules")[0].GetString());
    }

    [Fact]
    public void FirmwareCompat_FieldNamesAreStable()
    {
        var db = new FirmwareCompatDatabase { SchemaVersion = 2, Updated = "2026-06-01" };
        db.Entries.Add(new FirmwareCompatEntry
        {
            Controller = "Samsung 990 Pro",
            Firmware = "4B2QJXD7",
            Level = FirmwareCompatLevel.Good,
            Note = "Works well",
            PowerLossRisk = true,
            Confidence = "verified",
        });
        var data = Parse("firmware", CliJson.BuildFirmwareCompat(db)).GetProperty("data");

        Assert.Equal(2, data.GetProperty("schemaVersion").GetInt32());
        Assert.Equal("2026-06-01", data.GetProperty("updated").GetString());
        Assert.Equal(1, data.GetProperty("entryCount").GetInt32());
        var entry = data.GetProperty("entries")[0];
        Assert.Equal("Samsung 990 Pro", entry.GetProperty("controller").GetString());
        Assert.Equal("4B2QJXD7", entry.GetProperty("firmware").GetString());
        Assert.Equal("Good", entry.GetProperty("level").GetString());
        Assert.True(entry.GetProperty("powerLossRisk").GetBoolean());
        Assert.Equal("verified", entry.GetProperty("confidence").GetString());
    }

    [Fact]
    public void FeatureStore_FieldNamesAreStable()
    {
        var configs = new List<FeatureConfigState>
        {
            new(735209102, true, 2, 8, "Runtime"),
            new(735209102, true, 2, 8, "Boot"),
            new FeatureConfigState(48613417, true, 2, 8, "Runtime") { IsCandidate = true },
        };
        var data = Parse("featurestore", CliJson.BuildFeatureStore(true, configs)).GetProperty("data");

        Assert.True(data.GetProperty("hasFallbackEvidence").GetBoolean());
        var first = data.GetProperty("configurations")[0];
        Assert.Equal(735209102, first.GetProperty("featureId").GetInt32());
        Assert.Equal("Runtime", first.GetProperty("store").GetString());
        Assert.True(first.GetProperty("found").GetBoolean());
        Assert.Equal(2, first.GetProperty("enabledState").GetInt32());
        Assert.Equal(8, first.GetProperty("priority").GetInt32());
        Assert.True(first.GetProperty("isEnabled").GetBoolean());
        Assert.True(data.GetProperty("configurations")[2].GetProperty("isCandidate").GetBoolean());
    }

    [Fact]
    public void Benchmark_UsesTheVersionedEnvelopeWithStableFieldNames()
    {
        var result = new BenchmarkResult
        {
            Label = "Post-Patch",
            Timestamp = "2026-10-05 09:00:00",
            Read = new BenchmarkMetrics { IOPS = 812345, ThroughputMBs = 3173.2, AvgLatencyMs = 0.079 },
            Write = new BenchmarkMetrics { IOPS = 701234 },
            Desktop = new BenchmarkProfileResult { Read = new BenchmarkMetrics { IOPS = 21000 } },
        };

        var root = Parse("benchmark", result);
        Assert.Equal(CliJson.SchemaVersion, root.GetProperty("schemaVersion").GetInt32());
        Assert.Equal("benchmark", root.GetProperty("command").GetString());

        var data = root.GetProperty("data");
        Assert.Equal("Post-Patch", data.GetProperty("label").GetString());
        Assert.Equal(812345, data.GetProperty("read").GetProperty("iops").GetDouble());
        Assert.Equal(3173.2, data.GetProperty("read").GetProperty("throughputMBs").GetDouble());
        Assert.Equal(0.079, data.GetProperty("read").GetProperty("avgLatencyMs").GetDouble());
        Assert.Equal(701234, data.GetProperty("write").GetProperty("iops").GetDouble());
        Assert.Equal("desktop-qd1", data.GetProperty("desktop").GetProperty("profileId").GetString());
        Assert.Equal(21000, data.GetProperty("desktop").GetProperty("read").GetProperty("iops").GetDouble());
    }

    [Fact]
    public void BenchmarkCommand_WritesItsJsonThroughTheEnvelope()
    {
        // `benchmark --json` printed a bare PascalCase object with no schemaVersion or command,
        // so automation that reads every other command's envelope broke on this one.
        var program = ReadRepoFile("src", "NVMeDriverPatcher.Cli", "Program.cs");
        var start = program.IndexOf("static int BenchmarkCommand(", StringComparison.Ordinal);
        var end = program.IndexOf("static int CompareBenchmarksCommand(", Math.Max(start, 0), StringComparison.Ordinal);
        Assert.True(start >= 0 && end > start, "BenchmarkCommand not found in the CLI");

        var body = program[start..end];
        Assert.Contains("CliJson.Serialize(\"benchmark\", result)", body, StringComparison.Ordinal);
        Assert.DoesNotContain("JsonSerializer.Serialize", body, StringComparison.Ordinal);
    }

    private static string ReadRepoFile(params string[] relative)
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "NVMeDriverPatcher.sln")))
            dir = dir.Parent;
        Assert.NotNull(dir);
        return File.ReadAllText(Path.Combine(new[] { dir!.FullName }.Concat(relative).ToArray()));
    }
}
