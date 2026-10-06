using System.Globalization;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace NVMeDriverPatcher.Services;

public sealed class DismStep
{
    public string Description { get; set; } = string.Empty;
    // Resolved to System32 rather than left bare: WinRE injection runs elevated, so a
    // PATH- or CWD-planted dism.exe would inherit that token.
    public string Exe { get; set; } = SystemToolPathService.Resolve("dism.exe");
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

/// <summary>One WinRE image backup this tool made, named <c>&lt;image&gt;.&lt;yyyyMMdd-HHmmss&gt;.bak</c>.</summary>
internal sealed record WinReBackup(string Path, string ImageName, DateTime TakenUtc, long Bytes);

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
    /// <summary>
    /// The stornvme package DISM can inject: the Driver Store copy, where the INF sits next to its
    /// stornvme.sys. The INF under %WINDIR%\INF is a copy on its own, and DISM fails on it with
    /// 0x80070002 looking for the .sys beside it. When no package is staged, the %WINDIR%\INF path
    /// comes back so the preview still names the file it was looking for.
    /// </summary>
    public static string DefaultStornvmeInf()
    {
        var windir = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
        return FindDriverStorePackage(Path.Combine(windir, "System32", "DriverStore", "FileRepository"), "stornvme.inf")
            ?? Path.Combine(windir, "INF", "stornvme.inf");
    }

    /// <summary>
    /// The INF of the newest <paramref name="infName"/> package under the Driver Store repository.
    /// A folder counts only when it holds the INF and a .sys; several can be staged after servicing,
    /// so the highest DriverVer wins.
    /// </summary>
    internal static string? FindDriverStorePackage(string repository, string infName)
    {
        if (!Directory.Exists(repository)) return null;
        string? best = null;
        Version? bestVersion = null;
        foreach (var dir in Directory.EnumerateDirectories(repository, Path.GetFileNameWithoutExtension(infName) + ".inf_*"))
        {
            var inf = Path.Combine(dir, infName);
            if (!File.Exists(inf) || !Directory.EnumerateFiles(dir, "*.sys").Any()) continue;
            var version = ReadDriverVersion(inf) ?? new Version(0, 0);
            if (best is null || version > bestVersion)
            {
                best = inf;
                bestVersion = version;
            }
        }
        return best;
    }

