using System.IO;
using System.Security.Cryptography;
using System.Text;

namespace NVMeDriverPatcher.Services;

public sealed class DismStep
{
    public string Description { get; set; } = string.Empty;
    public string Exe { get; set; } = "dism.exe";
    public string[] Args { get; set; } = Array.Empty<string>();

    public string CommandLine => Exe + " " + string.Join(" ", Args);
}

public sealed class WinReInjectionPlan
{
    public string WinReImagePath { get; set; } = string.Empty;
    public string MountDir { get; set; } = string.Empty;
    public string DriverInfPath { get; set; } = string.Empty;
    public List<DismStep> Steps { get; } = new();
    public List<string> Warnings { get; } = new();
    public bool IsExecutable { get; set; }
}

public sealed class WinReInjectionApplyResult
{
    public bool Success { get; set; }
    public string Summary { get; set; } = string.Empty;
    public string? BackupPath { get; set; }
    public string? OriginalSha256 { get; set; }
    public string? BackupSha256 { get; set; }
    public string? FinalSha256 { get; set; }
    public List<string> Log { get; } = [];
}

internal delegate Task DismCommandRunner(
    string exe,
    string[] args,
    int timeoutSeconds,
    CancellationToken cancellationToken);

// Plans, previews, and explicitly applies injecting the legacy stornvme.sys driver into WinRE's boot image so the
// recovery environment can always mount the system volume even if the native NVMe stack wedges
// startup. Preview remains the default; the destructive mount/commit path only runs when the CLI
// passes --apply, after a WinRE .wim backup and checksum logging.
public static class WinReDriverInjectionService
{
    public static string DefaultStornvmeInf()
    {
        var windir = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
        return Path.Combine(windir, "INF", "stornvme.inf");
    }

    public static string CreateDefaultMountDir(string workingDir) =>
        Path.Combine(workingDir, $"WinREMount-{Guid.NewGuid():N}");

    /// <summary>
    /// Pure: builds the ordered DISM plan (mount → add-driver → unmount/commit) plus blast-radius
    /// warnings. <paramref name="driverInfMissing"/>/<paramref name="imageMissing"/> let the caller
    /// pass probe results so the plan is marked non-executable with a clear reason rather than
    /// silently producing commands that would fail.
    /// </summary>
    public static WinReInjectionPlan BuildPlan(
        string winReImagePath, string mountDir, string driverInfPath,
        bool imageMissing = false, bool driverInfMissing = false)
    {
        var plan = new WinReInjectionPlan
        {
            WinReImagePath = winReImagePath,
            MountDir = mountDir,
            DriverInfPath = driverInfPath,
            IsExecutable = !imageMissing && !driverInfMissing
                           && !string.IsNullOrWhiteSpace(winReImagePath)
                           && !string.IsNullOrWhiteSpace(driverInfPath),
        };

        plan.Steps.Add(new DismStep
        {
            Description = "Mount the WinRE image read/write",
            Args = new[] { "/Mount-Image", $"/ImageFile:{winReImagePath}", "/Index:1", $"/MountDir:{mountDir}" },
        });
        plan.Steps.Add(new DismStep
        {
            Description = "Add the legacy stornvme driver to the mounted image",
            Args = new[] { $"/Image:{mountDir}", "/Add-Driver", $"/Driver:{driverInfPath}" },
        });
        plan.Steps.Add(new DismStep
        {
            Description = "Commit the change and unmount",
            Args = new[] { "/Unmount-Image", $"/MountDir:{mountDir}", "/Commit" },
        });

        if (imageMissing)
            plan.Warnings.Add($"WinRE image not found at '{winReImagePath}' — run reagentc /info to locate it (it may need reagentc /enable first).");
        if (driverInfMissing)
            plan.Warnings.Add($"Driver INF not found at '{driverInfPath}' — stornvme.inf ships in %WINDIR%\\INF on a healthy install.");

        plan.Warnings.Add("BLAST RADIUS: this mutates the recovery boot image. Back up the WinRE .wim first (copy it elsewhere).");
        plan.Warnings.Add("If a mount is interrupted, run 'Dism /Cleanup-Mountpoints' before retrying.");
        plan.Warnings.Add("After committing, boot into WinRE once and confirm the system volume is accessible BEFORE relying on it for recovery.");
        return plan;
    }

