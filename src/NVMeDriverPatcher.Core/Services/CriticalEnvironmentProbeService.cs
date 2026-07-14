using System.Management;
using System.Security;
using System.Security.Principal;
using System.Text.RegularExpressions;
using Microsoft.Win32;

namespace NVMeDriverPatcher.Services;

public enum CriticalProbeVerdict
{
    Pass,
    Fail,
    Unknown
}

public enum CriticalProbeReasonCode
{
    ConfirmedSafe,
    ConfirmedDisabled,
    ConfirmedPresent,
    DeviceAbsent,
    AccessDenied,
    Timeout,
    UnsupportedApi,
    InvalidEvidence,
    QueryFailed,
    RecoveryProtectorMissing,
    UnstableState,
    ProtectionStateUnknown,
    DirectoryJoinUnknown
}

public enum MutationProbeScope
{
    RegistryPatch,
    FeatureStoreFallback
}

public sealed record CriticalProbeResult
{
    public string Id { get; init; } = string.Empty;
    public string Label { get; init; } = string.Empty;
    public CriticalProbeVerdict Verdict { get; init; }
    public CriticalProbeReasonCode ReasonCode { get; init; }
    public string Detail { get; init; } = string.Empty;
    public string? NativeError { get; init; }
    public IReadOnlyList<string> Evidence { get; init; } = Array.Empty<string>();
    public DateTimeOffset ObservedAtUtc { get; init; }
    public bool BlocksMutation => Verdict != CriticalProbeVerdict.Pass;
}

public sealed class CriticalProbeReport
{
    public MutationProbeScope Scope { get; init; }
    public List<CriticalProbeResult> Items { get; } = new();
    public BitLockerRecoveryProof? BitLockerRecovery { get; set; }
    public bool AllPassed => Items.Count > 0 && Items.All(item => !item.BlocksMutation);
    public bool HasUnknown => Items.Any(item => item.Verdict == CriticalProbeVerdict.Unknown);
    public int ExitCode => AllPassed ? 0 : HasUnknown ? 2 : 1;
    public string Summary => AllPassed
        ? $"Critical environment probes: {Items.Count}/{Items.Count} passed."
        : $"Critical environment probes block mutation: {string.Join(", ", Items.Where(item => item.BlocksMutation).Select(item => item.Label))}.";
}

internal sealed record VeraCryptProbeSnapshot(
    bool ServiceKeyPresent,
    int? ServiceStart,
    bool EfiMarkerPresent);

internal sealed record StorageDriverProbeSnapshot(
    string Name,
    string State,
    string StartMode);

internal interface ICriticalEnvironmentProbePlatform
{
    bool IsAdministrator();
    VeraCryptProbeSnapshot InspectVeraCrypt();
    IReadOnlyList<StorageDriverProbeSnapshot> InspectSystemDrivers();
    BitLockerRecoveryProof InspectBitLocker();
    (SafeBootKeyDisposition Minimal, SafeBootKeyDisposition Network) InspectSafeBootKeys();
}

/// <summary>
/// Authoritative, typed gate for boot-critical environment checks. A provider error never becomes
/// "not detected": it becomes Unknown with a stable reason code and blocks every enablement path.
/// </summary>
public static class CriticalEnvironmentProbeService
{
    private static readonly ICriticalEnvironmentProbePlatform LivePlatform = new WindowsCriticalProbePlatform();

    public static CriticalProbeReport EvaluateRegistryPatch() =>
        Evaluate(LivePlatform, MutationProbeScope.RegistryPatch, DateTimeOffset.UtcNow);

    public static CriticalProbeReport EvaluateFeatureStoreFallback() =>
        Evaluate(LivePlatform, MutationProbeScope.FeatureStoreFallback, DateTimeOffset.UtcNow);

    internal static CriticalProbeReport Evaluate(
        ICriticalEnvironmentProbePlatform platform,
        MutationProbeScope scope,
        DateTimeOffset observedAtUtc)
    {
        var report = new CriticalProbeReport { Scope = scope };
        report.Items.Add(ProbeAdministrator(platform, observedAtUtc));
        report.Items.Add(ProbeVeraCrypt(platform, observedAtUtc));
        report.Items.Add(ProbeIntelStorage(platform, observedAtUtc));
        report.Items.Add(ProbeBitLocker(platform, report, observedAtUtc));
        if (scope == MutationProbeScope.RegistryPatch)
            report.Items.Add(ProbeSafeBoot(platform, observedAtUtc));
        return report;
    }

