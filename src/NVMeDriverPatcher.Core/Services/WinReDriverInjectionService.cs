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
    /// <summary>The image already carried this stornvme version or newer, so no copy was added.
    /// Nothing was backed up or changed either, unless extra copies had to be removed.</summary>
    public bool AlreadyCurrent { get; set; }
    /// <summary>The new copy went in and was committed, but the older copies couldn't be removed.</summary>
    public bool RemovalFailed { get; set; }
    public List<string> Log { get; } = [];
}

/// <summary>One WinRE image backup this tool made, named <c>&lt;image&gt;.&lt;yyyyMMdd-HHmmss&gt;.bak</c>.</summary>
internal sealed record WinReBackup(string Path, string ImageName, DateTime TakenUtc, long Bytes);

/// <summary>One out-of-box driver package in a mounted image, as <c>dism /Get-Drivers</c> lists it.</summary>
internal sealed record ImageDriverPackage(string PublishedName, string OriginalFileName, Version? Version);

/// <summary>
/// What the image's own stornvme copies say about this run. <see cref="AlreadyCurrent"/> means no
/// copy is added. <see cref="Superseded"/> lists the copies to remove so the image keeps one, which
/// can be non-empty even when the image is current (an older tool stacked duplicates).
/// <see cref="Unreadable"/> copies have no version this tool can compare and are never removed.
/// </summary>
internal sealed record StornvmeImageCheck(bool AlreadyCurrent, IReadOnlyList<ImageDriverPackage> Superseded, string Detail)
{
    public IReadOnlyList<ImageDriverPackage> Unreadable { get; init; } = [];
}

