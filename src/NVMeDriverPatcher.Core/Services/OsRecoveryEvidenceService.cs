using System.Globalization;
using System.Management;
using Microsoft.Win32;
using NVMeDriverPatcher.Models;

namespace NVMeDriverPatcher.Services;

/// <summary>
/// Advisory evidence for Windows recovery features that complement, but do not replace, the
/// app's registry backup and offline Recovery Kit. A null state means Windows did not expose a
/// trustworthy setting to this process; it is never converted into a hard recovery failure.
/// </summary>
public sealed class OsRecoveryEvidence
{
    public bool PointInTimeRestoreSupported { get; init; }
    public bool? PointInTimeRestoreEnabled { get; init; }
    public bool RestorePointQuerySucceeded { get; init; }
    public DateTimeOffset? NewestRestorePointUtc { get; init; }
    public bool QuickMachineRecoverySupported { get; init; }
    public bool? QuickMachineRecoveryEnabled { get; init; }
    public bool? QuickMachineRecoveryAutoRemediationEnabled { get; init; }
    public bool QuickMachineRecoveryQuerySucceeded { get; init; }

    public string PointInTimeRestoreSummary
    {
        get
        {
            if (!PointInTimeRestoreSupported)
                return "Point-in-Time Restore is not exposed on this Windows build.";

            var state = PointInTimeRestoreEnabled switch
            {
                true => "enabled",
                false => "disabled by an explicit OS policy",
                _ => "available; current enablement is not directly exposed"
            };

            var point = !RestorePointQuerySucceeded
                ? "newest restore-point age unavailable (SystemRestore query failed)"
                : NewestRestorePointUtc is { } newest
                    ? $"newest restore point {FormatAge(newest)} ({newest:O})"
                    : "no restore point observed";
            return $"Point-in-Time Restore: {state}; {point}.";
        }
    }

    public string QuickMachineRecoverySummary
    {
        get
        {
            if (!QuickMachineRecoverySupported)
                return "Quick Machine Recovery is not exposed on this Windows build.";
            if (!QuickMachineRecoveryQuerySucceeded)
                return "Quick Machine Recovery state is not exposed by reagentc or current policy.";

            var state = QuickMachineRecoveryEnabled switch
            {
                true => "enabled",
                false => "disabled",
                _ => "state not reported"
            };
            var auto = QuickMachineRecoveryAutoRemediationEnabled switch
            {
                true => "auto-remediation enabled",
                false => "auto-remediation disabled",
                _ => "auto-remediation state not reported"
            };
            return $"Quick Machine Recovery: {state}; {auto}.";
        }
    }

    public string Summary => $"OS-native recovery advisory — {PointInTimeRestoreSummary} {QuickMachineRecoverySummary}";

    private static string FormatAge(DateTimeOffset timestamp)
    {
        var age = DateTimeOffset.UtcNow - timestamp.ToUniversalTime();
        if (age < TimeSpan.Zero) return "captured in the future";
        if (age.TotalDays >= 1) return $"{(int)age.TotalDays} day(s) ago";
        if (age.TotalHours >= 1) return $"{(int)age.TotalHours} hour(s) ago";
        return $"{Math.Max(0, (int)age.TotalMinutes)} minute(s) ago";
    }
}

public static class OsRecoveryEvidenceService
{
    // Microsoft documents PiTR/Recovery CSP exposure on 24H2 build 26100.8737 and later.
    public const int PointInTimeRestoreMinimumBuild = 26100;
    public const int PointInTimeRestoreMinimumUbr = 8737;

    // Microsoft documents QMR availability on 24H2 build 26100.4700 and later.
    public const int QuickMachineRecoveryMinimumBuild = 26100;
    public const int QuickMachineRecoveryMinimumUbr = 4700;

    private static readonly string[] PointInTimeRestorePolicySubkeys =
    [
        // Recovery CSP path used by current Windows 11 builds.
        @"SOFTWARE\Microsoft\PolicyManager\current\device\Recovery\PointInTimeRestore",
        // Older Insider CSP path retained for hosts that shipped the feature before it moved
        // under the Recovery node.
        @"SOFTWARE\Microsoft\PolicyManager\current\device\PointInTimeRestore",
        @"SOFTWARE\Microsoft\PolicyManager\default\device\Recovery\PointInTimeRestore",
        @"SOFTWARE\Microsoft\PolicyManager\default\device\PointInTimeRestore"
    ];

