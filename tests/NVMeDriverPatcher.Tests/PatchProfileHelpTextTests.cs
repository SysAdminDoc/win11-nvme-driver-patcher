namespace NVMeDriverPatcher.Tests;

public sealed class PatchProfileHelpTextTests
{
    [Fact]
    public void ProfileHelpText_DoesNotConflatePatchProfileWithWindowsSafeMode()
    {
        var sourcePath = Path.GetFullPath(Path.Combine(
            Path.GetDirectoryName(typeof(PatchProfileHelpTextTests).Assembly.Location)!,
            "..", "..", "..", "..", "..",
            "src", "NVMeDriverPatcher", "ViewModels", "MainViewModel.cs"));
        var source = File.ReadAllText(sourcePath);

        Assert.Contains("SafeProfileHelpText", source, StringComparison.Ordinal);
        Assert.Contains("Safe profile writes only feature flag 735209102", source, StringComparison.Ordinal);
        Assert.Contains("Full profile adds 1853569164", source, StringComparison.Ordinal);
        Assert.DoesNotContain("Safe Mode writes", source, StringComparison.Ordinal);
        Assert.DoesNotContain("Try Safe Mode first", source, StringComparison.Ordinal);
    }

    [Fact]
    public void FullProfileHelpAndConfirmation_MentionTheDismReport()
    {
        // Issue #19: 156965516 alone made DISM /ScanHealth report store corruption on 24H2.
        var sourcePath = Path.GetFullPath(Path.Combine(
            Path.GetDirectoryName(typeof(PatchProfileHelpTextTests).Assembly.Location)!,
            "..", "..", "..", "..", "..",
            "src", "NVMeDriverPatcher", "ViewModels", "MainViewModel.cs"));
        var lines = File.ReadAllLines(sourcePath);

        var fullHelp = Assert.Single(lines, l => l.Contains("const string FullProfileHelpText", StringComparison.Ordinal));
        Assert.Contains("while 156965516 is set DISM /ScanHealth", fullHelp, StringComparison.Ordinal);
        var fullConfirm = Assert.Single(lines, l => l.Contains("\"Mode: FULL.", StringComparison.Ordinal));
        Assert.Contains("While 156965516 is set, DISM /ScanHealth", fullConfirm, StringComparison.Ordinal);
    }
}