    private static readonly Regex RxDriverVer = new(
        @"^\s*DriverVer\s*=\s*[^,]*,\s*(\d+(?:\.\d+){1,3})", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    /// <summary>The version half of the INF's <c>DriverVer = mm/dd/yyyy,w.x.y.z</c> line.</summary>
    internal static Version? ReadDriverVersion(string infPath)
    {
        try
        {
            foreach (var line in File.ReadLines(infPath))
            {
                var match = RxDriverVer.Match(line);
                if (match.Success && Version.TryParse(match.Groups[1].Value, out var version)) return version;
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
        return null;
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
            plan.Warnings.Add($"WinRE image not found at '{winReImagePath}'. Run reagentc /info to locate it (it may need reagentc /enable first).");
        if (driverInfMissing)
            plan.Warnings.Add($"Driver package not found at '{driverInfPath}'. DISM needs the Driver Store copy of stornvme.inf, the one next to stornvme.sys under System32\\DriverStore\\FileRepository.");

        plan.Warnings.Add("BLAST RADIUS: this mutates the recovery boot image. Back up the WinRE .wim first (copy it elsewhere).");
        plan.Warnings.Add("If a mount is interrupted, run 'Dism /Cleanup-Mountpoints' before retrying.");
        plan.Warnings.Add("After committing, boot into WinRE once and confirm the system volume is accessible BEFORE relying on it for recovery.");
        return plan;
    }

    /// <summary>Pure: renders the plan for the CLI/GUI preview (dry-run output).</summary>
    public static string RenderPlan(WinReInjectionPlan plan)
    {
        var sb = new StringBuilder();
        sb.AppendLine("WinRE stornvme injection: PLANNED DISM operations (preview only, nothing mutated):");
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
            : "Plan is NOT runnable as-is. Resolve the warnings below first.");
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

            SweepPartialBackups(backupDir, Write);
            result.OriginalSha256 = await ComputeSha256Async(plan.WinReImagePath, cancellationToken).ConfigureAwait(false);
            result.BackupPath = BuildBackupPath(backupDir, plan.WinReImagePath, DateTimeOffset.UtcNow);
            Write($"[INFO] WinRE image SHA-256 before injection: {result.OriginalSha256}");
            Write($"[INFO] Backing up WinRE image to {result.BackupPath}");
            // The copy runs under a partial name until its checksum is verified, so a copy cut short
            // (console closed, power loss) never sits under a name the retention trusts.
            var partial = result.BackupPath + PartialSuffix;
            File.Copy(plan.WinReImagePath, partial, overwrite: false);
            // Winre.wim is Hidden and System, and the copy inherits both, which would hide the backup
            // from Explorer in the one folder the summary points people at.
            File.SetAttributes(partial, FileAttributes.Normal);
            result.BackupSha256 = await ComputeSha256Async(partial, cancellationToken).ConfigureAwait(false);
            Write($"[INFO] Backup SHA-256: {result.BackupSha256}");
            if (!string.Equals(result.OriginalSha256, result.BackupSha256, StringComparison.OrdinalIgnoreCase))
            {
                // A copy that doesn't match the image can't restore it.
                DiscardUnverifiedBackup(partial, Write);
                result.BackupPath = null;
                result.Summary = "WinRE backup checksum mismatch; injection aborted before mounting.";
                Write("[ERROR] " + result.Summary);
                return result;
            }
            File.Move(partial, result.BackupPath);

            // Each backup is a full copy of the image (0.5 to 1 GB). Older ones go only now that
            // the new one is verified, and whatever DISM does next.
            PruneBackups(backupDir, plan.WinReImagePath, result.BackupPath, Write);

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
                    await runner(SystemToolPathService.Resolve("dism.exe"),
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
                    await runner(SystemToolPathService.Resolve("dism.exe"),
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
        var stamp = timestampUtc.UtcDateTime.ToString(BackupStampFormat, System.Globalization.CultureInfo.InvariantCulture);
        return Path.Combine(backupDir, $"{name}.{stamp}.bak");
    }

    /// <summary>A copy still being made, or cut short. Never a backup until it's verified and renamed.</summary>
    internal const string PartialSuffix = ".partial";

    private const string BackupStampFormat = "yyyyMMdd-HHmmss";

    /// <summary>
    /// Pure: which of one image's backups stay. The oldest is the image from before this tool's
    /// first injection, the only way back to the recovery image Windows shipped; the newest undoes
    /// the last injection. Anything between is a state nobody needs. The backup just made always
    /// stays, even when a clock that went backwards makes it look like the oldest.
    /// </summary>
    internal static HashSet<string> BackupsToKeep(IEnumerable<WinReBackup> backups, string? justMade = null)
    {
        var ordered = backups
            .OrderByDescending(backup => backup.TakenUtc)
            .ThenByDescending(backup => backup.Path, StringComparer.OrdinalIgnoreCase)
            .ToList();
        var keep = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (ordered.Count == 0) return keep;
        keep.Add(ordered[0].Path);
        keep.Add(ordered[^1].Path);
        if (justMade is not null) keep.Add(Path.GetFullPath(justMade));
        return keep;
    }

    /// <summary>Pure: splits a name <see cref="BuildBackupPath"/> produced into its image name and time.</summary>
    internal static bool TryParseBackupName(string fileName, out string imageName, out DateTime takenUtc)
    {
        imageName = string.Empty;
        takenUtc = default;
        const string suffix = ".bak";
        if (string.IsNullOrEmpty(fileName) || !fileName.EndsWith(suffix, StringComparison.OrdinalIgnoreCase)) return false;

        var stem = fileName[..^suffix.Length];
        int stampLength = BackupStampFormat.Length;
        if (stem.Length <= stampLength + 1 || stem[^(stampLength + 1)] != '.') return false;
        if (!DateTime.TryParseExact(stem[^stampLength..], BackupStampFormat, CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out takenUtc))
            return false;

        imageName = stem[..^(stampLength + 1)];
        return true;
    }

    /// <summary>This tool's WinRE image backups in a directory, newest first. Other files are ignored.</summary>
    internal static IReadOnlyList<WinReBackup> ListBackups(string backupDir)
    {
        if (string.IsNullOrWhiteSpace(backupDir) || !Directory.Exists(backupDir)) return [];

        var found = new List<WinReBackup>();
        foreach (var path in Directory.EnumerateFiles(backupDir, "*.bak", SearchOption.TopDirectoryOnly))
        {
            if (!TryParseBackupName(Path.GetFileName(path), out var imageName, out var takenUtc)) continue;
            long bytes = 0;
            try { bytes = new FileInfo(path).Length; } catch { }
            found.Add(new WinReBackup(path, imageName, takenUtc, bytes));
        }
        return found
            .OrderByDescending(backup => backup.TakenUtc)
            .ThenByDescending(backup => backup.Path, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    /// <summary>
    /// Deletes this image's backups that <see cref="BackupsToKeep"/> doesn't keep. A file that can't
    /// be deleted is a warning; the injection carries on.
    /// </summary>
    internal static int PruneBackups(string backupDir, string imagePath, string? justMade, Action<string>? log = null)
    {
        var imageName = Path.GetFileName(imagePath);
        var mine = ListBackups(backupDir)
            .Where(backup => string.Equals(backup.ImageName, imageName, StringComparison.OrdinalIgnoreCase))
            .ToList();
        var kept = BackupsToKeep(mine, justMade);

        int removed = 0;
        foreach (var old in mine.Where(backup => !kept.Contains(Path.GetFullPath(backup.Path))))
        {
            try
            {
                File.Delete(old.Path);
                removed++;
                log?.Invoke($"[INFO] Removed WinRE backup {old.Path} ({old.Bytes / 1024.0 / 1024.0:F0} MB). The oldest (from before the first injection) and the newest stay.");
            }
            catch (Exception ex)
            {
                log?.Invoke($"[WARN] Couldn't remove older WinRE backup {old.Path}: {ex.Message}");
            }
        }
        return removed;
    }

    /// <summary>Removes copies an earlier run left half made. They never count as backups, but
    /// at 0.5 to 1 GB each they shouldn't sit there either.</summary>
    internal static int SweepPartialBackups(string backupDir, Action<string>? log = null)
    {
        if (string.IsNullOrWhiteSpace(backupDir) || !Directory.Exists(backupDir)) return 0;
        int removed = 0;
        foreach (var path in Directory.EnumerateFiles(backupDir, "*.bak" + PartialSuffix, SearchOption.TopDirectoryOnly))
        {
            try
            {
                File.Delete(path);
                removed++;
                log?.Invoke($"[INFO] Removed an unfinished WinRE backup left by an earlier run: {path}");
            }
            catch (Exception ex)
            {
                log?.Invoke($"[WARN] Couldn't remove the unfinished WinRE backup {path}: {ex.Message}");
            }
        }
        return removed;
    }

    /// <summary>Deletes a backup whose checksum didn't match the image. True when it's gone.</summary>
    internal static bool DiscardUnverifiedBackup(string? backupPath, Action<string>? log = null)
    {
        if (string.IsNullOrWhiteSpace(backupPath)) return false;
        try
        {
            File.Delete(backupPath);
            log?.Invoke($"[WARN] Deleted the backup that didn't match the image: {backupPath}");
            return true;
        }
        catch (Exception ex)
        {
            log?.Invoke($"[WARN] Couldn't delete the backup that didn't match the image ({backupPath}): {ex.Message}. Don't restore from it.");
            return false;
        }
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

        var output = await Task.WhenAll(stdout, stderr).ConfigureAwait(false);
        if (proc.ExitCode != 0)
        {
            throw new InvalidOperationException(
                $"{Path.GetFileName(file)} {string.Join(' ', args)} exit {proc.ExitCode}: {output[1].Trim()} {output[0].Trim()}".Trim());
        }
    }
}
