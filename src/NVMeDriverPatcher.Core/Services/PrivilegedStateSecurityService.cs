using System.Runtime.InteropServices;
using System.Security.AccessControl;
using System.Security.Principal;
using Microsoft.Win32.SafeHandles;
using NVMeDriverPatcher.Models;

namespace NVMeDriverPatcher.Services;

public enum StateDirectoryRole
{
    SharedRoot,
    Privileged,
    Watchdog
}

public sealed record StateDirectorySecurityResult(bool Success, string Directory, string Summary)
{
    public static StateDirectorySecurityResult Failed(string directory, string summary) =>
        new(false, directory, summary);
}

/// <summary>
/// Establishes and revalidates the trust boundary for files later consumed by elevated
/// mutation/recovery code. Runtime state always lives below ProgramData even in portable mode.
/// The shared root is read-only to standard users; rollback state is Administrators/SYSTEM-only;
/// LocalService can modify only the dedicated watchdog child.
/// </summary>
public static class PrivilegedStateSecurityService
{
    public const string WatchdogServiceSid =
        "S-1-5-80-153395662-1388266646-3167021078-3452987457-2818666036";

    private static readonly SecurityIdentifier AdministratorsSid =
        new(WellKnownSidType.BuiltinAdministratorsSid, null);
    private static readonly SecurityIdentifier SystemSid =
        new(WellKnownSidType.LocalSystemSid, null);
    private static readonly SecurityIdentifier UsersSid =
        new(WellKnownSidType.BuiltinUsersSid, null);
    private static readonly SecurityIdentifier LocalServiceSid =
        new(WellKnownSidType.LocalServiceSid, null);
    private static readonly SecurityIdentifier ServiceSid = new(WatchdogServiceSid);

    private static readonly string[] LegacyPrivilegedFiles =
    [
        MutationLedgerService.LedgerFileName,
        MutationLedgerService.LedgerFileName + ".bak",
        SafeBootStateService.JournalFileName,
        SafeBootStateService.JournalFileName + ".bak",
        WindowsBuildRulesService.BundledRulesFile
    ];

    // Windows maps generic rights to specific ones when a descriptor is applied to a real object,
    // but an ace can still reach us carrying raw GENERIC_WRITE/GENERIC_ALL (an SDDL string that was
    // never applied, a descriptor read back from a foreign writer). Those bits share nothing with
    // the specific masks below, so without them such an ace reads as "grants no write" and the
    // whole check silently waves it through. Same trap the PowerShell ACL probe already spells out.
    private const FileSystemRights GenericAllRight = (FileSystemRights)0x10000000;
    private const FileSystemRights GenericWriteRight = (FileSystemRights)0x40000000;

    // Spelled out as individual write bits, never as the composite FileSystemRights values.
    // Write (0x116), Modify (0x301BF) and FullControl (0x1F01FF) each fold in the STANDARD rights
    // READ_CONTROL (0x20000) and SYNCHRONIZE (0x100000) plus the read bits, so ANDing a plain
    // `ReadAndExecute, Synchronize` ace (0x1200A9) against them is non-zero — every read-only ace
    // read as write-capable. That made the shared root, whose whole point is Users:ReadAndExecute,
    // fail its own validation permanently: the tree was re-ACLed on every elevated call, and any
    // caller that could not re-ACL (the LocalService watchdog, the standard-user tray) saw its own
    // correct state as untrusted. Same composite-mask trap the PowerShell ACL probe hit in 2026-08.
    private const FileSystemRights WriteCapableRights =
        (FileSystemRights)0x0002 |      // WriteData / CreateFiles
        (FileSystemRights)0x0004 |      // AppendData / CreateDirectories
        (FileSystemRights)0x0010 |      // WriteExtendedAttributes
        (FileSystemRights)0x0040 |      // DeleteSubdirectoriesAndFiles
        (FileSystemRights)0x0100 |      // WriteAttributes
        (FileSystemRights)0x10000 |     // Delete
        (FileSystemRights)0x40000 |     // ChangePermissions (WRITE_DAC)
        (FileSystemRights)0x80000 |     // TakeOwnership (WRITE_OWNER)
        GenericAllRight |
        GenericWriteRight;

    public static StateDirectorySecurityResult EnsureForMutation(string workingDir)
    {
        string directory;
        try { directory = AppConfig.GetPrivilegedStateDirectory(workingDir); }
        catch (Exception ex)
        {
            return StateDirectorySecurityResult.Failed(
                workingDir, $"Privileged state path resolution failed: {ex.GetType().Name}: {ex.Message}");
        }

        if (!AppConfig.IsRuntimeWorkingDirectory(workingDir))
            return new(true, directory, "Explicit isolated state directory accepted.");

        var prepared = EnsureRuntimeTree();
        if (!prepared.Success)
            return prepared;
        return ValidateDirectory(directory, StateDirectoryRole.Privileged);
    }

