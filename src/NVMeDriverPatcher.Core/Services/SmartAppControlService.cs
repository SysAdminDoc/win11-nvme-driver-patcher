using Microsoft.Win32;

namespace NVMeDriverPatcher.Services;

public enum SmartAppControlState
{
    /// <summary>The value is missing or unreadable (older Windows, or SAC was never set up).</summary>
    Unknown,
    Off,
    /// <summary>Enforcing: unsigned code with no cloud reputation is blocked, with no per-app exception.</summary>
    On,
    /// <summary>Watching, not blocking. Windows may switch it to On by itself.</summary>
    Evaluation
}

/// <summary>
/// Reads Smart App Control's state. The tool's release files aren't signed, so with SAC on,
/// Windows can block them (or a newer version the user downloads) and there's no exception to
/// add for one app. Read-only.
/// </summary>
public static class SmartAppControlService
{
    internal const string PolicySubKey = @"SYSTEM\CurrentControlSet\Control\CI\Policy";
    internal const string StateValueName = "VerifiedAndReputablePolicyState";

    public static SmartAppControlState Read()
    {
        try
        {
            using var hklm = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64);
            using var key = hklm.OpenSubKey(PolicySubKey);
            return Classify(key?.GetValue(StateValueName));
        }
        catch
        {
            return SmartAppControlState.Unknown;
        }
    }

    internal static SmartAppControlState Classify(object? raw) => raw switch
    {
        0 => SmartAppControlState.Off,
        1 => SmartAppControlState.On,
        2 => SmartAppControlState.Evaluation,
        _ => SmartAppControlState.Unknown
    };

    public static string Describe(SmartAppControlState state) => state switch
    {
        SmartAppControlState.On => "On. Windows blocks unsigned apps it has no reputation for, and this tool's files aren't signed.",
        SmartAppControlState.Evaluation => "Evaluation. Nothing is blocked yet, but Windows may turn it on by itself.",
        SmartAppControlState.Off => "Off.",
        _ => $"Unknown ({StateValueName} isn't set or couldn't be read)."
    };

    /// <summary>What to tell someone about to download a new version, or null when SAC can't block it.</summary>
    public static string? DownloadNote(SmartAppControlState state) => state switch
    {
        SmartAppControlState.On =>
            "Smart App Control is on, and this tool's release files aren't signed, so Windows may block the new version. " +
            "There's no per-app exception. See \"If Windows blocks it\" in the README for the way through.",
        SmartAppControlState.Evaluation =>
            "Smart App Control is in evaluation mode. If Windows switches it on, it may block this tool's unsigned files.",
        _ => null
    };
}