/// <summary>Runs one DISM command and returns its standard output; throws on a nonzero exit.</summary>
internal delegate Task<string> DismCommandRunner(
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

    private static readonly Regex RxOemInf = new(@"^oem\d+\.inf$", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    /// <summary>
    /// Reads <c>dism /Get-Drivers /English</c> list output: a block per package of
    /// <c>Key : Value</c> lines, each block starting at its Published Name. The tool and image
    /// version lines above the first block are skipped.
    /// </summary>
    internal static IReadOnlyList<ImageDriverPackage> ParseDriverList(string output)
    {
        var packages = new List<ImageDriverPackage>();
        string? published = null;
        string original = string.Empty;
        Version? version = null;

        void Flush()
        {
            if (published is not null) packages.Add(new ImageDriverPackage(published, original, version));
            published = null;
            original = string.Empty;
            version = null;
        }

        foreach (var line in (output ?? string.Empty).Split('\n'))
        {
            var colon = line.IndexOf(':');
            if (colon < 0) continue;
            var key = line[..colon].Trim();
            var value = line[(colon + 1)..].Trim();
            if (key.Equals("Published Name", StringComparison.OrdinalIgnoreCase))
            {
                Flush();
                published = value;
            }
            else if (published is null)
                continue;
            else if (key.Equals("Original File Name", StringComparison.OrdinalIgnoreCase))
                original = value;
            else if (key.Equals("Version", StringComparison.OrdinalIgnoreCase))
                version = Version.TryParse(value, out var parsed) ? parsed : null;
        }
        Flush();
        return packages;
    }

    /// <summary>
    /// Pure: compares the image's injected copies of the staged INF with the staged package's
    /// DriverVer. A copy at that version or newer means the image is current and nothing is added.
    /// Either way the image should end with one copy: when it's current, every other versioned copy
    /// (older ones and same-version duplicates) is superseded by the newest; when it isn't, every
    /// versioned copy is superseded by the staged one. A copy whose version didn't parse is left in
    /// place and reported, because it could be a newer package this tool can't judge.
    /// </summary>
    internal static StornvmeImageCheck CheckImageCopies(
        IReadOnlyList<ImageDriverPackage> imageDrivers, string driverInfPath, Version? stagedVersion)
    {
        var infName = Path.GetFileName(driverInfPath);
        var copies = imageDrivers
            .Where(p => string.Equals(p.OriginalFileName, infName, StringComparison.OrdinalIgnoreCase)
                        && RxOemInf.IsMatch(p.PublishedName))
            .ToList();
        if (copies.Count == 0)
            return new StornvmeImageCheck(false, [], $"The WinRE image has no injected copy of {infName} yet.");

        static string Describe(ImageDriverPackage p) => $"{p.PublishedName} ({p.Version?.ToString() ?? "version unknown"})";
        static string Names(IEnumerable<ImageDriverPackage> packages) => string.Join(", ", packages.Select(Describe));
        static int OemNumber(ImageDriverPackage p) =>
            int.TryParse(p.PublishedName.AsSpan(3, p.PublishedName.Length - 7), out var n) ? n : -1;

        var unreadable = copies.Where(p => p.Version is null).ToList();
        var versioned = copies.Where(p => p.Version is not null).ToList();
        var unreadableNote = unreadable.Count == 0 ? "" :
            $" {Names(unreadable)} didn't report a version this tool can read, so it's left in place.";

        // The copy to keep: newest version, and between same-version duplicates the higher oem
        // number, which is the later staging. Lists stay in the image's own order.
        var newest = versioned
            .OrderByDescending(p => p.Version)
            .ThenByDescending(p => OemNumber(p))
            .FirstOrDefault();
        bool current = newest is not null && (stagedVersion is null || newest.Version >= stagedVersion);
        if (stagedVersion is null && newest is null)
        {
            return new StornvmeImageCheck(true, [],
                $"The WinRE image already carries {Names(copies)}, and the staged {infName} has no DriverVer to compare it with, so another copy isn't added.")
            { Unreadable = unreadable };
        }

        if (current)
        {
            var extras = versioned.Where(p => !ReferenceEquals(p, newest)).ToList();
            var compared = stagedVersion is null
                ? $"and the staged {infName} has no DriverVer to compare it with, so another copy isn't added"
                : $"the same as or newer than the staged {stagedVersion}";
            var extrasNote = extras.Count == 0 ? "" :
                $" The extra {Names(extras)} {(extras.Count == 1 ? "is" : "are")} removed so the image keeps one copy.";
            return new StornvmeImageCheck(true, extras,
                $"The WinRE image already carries {Describe(newest!)}, {compared}.{extrasNote}{unreadableNote}")
            { Unreadable = unreadable };
        }

        var older = versioned.Count == 0 ? "" : $" The older {Names(versioned)} {(versioned.Count == 1 ? "is" : "are")} removed after it goes in.";
        return new StornvmeImageCheck(false, versioned,
            $"Staged {stagedVersion} goes into the WinRE image.{older}{unreadableNote}")
        { Unreadable = unreadable };
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
        sb.AppendLine("  --apply first mounts the image read-only and lists its drivers. If it already carries this");
        sb.AppendLine("  stornvme version or newer, it doesn't add another copy. Older or duplicate injected copies");
        sb.AppendLine("  are removed (/Remove-Driver) in the same mount, so the image keeps one copy.");
        sb.AppendLine();
        var dism = plan.Steps.Count > 0 ? plan.Steps[0].Exe : SystemToolPathService.Resolve("dism.exe");
        sb.AppendLine("  0. By hand, check the image first (read-only, nothing changes):");
        sb.AppendLine($"     {dism} /Mount-Image /ImageFile:{plan.WinReImagePath} /Index:1 /MountDir:{plan.MountDir} /ReadOnly");
        sb.AppendLine($"     {dism} /Image:{plan.MountDir} /Get-Drivers /English");
        sb.AppendLine($"     {dism} /Unmount-Image /MountDir:{plan.MountDir} /Discard");
        sb.AppendLine($"     If it lists an oem<N>.inf whose original name is {Path.GetFileName(plan.DriverInfPath)} at this version or newer, skip step 2.");
        int i = 1;
        foreach (var step in plan.Steps)
        {
            sb.AppendLine($"  {i++}. {step.Description}");
            sb.AppendLine($"     {step.CommandLine}");
            if (i == 3)
            {
                sb.AppendLine("     Then remove every other stornvme oem<N>.inf the check listed, keeping only the newest:");
                sb.AppendLine($"     {dism} /Image:{plan.MountDir} /Remove-Driver /Driver:oem<N>.inf");
            }
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
            var dism = plan.Steps[0].Exe;

            // DISM stages the package as a new oem<N>.inf on every /Add-Driver, even when the image
            // already holds that exact version, so each run grew winre.wim by a few MB. A read-only
            // look at the image's own driver list decides first, before any backup is taken.
            Write("[INFO] Checking the stornvme copy already in the WinRE image (read-only mount)...");
            dismStarted = true;
            await runner(dism,
                new[] { "/Mount-Image", $"/ImageFile:{plan.WinReImagePath}", "/Index:1", $"/MountDir:{plan.MountDir}", "/ReadOnly" },
                300, cancellationToken).ConfigureAwait(false);
            mounted = true;
            var listing = await runner(dism, new[] { $"/Image:{plan.MountDir}", "/Get-Drivers", "/English" },
                300, cancellationToken).ConfigureAwait(false);
            await runner(dism, new[] { "/Unmount-Image", $"/MountDir:{plan.MountDir}", "/Discard" },
                180, cancellationToken).ConfigureAwait(false);
            mounted = false;

            var check = CheckImageCopies(ParseDriverList(listing), plan.DriverInfPath, ReadDriverVersion(plan.DriverInfPath));
            Write("[INFO] " + check.Detail);
            result.AlreadyCurrent = check.AlreadyCurrent;
            if (check.AlreadyCurrent && check.Superseded.Count == 0)
            {
                result.Success = true;
                result.Summary = "WinRE image is already current. Nothing was injected, backed up or committed.";
                Write("[OK] " + result.Summary);
                return result;
            }

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
            try
            {
                await PublishBackupAsync(partial, result.BackupPath, cancellationToken: cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // The verified copy stays under its partial name, where the next run sweeps it;
                // nothing has been mounted yet, so there's nothing to undo.
                result.BackupPath = null;
                result.Summary = $"WinRE backup was copied and verified but couldn't be renamed into place ({ex.Message}); injection aborted before mounting.";
                Write("[ERROR] " + result.Summary);
                return result;
            }

            // Each backup is a full copy of the image (0.5 to 1 GB). Older ones go only now that
            // the new one is verified, and whatever DISM does next.
            PruneBackups(backupDir, plan.WinReImagePath, result.BackupPath, Write);

            Write("[INFO] Mounting WinRE image...");
            dismStarted = true;
            await runner(plan.Steps[0].Exe, plan.Steps[0].Args, 300, cancellationToken).ConfigureAwait(false);
            mounted = true;

            if (!check.AlreadyCurrent)
            {
                Write("[INFO] Adding stornvme.inf to mounted WinRE image...");
                await runner(plan.Steps[1].Exe, plan.Steps[1].Args, 300, cancellationToken).ConfigureAwait(false);
            }

            var replaced = string.Join(", ", check.Superseded.Select(p => p.PublishedName));
            string? removalError = null;
            if (check.Superseded.Count > 0)
            {
                // A new package takes the next oem<N>.inf, so the older names still point at the old copies.
                Write($"[INFO] Removing the extra stornvme copies from the mounted image: {replaced}...");
                var removeArgs = new List<string> { $"/Image:{plan.MountDir}", "/Remove-Driver" };
                removeArgs.AddRange(check.Superseded.Select(p => $"/Driver:{p.PublishedName}"));
                try
                {
                    await runner(dism, removeArgs.ToArray(), 300, cancellationToken).ConfigureAwait(false);
                }
                catch (Exception ex) when (!check.AlreadyCurrent && ex is not OperationCanceledException)
                {
                    // The new copy is in. Discarding it over a leftover would leave WinRE without the
                    // current driver, so commit and name what's left. A remove-only run has nothing
                    // else to keep, so its failure still discards.
                    removalError = ex.Message;
                    result.RemovalFailed = true;
                    Write($"[WARN] Couldn't remove {replaced} ({ex.Message}). Committing the new copy anyway.");
                }
            }

            Write("[INFO] Committing and unmounting WinRE image...");
            await runner(plan.Steps[2].Exe, plan.Steps[2].Args, 300, cancellationToken).ConfigureAwait(false);
            mounted = false;

            result.FinalSha256 = await ComputeSha256Async(plan.WinReImagePath, cancellationToken).ConfigureAwait(false);
            Write($"[INFO] WinRE image SHA-256 after injection: {result.FinalSha256}");
            result.Success = true;
            const string bootCheck = "Boot into WinRE once and confirm the system volume is accessible.";
            result.Summary = check.AlreadyCurrent
                ? $"WinRE image already had a current stornvme.inf. Removed the extra {replaced} so it keeps one copy. {bootCheck}"
                : removalError is not null
                    ? $"WinRE image updated with stornvme.inf, but the older {replaced} couldn't be removed ({removalError}), so the image carries more than one copy. {bootCheck}"
                    : check.Superseded.Count > 0
                        ? $"WinRE image updated with stornvme.inf, replacing the older {replaced}. {bootCheck}"
                        : $"WinRE image updated with stornvme.inf. {bootCheck}";
            Write((removalError is null ? "[OK] " : "[WARN] ") + result.Summary);
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

    /// <summary>
    /// Renames the verified copy from its partial name to its backup name. The rename fails for a
    /// moment while a scanner (Defender reads every new file, and this one is 0.5 to 1 GB) or a
    /// backup agent still has the copy open, so it's retried for up to <paramref name="patience"/>
    /// (two seconds by default) before the failure is handed back to the caller.
    /// </summary>
    internal static async Task PublishBackupAsync(
        string partial,
        string backupPath,
        TimeSpan? patience = null,
        CancellationToken cancellationToken = default)
    {
        var deadline = Environment.TickCount64 + (long)(patience ?? TimeSpan.FromSeconds(2)).TotalMilliseconds;
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                File.Move(partial, backupPath);
                return;
            }
            catch (Exception ex) when (Environment.TickCount64 < deadline && ex is IOException or UnauthorizedAccessException)
            {
                await Task.Delay(Math.Min(25 * attempt, 250), cancellationToken).ConfigureAwait(false);
            }
        }
    }

    private const string BackupStampFormat = "yyyyMMdd-HHmmss";

    /// <summary>
    /// Pure: which of one image's backups stay. The oldest is the image from before this tool's
    /// first injection, the only way back to the recovery image Windows shipped; the newest of
    /// the earlier ones undoes the last injection. Anything between is a state nobody needs. The
    /// backup just made is kept out of that ordering and always stays, so a clock that went
    /// backwards can't make it the "oldest" and push the real original out. Paths come back in
    /// full form, the way <see cref="PruneBackups"/> looks them up.
    /// </summary>
    internal static HashSet<string> BackupsToKeep(IEnumerable<WinReBackup> backups, string? justMade = null)
    {
        var justMadeFull = justMade is null ? null : Path.GetFullPath(justMade);
        var ordered = backups
            .Where(backup => justMadeFull is null ||
                             !string.Equals(Path.GetFullPath(backup.Path), justMadeFull, StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(backup => backup.TakenUtc)
            .ThenByDescending(backup => backup.Path, StringComparer.OrdinalIgnoreCase)
            .ToList();
        var keep = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (justMadeFull is not null) keep.Add(justMadeFull);
        if (ordered.Count == 0) return keep;
        keep.Add(Path.GetFullPath(ordered[0].Path));
        keep.Add(Path.GetFullPath(ordered[^1].Path));
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
                log?.Invoke($"[INFO] Removed WinRE backup {old.Path} ({old.Bytes / 1024.0 / 1024.0:F0} MB). " +
                    (justMade is null
                        ? "The oldest (from before the first injection) and the newest stay."
                        : "The oldest (from before the first injection), the newest earlier one and the copy just made stay."));
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

    private static async Task<string> RunProcessAsync(
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
        return output[0];
    }
}
