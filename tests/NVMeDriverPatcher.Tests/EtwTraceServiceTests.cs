using System.Diagnostics;
using System.Text.Json;
using System.Xml.Linq;
using NVMeDriverPatcher.Services;

namespace NVMeDriverPatcher.Tests;

public sealed class EtwTraceServiceTests : IDisposable
{
    private readonly string _tempRoot = Path.Combine(
        Path.GetTempPath(), $"NVMeDriverPatcher.EtwTests.{Guid.NewGuid():N}");

    public EtwTraceServiceTests()
    {
        Directory.CreateDirectory(_tempRoot);
    }

    [Fact]
    public void BuildNvmeDiskProviderProfile_DeclaresMicrosoftProviderAndFileProfile()
    {
        var xml = XDocument.Parse(EtwTraceService.BuildNvmeDiskProviderProfile());
        var provider = xml.Descendants("EventProvider")
            .Single(element => element.Attribute("Id")?.Value == "NvmeDiskProvider");
        var profile = xml.Descendants("Profile")
            .Single(element => element.Attribute("Id")?.Value == "NvmeDiskWatch.Verbose.File");
        var profiles = xml.Descendants("Profile")
            .Where(element => element.Attribute("Name")?.Value == "NvmeDiskWatch")
            .Select(element => element.Attribute("Id")?.Value)
            .ToArray();

        Assert.Equal("9799276c-fb04-47e8-845e-36946045c218", provider.Attribute("Name")?.Value);
        Assert.Equal("5", provider.Attribute("Level")?.Value);
        Assert.Equal("false", provider.Attribute("Strict")?.Value);
        Assert.Equal("NvmeDiskWatch.Verbose.File", profile.Attribute("Id")?.Value);
        Assert.Contains("NvmeDiskWatch.Verbose.Memory", profiles);
        Assert.Equal("0x0", provider.Descendants("Keyword").Single().Attribute("Value")?.Value);
    }

    [Fact]
    public void BuildNvmeDiskProviderProfile_IsAcceptedByInstalledWpr()
    {
        if (!EtwTraceService.IsWprAvailable())
            return;

        var profilePath = Path.Combine(_tempRoot, "nvmedisk.wprp");
        File.WriteAllText(profilePath, EtwTraceService.BuildNvmeDiskProviderProfile());
        var startInfo = new ProcessStartInfo(SystemToolPathService.Resolve("wpr.exe"))
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };
        startInfo.ArgumentList.Add("-profiles");
        startInfo.ArgumentList.Add(profilePath);
        var result = TestProcessRunner.Run(startInfo, TimeSpan.FromSeconds(30));

        // The command must enumerate the custom profile rather than merely parsing XML in the
        // test process. This catches WPR schema/collector mistakes that an XML parser accepts.
        Assert.False(result.TimedOut, result.StdErr);
        Assert.Equal(0, result.ExitCode);
    }

    [Fact]
    public void ShouldRequestNvmeDiskProvider_OnlyForBoundPostPatchStack()
    {
        Assert.True(EtwTraceService.ShouldRequestNvmeDiskProvider(EtwTracePhase.PostPatch, nativeStackBound: true));
        Assert.False(EtwTraceService.ShouldRequestNvmeDiskProvider(EtwTracePhase.PostPatch, nativeStackBound: false));
        Assert.False(EtwTraceService.ShouldRequestNvmeDiskProvider(EtwTracePhase.PrePatch, nativeStackBound: true));
    }

    [Fact]
    public void ContainsNvmeDiskProvider_MatchesNameOrGuidFromWprStatus()
    {
        Assert.True(EtwTraceService.ContainsNvmeDiskProvider(
            "Providers\r\nMicrosoft-Windows-NvmeDisk: 0x0: 0x05", string.Empty));
        Assert.True(EtwTraceService.ContainsNvmeDiskProvider(
            "Providers\r\n9799276c-fb04-47e8-845e-36946045c218: 0x0: 0x05", string.Empty));
        Assert.False(EtwTraceService.ContainsNvmeDiskProvider(
            "Providers\r\nMicrosoft-Windows-StorPort: 0x0: 0x05", string.Empty));
    }

    [Fact]
    public void GetLatestProviderEvidence_ReadsNewestCaptureMetadata()
    {
        var etlDir = Path.Combine(_tempRoot, "etl");
        Directory.CreateDirectory(etlDir);
        var older = new EtwTraceProviderEvidence
        {
            Phase = EtwTracePhase.PrePatch,
            CapturedAtUtc = DateTime.UtcNow.AddMinutes(-1),
            TraceFileName = "pre.etl",
            ProviderStatus = "not applicable to the pre-patch capture"
        };
        var newer = new EtwTraceProviderEvidence
        {
            Phase = EtwTracePhase.PostPatch,
            CapturedAtUtc = DateTime.UtcNow,
            TraceFileName = "post.etl",
            NativeStackProbeSucceeded = true,
            NativeStackBound = true,
            ProviderRequested = true,
            ProviderPresent = true,
            ProviderStatus = "present in the active WPR session"
        };
        var olderPath = Path.Combine(etlDir, "pre.etl" + EtwTraceService.EvidenceFileSuffix);
        var newerPath = Path.Combine(etlDir, "post.etl" + EtwTraceService.EvidenceFileSuffix);
        File.WriteAllText(olderPath, JsonSerializer.Serialize(older));
        File.WriteAllText(newerPath, JsonSerializer.Serialize(newer));
        File.SetLastWriteTimeUtc(olderPath, DateTime.UtcNow.AddMinutes(-1));
        File.SetLastWriteTimeUtc(newerPath, DateTime.UtcNow);

        var result = EtwTraceService.GetLatestProviderEvidence(_tempRoot);

        Assert.NotNull(result);
        Assert.Equal(EtwTracePhase.PostPatch, result!.Phase);
        Assert.True(result.ProviderPresent);
        Assert.Equal("post.etl", result.TraceFileName);
    }

    public void Dispose()
    {
        try { Directory.Delete(_tempRoot, recursive: true); } catch { }
    }
}
