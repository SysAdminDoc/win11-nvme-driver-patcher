using NVMeDriverPatcher.Models;
using NVMeDriverPatcher.Services;

namespace NVMeDriverPatcher.Tests;

public sealed class TuningServiceTests
{
    [Theory]
    [InlineData(TuningProfile.Key_QueueDepth)]
    [InlineData("ioqueuedepth")]
    [InlineData(TuningProfile.Key_MaxReadSplit)]
    [InlineData(TuningProfile.Key_MaxWriteSplit)]
    [InlineData(TuningProfile.Key_IoSubmissionQueueCount)]
    [InlineData(TuningProfile.Key_IdlePowerTimeout)]
    [InlineData(TuningProfile.Key_StandbyPowerTimeout)]
    public void IsKnownParameterName_AllowsSupportedStorNvmeValues(string name)
    {
        Assert.True(TuningService.IsKnownParameterName(name));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("ArbitraryValue")]
    [InlineData("IoQueueDepth ")]
    public void IsKnownParameterName_RejectsUnknownOrMalformedValues(string name)
    {
        Assert.False(TuningService.IsKnownParameterName(name));
    }

    [Fact]
    public void IsNativeNvmeBound_IsCallable_WithoutThrowing()
    {
        // Contract/smoke: the bound-driver probe must never throw (it fails open so a WMI hiccup
        // can't block legacy-stack tuning). The value is environment-dependent, so only assert it
        // returns without throwing.
        var ex = Record.Exception(() => TuningService.IsNativeNvmeBound());
        Assert.Null(ex);
    }
}