    /// <summary>Pure: renders the plan for the CLI/GUI preview (dry-run output).</summary>
    public static string RenderPlan(WinReInjectionPlan plan)
    {
        var sb = new StringBuilder();
        sb.AppendLine("WinRE stornvme injection — PLANNED DISM operations (preview only, nothing mutated):");
        sb.AppendLine($"  WinRE image : {plan.WinReImagePath}");
        sb.AppendLine($"  Driver INF  : {plan.DriverInfPath}");
        sb.AppendLine($"  Mount dir   : {plan.MountDir}");
        sb.AppendLine();
        int i = 1;
        foreach (var step in plan.Steps)
        {
            sb.AppendLine($"  {i++}. {step.Description}");
            sb.AppendLine($"     {step.CommandLine}");
        }
        sb.AppendLine();
        sb.AppendLine(plan.IsExecutable
            ? "Plan is runnable. Review the warnings, then run the commands above from an elevated prompt."
            : "Plan is NOT runnable as-is — resolve the warnings below first.");
        foreach (var w in plan.Warnings)
            sb.AppendLine($"  ! {w}");
        return sb.ToString().TrimEnd();
    }

    public static Task<WinReInjectionApplyResult> ApplyAsync(
        WinReInjectionPlan plan,
        string workingDir,
        Action<string>? log = null,
        CancellationToken cancellationToken = default) =>
        ApplyAsync(plan, workingDir, RunProcessAsync, log, cancellationToken);

    internal static async Task<WinReInjectionApplyResult> ApplyAsync(
        WinReInjectionPlan plan,
        string workingDir,
        DismCommandRunner runner,
        Action<string>? log = null,
        CancellationToken cancellationToken = default)
    {
        var result = new WinReInjectionApplyResult();
        bool mounted = false;
        bool dismStarted = false;

        void Write(string message)
        {
            result.Log.Add(message);
            log?.Invoke(message);
        }

        if (!plan.IsExecutable)
        {
            result.Summary = "WinRE injection plan is not executable. Resolve preview warnings first.";
            Write("[ERROR] " + result.Summary);
            return result;
        }

        if (!File.Exists(plan.WinReImagePath))
        {
            result.Summary = $"WinRE image not found at '{plan.WinReImagePath}'.";
            Write("[ERROR] " + result.Summary);
            return result;
        }

        if (!File.Exists(plan.DriverInfPath))
        {
            result.Summary = $"Driver INF not found at '{plan.DriverInfPath}'.";
            Write("[ERROR] " + result.Summary);
            return result;
        }

        try
        {
            Directory.CreateDirectory(workingDir);
            Directory.CreateDirectory(plan.MountDir);
            var backupDir = Path.Combine(workingDir, "backups");
            Directory.CreateDirectory(backupDir);

            result.OriginalSha256 = await ComputeSha256Async(plan.WinReImagePath, cancellationToken).ConfigureAwait(false);
            result.BackupPath = BuildBackupPath(backupDir, plan.WinReImagePath, DateTimeOffset.UtcNow);
            Write($"[INFO] WinRE image SHA-256 before injection: {result.OriginalSha256}");
            Write($"[INFO] Backing up WinRE image to {result.BackupPath}");
            File.Copy(plan.WinReImagePath, result.BackupPath, overwrite: false);
            result.BackupSha256 = await ComputeSha256Async(result.BackupPath, cancellationToken).ConfigureAwait(false);
            Write($"[INFO] Backup SHA-256: {result.BackupSha256}");
            if (!string.Equals(result.OriginalSha256, result.BackupSha256, StringComparison.OrdinalIgnoreCase))
            {
                result.Summary = "WinRE backup checksum mismatch; injection aborted before mounting.";
                Write("[ERROR] " + result.Summary);
                return result;
            }

            Write("[INFO] Mounting WinRE image...");
            dismStarted = true;
            await runner(plan.Steps[0].Exe, plan.Steps[0].Args, 300, cancellationToken).ConfigureAwait(false);
            mounted = true;

            Write("[INFO] Adding stornvme.inf to mounted WinRE image...");
            await runner(plan.Steps[1].Exe, plan.Steps[1].Args, 300, cancellationToken).ConfigureAwait(false);

            Write("[INFO] Committing and unmounting WinRE image...");
            await runner(plan.Steps[2].Exe, plan.Steps[2].Args, 300, cancellationToken).ConfigureAwait(false);
            mounted = false;

            result.FinalSha256 = await ComputeSha256Async(plan.WinReImagePath, cancellationToken).ConfigureAwait(false);
            Write($"[INFO] WinRE image SHA-256 after injection: {result.FinalSha256}");
            result.Success = true;
            result.Summary = "WinRE image updated with stornvme.inf. Boot into WinRE once and confirm the system volume is accessible.";
            Write("[OK] " + result.Summary);
        }
        catch (Exception ex)
        {
            result.Summary = $"WinRE injection failed: {ex.Message}";
            Write("[ERROR] " + result.Summary);

            if (mounted)
            {
                try
                {
                    Write("[WARN] Discarding mounted WinRE image changes...");
                    await runner("dism.exe",
                        new[] { "/Unmount-Image", $"/MountDir:{plan.MountDir}", "/Discard" },
                        180,
                        CancellationToken.None).ConfigureAwait(false);
                    mounted = false;
                    Write("[INFO] Mounted WinRE image discarded.");
                }
                catch (Exception discardEx)
                {
                    Write($"[WARN] DISM discard failed: {discardEx.Message}");
                }
            }

            if (dismStarted)
            {
                try
                {
                    Write("[WARN] Running DISM mountpoint cleanup...");
                    await runner("dism.exe",
                        new[] { "/Cleanup-Mountpoints" },
                        180,
                        CancellationToken.None).ConfigureAwait(false);
                    Write("[INFO] DISM mountpoint cleanup completed.");
                }
                catch (Exception cleanupEx)
                {
                    Write($"[WARN] DISM cleanup failed: {cleanupEx.Message}");
                }
            }
        }
        finally
        {
            try
            {
                if (!mounted && Directory.Exists(plan.MountDir))
                    Directory.Delete(plan.MountDir, recursive: true);
            }
            catch (Exception ex)
            {
                Write($"[WARN] Could not remove mount directory '{plan.MountDir}': {ex.Message}");
            }
        }

        return result;
    }

