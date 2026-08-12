using System.Diagnostics;

namespace NVMeDriverPatcher.Tests;

internal sealed record BoundedProcessResult(
    int ExitCode,
    string StdOut,
    string StdErr,
    bool TimedOut);

/// <summary>
/// Runs external tools without allowing a full stdout/stderr pipe or a wedged child to hang the
/// test process. Both streams drain concurrently; a timeout kills the entire child tree and does
/// not read ExitCode until the process is known to have exited.
/// </summary>
internal static class TestProcessRunner
{
    public static BoundedProcessResult Run(ProcessStartInfo startInfo, TimeSpan timeout) =>
        RunAsync(startInfo, timeout).GetAwaiter().GetResult();

    public static async Task<BoundedProcessResult> RunAsync(
        ProcessStartInfo startInfo,
        TimeSpan timeout,
        CancellationToken cancellationToken = default)
    {
        if (timeout <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(timeout));

        using var process = new Process { StartInfo = startInfo };
        process.Start();

        var stdoutTask = process.StandardOutput.ReadToEndAsync();
        var stderrTask = process.StandardError.ReadToEndAsync();
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutCts.CancelAfter(timeout);

        var timedOut = false;
        try
        {
            await process.WaitForExitAsync(timeoutCts.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (timeoutCts.IsCancellationRequested)
        {
            timedOut = !cancellationToken.IsCancellationRequested;
            try { process.Kill(entireProcessTree: true); } catch { }
        }

        if (!timedOut && !cancellationToken.IsCancellationRequested)
        {
            // WaitForExitAsync signals process exit; the asynchronous stream tasks can finish a
            // few instructions later as the redirected pipe handles close.
            await Task.WhenAll(stdoutTask, stderrTask).ConfigureAwait(false);
            return new(process.ExitCode, stdoutTask.Result, stderrTask.Result, TimedOut: false);
        }

        if (cancellationToken.IsCancellationRequested)
            throw new OperationCanceledException(cancellationToken);

        // A child that ignored Kill/close semantics must not make the test suite wait forever for
        // its redirected handles. Preserve output that is already complete and report an honest
        // sentinel exit code for the timed-out process.
        try { await Task.WhenAll(stdoutTask, stderrTask).WaitAsync(TimeSpan.FromSeconds(2)).ConfigureAwait(false); }
        catch { }

        return new(
            ExitCode: -1,
            StdOut: CompletedValue(stdoutTask),
            StdErr: CompletedValue(stderrTask),
            TimedOut: true);
    }

    private static string CompletedValue(Task<string> task) =>
        task.Status == TaskStatus.RanToCompletion ? task.Result : string.Empty;
}
