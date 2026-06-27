using System.Diagnostics.Eventing.Reader;
using System.Security.AccessControl;
using System.Security.Principal;

namespace NVMeDriverPatcher.Services;

public sealed class EventLogChannelAclResult
{
    public bool Success { get; set; }
    public bool Changed { get; set; }
    public string Summary { get; set; } = string.Empty;
    public string? Error { get; set; }
}

public static class EventLogChannelAclService
{
    public const string SystemChannelName = "System";
    internal const int EventLogReadAccess = 0x1;
    private static readonly SecurityIdentifier LocalServiceSid = new(WellKnownSidType.LocalServiceSid, null);

    public static EventLogChannelAclResult EnsureSystemLogLocalServiceReadAccess()
    {
        try
        {
            using var config = new EventLogConfiguration(SystemChannelName);
            var current = config.SecurityDescriptor;
            var updated = EnsureLocalServiceReadAce(current, out var changed);

            if (changed)
            {
                config.SecurityDescriptor = updated;
                config.SaveChanges();
            }

            return new EventLogChannelAclResult
            {
                Success = true,
                Changed = changed,
                Summary = changed
                    ? "Granted LocalService read access to the System event log."
                    : "LocalService already has System event log read access."
            };
        }
        catch (Exception ex)
        {
            return new EventLogChannelAclResult
            {
                Success = false,
                Error = $"{ex.GetType().Name}: {ex.Message}",
                Summary = "Could not grant LocalService read access to the System event log."
            };
        }
    }

    internal static string EnsureLocalServiceReadAce(string sddl, out bool changed)
    {
        if (string.IsNullOrWhiteSpace(sddl))
            throw new ArgumentException("Event log channel SDDL is empty.", nameof(sddl));

        var descriptor = new CommonSecurityDescriptor(isContainer: false, isDS: false, sddl);
        if (descriptor.DiscretionaryAcl is null)
            throw new InvalidOperationException("Event log channel SDDL has no DACL.");

        if (HasLocalServiceReadAce(descriptor))
        {
            changed = false;
            return descriptor.GetSddlForm(AccessControlSections.All);
        }

        descriptor.DiscretionaryAcl.AddAccess(
            AccessControlType.Allow,
            LocalServiceSid,
            EventLogReadAccess,
            InheritanceFlags.None,
            PropagationFlags.None);

        changed = true;
        return descriptor.GetSddlForm(AccessControlSections.All);
    }

    internal static bool HasLocalServiceReadAce(string sddl)
    {
        if (string.IsNullOrWhiteSpace(sddl))
            return false;

        try
        {
            return HasLocalServiceReadAce(new CommonSecurityDescriptor(isContainer: false, isDS: false, sddl));
        }
        catch
        {
            return false;
        }
    }

    private static bool HasLocalServiceReadAce(CommonSecurityDescriptor descriptor)
    {
        if (descriptor.DiscretionaryAcl is null)
            return false;

        foreach (GenericAce ace in descriptor.DiscretionaryAcl)
        {
            if (ace is QualifiedAce qualified &&
                qualified.AceQualifier == AceQualifier.AccessAllowed &&
                qualified.SecurityIdentifier.Equals(LocalServiceSid) &&
                (qualified.AccessMask & EventLogReadAccess) == EventLogReadAccess)
            {
                return true;
            }
        }

        return false;
    }
}
