using NVMeDriverPatcher.Services;

namespace NVMeDriverPatcher.Tests;

public sealed class PatchServiceTests
{
    [Theory]
    [InlineData(0, true)]
    [InlineData(1116, true)]
    [InlineData(5, false)]
    [InlineData(1, false)]
    public void IsCancelRestartSuccessExitCode_OnlyAcceptsCanceledOrNoShutdownInProgress(int exitCode, bool expected)
    {
        Assert.Equal(expected, PatchService.IsCancelRestartSuccessExitCode(exitCode));
    }

    [Theory]
    [InlineData("C:", "C:")]
    [InlineData("c:", "C:")]
    [InlineData("D:\\", "D:")]
    [InlineData(null, "C:")]
    [InlineData("", "C:")]
    [InlineData("not-a-drive", "C:")]
    public void NormalizeSystemDrive_ReturnsManageBdeCompatibleDriveName(string? raw, string expected)
    {
        Assert.Equal(expected, PatchService.NormalizeSystemDrive(raw));
    }

    [Fact]
    public void ReportProgress_SwallowsCallbackFailures()
    {
        PatchService.ReportProgress((_, _) => throw new InvalidOperationException("dispatcher closed"), 10, "working");
    }

    [Fact]
    public void SanitizeRestorePointDescription_EscapesPowerShellStringContent()
    {
        var sanitized = PatchService.SanitizeRestorePointDescription("pre'patch\r\nnext");

        Assert.Equal("pre''patch  next", sanitized);
    }

    [Fact]
    public void SanitizeRestorePointDescription_CapsLongDescriptions()
    {
        var sanitized = PatchService.SanitizeRestorePointDescription(new string('x', 250));

        Assert.Equal(200, sanitized.Length);
    }

    [Fact]
    public void CreateRestorePointStartInfo_UsesTokenizedPowerShellArguments()
    {
        var psi = PatchService.CreateRestorePointStartInfo("pre'patch");

        Assert.Equal("powershell.exe", psi.FileName);
        Assert.False(psi.UseShellExecute);
        Assert.Equal(string.Empty, psi.Arguments);
        Assert.Equal(
            new[]
            {
                "-NoProfile",
                "-NonInteractive",
                "-Command",
                "Checkpoint-Computer -Description 'pre''patch' -RestorePointType 'MODIFY_SETTINGS' -ErrorAction Stop"
            },
            psi.ArgumentList.ToArray());
    }
}
