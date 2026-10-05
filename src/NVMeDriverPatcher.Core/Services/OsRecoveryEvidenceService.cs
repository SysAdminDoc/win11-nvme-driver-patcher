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
                return "This Windows build doesn't offer Point-in-Time Restore.";

            var state = PointInTimeRestoreEnabled switch
            {
                true => "Point-in-Time Restore is on.",
                false => "Point-in-Time Restore is turned off by policy.",
                _ => "Point-in-Time Restore is available, but Windows doesn't say whether it's on."
            };

            // The exact timestamp stays in the JSON and diagnostics fields; the sentence gives the age.
            var point = !RestorePointQuerySucceeded
                ? "Windows didn't return its restore point list, so the newest point's age is unknown."
                : NewestRestorePointUtc is { } newest
                    ? $"The newest restore point {FormatAge(newest)}."
                    : "There are no restore points yet.";
            return $"{state} {point}";
        }
    }

    public string QuickMachineRecoverySummary
    {
        get
        {
            if (!QuickMachineRecoverySupported)
                return "This Windows build doesn't offer Quick Machine Recovery.";
            if (!QuickMachineRecoveryQuerySucceeded)
                return "Neither reagentc nor policy reports whether Quick Machine Recovery is on.";

            var state = QuickMachineRecoveryEnabled switch
            {
                true => "Quick Machine Recovery is on.",
                false => "Quick Machine Recovery is off.",
                _ => "Windows doesn't report whether Quick Machine Recovery is on."
            };
            var auto = QuickMachineRecoveryAutoRemediationEnabled switch
            {
                true => "Its automatic remediation is on.",
                false => "Its automatic remediation is off.",
                _ => "Its automatic remediation setting isn't reported."
            };
            return $"{state} {auto}";
        }
    }

    public string Summary => $"{PointInTimeRestoreSummary} {QuickMachineRecoverySummary}";

    private static string FormatAge(DateTimeOffset timestamp)
    {
        var age = DateTimeOffset.UtcNow - timestamp.ToUniversalTime();
        if (age < TimeSpan.Zero) return "has a timestamp in the future";
        if (age.TotalDays >= 1) return $"is {Count((int)age.TotalDays, "day")} old";
        if (age.TotalHours >= 1) return $"is {Count((int)age.TotalHours, "hour")} old";
        return $"is {Count(Math.Max(0, (int)age.TotalMinutes), "minute")} old";
    }

    private static string Count(int value, string unit) => value == 1 ? $"1 {unit}" : $"{value} {unit}s";
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