    public static StateDirectorySecurityResult EnsureForWatchdog(string workingDir)
    {
        string directory;
        try { directory = AppConfig.GetWatchdogStateDirectory(workingDir); }
        catch (Exception ex)
        {
            return StateDirectorySecurityResult.Failed(
                workingDir, $"Watchdog state path resolution failed: {ex.GetType().Name}: {ex.Message}");
        }

        if (!AppConfig.IsRuntimeWorkingDirectory(workingDir))
        {
            Directory.CreateDirectory(directory);
            return new(true, directory, "Explicit isolated watchdog directory accepted.");
        }

        var prepared = EnsureRuntimeTree(StateDirectoryRole.Watchdog);
        if (!prepared.Success)
            return prepared;
        return ValidateDirectory(directory, StateDirectoryRole.Watchdog);
    }

    public static StateDirectorySecurityResult EnsureForUpdates()
    {
        string directory;
        try { directory = AppConfig.GetUpdateStagingDirectory(); }
        catch (Exception ex)
        {
            return StateDirectorySecurityResult.Failed(
                string.Empty, $"Update staging path resolution failed: {ex.GetType().Name}: {ex.Message}");
        }

        var prepared = EnsureRuntimeTree();
        if (!prepared.Success)
            return prepared;
        try
        {
            return PrepareChild(directory, StateDirectoryRole.Privileged);
        }
        catch (Exception ex)
        {
            return StateDirectorySecurityResult.Failed(
                directory, $"Could not establish protected update staging: {ex.GetType().Name}: {ex.Message}");
        }
    }

    public static StateDirectorySecurityResult EnsureRuntimeTree() =>
        EnsureRuntimeTree(StateDirectoryRole.Privileged);

    /// <summary>
    /// The directories a caller in <paramref name="callerRole"/> must prove trustworthy before it
    /// may use the runtime tree. The watchdog service runs as LocalService, which deliberately has
    /// NO ace on the privileged child — reading that child's DACL throws for it, so a watchdog
    /// caller must never be asked to validate a surface it is not entitled to read. Anything else
    /// turns the strictest part of the boundary into a self-inflicted denial of service.
    /// </summary>
    internal static IReadOnlyList<(string Path, StateDirectoryRole Role)> RequiredValidationScope(
        string root,
        StateDirectoryRole callerRole)
    {
        var privileged = Path.Combine(root, AppConfig.PrivilegedStateFolderName);
        var watchdog = Path.Combine(root, AppConfig.WatchdogStateFolderName);
        return callerRole == StateDirectoryRole.Watchdog
            ? [(root, StateDirectoryRole.SharedRoot), (watchdog, StateDirectoryRole.Watchdog)]
            : [(root, StateDirectoryRole.SharedRoot),
               (privileged, StateDirectoryRole.Privileged),
               (watchdog, StateDirectoryRole.Watchdog)];
    }