    internal static string BuildBackupPath(string backupDir, string imagePath, DateTimeOffset timestampUtc)
    {
        var name = Path.GetFileName(imagePath);
        var stamp = timestampUtc.UtcDateTime.ToString("yyyyMMdd-HHmmss", System.Globalization.CultureInfo.InvariantCulture);
        return Path.Combine(backupDir, $"{name}.{stamp}.bak");
    }

    internal static async Task<string> ComputeSha256Async(string path, CancellationToken cancellationToken = default)
    {
        await using var fs = new FileStream(
            path, FileMode.Open, FileAccess.Read, FileShare.Read, bufferSize: 81920, useAsync: true);
        using var sha = SHA256.Create();
        var hash = await sha.ComputeHashAsync(fs, cancellationToken).ConfigureAwait(false);
        return Convert.ToHexString(hash).ToLowerInvariant();
    }

    private static async Task RunProcessAsync(
        string file,
        string[] args,
        int timeoutSeconds,
        CancellationToken cancellationToken)
    {
        var psi = new System.Diagnostics.ProcessStartInfo(file)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        foreach (var a in args) psi.ArgumentList.Add(a);

        using var proc = System.Diagnostics.Process.Start(psi)
            ?? throw new InvalidOperationException($"{file} did not start.");
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        cts.CancelAfter(TimeSpan.FromSeconds(Math.Max(5, timeoutSeconds)));
        var stdout = proc.StandardOutput.ReadToEndAsync(cts.Token);
        var stderr = proc.StandardError.ReadToEndAsync(cts.Token);
        try
        {
            await proc.WaitForExitAsync(cts.Token).ConfigureAwait(false);
        }
        catch
        {
            try { proc.Kill(entireProcessTree: true); } catch { }
            throw;
        }

        if (proc.ExitCode != 0)
        {
            var err = await stderr.ConfigureAwait(false);
            var outp = await stdout.ConfigureAwait(false);
            throw new InvalidOperationException(
                $"{Path.GetFileName(file)} {string.Join(' ', args)} exit {proc.ExitCode}: {err.Trim()} {outp.Trim()}".Trim());
        }
    }
}
