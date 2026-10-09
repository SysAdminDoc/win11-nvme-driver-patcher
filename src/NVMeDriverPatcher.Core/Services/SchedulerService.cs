using System.Diagnostics;
using System.Security.AccessControl;
using System.Security.Principal;
using NVMeDriverPatcher.Models;

namespace NVMeDriverPatcher.Services;

/// <summary>Whether a program may be the target of a SYSTEM task, and in plain words why not.</summary>
public sealed record TaskTargetCheck(bool IsProtected, string Reason);

/// <summary>A path's owner and DACL, and whether the path is a junction or symbolic link.</summary>
internal sealed record PathSecurity(FileSystemSecurity Descriptor, bool IsReparsePoint = false);

/// <summary>Where a path sits relative to the protected root, which decides which rights matter.</summary>
internal enum TargetPathRole
{
    /// <summary>The exe itself.</summary>
    Program,
    /// <summary>A folder from the exe's own folder up to and including the protected root.</summary>
    Folder,
    /// <summary>A folder above the protected root.</summary>
    AboveRoot,
    /// <summary>The drive or share the path lives on.</summary>
    VolumeRoot
}

// Wraps schtasks.exe to register a boot-time verifier + periodic watchdog evaluator. Keeps
// verification + auto-revert decisions running even when the user never launches the app.
// Both tasks invoke the CLI binary; the GUI has no role.
public static class SchedulerService
{
    public const string BootTaskName = @"SysAdminDoc\NVMePatcher\BootVerify";
    public const string WatchdogTaskName = @"SysAdminDoc\NVMePatcher\WatchdogSweep";

    public static bool RegisterBootVerify(string cliPath, Action<string>? log = null) =>
        GuardTaskTarget(cliPath, log) && RunSchtasks(BuildBootVerifyArgs(cliPath), log);

    public static bool RegisterWatchdogSweep(string cliPath, int intervalMinutes, Action<string>? log = null) =>
        GuardTaskTarget(cliPath, log) && RunSchtasks(BuildWatchdogSweepArgs(cliPath, intervalMinutes), log);

    /// <summary>
    /// Both tasks run the CLI as SYSTEM, so whoever can replace the exe gets SYSTEM. Only a CLI
    /// under Program Files or in the folder the MSI installed to (it pins an admin-only DACL there,
    /// and records the folder under HKLM) may be the target, and only when the permissions on disk
    /// back that up. A copy in Downloads or on the desktop can be swapped by any program the user
    /// runs, and so can one in a Program Files folder another vendor left writable. A refusal names
    /// the file or folder that failed and why.
    /// </summary>
    public static TaskTargetCheck CheckTaskTarget(string? exePath) =>
        CheckTaskTarget(exePath, DefaultProtectedRoots(), ReadPathSecurity);

    private static string[] DefaultProtectedRoots() => new[]
    {
        Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
        Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
        ReadMsiInstallLocation() ?? string.Empty,
    };