    internal static StateDirectorySecurityResult EnsureRuntimeTree(StateDirectoryRole callerRole)
    {
        var root = AppConfig.GetSharedWorkingDirPath();
        if (string.IsNullOrWhiteSpace(root))
            return StateDirectorySecurityResult.Failed(string.Empty, "ProgramData state root is unavailable.");

        try
        {
            if (Directory.Exists(root) && HasReparsePoint(root))
                return StateDirectorySecurityResult.Failed(root, "ProgramData state root is a reparse point; refusing privileged state access.");

            var rootExisted = Directory.Exists(root);
            Directory.CreateDirectory(root);
            var existingPrivileged = Path.Combine(root, AppConfig.PrivilegedStateFolderName);
            var existingWatchdog = Path.Combine(root, AppConfig.WatchdogStateFolderName);
            var scope = RequiredValidationScope(root, callerRole);
            if (scope.All(entry => TryValidateDirectory(entry.Path, entry.Role, out _)))
            {
                return new(
                    true,
                    callerRole == StateDirectoryRole.Watchdog ? existingWatchdog : existingPrivileged,
                    "Protected ProgramData state directories are ready and validated.");
            }

            // Repair rewrites owners and DACLs, which needs an elevated token. The LocalService
            // watchdog and the standard-user tray have neither WRITE_OWNER on the tree nor
            // SeRestorePrivilege (`sc privs` strips the service to SeChangeNotifyPrivilege), so the
            // attempt throws and the caller reads it as untrusted state. The honest answer is that
            // the tree still needs the installer or an elevated run.
            if (!IsProcessElevated())
            {
                return StateDirectorySecurityResult.Failed(
                    root,
                    "Protected ProgramData state directories are missing or untrusted, and this process is not " +
                    "elevated enough to establish them. Run the elevated app, or re-run the watchdog installer's " +
                    "/grant-runtime-access verb.");
            }

            var rootTrustedBefore = rootExisted && TryValidateDirectory(root, StateDirectoryRole.SharedRoot, out _);
            if (!rootTrustedBefore && LegacyPrivilegedFiles.Any(name => File.Exists(Path.Combine(root, name))))
            {
                return StateDirectorySecurityResult.Failed(
                    root,
                    "Untrusted legacy mutation/recovery state exists in the shared root. Remove or independently recover it before enabling the driver.");
            }

            ApplyDirectorySecurity(root, StateDirectoryRole.SharedRoot);
            ProtectExistingSharedFiles(root);

            var privileged = existingPrivileged;
            var watchdog = existingWatchdog;
            var privilegedResult = PrepareChild(privileged, StateDirectoryRole.Privileged);
            if (!privilegedResult.Success) return privilegedResult;
            var watchdogResult = PrepareChild(watchdog, StateDirectoryRole.Watchdog);
            if (!watchdogResult.Success) return watchdogResult;

            if (rootTrustedBefore)
            {
                MigrateTrustedFiles(root, privileged, LegacyPrivilegedFiles);
                MigrateTrustedFiles(root, watchdog, ["watchdog.json", "watchdog.json.bak"]);
            }

            return new(true, privileged, "Protected ProgramData state directories are ready and validated.");
        }
        catch (Exception ex)
        {
            return StateDirectorySecurityResult.Failed(
                root, $"Could not establish the protected ProgramData state boundary: {ex.GetType().Name}: {ex.Message}");
        }
    }

    public static StateDirectorySecurityResult ValidateCriticalFile(string path, StateDirectoryRole role)
    {
        try
        {
            if (!File.Exists(path))
                return StateDirectorySecurityResult.Failed(path, "Critical state file is missing.");
            if (HasReparsePoint(path))
                return StateDirectorySecurityResult.Failed(path, "Critical state file is a reparse point.");
            if (!TryGetHardLinkCount(path, out var links) || links != 1)
                return StateDirectorySecurityResult.Failed(path, "Critical state file has an unverifiable or non-single hard-link count.");

            // A watchdog state file published by the LocalService service cannot carry a
            // re-applied explicit DACL, so it inherits one from the watchdog child instead. That is
            // equivalent protection — the inherited aces ARE the parent's — but only while the
            // parent itself is trusted, so accept inheritance only after proving that.
            bool inheritedProtectionIsAcceptable =
                role == StateDirectoryRole.Watchdog && ParentDirectoryIsTrusted(path, role);

            var security = new FileInfo(path).GetAccessControl(AccessControlSections.Owner | AccessControlSections.Access);
            if (!DescriptorAllowsOnlyExpectedWriters(
                    security, role, requireProtectedAcl: !inheritedProtectionIsAcceptable))
                return StateDirectorySecurityResult.Failed(path, "Critical state file owner or DACL is not trusted.");
            return new(true, path, "Critical state file metadata is trusted.");
        }
        catch (Exception ex)
        {
            return StateDirectorySecurityResult.Failed(
                path, $"Critical state metadata could not be verified: {ex.GetType().Name}: {ex.Message}");
        }
    }

    /// <summary>
    /// Guarantees the role's protection on a published state file. Elevated callers re-apply the
    /// explicit DACL outright; the LocalService watchdog cannot (no WRITE_OWNER, no
    /// SeRestorePrivilege) and instead proves the file already carries the parent's protection.
    /// Both routes end at the same post-condition — no unexpected writer — so a service that only
    /// has the second route must not be treated as a failed publication.
    /// </summary>
    public static StateDirectorySecurityResult EnsureCriticalFileProtection(string path, StateDirectoryRole role)
    {
        var applied = ProtectCriticalFile(path, role);
        return applied.Success ? applied : ValidateCriticalFile(path, role);
    }

    private static bool ParentDirectoryIsTrusted(string path, StateDirectoryRole role)
    {
        var parent = Path.GetDirectoryName(path);
        return !string.IsNullOrWhiteSpace(parent) && TryValidateDirectory(parent, role, out _);
    }

