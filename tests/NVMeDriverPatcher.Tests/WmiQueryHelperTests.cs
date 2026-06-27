using NVMeDriverPatcher.Services;

namespace NVMeDriverPatcher.Tests;

public sealed class WmiQueryHelperTests
{
    [Fact]
    public void CreateEnumerationOptions_UsesDefaultTimeoutAndImmediateReturn()
    {
        var options = WmiQueryHelper.CreateEnumerationOptions();

        Assert.Equal(WmiQueryHelper.DefaultTimeout, options.Timeout);
        Assert.True(options.ReturnImmediately);
    }

    [Fact]
    public void CreateEnumerationOptions_UsesCustomTimeout()
    {
        var timeout = TimeSpan.FromSeconds(7);

        var options = WmiQueryHelper.CreateEnumerationOptions(timeout);

        Assert.Equal(timeout, options.Timeout);
        Assert.True(options.ReturnImmediately);
    }
}
