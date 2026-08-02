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
}
