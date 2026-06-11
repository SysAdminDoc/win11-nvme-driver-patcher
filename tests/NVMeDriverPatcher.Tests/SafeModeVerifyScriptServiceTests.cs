using NVMeDriverPatcher.Models;
using NVMeDriverPatcher.Services;

namespace NVMeDriverPatcher.Tests;

public sealed class SafeModeVerifyScriptServiceTests : IDisposable
{
    private readonly string _tempRoot = Path.Combine(Path.GetTempPath(), $"NVMeDriverPatcher.SafeModeVerify.Tests.{Guid.NewGuid():N}");

    public SafeModeVerifyScriptServiceTests()
    {
        Directory.CreateDirectory(_tempRoot);
    }

    [Fact]
    public void Generate_ScriptCoversBothSafeBootKeyStyles()
    {
        var config = new AppConfig { WorkingDir = _tempRoot };
        var path = SafeModeVerifyScriptService.Generate(config);

        Assert.True(File.Exists(path));
        var script = File.ReadAllText(path);

        // The Safe-Mode verification must check the GUID-class keys AND the
        // KB5079391-era service-name keys — a script checking only one style would
        // green-light a machine that BSODs in 25H2 Safe Mode.
        Assert.Contains(AppConfig.SafeBootGuid, script);
        Assert.Contains(AppConfig.SafeBootServiceName, script);
        Assert.Contains(AppConfig.SafeBootValue, script);
        Assert.Contains(AppConfig.SafeBootServiceValue, script);
    }

    [Fact]
    public void Generate_ScriptIsAdminGatedAndStrictMode()
    {
        var config = new AppConfig { WorkingDir = _tempRoot };
        var script = File.ReadAllText(SafeModeVerifyScriptService.Generate(config));
        Assert.Contains("#Requires -RunAsAdministrator", script);
        Assert.Contains("Set-StrictMode", script);
    }

    [Fact]
    public void Generate_ScriptHasBalancedBracesAndQuotes()
    {
        // Cheap structural sanity without dragging the PowerShell SDK into the test
        // project: unbalanced braces or double quotes are the classic string-builder
        // regression for generated scripts.
        var config = new AppConfig { WorkingDir = _tempRoot };
        var script = File.ReadAllText(SafeModeVerifyScriptService.Generate(config));
        Assert.Equal(script.Count(c => c == '{'), script.Count(c => c == '}'));
        Assert.True(script.Count(c => c == '"') % 2 == 0, "odd number of double quotes");
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
