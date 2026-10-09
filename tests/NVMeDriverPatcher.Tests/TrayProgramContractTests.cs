using System.Runtime.CompilerServices;

namespace NVMeDriverPatcher.Tests;

// The tray is a per-session agent: it must not claim a machine-wide mutex (a second RDP or
// fast-user-switch session would get no icon), and its poll does WMI and event-log reads that
// must stay off the UI thread.
public sealed class TrayProgramContractTests
{
    private static string Tray([CallerFilePath] string sourceFile = "") =>
        File.ReadAllText(Path.GetFullPath(Path.Combine(
            Path.GetDirectoryName(sourceFile)!, "..", "..", "src", "NVMeDriverPatcher.Tray", "Program.cs")));

    [Fact]
    public void SingleInstanceMutex_IsPerSession()
    {
        var program = Tray();
        Assert.Contains("\"Local\\NVMeDriverPatcher.Tray.Single\"", program, StringComparison.Ordinal);
        Assert.DoesNotContain("Global\\\\", program, StringComparison.Ordinal);
    }

    [Fact]
    public void Poll_RunsOffTheUiThreadAndMarshalsBack()
    {
        var program = Tray();
        var refresh = program.IndexOf("static void Refresh()", StringComparison.Ordinal);
        var apply = program.IndexOf("static void ApplyPoll(", StringComparison.Ordinal);
        Assert.True(refresh >= 0 && apply > refresh);
        var refreshBody = program[refresh..apply];
        Assert.Contains("Task.Run(", refreshBody, StringComparison.Ordinal);
        Assert.Contains("BeginInvoke(", refreshBody, StringComparison.Ordinal);
        // The heavy reads live in Poll, which only the worker calls.
        Assert.DoesNotContain("PatchVerificationService.Evaluate", refreshBody, StringComparison.Ordinal);
        Assert.DoesNotContain("_statusItem.Text", refreshBody, StringComparison.Ordinal);
    }
}