    public static OsRecoveryEvidence Probe(WindowsBuildDetails? build = null)
    {
        build ??= DriveService.GetWindowsBuildDetails();
        var pitRSupported = IsPointInTimeRestoreSupported(build);
        var qmrSupported = IsQuickMachineRecoverySupported(build);

        var restorePointQuery = pitRSupported
            ? QueryNewestRestorePoint()
            : (Succeeded: false, NewestUtc: (DateTimeOffset?)null);
        var qmr = qmrSupported
            ? WinReBcdPrepService.ProbeQuickMachineRecovery()
            : new QuickMachineRecoverySettings();

        return new OsRecoveryEvidence
        {
            PointInTimeRestoreSupported = pitRSupported,
            PointInTimeRestoreEnabled = pitRSupported ? ReadPointInTimeRestoreEnabled() : null,
            RestorePointQuerySucceeded = restorePointQuery.Succeeded,
            NewestRestorePointUtc = restorePointQuery.NewestUtc,
            QuickMachineRecoverySupported = qmrSupported,
            QuickMachineRecoveryEnabled = qmr.QuerySucceeded ? qmr.CloudRemediationEnabled : null,
            QuickMachineRecoveryAutoRemediationEnabled = qmr.QuerySucceeded ? qmr.AutoRemediationEnabled : null,
            QuickMachineRecoveryQuerySucceeded = qmr.QuerySucceeded
        };
    }

    public static bool IsPointInTimeRestoreSupported(WindowsBuildDetails? build)
    {
        return IsAtLeast(build, PointInTimeRestoreMinimumBuild, PointInTimeRestoreMinimumUbr);
    }

    public static bool IsQuickMachineRecoverySupported(WindowsBuildDetails? build)
    {
        return IsAtLeast(build, QuickMachineRecoveryMinimumBuild, QuickMachineRecoveryMinimumUbr);
    }

    internal static bool? ParsePolicyBoolean(object? value)
    {
        if (value is null) return null;
        if (value is bool boolean) return boolean;
        if (value is int integer && integer is 0 or 1) return integer == 1;
        if (value is long longValue && longValue is 0 or 1) return longValue == 1;
        if (value is uint unsigned && unsigned is 0 or 1) return unsigned == 1;
        if (value is string text && bool.TryParse(text.Trim(), out var parsedBoolean)) return parsedBoolean;
        if (value is string numeric && int.TryParse(numeric.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsedInt) && parsedInt is 0 or 1)
            return parsedInt == 1;
        return null;
    }

    internal static DateTimeOffset? ParseRestorePointCreationTime(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        try
        {
            var local = ManagementDateTimeConverter.ToDateTime(value.Trim());
            return new DateTimeOffset(local).ToUniversalTime();
        }
        catch { }

        return DateTimeOffset.TryParse(
            value,
            CultureInfo.InvariantCulture,
            DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal,
            out var parsed)
            ? parsed
            : null;
    }

    internal static (bool Succeeded, DateTimeOffset? NewestUtc) QueryNewestRestorePoint()
    {
        try
        {
            using var searcher = new ManagementObjectSearcher(
                @"root\default",
                "SELECT CreationTime FROM SystemRestore");
            using var collection = WmiQueryHelper.ExecuteWithTimeout(searcher);
            DateTimeOffset? newest = null;
            foreach (var raw in collection)
            {
                if (raw is not ManagementObject point) continue;
                using (point)
                {
                    var timestamp = ParseRestorePointCreationTime(point["CreationTime"]?.ToString());
                    if (timestamp is not null && (newest is null || timestamp > newest))
                        newest = timestamp;
                }
            }
            return (true, newest);
        }
        catch
        {
            return (false, null);
        }
    }

    private static bool? ReadPointInTimeRestoreEnabled()
    {
        foreach (var subkey in PointInTimeRestorePolicySubkeys)
        {
            try
            {
                using var hklm = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64);
                using var key = hklm.OpenSubKey(subkey);
                var value = key?.GetValue("EnablePointInTimeRestore", null, RegistryValueOptions.DoNotExpandEnvironmentNames);
                var parsed = ParsePolicyBoolean(value);
                if (parsed is not null) return parsed;
            }
            catch { }
        }
        return null;
    }

    private static bool IsAtLeast(WindowsBuildDetails? build, int minimumBuild, int minimumUbr)
    {
        if (build is null || build.BuildNumber < minimumBuild) return false;
        return build.BuildNumber > minimumBuild || build.UBR >= minimumUbr;
    }
}
