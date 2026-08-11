using System.Security.AccessControl;
using System.Security.Principal;
using System.Threading;

namespace NVMeDriverPatcher.Services;

/// <summary>
/// Creates the machine-wide named mutexes this app shares across four processes running as four
/// different principals: the elevated GUI/CLI, the standard-user tray, the LocalService watchdog,
/// and SYSTEM scheduled tasks.
/// </summary>
/// <remarks>
/// A plain <c>new Mutex(false, name)</c> stamps the creating token's DEFAULT DACL on the kernel
/// object, and that DACL does not include the other principals. Whichever process got there first
/// therefore locked the others out: their constructor threw <see cref="UnauthorizedAccessException"/>
/// while any of the creator's handles stayed open — and the watchdog service holds its state mutex
/// across an entire event-log scan. The watchdog paths degraded to Unavailable, but
/// <c>ConfigService.Load</c> surfaced it as a hard failure, so a perfectly valid elevated
/// <c>apply</c> or <c>status</c> could exit 99 "Unhandled error" because a background service
/// happened to be mid-scan.
/// </remarks>
internal static class CrossPrivilegeMutex
{
    /// <summary>
    /// Opens (or creates) <paramref name="name"/> with a descriptor every participating principal
    /// can synchronise on. Falls back to the plain constructor when the ACL'd create is refused,
    /// so a locked-down host degrades to the old behaviour instead of failing outright.
    /// </summary>
    public static Mutex Create(string name)
    {
        try
        {
            return MutexAcl.Create(initiallyOwned: false, name, out _, BuildSecurity());
        }
        catch (UnauthorizedAccessException)
        {
            // The object already exists with a descriptor we may not rewrite. Opening it is enough
            // to take the lock, which is all we actually need.
            try { return Mutex.OpenExisting(name); }
            catch { return new Mutex(initiallyOwned: false, name); }
        }
        catch (NotSupportedException)
        {
            return new Mutex(initiallyOwned: false, name);
        }
    }

    private static MutexSecurity BuildSecurity()
    {
        var security = new MutexSecurity();
        // Synchronize|Modify is exactly "may take and release this lock" — no right to change the
        // object's own DACL, so widening who can queue for the lock grants nothing else.
        const MutexRights rights = MutexRights.Synchronize | MutexRights.Modify;

        foreach (var sid in new[]
                 {
                     new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null),
                     new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null),
                     new SecurityIdentifier(WellKnownSidType.LocalServiceSid, null),
                     new SecurityIdentifier(WellKnownSidType.AuthenticatedUserSid, null),
                     new SecurityIdentifier(PrivilegedStateSecurityService.WatchdogServiceSid)
                 })
        {
            security.AddAccessRule(new MutexAccessRule(sid, rights, AccessControlType.Allow));
        }
        return security;
    }
}