    private static string? ReadMsiInstallLocation()
    {
        try
        {
            using var hklm = Microsoft.Win32.RegistryKey.OpenBaseKey(
                Microsoft.Win32.RegistryHive.LocalMachine, Microsoft.Win32.RegistryView.Registry64);
            using var key = hklm.OpenSubKey(@"Software\SysAdminDoc\NVMeDriverPatcher");
            return key?.GetValue("InstallLocation") as string;
        }
        catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException or IOException)
        {
            return null;
        }
    }

    /// <summary>
    /// The path half of the check: the protected root (full path, no trailing separator) that
    /// <paramref name="exePath"/> sits under, or null. A prefix alone proves nothing about the
    /// folders' permissions; <c>CheckTaskTarget</c> reads those.
    /// </summary>
    internal static string? MatchProtectedRoot(string? exePath, IEnumerable<string> protectedRoots)
    {
        if (string.IsNullOrWhiteSpace(exePath) || !Path.IsPathFullyQualified(exePath)) return null;
        string full;
        try
        {
            full = Path.GetFullPath(exePath);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return null;
        }
        foreach (var candidate in protectedRoots)
        {
            if (string.IsNullOrWhiteSpace(candidate) || !Path.IsPathFullyQualified(candidate)) continue;
            string root;
            try
            {
                root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(candidate));
            }
            catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
            {
                continue;
            }
            // An MSI installed straight to D:\ records InstallLocation D:\, which would vouch for
            // every program on the drive.
            if (IsVolumeRoot(root)) continue;
            if (full.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)) return root;
        }
        return null;
    }

    private static bool IsVolumeRoot(string fullPath) =>
        Path.GetPathRoot(fullPath) is { } volume &&
        string.Equals(volume.TrimEnd('\\', '/'), fullPath.TrimEnd('\\', '/'), StringComparison.OrdinalIgnoreCase);

    private const string TrustedInstallerSid = "S-1-5-80-956008885-3418522649-1831038044-1853292631-2271478464";

    private static readonly SecurityIdentifier[] TrustedSids =
    {
        new(WellKnownSidType.LocalSystemSid, null),
        new(WellKnownSidType.BuiltinAdministratorsSid, null),
        new(TrustedInstallerSid),
    };

    // Windows maps generic rights to specific ones when it applies a descriptor, but an entry can
    // still carry raw GENERIC_WRITE or GENERIC_ALL, and those share no bits with the specific masks.
    private const FileSystemRights GenericAllRight = (FileSystemRights)0x10000000;
    private const FileSystemRights GenericWriteRight = (FileSystemRights)0x40000000;

    // Individual bits, never the composite Write/Modify/FullControl values: those fold in
    // READ_CONTROL and SYNCHRONIZE, so a read-only Users entry (0x1200a9) would look like a writer.
    private const FileSystemRights ReplaceRights =
        FileSystemRights.WriteData |
        FileSystemRights.AppendData |
        FileSystemRights.DeleteSubdirectoriesAndFiles |
        FileSystemRights.Delete |
        FileSystemRights.ChangePermissions |
        FileSystemRights.TakeOwnership |
        GenericAllRight |
        GenericWriteRight;

    // Above the protected root the folders' contents don't matter, only the path through them.
    // Whoever can rename one (DELETE on it, or DELETE_CHILD on its parent) can move the real tree
    // aside and build their own at the same path, so those rights, and the rights to re-permission
    // the folder, are what's checked there. Creating folders, which every user can do in C:\, is fine.
    private const FileSystemRights RenameRights =
        FileSystemRights.Delete |
        FileSystemRights.DeleteSubdirectoriesAndFiles |
        FileSystemRights.ChangePermissions |
        FileSystemRights.TakeOwnership |
        GenericAllRight;

    /// <summary>
    /// Checks the exe and every folder from its own up to and including the protected root: each
    /// must be owned by SYSTEM, Administrators or TrustedInstaller, and grant no write, delete or
    /// re-permission right to anyone else. The folders above the root must not let anyone else
    /// rename them. Anything that can't be read is refused.
    /// </summary>
    internal static TaskTargetCheck CheckTaskTarget(
        string? exePath,
        IEnumerable<string> protectedRoots,
        Func<string, PathSecurity> readSecurity)
    {
        if (string.IsNullOrWhiteSpace(exePath))
            return new(false, "No program path was given.");
        var root = MatchProtectedRoot(exePath, protectedRoots);
        if (root is null)
            return new(false, $"{exePath} isn't under Program Files or the folder the MSI installed to.");

        var exe = Path.GetFullPath(exePath);
        var chain = new List<(string Path, TargetPathRole Role)> { (exe, TargetPathRole.Program) };
        var insideRoot = true;
        for (var dir = Path.GetDirectoryName(exe); dir is not null; dir = Path.GetDirectoryName(dir))
        {
            var role = insideRoot
                ? TargetPathRole.Folder
                : (Path.GetDirectoryName(dir) is null) ? TargetPathRole.VolumeRoot : TargetPathRole.AboveRoot;
            chain.Add((dir, role));
            if (string.Equals(dir, root, StringComparison.OrdinalIgnoreCase)) insideRoot = false;
        }

        foreach (var (path, role) in chain)
        {
            var weakness = Inspect(path, role, readSecurity);
            if (weakness is not null) return new(false, weakness);
        }
        return new(true, $"{exe} and the folders above it can only be changed by administrators.");
    }

    private static string? Inspect(string path, TargetPathRole role, Func<string, PathSecurity> readSecurity)
    {
        PathSecurity found;
        try
        {
            found = readSecurity(path);
        }
        catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException)
        {
            return $"{path} doesn't exist.";
        }
        catch (Exception ex)
        {
            // Fail closed: a descriptor that can't be read can't show who may write there.
            return $"Couldn't read the permissions on {path} ({ex.Message}), so it can't be trusted.";
        }
        return FindWeakness(path, found, role);
    }

    /// <summary>
    /// Pure: the reason <paramref name="path"/> can't be trusted, or null when its owner and every
    /// allow entry that applies to it belong to SYSTEM, Administrators or TrustedInstaller.
    /// </summary>
    internal static string? FindWeakness(string path, PathSecurity found, TargetPathRole role)
    {
        if (found.IsReparsePoint)
            return $"{path} is a junction or symbolic link, so the folder that really holds the program can't be vouched for.";

        var owner = found.Descriptor.GetOwner(typeof(SecurityIdentifier)) as SecurityIdentifier;
        if (owner is null || !IsTrusted(owner))
            return $"{path} is owned by {Describe(owner)}, and an owner can always change who may write there.";

        var watched = role switch
        {
            TargetPathRole.AboveRoot => RenameRights,
            // A drive's root can't be renamed or deleted, so DELETE on it means nothing.
            TargetPathRole.VolumeRoot => RenameRights & ~FileSystemRights.Delete,
            _ => ReplaceRights,
        };
        foreach (FileSystemAccessRule rule in found.Descriptor.GetAccessRules(true, true, typeof(SecurityIdentifier)))
        {
            // An inherit-only entry (CREATOR OWNER on Program Files, for one) only seeds new
            // children. It grants nothing on this object, and every child on the path is checked.
            if (rule.AccessControlType != AccessControlType.Allow ||
                (rule.PropagationFlags & PropagationFlags.InheritOnly) != 0 ||
                (rule.FileSystemRights & watched) == 0)
                continue;
            var sid = rule.IdentityReference as SecurityIdentifier;
            if (sid is not null && IsTrusted(sid)) continue;

            var who = Describe(sid);
            return role switch
            {
                TargetPathRole.Program => $"{who} can change or replace {path}.",
                TargetPathRole.Folder => $"{who} can add, change or delete files in {path}.",
                _ => $"{who} can rename or re-permission {path}, which would let them swap in a folder of their own.",
            };
        }
        return null;
    }

    private static bool IsTrusted(SecurityIdentifier sid) => TrustedSids.Any(trusted => trusted.Equals(sid));

    private static string Describe(SecurityIdentifier? sid)
    {
        if (sid is null) return "an account that couldn't be identified";
        try
        {
            return sid.Translate(typeof(NTAccount)).Value;
        }
        catch (SystemException)
        {
            // IdentityNotMappedException and friends: the raw SID still names the account.
            return sid.Value;
        }
    }

    /// <summary>Reads a file's or folder's owner and DACL from disk. Throws when it can't.</summary>
    internal static PathSecurity ReadPathSecurity(string path)
    {
        const AccessControlSections sections = AccessControlSections.Owner | AccessControlSections.Access;
        var attributes = File.GetAttributes(path);
        FileSystemSecurity descriptor = (attributes & FileAttributes.Directory) != 0
            ? new DirectoryInfo(path).GetAccessControl(sections)
            : new FileInfo(path).GetAccessControl(sections);
        return new PathSecurity(descriptor, (attributes & FileAttributes.ReparsePoint) != 0);
    }

    private static bool GuardTaskTarget(string cliPath, Action<string>? log)
    {
        var check = CheckTaskTarget(cliPath);
        if (check.IsProtected) return true;
        log?.Invoke($"[ERROR] Not registering a SYSTEM task for {cliPath}: {check.Reason}");
        return false;
    }

    public static bool Unregister(string taskName, Action<string>? log = null) =>
        RunSchtasks(BuildUnregisterArgs(taskName), log);

    // Pure schtasks.exe argument builders — extracted so the command shape (task name, action,
    // schedule, interval clamping) is unit-testable without spawning schtasks.

    // At login as SYSTEM, run `NVMeDriverPatcher.Cli watchdog --auto-revert` so the auto-revert
    // consumer runs even if the user never launches the GUI. /RL HIGHEST is required because the
    // CLI self-elevates via its manifest.
    internal static string[] BuildBootVerifyArgs(string cliPath) => new[]
    {
        "/Create", "/F", "/RU", "SYSTEM", "/RL", "HIGHEST",
        "/TN", BootTaskName,
        "/TR", $"\"{cliPath}\" watchdog --auto-revert",
        "/SC", "ONSTART"
    };

    internal static string[] BuildWatchdogSweepArgs(string cliPath, int intervalMinutes)
    {
        intervalMinutes = Math.Clamp(intervalMinutes, 5, 1440);
        string[] head =
        {
            "/Create", "/F", "/RU", "SYSTEM", "/RL", "HIGHEST",
            "/TN", WatchdogTaskName,
            "/TR", $"\"{cliPath}\" watchdog"
        };
        // schtasks /SC MINUTE only accepts /MO 1-1439, so the 24h ceiling has to be expressed as
        // a daily schedule; "/SC MINUTE /MO 1440" is rejected with "The /MO value is invalid".
        return intervalMinutes >= 1440
            ? [.. head, "/SC", "DAILY"]
            : [.. head, "/SC", "MINUTE", "/MO", intervalMinutes.ToString(System.Globalization.CultureInfo.InvariantCulture)];
    }

    internal static string[] BuildUnregisterArgs(string taskName) =>
        new[] { "/Delete", "/F", "/TN", taskName };

    internal static string[] BuildQueryXmlArgs(string taskName) =>
        new[] { "/Query", "/TN", taskName, "/XML" };

    /// <summary>
    /// Reads back the program each of this app's tasks runs and puts it through the same check
    /// register-tasks uses. Tasks registered before that check existed, or pointed somewhere else
    /// since, are never re-checked otherwise, and they still run as SYSTEM on every trigger.
    /// </summary>
    public static IReadOnlyList<ScheduledTaskTargetAudit> AuditRegisteredTaskTargets()
    {
        var audits = new List<ScheduledTaskTargetAudit>();
        foreach (var taskName in new[] { BootTaskName, WatchdogTaskName })
        {
            int? exitCode = null;
            string xml = string.Empty;
            try
            {
                var run = LaunchSchtasks(BuildQueryXmlArgs(taskName), 10_000);
                if (run.Outcome == SchtasksOutcome.Exited)
                {
                    exitCode = run.ExitCode;
                    xml = run.Stdout;
                }
            }
            catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException or IOException)
            {
                // Left as "couldn't ask"; EvaluateTaskQuery reports that rather than "not registered".
            }
            audits.Add(EvaluateTaskQuery(taskName, exitCode, xml, CheckTaskTarget));
        }
        return audits;
    }

    /// <summary>
    /// Turns one <c>schtasks /Query /TN name /XML</c> result into a verdict. <paramref name="exitCode"/>
    /// is null when schtasks didn't start or ran out of time.
    /// </summary>
    internal static ScheduledTaskTargetAudit EvaluateTaskQuery(
        string taskName,
        int? exitCode,
        string? taskXml,
        Func<string, TaskTargetCheck> check)
    {
        if (exitCode is null)
            return new(taskName, ScheduledTaskTargetState.Unreadable, null,
                $"Couldn't ask Task Scheduler about {taskName}, so the program it runs wasn't checked.");
        // schtasks has no locale-independent "no such task" code. Anything but success reads as
        // not registered, the same way IsRegistered treats it.
        if (exitCode != 0)
            return new(taskName, ScheduledTaskTargetState.NotRegistered, null, $"{taskName} isn't registered.");

        var commands = ParseTaskExecCommands(taskXml);
        if (commands is null || commands.Count == 0)
            return new(taskName, ScheduledTaskTargetState.Unreadable, null,
                $"Couldn't read which program {taskName} runs, so it wasn't checked. Run unregister-tasks, then register-tasks from the installed CLI.");

        // Task Scheduler expands %VARS% in the command before it runs it, so check what it runs.
        var targets = commands.Select(Environment.ExpandEnvironmentVariables).ToList();
        foreach (var target in targets)
        {
            if (IsGarbledPath(target))
                return new(taskName, ScheduledTaskTargetState.Unreadable, null,
                    $"Couldn't read which program {taskName} runs: its path has characters schtasks couldn't print in this console, so it wasn't checked. Open the task in Task Scheduler to see the program.");
            var verdict = check(target);
            if (!verdict.IsProtected)
                return new(taskName, ScheduledTaskTargetState.Unprotected, target,
                    $"{taskName} runs {target} as SYSTEM, and that program isn't protected. {verdict.Reason} Run unregister-tasks, then register-tasks from the installed CLI.");
        }
        return new(taskName, ScheduledTaskTargetState.Protected, targets[0],
            $"{taskName} runs {targets[0]}, which only administrators can change.");
    }

    /// <summary>
    /// schtasks prints through the console code page, so a character that page can't show comes
    /// back as '?' (or U+FFFD once .NET decodes it). '?' can't appear in a real path outside the
    /// \\?\ prefix, so such a path isn't the one the task runs, and checking it would warn about a
    /// file that isn't there.
    /// </summary>
    internal static bool IsGarbledPath(string path) =>
        path.Contains('\uFFFD') ||
        path.IndexOf('?', path.StartsWith(@"\\?\", StringComparison.Ordinal) ? 4 : 0) >= 0;

    private static readonly System.Xml.Linq.XNamespace TaskNamespace = "http://schemas.microsoft.com/windows/2004/02/mit/task";

    /// <summary>
    /// Pure: the program of every Exec action in a Task Scheduler XML definition, with the quotes
    /// schtasks keeps around a path that has spaces taken off. Null when the XML is empty or
    /// malformed, has no Actions, or has an Exec with no command.
    /// </summary>
    internal static IReadOnlyList<string>? ParseTaskExecCommands(string? taskXml)
    {
        if (string.IsNullOrWhiteSpace(taskXml)) return null;
        System.Xml.Linq.XDocument doc;
        try
        {
            // Trimmed because schtasks can lead with a blank line, and nothing may precede the
            // XML declaration.
            doc = System.Xml.Linq.XDocument.Parse(taskXml.Trim());
        }
        catch (System.Xml.XmlException)
        {
            return null;
        }

        var actions = doc.Descendants(TaskNamespace + "Actions").ToList();
        if (actions.Count == 0) return null;
        var commands = new List<string>();
        foreach (var exec in actions.SelectMany(a => a.Elements(TaskNamespace + "Exec")))
        {
            var command = exec.Element(TaskNamespace + "Command")?.Value.Trim().Trim('"').Trim();
            if (string.IsNullOrEmpty(command)) return null;
            commands.Add(command);
        }
        return commands;
    }

    public static bool IsRegistered(string taskName)
    {
        try
        {
            // schtasks /Query emits a formatted task summary that easily fills the pipe buffer when
            // the task name matches a localized Windows entry; LaunchSchtasks drains it.
            var run = LaunchSchtasks(new[] { "/Query", "/TN", taskName }, 10_000);
            return run.Outcome == SchtasksOutcome.Exited && run.ExitCode == 0;
        }
        catch { return false; }
    }

    private static bool RunSchtasks(string[] args, Action<string>? log)
    {
        try
        {
            var run = LaunchSchtasks(args, 30_000);
            if (run.Outcome == SchtasksOutcome.NotStarted)
            {
                log?.Invoke("[ERROR] schtasks.exe did not start.");
                return false;
            }
            if (run.Outcome == SchtasksOutcome.TimedOut)
            {
                log?.Invoke("[ERROR] schtasks.exe timed out.");
                return false;
            }
            if (run.ExitCode != 0)
            {
                log?.Invoke($"[ERROR] schtasks {args[0]} exit {run.ExitCode}: {run.Stderr.Trim()}");
                return false;
            }
            return true;
        }
        catch (Exception ex)
        {
            log?.Invoke($"[ERROR] schtasks: {ex.Message}");
            return false;
        }
    }

    private enum SchtasksOutcome { Exited, NotStarted, TimedOut }

    private readonly record struct SchtasksRun(SchtasksOutcome Outcome, int ExitCode, string Stdout, string Stderr);

    // Every schtasks call goes through here: the System32 copy, never a bare name, and both pipes
    // drained asynchronously before the wait, so a full buffer can't deadlock the child against us.
    private static SchtasksRun LaunchSchtasks(IEnumerable<string> args, int timeoutMs)
    {
        var psi = new ProcessStartInfo(SystemToolPathService.Resolve("schtasks.exe"))
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        foreach (var a in args) psi.ArgumentList.Add(a);
        using var proc = Process.Start(psi);
        if (proc is null) return new(SchtasksOutcome.NotStarted, -1, string.Empty, string.Empty);

        var stdoutTask = proc.StandardOutput.ReadToEndAsync();
        var stderrTask = proc.StandardError.ReadToEndAsync();
        if (!proc.WaitForExit(timeoutMs))
        {
            try { proc.Kill(true); } catch { }
            return new(SchtasksOutcome.TimedOut, -1, string.Empty, string.Empty);
        }
        string stdout = string.Empty, stderr = string.Empty;
        try { stdout = stdoutTask.GetAwaiter().GetResult(); } catch { }
        try { stderr = stderrTask.GetAwaiter().GetResult(); } catch { }
        return new(SchtasksOutcome.Exited, proc.ExitCode, stdout, stderr);
    }
}

/// <summary>What a registered task's program turned out to be.</summary>
public enum ScheduledTaskTargetState
{
    NotRegistered,
    /// <summary>Runs a program only administrators can change.</summary>
    Protected,
    /// <summary>Runs a program someone else could swap.</summary>
    Unprotected,
    /// <summary>Exists, or may, but its program couldn't be read.</summary>
    Unreadable
}

/// <summary>One of this app's scheduled tasks and whether the program it runs is safe to run as SYSTEM.</summary>
public sealed record ScheduledTaskTargetAudit(
    string TaskName,
    ScheduledTaskTargetState State,
    string? Target,
    string Detail)
{
    public bool NeedsAttention =>
        State is ScheduledTaskTargetState.Unprotected or ScheduledTaskTargetState.Unreadable;
}
