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

        // Full alone leaves it off, so the help says it's a separate box and the plain Full
        // confirmation says it stays off. The opt-in confirmation carries the DISM caveat.
        var fullHelp = Assert.Single(lines, l => l.Contains("const string FullProfileHelpText", StringComparison.Ordinal));
        Assert.Contains("156965516 (Standalone_Future) has its own checkbox", fullHelp, StringComparison.Ordinal);
        Assert.Contains("DISM /ScanHealth reports component store corruption", fullHelp, StringComparison.Ordinal);
        var fullConfirm = Assert.Single(lines, l => l.Contains("\"Mode: FULL.", StringComparison.Ordinal));
        Assert.Contains("156965516 stays off", fullConfirm, StringComparison.Ordinal);
        var optInConfirm = Assert.Single(lines, l => l.Contains("\"Mode: FULL with 156965516.", StringComparison.Ordinal));
        Assert.Contains("While 156965516 is set, DISM /ScanHealth", optInConfirm, StringComparison.Ordinal);
    }
}
