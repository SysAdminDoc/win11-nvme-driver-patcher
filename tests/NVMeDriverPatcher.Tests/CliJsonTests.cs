using System.Text.Json;
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
    public void Watchdog_FieldNamesAreStable()
    {
        var report = new WatchdogReport { Verdict = WatchdogVerdict.Healthy, TotalEvents = 2 };
        report.Counts.Add(new WatchdogEventCount { Source = "disk", Id = 51, Description = "paging error", Count = 2 });
        var data = Parse("watchdog", CliJson.BuildWatchdog(report)).GetProperty("data");

        Assert.Equal("Healthy", data.GetProperty("verdict").GetString());
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
    }

    [Fact]
    public void BypassIo_FieldNamesAreStable()
    {
        var result = new BypassIOResult
        {
            Supported = false, StorageType = "NVMe", DriverCompat = "nvmedisk.sys",
            BlockedBy = "native stack", Warning = "DirectStorage slower",
        };
        var data = Parse("bypassio", CliJson.BuildBypassIo(result)).GetProperty("data");

        Assert.False(data.GetProperty("supported").GetBoolean());
        Assert.Equal("NVMe", data.GetProperty("storageType").GetString());
        Assert.Equal("nvmedisk.sys", data.GetProperty("driverCompat").GetString());
        Assert.Equal("native stack", data.GetProperty("blockedBy").GetString());
        Assert.Equal("DirectStorage slower", data.GetProperty("warning").GetString());
    }

    [Fact]
    public void Controllers_FieldNamesAreStable()
    {
        var report = new PerControllerAuditReport();
        report.Controllers.Add(new ControllerAudit
        {
            IsNative = true, FriendlyName = "WD SN850X", BoundDriver = "nvmedisk.sys",
            InstanceId = "SCSI\\...", InfName = "nvmedisk.inf", DriverProvider = "Microsoft",
            DeviceClass = "DiskDrive", HardwareId = "SCSI\\DiskNVMe", CompatibleId = "GenNvmeDisk",
        });
        var data = Parse("controllers", CliJson.BuildControllers(report)).GetProperty("data");

        Assert.Equal(1, data.GetProperty("nativeCount").GetInt32());
        Assert.Equal(0, data.GetProperty("legacyCount").GetInt32());
        var c = data.GetProperty("controllers")[0];
        Assert.True(c.GetProperty("isNative").GetBoolean());
        Assert.Equal("WD SN850X", c.GetProperty("friendlyName").GetString());
        Assert.Equal("nvmedisk.sys", c.GetProperty("boundDriver").GetString());
        Assert.Equal("nvmedisk.inf", c.GetProperty("infName").GetString());
        Assert.Equal("Microsoft", c.GetProperty("driverProvider").GetString());
        Assert.Equal("GenNvmeDisk", c.GetProperty("compatibleId").GetString());
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
        Assert.Equal("verified", entry.GetProperty("confidence").GetString());
    }

    [Fact]
    public void FeatureStore_FieldNamesAreStable()
    {
        var configs = new List<FeatureConfigState>
        {
            new(735209102, true, 2, 8, "Runtime"),
            new(735209102, true, 2, 8, "Boot"),
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
    }
}
