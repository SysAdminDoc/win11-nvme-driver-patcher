using NVMeDriverPatcher.Services;

namespace NVMeDriverPatcher.Tests;

public sealed class MinidumpTriageServiceTests
{
    [Fact]
    public void BuildSummary_NoDumps_MessagesCleanSlate()
    {
        var report = new MinidumpTriageReport { TotalFound = 0, NewerThanPatch = 0, NVMeRelated = 0 };
        var s = MinidumpTriageService.BuildSummary(report);
        Assert.Contains("Clean slate", s, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void BuildSummary_OnlyOldDumps_MentionsOlder()
    {
        var report = new MinidumpTriageReport { TotalFound = 3, NewerThanPatch = 0, NVMeRelated = 0 };
        var s = MinidumpTriageService.BuildSummary(report);
        Assert.Contains("No new crash dumps since patch", s);
        Assert.Contains("3 older dump(s)", s);
    }

    [Fact]
    public void BuildSummary_NewDumpsButNoneNVMe_MessagesNoNVMeReference()
    {
        var report = new MinidumpTriageReport { TotalFound = 3, NewerThanPatch = 2, NVMeRelated = 0 };
        var s = MinidumpTriageService.BuildSummary(report);
        Assert.Contains("none reference the NVMe stack", s);
    }

    [Fact]
    public void BuildSummary_NVMeReferencingDumps_MessagesInvestigate()
    {
        var report = new MinidumpTriageReport { TotalFound = 3, NewerThanPatch = 2, NVMeRelated = 1 };
        var s = MinidumpTriageService.BuildSummary(report);
        Assert.Contains("1/2", s);
        Assert.Contains("investigate", s);
    }
}
