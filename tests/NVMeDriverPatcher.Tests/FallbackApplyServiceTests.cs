using NVMeDriverPatcher.Services;

namespace NVMeDriverPatcher.Tests;

public sealed class FallbackApplyServiceTests
{
    [Fact]
    public async Task ApplyAsync_NativeSuccessDoesNotInvokeViVeTool()
    {
        var nativeCalls = 0;
        var viveCalls = 0;

        var result = await FallbackApplyService.ApplyAsync(
            "C:\\temp\\nvme",
            _ => { },
            CancellationToken.None,
            ids =>
            {
                nativeCalls++;
                return new FeatureStoreWriteResult
                {
                    Success = true,
                    Summary = "native ok",
                    AppliedIds = ids.ToArray()
                };
            },
            (_, _, _) =>
            {
                viveCalls++;
                return Task.FromResult(new ViVeToolService.ViVeToolResult
                {
                    Success = true,
                    AppliedIDs = ["should-not-run"],
                    IntegritySignal = "weak"
                });
            });

        Assert.True(result.Success);
        Assert.Equal(FallbackApplyService.NativeMethod, result.Method);
        Assert.Equal("native", result.IntegritySignal);
        Assert.NotEmpty(result.AppliedIds);
        Assert.Equal(1, nativeCalls);
        Assert.Equal(0, viveCalls);
    }

    [Fact]
    public async Task ApplyAsync_NativeFailureInvokesViVeToolAndReportsIntegrity()
    {
        var logs = new List<string>();
        var viveCalls = 0;

        var result = await FallbackApplyService.ApplyAsync(
            "C:\\temp\\nvme",
            logs.Add,
            CancellationToken.None,
            _ => new FeatureStoreWriteResult
            {
                Success = false,
                Summary = "native verification failed"
            },
            (workingDir, log, ct) =>
            {
                viveCalls++;
                Assert.Equal("C:\\temp\\nvme", workingDir);
                Assert.False(ct.IsCancellationRequested);
                log?.Invoke("vive fallback invoked");
                return Task.FromResult(new ViVeToolService.ViVeToolResult
                {
                    Success = true,
                    Message = "vive ok",
                    AppliedIDs = ["60786016", "48433719"],
                    IntegritySignal = "sha256"
                });
            });

        Assert.True(result.Success);
        Assert.Equal(FallbackApplyService.ViVeToolMethod, result.Method);
        Assert.Equal("sha256", result.IntegritySignal);
        Assert.Equal(["60786016", "48433719"], result.AppliedIds);
        Assert.Equal(1, viveCalls);
        Assert.Contains(logs, l => l.Contains("secondary fallback", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(logs, l => l.Contains("integrity signal: sha256", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task ApplyAsync_NativeExceptionStillOffersViVeToolFallback()
    {
        var result = await FallbackApplyService.ApplyAsync(
            "C:\\temp\\nvme",
            _ => { },
            CancellationToken.None,
            _ => throw new InvalidOperationException("rtl unavailable"),
            (_, _, _) => Task.FromResult(new ViVeToolService.ViVeToolResult
            {
                Success = true,
                Message = "vive ok",
                AppliedIDs = ["60786016"],
                IntegritySignal = "weak"
            }));

        Assert.True(result.Success);
        Assert.Equal(FallbackApplyService.ViVeToolMethod, result.Method);
        Assert.Equal("weak", result.IntegritySignal);
    }

    [Fact]
    public async Task ApplyAsync_WhenBothPathsFailReturnsCombinedFailure()
    {
        var result = await FallbackApplyService.ApplyAsync(
            "C:\\temp\\nvme",
            _ => { },
            CancellationToken.None,
            _ => new FeatureStoreWriteResult
            {
                Success = false,
                Summary = "native failed"
            },
            (_, _, _) => Task.FromResult(new ViVeToolService.ViVeToolResult
            {
                Success = false,
                Message = "vive failed",
                IntegritySignal = "weak"
            }));

        Assert.False(result.Success);
        Assert.Contains("native failed", result.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("vive failed", result.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal("weak", result.IntegritySignal);
    }
}