    private static CriticalProbeResult ProbeAdministrator(
        ICriticalEnvironmentProbePlatform platform,
        DateTimeOffset observedAtUtc)
    {
        const string id = "Administrator";
        const string label = "Administrator privileges";
        try
        {
            bool administrator = platform.IsAdministrator();
            return Result(
                id,
                label,
                administrator ? CriticalProbeVerdict.Pass : CriticalProbeVerdict.Fail,
                administrator ? CriticalProbeReasonCode.ConfirmedSafe : CriticalProbeReasonCode.AccessDenied,
                administrator ? "Process token is elevated." : "Process token is not elevated; mutation is blocked.",
                [administrator ? "WindowsPrincipal=Administrator" : "WindowsPrincipal=StandardUser"],
                observedAtUtc);
        }
        catch (Exception ex)
        {
            return Unknown(id, label, ex, observedAtUtc);
        }
    }

    private static CriticalProbeResult ProbeVeraCrypt(
        ICriticalEnvironmentProbePlatform platform,
        DateTimeOffset observedAtUtc)
    {
        const string id = "VeraCrypt";
        const string label = "VeraCrypt system encryption";
        try
        {
            var snapshot = platform.InspectVeraCrypt();
            var evidence = new[]
            {
                snapshot.ServiceKeyPresent
                    ? $"veracrypt service Start={(snapshot.ServiceStart?.ToString() ?? "missing")}"
                    : "veracrypt service key absent",
                snapshot.EfiMarkerPresent ? "EFI VeraCrypt marker present" : "EFI VeraCrypt marker absent"
            };
            if (snapshot.ServiceKeyPresent && snapshot.ServiceStart is null)
            {
                return Result(id, label, CriticalProbeVerdict.Unknown, CriticalProbeReasonCode.InvalidEvidence,
                    "VeraCrypt service exists but its Start value is missing or invalid.", evidence, observedAtUtc);
            }
            if (snapshot.ServiceStart == 0 || snapshot.EfiMarkerPresent)
            {
                return Result(id, label, CriticalProbeVerdict.Fail, CriticalProbeReasonCode.ConfirmedPresent,
                    "VeraCrypt boot evidence is present; nvmedisk.sys enablement is blocked.", evidence, observedAtUtc);
            }
            return Result(id, label, CriticalProbeVerdict.Pass, CriticalProbeReasonCode.ConfirmedDisabled,
                "No VeraCrypt system-encryption boot evidence was found.", evidence, observedAtUtc);
        }
        catch (Exception ex)
        {
            return Unknown(id, label, ex, observedAtUtc);
        }
    }

    private static CriticalProbeResult ProbeIntelStorage(
        ICriticalEnvironmentProbePlatform platform,
        DateTimeOffset observedAtUtc)
    {
        const string id = "IntelStorage";
        const string label = "Intel RST/VMD";
        try
        {
            var matches = platform.InspectSystemDrivers()
                .Where(driver => WindowsCriticalProbePlatform.IsBlockingIntelStorageDriver(driver.Name))
                .ToList();
            if (matches.Count == 0)
            {
                return Result(id, label, CriticalProbeVerdict.Pass, CriticalProbeReasonCode.DeviceAbsent,
                    "No Intel RST or VMD system-driver service was found.",
                    ["Win32_SystemDriver query completed; blocking driver count=0"], observedAtUtc);
            }

            var evidence = matches
                .Select(driver => $"{driver.Name} (state={driver.State}, start={driver.StartMode})")
                .ToArray();
            if (matches.All(driver =>
                    driver.State.Equals("Stopped", StringComparison.OrdinalIgnoreCase) &&
                    driver.StartMode.Equals("Disabled", StringComparison.OrdinalIgnoreCase)))
            {
                return Result(id, label, CriticalProbeVerdict.Pass, CriticalProbeReasonCode.ConfirmedDisabled,
                    "Intel RST/VMD driver services are installed but authoritatively stopped and disabled.",
                    evidence, observedAtUtc);
            }
            return Result(id, label, CriticalProbeVerdict.Fail, CriticalProbeReasonCode.ConfirmedPresent,
                "Intel RST/VMD driver evidence is present; boot-safe nvmedisk.sys enablement is not proved.",
                evidence, observedAtUtc);
        }
        catch (Exception ex)
        {
            return Unknown(id, label, ex, observedAtUtc);
        }
    }

