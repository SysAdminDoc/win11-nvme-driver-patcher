using NVMeDriverPatcher.Services;

namespace NVMeDriverPatcher.Tests;

public sealed class AutoRevertServiceTests
{
    [Fact]
    public void IsFatal_TrueForUnrecoverableExceptions()
    {
        // These must propagate out of MaybeRun rather than be summarized as a benign abort —
        // the revert path is safety-critical and must fail loud, not continue in a corrupt state.
        Assert.True(AutoRevertService.IsFatal(new OutOfMemoryException()));
        Assert.True(AutoRevertService.IsFatal(new AccessViolationException()));
    }

    [Fact]
    public void IsFatal_FalseForOrdinaryExceptions()
    {
        // Ordinary failures stay caught + reported (the WMI/registry/process errors MaybeRun expects).
        Assert.False(AutoRevertService.IsFatal(new InvalidOperationException()));
        Assert.False(AutoRevertService.IsFatal(new UnauthorizedAccessException()));
        Assert.False(AutoRevertService.IsFatal(new IOException()));
        Assert.False(AutoRevertService.IsFatal(new Exception()));
    }
}
