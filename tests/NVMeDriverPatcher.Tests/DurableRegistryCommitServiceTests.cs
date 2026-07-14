using Microsoft.Win32;
using NVMeDriverPatcher.Models;
using NVMeDriverPatcher.Services;

namespace NVMeDriverPatcher.Tests;

public sealed class DurableRegistryCommitServiceTests
{
    [Fact]
    public void Commit_RequiresWriteThenFlushThenFreshRead()
    {
        var mutation = Mutation("735209102", 1, RegistryValueKind.DWord);
        var platform = new FakePlatform();

        var result = DurableRegistryCommitService.Commit(mutation, platform);

        Assert.True(result.Success, result.Summary);
        Assert.Equal(new[] { "write", "flush", "read-fresh" }, platform.Events);
    }

    [Theory]
    [InlineData("write")]
    [InlineData("flush")]
    [InlineData("reopen/readback")]
    public void Commit_FailsClosedAtEveryDurabilityStage(string failureStage)
    {
        var mutation = Mutation(string.Empty, "nvmedisk", RegistryValueKind.String);
        var platform = new FakePlatform { FailureStage = failureStage };

        var result = DurableRegistryCommitService.Commit(mutation, platform);

        Assert.False(result.Success);
        Assert.Equal(failureStage, result.Stage);
        Assert.Contains("SYSTEM\\Test\\(Default)", result.Summary, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(PatchProfile.Safe, false, 5, 3)]
    [InlineData(PatchProfile.Safe, true, 6, 4)]
    [InlineData(PatchProfile.Full, false, 7, 5)]
    [InlineData(PatchProfile.Full, true, 8, 6)]
    public void BuildRequiredRegistryMutations_CoversFeatureAndEverySafeBootWrite(
        PatchProfile profile,
        bool includeServer,
        int expectedWrites,
        int expectedCounted)
    {
        var mutations = PatchService.BuildRequiredRegistryMutations(profile, includeServer);

        Assert.Equal(expectedWrites, mutations.Count);
        Assert.Equal(expectedCounted, mutations.Count(mutation => mutation.CountsTowardPatchTotal));
        Assert.Contains(mutations, mutation => mutation.Path == AppConfig.SafeBootMinimalPath);
        Assert.Contains(mutations, mutation => mutation.Path == AppConfig.SafeBootNetworkPath);
        Assert.Contains(mutations, mutation => mutation.Path == AppConfig.SafeBootMinimalServicePath);
        Assert.Contains(mutations, mutation => mutation.Path == AppConfig.SafeBootNetworkServicePath);
    }

    [Fact]
    public void EveryRequiredWrite_RejectsInjectedFlushAndReadbackFaults()
    {
        var mutations = PatchService.BuildRequiredRegistryMutations(PatchProfile.Full, includeServer: true);

        foreach (var mutation in mutations)
        {
            var flushFailure = DurableRegistryCommitService.Commit(
                mutation,
                new FakePlatform { FailureStage = "flush" });
            Assert.False(flushFailure.Success);
            Assert.Equal("flush", flushFailure.Stage);
            Assert.Contains(mutation.Path, flushFailure.Summary, StringComparison.Ordinal);

            var mismatch = DurableRegistryCommitService.Commit(
                mutation,
                new FakePlatform { ReturnMismatchedValue = true });
            Assert.False(mismatch.Success);
            Assert.Equal("reopen/readback", mismatch.Stage);
            Assert.Contains(mutation.Path, mismatch.Summary, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void CommitAll_InjectedFaultAtEveryWrite_NeverReportsBatchApplied()
    {
        var mutations = PatchService.BuildRequiredRegistryMutations(PatchProfile.Full, includeServer: true);

        for (var ordinal = 1; ordinal <= mutations.Count; ordinal++)
        {
            var flushFailure = DurableRegistryCommitService.CommitAll(
                mutations,
                new FakePlatform { FailureStage = "flush", FailOnMutationNumber = ordinal });
            Assert.False(flushFailure.Success);
            Assert.Contains(mutations[ordinal - 1].Path, flushFailure.Summary, StringComparison.Ordinal);

            var readbackMismatch = DurableRegistryCommitService.CommitAll(
                mutations,
                new FakePlatform { MismatchOnMutationNumber = ordinal });
            Assert.False(readbackMismatch.Success);
            Assert.Contains(mutations[ordinal - 1].Path, readbackMismatch.Summary, StringComparison.Ordinal);
        }
    }

    private static DurableRegistryMutation Mutation(
        string valueName,
        object expected,
        RegistryValueKind kind) =>
        new(@"SYSTEM\Test", valueName, expected, kind, "test mutation", true);

    private sealed class FakePlatform : IDurableRegistryPlatform
    {
        private DurableRegistryMutation? _written;
        private int _writeOrdinal;

        public string? FailureStage { get; init; }
        public bool ReturnMismatchedValue { get; init; }
        public int? FailOnMutationNumber { get; init; }
        public int? MismatchOnMutationNumber { get; init; }
        public List<string> Events { get; } = [];

        public void Write(DurableRegistryMutation mutation)
        {
            Events.Add("write");
            _writeOrdinal++;
            if (ShouldFail("write"))
                throw new UnauthorizedAccessException("simulated write denial");
            _written = mutation;
        }

        public void Flush(string path)
        {
            Events.Add("flush");
            if (ShouldFail("flush"))
                throw new IOException("simulated flush failure");
            Assert.Equal(_written?.Path, path);
        }

        public object? ReadFresh(string path, string valueName)
        {
            Events.Add("read-fresh");
            if (ShouldFail("reopen/readback"))
                throw new IOException("simulated reopen failure");
            Assert.Equal(_written?.Path, path);
            Assert.Equal(_written?.ValueName, valueName);
            if (ReturnMismatchedValue || MismatchOnMutationNumber == _writeOrdinal)
                return _written?.ExpectedValue is int ? 0 : "unexpected";
            return _written?.ExpectedValue;
        }

        private bool ShouldFail(string stage) =>
            FailureStage == stage &&
            (FailOnMutationNumber is null || FailOnMutationNumber == _writeOrdinal);
    }
}