    private static CriticalProbeResult ProbeBitLocker(
        ICriticalEnvironmentProbePlatform platform,
        CriticalProbeReport report,
        DateTimeOffset observedAtUtc)
    {
        const string id = "BitLocker";
        const string label = "BitLocker recovery";
        try
        {
            var proof = platform.InspectBitLocker();
            report.BitLockerRecovery = proof;
            var evidence = new List<string>
            {
                $"mount={proof.Volume.MountPoint}",
                $"conversion={proof.Volume.ConversionStatus}",
                $"protection={proof.Volume.ProtectionStatus}",
                $"directoryJoin={proof.DirectoryJoin.Kind}"
            };
            evidence.AddRange(proof.Volume.RecoveryProtectorIds.Select(idValue => "protectorId=" + idValue));

            if (!proof.Volume.ProbeSucceeded)
            {
                return Result(id, label, CriticalProbeVerdict.Unknown,
                    MapFailureCode(proof.Volume.FailureCode), proof.Detail, evidence, observedAtUtc,
                    FormatNativeError(proof.Volume.FailureCode, proof.Volume.NativeError));
            }
            if (!proof.Volume.IsEncrypted)
            {
                return Result(id, label, CriticalProbeVerdict.Pass, CriticalProbeReasonCode.ConfirmedDisabled,
                    proof.Detail, evidence, observedAtUtc);
            }
            if (!proof.DirectoryJoin.ProbeSucceeded)
            {
                return Result(id, label, CriticalProbeVerdict.Unknown, CriticalProbeReasonCode.DirectoryJoinUnknown,
                    proof.Detail, evidence, observedAtUtc, proof.DirectoryJoin.FailureCode);
            }
            if (proof.Volume.ProtectionStatus == 2)
            {
                return Result(id, label, CriticalProbeVerdict.Unknown, CriticalProbeReasonCode.ProtectionStateUnknown,
                    proof.Detail, evidence, observedAtUtc);
            }
            if (!proof.Volume.IsFullyEncrypted)
            {
                return Result(id, label, CriticalProbeVerdict.Fail, CriticalProbeReasonCode.UnstableState,
                    proof.Detail, evidence, observedAtUtc);
            }
            if (proof.Volume.RecoveryProtectorIds.Count == 0)
            {
                return Result(id, label, CriticalProbeVerdict.Fail, CriticalProbeReasonCode.RecoveryProtectorMissing,
                    proof.Detail, evidence, observedAtUtc);
            }
            return Result(id, label, CriticalProbeVerdict.Pass, CriticalProbeReasonCode.ConfirmedSafe,
                proof.Detail, evidence, observedAtUtc);
        }
        catch (Exception ex)
        {
            return Unknown(id, label, ex, observedAtUtc);
        }
    }

    private static CriticalProbeResult ProbeSafeBoot(
        ICriticalEnvironmentProbePlatform platform,
        DateTimeOffset observedAtUtc)
    {
        const string id = "SafeBoot";
        const string label = "SafeBoot registry";
        try
        {
            var keys = platform.InspectSafeBootKeys();
            var evidence = new[] { $"Minimal={keys.Minimal}", $"Network={keys.Network}" };
            if (keys.Minimal == SafeBootKeyDisposition.AccessDenied ||
                keys.Network == SafeBootKeyDisposition.AccessDenied)
            {
                return Result(id, label, CriticalProbeVerdict.Fail, CriticalProbeReasonCode.AccessDenied,
                    "SafeBoot GUID key access is denied; recovery entries cannot be proved writable.",
                    evidence, observedAtUtc);
            }
            return Result(id, label, CriticalProbeVerdict.Pass, CriticalProbeReasonCode.ConfirmedSafe,
                "SafeBoot GUID key state is readable and preserves any pre-existing values.",
                evidence, observedAtUtc);
        }
        catch (Exception ex)
        {
            return Unknown(id, label, ex, observedAtUtc);
        }
    }

    private static CriticalProbeResult Unknown(
        string id,
        string label,
        Exception exception,
        DateTimeOffset observedAtUtc)
    {
        var (reason, nativeError) = ClassifyException(exception);
        return Result(id, label, CriticalProbeVerdict.Unknown, reason,
            $"{label} could not be verified ({reason}); mutation is blocked.",
            [$"exception={exception.GetType().Name}"], observedAtUtc, nativeError);
    }

    internal static (CriticalProbeReasonCode Reason, string NativeError) ClassifyException(Exception exception)
    {
        if (exception is UnauthorizedAccessException or SecurityException)
            return (CriticalProbeReasonCode.AccessDenied, $"HRESULT=0x{unchecked((uint)exception.HResult):X8}");
        if (exception is TimeoutException ||
            exception is ManagementException timeoutManagement &&
            timeoutManagement.ErrorCode.ToString().Contains("timeout", StringComparison.OrdinalIgnoreCase))
            return (CriticalProbeReasonCode.Timeout, NativeExceptionCode(exception));
        if (exception is PlatformNotSupportedException or DllNotFoundException or EntryPointNotFoundException)
            return (CriticalProbeReasonCode.UnsupportedApi, NativeExceptionCode(exception));
        if (exception is ManagementException management &&
            management.ErrorCode.ToString() is "InvalidNamespace" or "InvalidClass" or "NotFound")
            return (CriticalProbeReasonCode.UnsupportedApi, NativeExceptionCode(exception));
        return (CriticalProbeReasonCode.QueryFailed, NativeExceptionCode(exception));
    }

