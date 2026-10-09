using System.Runtime.CompilerServices;
using NVMeDriverPatcher.Services;

namespace NVMeDriverPatcher.Tests;

// Smart App Control's VerifiedAndReputablePolicyState: 0 off, 1 on, 2 evaluation. Anything else,
// including a missing value or another type, reads as Unknown rather than as off.
public sealed class SmartAppControlServiceTests
{
    [Theory]
    [InlineData(0, SmartAppControlState.Off)]
    [InlineData(1, SmartAppControlState.On)]
    [InlineData(2, SmartAppControlState.Evaluation)]
    [InlineData(3, SmartAppControlState.Unknown)]
    [InlineData(-1, SmartAppControlState.Unknown)]
    public void Classify_MapsTheDwordStates(int raw, SmartAppControlState expected)
    {
        Assert.Equal(expected, SmartAppControlService.Classify(raw));
    }

    [Fact]
    public void Classify_TreatsMissingOrNonDwordValuesAsUnknown()
    {
        Assert.Equal(SmartAppControlState.Unknown, SmartAppControlService.Classify(null));
        Assert.Equal(SmartAppControlState.Unknown, SmartAppControlService.Classify("1"));
        Assert.Equal(SmartAppControlState.Unknown, SmartAppControlService.Classify(1L));
    }

    [Fact]
    public void DownloadNote_WarnsWhenOnOrEvaluating_AndStaysQuietOtherwise()
    {
        var on = SmartAppControlService.DownloadNote(SmartAppControlState.On);
        Assert.NotNull(on);
        Assert.Contains("aren't signed", on, StringComparison.Ordinal);
        Assert.Contains("no per-app exception", on, StringComparison.Ordinal);
        Assert.Contains("README", on, StringComparison.Ordinal);

        Assert.Contains("evaluation mode", SmartAppControlService.DownloadNote(SmartAppControlState.Evaluation), StringComparison.Ordinal);
        Assert.Null(SmartAppControlService.DownloadNote(SmartAppControlState.Off));
        Assert.Null(SmartAppControlService.DownloadNote(SmartAppControlState.Unknown));
    }

    [Fact]
    public void Describe_NamesEveryState()
    {
        foreach (var state in Enum.GetValues<SmartAppControlState>())
            Assert.False(string.IsNullOrWhiteSpace(SmartAppControlService.Describe(state)));
        Assert.Contains("VerifiedAndReputablePolicyState", SmartAppControlService.Describe(SmartAppControlState.Unknown), StringComparison.Ordinal);
    }

    [Fact]
    public void Read_OnThisMachine_DoesNotThrow()
    {
        // Read-only; the value is readable without elevation.
        Assert.True(Enum.IsDefined(SmartAppControlService.Read()));
    }

    [Fact]
    public void ReadmeSection_ExistsForTheNoteToPointAt()
    {
        var readme = File.ReadAllText(Path.Combine(RepoRoot(), "README.md"));
        Assert.Contains("### If Windows blocks it", readme, StringComparison.Ordinal);
        Assert.Contains("\"If Windows blocks it\"", SmartAppControlService.DownloadNote(SmartAppControlState.On), StringComparison.Ordinal);
        Assert.Contains("Smart App Control", readme, StringComparison.Ordinal);
    }

    private static string RepoRoot([CallerFilePath] string sourceFile = "") =>
        Path.GetFullPath(Path.Combine(Path.GetDirectoryName(sourceFile)!, "..", ".."));
}
