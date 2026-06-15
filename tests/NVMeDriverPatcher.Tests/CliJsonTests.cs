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
}