    private static string NativeExceptionCode(Exception exception) => exception is ManagementException management
        ? $"WMI={management.ErrorCode}; HRESULT=0x{unchecked((uint)exception.HResult):X8}"
        : $"HRESULT=0x{unchecked((uint)exception.HResult):X8}";

    private static CriticalProbeReasonCode MapFailureCode(string? failureCode)
    {
        if (failureCode?.Contains("AccessDenied", StringComparison.OrdinalIgnoreCase) == true)
            return CriticalProbeReasonCode.AccessDenied;
        if (failureCode?.Contains("timeout", StringComparison.OrdinalIgnoreCase) == true)
            return CriticalProbeReasonCode.Timeout;
        if (failureCode?.Contains("InvalidNamespace", StringComparison.OrdinalIgnoreCase) == true ||
            failureCode?.Contains("PlatformNotSupported", StringComparison.OrdinalIgnoreCase) == true)
            return CriticalProbeReasonCode.UnsupportedApi;
        return CriticalProbeReasonCode.QueryFailed;
    }

    private static string? FormatNativeError(string? failureCode, uint? nativeError) => nativeError is null
        ? failureCode
        : $"{failureCode}; native=0x{nativeError.Value:X8}";

    private static CriticalProbeResult Result(
        string id,
        string label,
        CriticalProbeVerdict verdict,
        CriticalProbeReasonCode reasonCode,
        string detail,
        IReadOnlyList<string> evidence,
        DateTimeOffset observedAtUtc,
        string? nativeError = null) => new()
    {
        Id = id,
        Label = label,
        Verdict = verdict,
        ReasonCode = reasonCode,
        Detail = detail,
        NativeError = nativeError,
        Evidence = evidence,
        ObservedAtUtc = observedAtUtc
    };
}

internal sealed class WindowsCriticalProbePlatform : ICriticalEnvironmentProbePlatform
{
    private static readonly Regex BlockingIntelStorageDriver = new(
        @"^(iaStorAC|iaStorAVC|iaStorE|iaStorVD|vmd|vmd_bus)$",
        RegexOptions.IgnoreCase | RegexOptions.Compiled | RegexOptions.CultureInvariant);

    public bool IsAdministrator()
    {
        using var identity = WindowsIdentity.GetCurrent();
        return new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);
    }

    public VeraCryptProbeSnapshot InspectVeraCrypt()
    {
        bool servicePresent;
        int? serviceStart = null;
        using (var hklm = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64))
        using (var key = hklm.OpenSubKey(@"SYSTEM\CurrentControlSet\Services\veracrypt", writable: false))
        {
            servicePresent = key is not null;
            if (key is not null)
                serviceStart = key.GetValue("Start") is int start ? start : null;
        }

        var root = Environment.GetEnvironmentVariable("SystemDrive") ?? "C:";
        var marker = Path.Combine(root + Path.DirectorySeparatorChar, "EFI", "VeraCrypt");
        bool efiMarker = DirectoryExistsAuthoritatively(marker);
        return new(servicePresent, serviceStart, efiMarker);
    }

    public IReadOnlyList<StorageDriverProbeSnapshot> InspectSystemDrivers()
    {
        var drivers = new List<StorageDriverProbeSnapshot>();
        using var search = new ManagementObjectSearcher(
            "SELECT Name, State, StartMode FROM Win32_SystemDriver");
        using var results = WmiQueryHelper.ExecuteWithTimeout(search);
        foreach (var raw in results)
        {
            if (raw is not ManagementObject driver) continue;
            using (driver)
            {
                drivers.Add(new(
                    driver["Name"]?.ToString() ?? string.Empty,
                    driver["State"]?.ToString() ?? "Unknown",
                    driver["StartMode"]?.ToString() ?? "Unknown"));
            }
        }
        return drivers;
    }

    public BitLockerRecoveryProof InspectBitLocker() => BitLockerRecoveryService.InspectSystemVolume();

    public (SafeBootKeyDisposition Minimal, SafeBootKeyDisposition Network) InspectSafeBootKeys() =>
        SafeBootStateService.ClassifyGuidKeys(new RealSafeBootRegistry());

    internal static bool IsBlockingIntelStorageDriver(string? name) =>
        !string.IsNullOrWhiteSpace(name) && BlockingIntelStorageDriver.IsMatch(name);

    internal static bool DirectoryExistsAuthoritatively(string path)
    {
        try
        {
            return (File.GetAttributes(path) & FileAttributes.Directory) != 0;
        }
        catch (DirectoryNotFoundException)
        {
            return false;
        }
        catch (FileNotFoundException)
        {
            return false;
        }
    }
}
