using NVMeDriverPatcher.Services;

namespace NVMeDriverPatcher.Tests;

public sealed class WatchdogServiceStateServiceTests
{
    [Theory]
    [InlineData(WatchdogServiceState.NotInstalled)]
    [InlineData(WatchdogServiceState.Running)]
    [InlineData(WatchdogServiceState.Stopped)]
    [InlineData(WatchdogServiceState.Pending)]
    [InlineData(WatchdogServiceState.Unknown)]
    public void Describe_CoversEveryState(WatchdogServiceState state)
    {
        var text = WatchdogServiceStateService.Describe(state);
        Assert.False(string.IsNullOrWhiteSpace(text));
    }

    [Fact]
    public void ServiceName_MatchesWatchdogProjectAndWixAuthoring()
    {
        // Pinned: the watchdog exe's sc.exe install path, the MSI ServiceInstall element,
        // and this query service must all agree on one service identity.
        Assert.Equal("NVMeDriverPatcherWatchdog", WatchdogServiceStateService.ServiceName);
    }

    [Fact]
    public void Query_NeverThrows_AndReturnsDefinedState()
    {
        // Environment-agnostic: on dev machines the service is usually absent
        // (-> NotInstalled); on a machine with the service installed any other
        // defined state is acceptable. The contract under test is "no throw,
        // defined enum value" for non-admin callers.
        var state = WatchdogServiceStateService.Query();
        Assert.True(Enum.IsDefined(state));
    }
}
