using NVMeDriverPatcher.Models;
using NVMeDriverPatcher.Services;

namespace NVMeDriverPatcher.Tests;

public sealed class TuningProfileIoServiceTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "NVMePatcher_TuningIoTests_" + Guid.NewGuid().ToString("N"));

    public TuningProfileIoServiceTests()
    {
        Directory.CreateDirectory(_dir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { }
    }

    [Fact]
    public void ExportImport_RoundTripsProfile()
    {
        var path = Path.Combine(_dir, "profile.json");
        var profile = new TuningProfile
        {
            Name = "Workstation",
            Description = "Test profile",
            QueueDepth = 96,
            NvmeMaxReadSplit = 256,
            NoLowPowerTransitions = 1
        };

        TuningProfileIoService.Export("Lab", profile, path);
        var (imported, summary) = TuningProfileIoService.Import(path);

        Assert.NotNull(imported);
        Assert.Equal(96, imported!.QueueDepth);
        Assert.Equal(256, imported.NvmeMaxReadSplit);
        Assert.Equal(1, imported.NoLowPowerTransitions);
        Assert.Contains("Lab", summary);
    }

    [Fact]
    public void Export_BlankNameFallsBackToCustom()
    {
        var path = Path.Combine(_dir, "blank-name.json");

        TuningProfileIoService.Export("   ", TuningProfile.Balanced, path);
        var (_, summary) = TuningProfileIoService.Import(path);

        Assert.Contains("Custom", summary);
    }

    [Fact]
    public void Import_MissingFileReturnsSummary()
    {
        var (profile, summary) = TuningProfileIoService.Import(Path.Combine(_dir, "missing.json"));

        Assert.Null(profile);
        Assert.Contains("not found", summary);
    }
}
