using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;
using NVMeDriverPatcher.Models;

namespace NVMeDriverPatcher.Tests;

// Microsoft's StorNVMe power management page says StorNVMe doesn't use the drive's APST: Windows
// picks idle states from the power plan. How nvmedisk idles a drive isn't documented, and the
// old "~15% battery" figure had no source. Neither claim may come back in shipped text.
public sealed class LaptopPowerWordingTests
{
    private static readonly Regex UnsupportedClaim = new(
        @"APST\s+(power[- ]management\s+)?(broken|regression)|breaks\s+APST|disables\s+APST|reduce\s+APST|~\s*1[05]\s*%|10-15\s*%",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    [Fact]
    public void ShippedTextAndReadme_DoNotClaimNvmediskBreaksApstOrCiteAnUnsourcedBatteryFigure()
    {
        var root = RepoRoot();
        var files = Directory.EnumerateFiles(Path.Combine(root, "src"), "*.*", SearchOption.AllDirectories)
            .Where(f => f.EndsWith(".cs", StringComparison.OrdinalIgnoreCase) || f.EndsWith(".xaml", StringComparison.OrdinalIgnoreCase))
            .Where(f => !f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.OrdinalIgnoreCase) &&
                        !f.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.OrdinalIgnoreCase))
            .Append(Path.Combine(root, "README.md"));

        var hits = new List<string>();
        foreach (var file in files)
        {
            var lines = File.ReadAllLines(file);
            for (int i = 0; i < lines.Length; i++)
                if (UnsupportedClaim.IsMatch(lines[i]))
                    hits.Add($"{Path.GetRelativePath(root, file)}:{i + 1}: {lines[i].Trim()}");
        }

        Assert.True(hits.Count == 0, "Unsupported laptop power claims:\n" + string.Join("\n", hits));
    }

    [Fact]
    public void Detector_CatchesTheOldWording()
    {
        Assert.Matches(UnsupportedClaim, "Laptop -- APST broken, ~15% battery impact");
        Assert.Matches(UnsupportedClaim, "Laptop detected. APST power-management regression (~15% battery).");
        Assert.Matches(UnsupportedClaim, "Native NVMe breaks APST power management.");
        Assert.Matches(UnsupportedClaim, "Laptop power: nvmedisk.sys disables APST.");
        Assert.Matches(UnsupportedClaim, "Expect ~10-15% shorter battery life on idle workloads after patching.");
        Assert.DoesNotMatch(UnsupportedClaim, "Microsoft doesn't document how nvmedisk.sys idles the drive, so battery life after patching isn't known.");
    }

    [Fact]
    public void UnconfirmedTuningKeys_AreOnesTheTuningServiceWrites()
    {
        // The panel's "can't be confirmed" note only means something for values Apply writes.
        var written = new[]
        {
            TuningProfile.Key_QueueDepth, TuningProfile.Key_MaxReadSplit, TuningProfile.Key_MaxWriteSplit,
            TuningProfile.Key_IoSubmissionQueueCount, TuningProfile.Key_IdlePowerTimeout, TuningProfile.Key_StandbyPowerTimeout
        };
        Assert.All(TuningProfile.UnconfirmedKeys, key => Assert.Contains(key, written));
        Assert.DoesNotContain(TuningProfile.Key_QueueDepth, TuningProfile.UnconfirmedKeys);
        Assert.DoesNotContain(TuningProfile.Key_IoSubmissionQueueCount, TuningProfile.UnconfirmedKeys);
    }

    private static string RepoRoot([CallerFilePath] string sourceFile = "") =>
        Path.GetFullPath(Path.Combine(Path.GetDirectoryName(sourceFile)!, "..", ".."));
}
