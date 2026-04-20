using NVMeDriverPatcher.Services;

namespace NVMeDriverPatcher.Tests;

// WinReBcdPrepService.Probe shells out to reagentc/bcdedit — can't be fully tested without
// root. What we CAN pin is the Summary ladder and the shape of the returned object for
// synthetic inputs. This test file is intentionally small: asserts the service emits
// stable English strings + always returns a non-null WinReProvisionInfo.
public sealed class WinReBcdPrepServiceTests
{
    [Fact]
    public void Probe_AlwaysReturnsNonNullReport()
    {
        // Even when reagentc isn't available (test runner may be non-admin), the service
        // must return a populated report — never throw, never null.
        var info = WinReBcdPrepService.Probe();
        Assert.NotNull(info);
        Assert.False(string.IsNullOrWhiteSpace(info.Summary));
    }
}
