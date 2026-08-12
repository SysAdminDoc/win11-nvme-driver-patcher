using System.Diagnostics;

namespace NVMeDriverPatcher.Tests;

public sealed class TestProcessRunnerTests
{
    [Fact]
    public async Task HangingChild_IsKilledAndReportedAsTimeout()
    {
        var startInfo = new ProcessStartInfo(NVMeDriverPatcher.Services.SystemToolPathService.PowerShell)
        {
            RedirectStandardError = true,
            RedirectStandardOutput = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        startInfo.ArgumentList.Add("-NoProfile");
        startInfo.ArgumentList.Add("-Command");
        startInfo.ArgumentList.Add("Start-Sleep -Seconds 30");

        var result = await TestProcessRunner.RunAsync(startInfo, TimeSpan.FromMilliseconds(250));

        Assert.True(result.TimedOut);
        Assert.Equal(-1, result.ExitCode);
    }
}