    internal static bool IsProcessElevated()
    {
        try
        {
            using var identity = WindowsIdentity.GetCurrent();
            return new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);
        }
        catch
        {
            return false;
        }
    }

    public static StateDirectorySecurityResult ProtectCriticalFile(string path, StateDirectoryRole role)
    {
        try
        {
            if (!File.Exists(path))
                return StateDirectorySecurityResult.Failed(path, "Critical state file is missing after publication.");
            if (HasReparsePoint(path))
                return StateDirectorySecurityResult.Failed(path, "Refusing to protect a reparse-point state file.");
            if (!TryGetHardLinkCount(path, out var links) || links != 1)
                return StateDirectorySecurityResult.Failed(path, "Refusing a state file with an unverifiable or non-single hard-link count.");

            var security = (FileSecurity)BuildSecurity(role, isDirectory: false);
            new FileInfo(path).SetAccessControl(security);
            return ValidateCriticalFile(path, role);
        }
        catch (Exception ex)
        {
            return StateDirectorySecurityResult.Failed(
                path, $"Critical state file protection failed: {ex.GetType().Name}: {ex.Message}");
        }
    }

    internal static bool DescriptorAllowsOnlyExpectedWriters(
        FileSystemSecurity security,
        StateDirectoryRole role,
        bool requireProtectedAcl)
    {
        if (requireProtectedAcl && !security.AreAccessRulesProtected)
            return false;
        // Watchdog state is written by LocalService, so a file it creates is owned by LocalService
        // and nothing in the service token can hand ownership to Administrators. Ownership grants
        // WRITE_DAC, but the role's own aces already give that identity Modify on the file and its
        // parent, so accepting it as owner concedes nothing it could not already do.
        var allowedOwners = role switch
        {
            StateDirectoryRole.Watchdog => new[] { AdministratorsSid, SystemSid, LocalServiceSid, ServiceSid },
            _ => new[] { AdministratorsSid, SystemSid }
        };
        var owner = security.GetOwner(typeof(SecurityIdentifier)) as SecurityIdentifier;
        if (owner is null || !allowedOwners.Any(allowed => allowed.Equals(owner)))
            return false;

        var allowedWriters = role switch
        {
            StateDirectoryRole.Watchdog => new[] { AdministratorsSid, SystemSid, LocalServiceSid, ServiceSid },
            _ => new[] { AdministratorsSid, SystemSid }
        };

        foreach (FileSystemAccessRule rule in security.GetAccessRules(true, true, typeof(SecurityIdentifier)))
        {
            if (rule.AccessControlType != AccessControlType.Allow ||
                (rule.FileSystemRights & WriteCapableRights) == 0)
                continue;
            if (rule.IdentityReference is not SecurityIdentifier sid ||
                !allowedWriters.Any(allowed => allowed.Equals(sid)))
                return false;
        }
        return true;
    }

    internal static bool IsTrustedFileMetadata(FileAttributes attributes, uint hardLinkCount) =>
        (attributes & FileAttributes.ReparsePoint) == 0 && hardLinkCount == 1;

    private static StateDirectorySecurityResult PrepareChild(string path, StateDirectoryRole role)
    {
        if (Directory.Exists(path) && HasReparsePoint(path))
            return StateDirectorySecurityResult.Failed(path, "State child is a reparse point.");
        if (Directory.Exists(path) &&
            !TryValidateDirectory(path, role, out _) &&
            Directory.EnumerateFileSystemEntries(path).Any())
        {
            return StateDirectorySecurityResult.Failed(
                path, "A non-empty state child has an untrusted owner or DACL; refusing to adopt its contents.");
        }

        Directory.CreateDirectory(path);
        ApplyDirectorySecurity(path, role);
        return ValidateDirectory(path, role);
    }

    private static void ProtectExistingSharedFiles(string root)
    {
        foreach (var path in Directory.EnumerateFiles(root, "*", SearchOption.TopDirectoryOnly))
        {
            if (HasReparsePoint(path))
                throw new IOException($"Shared state file is a reparse point: {Path.GetFileName(path)}");
            if (!TryGetHardLinkCount(path, out var links) || links != 1)
                throw new IOException($"Shared state file has unsafe hard-link metadata: {Path.GetFileName(path)}");
            new FileInfo(path).SetAccessControl(
                (FileSecurity)BuildSecurity(StateDirectoryRole.SharedRoot, isDirectory: false));
        }
    }

    private static void MigrateTrustedFiles(string sourceDirectory, string targetDirectory, IEnumerable<string> names)
    {
        foreach (var name in names)
        {
            var source = Path.Combine(sourceDirectory, name);
            var target = Path.Combine(targetDirectory, name);
            if (!File.Exists(source) || File.Exists(target)) continue;
            File.Move(source, target, overwrite: false);
            var protectedFile = ProtectCriticalFile(
                target,
                targetDirectory.EndsWith(AppConfig.WatchdogStateFolderName, StringComparison.OrdinalIgnoreCase)
                    ? StateDirectoryRole.Watchdog
                    : StateDirectoryRole.Privileged);
            if (!protectedFile.Success)
                throw new IOException(protectedFile.Summary);
        }
    }

    private static void ApplyDirectorySecurity(string path, StateDirectoryRole role) =>
        new DirectoryInfo(path).SetAccessControl((DirectorySecurity)BuildSecurity(role, isDirectory: true));

    private static FileSystemSecurity BuildSecurity(StateDirectoryRole role, bool isDirectory)
    {
        FileSystemSecurity security = isDirectory ? new DirectorySecurity() : new FileSecurity();
        security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
        security.SetOwner(AdministratorsSid);

        var inheritance = isDirectory
            ? InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit
            : InheritanceFlags.None;
        AddRule(security, SystemSid, FileSystemRights.FullControl, inheritance);
        AddRule(security, AdministratorsSid, FileSystemRights.FullControl, inheritance);
        if (role == StateDirectoryRole.SharedRoot)
        {
            AddRule(security, UsersSid, FileSystemRights.ReadAndExecute, inheritance);
            AddRule(security, LocalServiceSid, FileSystemRights.ReadAndExecute, inheritance);
            AddRule(security, ServiceSid, FileSystemRights.ReadAndExecute, inheritance);
        }
        else if (role == StateDirectoryRole.Watchdog)
        {
            AddRule(security, LocalServiceSid, FileSystemRights.Modify, inheritance);
            AddRule(security, ServiceSid, FileSystemRights.Modify, inheritance);
            // The non-admin tray exists to display this verdict, so it has to be able to read it.
            // Read-only: standard users still cannot write watchdog state, which is what the
            // trust boundary actually protects.
            AddRule(security, UsersSid, FileSystemRights.ReadAndExecute, inheritance);
        }
        return security;
    }

    private static void AddRule(
        FileSystemSecurity security,
        SecurityIdentifier sid,
        FileSystemRights rights,
        InheritanceFlags inheritance) =>
        security.AddAccessRule(new FileSystemAccessRule(
            sid, rights, inheritance, PropagationFlags.None, AccessControlType.Allow));

    private static StateDirectorySecurityResult ValidateDirectory(string path, StateDirectoryRole role) =>
        TryValidateDirectory(path, role, out var summary)
            ? new(true, path, summary)
            : StateDirectorySecurityResult.Failed(path, summary);

    private static bool TryValidateDirectory(string path, StateDirectoryRole role, out string summary)
    {
        try
        {
            if (!Directory.Exists(path))
            {
                summary = "State directory is missing.";
                return false;
            }
            if (HasReparsePoint(path))
            {
                summary = "State directory is a reparse point.";
                return false;
            }
            var security = new DirectoryInfo(path).GetAccessControl(AccessControlSections.Owner | AccessControlSections.Access);
            if (!DescriptorAllowsOnlyExpectedWriters(security, role, requireProtectedAcl: true))
            {
                summary = "State directory owner or DACL permits an unexpected writer.";
                return false;
            }
            summary = "State directory owner, protected DACL, and reparse metadata are trusted.";
            return true;
        }
        catch (Exception ex)
        {
            summary = $"State directory metadata is unavailable: {ex.GetType().Name}: {ex.Message}";
            return false;
        }
    }

    private static bool HasReparsePoint(string path) =>
        (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0;

    private static bool TryGetHardLinkCount(string path, out uint count)
    {
        count = 0;
        try
        {
            using SafeFileHandle handle = File.OpenHandle(
                path, FileMode.Open, FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete, FileOptions.None);
            if (!GetFileInformationByHandle(handle, out var info))
                return false;
            count = info.NumberOfLinks;
            return true;
        }
        catch
        {
            return false;
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ByHandleFileInformation
    {
        public uint FileAttributes;
        public System.Runtime.InteropServices.ComTypes.FILETIME CreationTime;
        public System.Runtime.InteropServices.ComTypes.FILETIME LastAccessTime;
        public System.Runtime.InteropServices.ComTypes.FILETIME LastWriteTime;
        public uint VolumeSerialNumber;
        public uint FileSizeHigh;
        public uint FileSizeLow;
        public uint NumberOfLinks;
        public uint FileIndexHigh;
        public uint FileIndexLow;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetFileInformationByHandle(
        SafeFileHandle file,
        out ByHandleFileInformation fileInformation);
}
