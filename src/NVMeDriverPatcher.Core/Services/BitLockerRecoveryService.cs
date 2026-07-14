using System.Diagnostics;
using System.Globalization;
using System.Management;
using System.Runtime.InteropServices;

namespace NVMeDriverPatcher.Services;

public enum DirectoryJoinKind
{
    None,
    ActiveDirectory,
    MicrosoftEntra,
    Hybrid
}

public sealed record DirectoryJoinEvidence(
    bool ProbeSucceeded,
    DirectoryJoinKind Kind,
    string? FailureCode = null);

public sealed record BitLockerVolumeEvidence
{
    public bool ProbeSucceeded { get; init; }
    public bool SystemVolumePresent { get; init; }
    public string MountPoint { get; init; } = string.Empty;
    public uint ConversionStatus { get; init; }
    public uint ProtectionStatus { get; init; }
    public uint? SuspendCount { get; init; }
    public IReadOnlyList<string> RecoveryProtectorIds { get; init; } = Array.Empty<string>();
    public string? FailureCode { get; init; }
    public uint? NativeError { get; init; }

    public bool IsEncrypted => SystemVolumePresent && ConversionStatus != 0;
    public bool IsFullyEncrypted => SystemVolumePresent && ConversionStatus == 1;
    public bool IsSuspendedForOneReboot => IsFullyEncrypted && ProtectionStatus == 0 && SuspendCount == 1;
}

public sealed record BitLockerRecoveryProof(
    BitLockerVolumeEvidence Volume,
    DirectoryJoinEvidence DirectoryJoin)
{
    public bool ReadyForMutation =>
        Volume.ProbeSucceeded &&
        (!Volume.IsEncrypted ||
         (DirectoryJoin.ProbeSucceeded &&
          Volume.IsFullyEncrypted &&
          Volume.ProtectionStatus is 0 or 1 &&
          Volume.RecoveryProtectorIds.Count > 0));

    public string Detail => BitLockerRecoveryService.DescribeProof(this);
}

public sealed record BitLockerNativeResult(bool Success, string Summary, uint? NativeError = null);

public sealed record BitLockerPreparationResult(
    bool Success,
    bool SuspensionRequired,
    bool SuspendedByThisCall,
    string Summary,
    BitLockerRecoveryProof Proof);

internal interface IBitLockerPlatform
{
    BitLockerVolumeEvidence InspectSystemVolume();
    DirectoryJoinEvidence InspectDirectoryJoin();
    BitLockerNativeResult BackupToActiveDirectory(string protectorId);
    BitLockerNativeResult BackupToMicrosoftEntra(string mountPoint, string protectorId);
    BitLockerNativeResult SuspendSystemVolumeForOneReboot();
    BitLockerNativeResult ResumeSystemVolume();
}

/// <summary>
/// Proves that an encrypted OS volume has a numerical-password recovery protector, refreshes that
/// protector in AD DS / Microsoft Entra when joined, and suspends through the locale-independent
/// Win32_EncryptableVolume provider. No recovery password is ever requested, returned, or logged.
/// </summary>
public static class BitLockerRecoveryService
{
    private static readonly IBitLockerPlatform LivePlatform = new WmiBitLockerPlatform();

    public static BitLockerRecoveryProof InspectSystemVolume() =>
        new(LivePlatform.InspectSystemVolume(), LivePlatform.InspectDirectoryJoin());

    public static BitLockerPreparationResult PrepareForMutation(
        Func<bool> persistSuspensionIntent,
        Action<string>? log = null) =>
        PrepareForMutation(LivePlatform, persistSuspensionIntent, log);

    internal static BitLockerPreparationResult PrepareForMutation(
        IBitLockerPlatform platform,
        Func<bool> persistSuspensionIntent,
        Action<string>? log = null)
    {
        var proof = new BitLockerRecoveryProof(
            platform.InspectSystemVolume(),
            platform.InspectDirectoryJoin());

        if (!proof.ReadyForMutation)
            return new(false, false, false, DescribeProof(proof), proof);

        if (!proof.Volume.IsEncrypted)
            return new(true, false, false, "System volume is not BitLocker-encrypted; suspension is not required.", proof);

        var ids = proof.Volume.RecoveryProtectorIds;
        log?.Invoke("BitLocker recovery-password protector ID(s): " + string.Join(", ", ids));
        log?.Invoke("Only protector identifiers are recorded; recovery password material is never read or logged.");

        foreach (var protectorId in ids)
        {
            if (proof.DirectoryJoin.Kind is DirectoryJoinKind.ActiveDirectory or DirectoryJoinKind.Hybrid)
            {
                var backup = platform.BackupToActiveDirectory(protectorId);
                if (!backup.Success)
                    return new(false, false, false, "Active Directory recovery-key backup failed: " + backup.Summary, proof);
                log?.Invoke($"[BitLocker] Protector {protectorId} backup to Active Directory confirmed.");
            }

            if (proof.DirectoryJoin.Kind is DirectoryJoinKind.MicrosoftEntra or DirectoryJoinKind.Hybrid)
            {
                var backup = platform.BackupToMicrosoftEntra(proof.Volume.MountPoint, protectorId);
                if (!backup.Success)
                    return new(false, false, false, "Microsoft Entra recovery-key backup failed: " + backup.Summary, proof);
                log?.Invoke($"[BitLocker] Protector {protectorId} backup to Microsoft Entra confirmed.");
            }
        }

        if (proof.Volume.IsSuspendedForOneReboot)
        {
            return new(true, true, false,
                "BitLocker is already suspended for exactly one reboot and a recovery-password protector is present.",
                proof);
        }

        if (!persistSuspensionIntent())
            return new(false, true, false,
                "BitLocker suspension intent could not be persisted before changing protector state.",
                proof);

        var suspend = platform.SuspendSystemVolumeForOneReboot();
        if (!suspend.Success)
            return new(false, true, true, "BitLocker suspension failed: " + suspend.Summary, proof);

        var verifiedVolume = platform.InspectSystemVolume();
        var verifiedProof = new BitLockerRecoveryProof(verifiedVolume, proof.DirectoryJoin);
        if (!verifiedVolume.ProbeSucceeded || !verifiedVolume.IsSuspendedForOneReboot)
        {
            return new(false, true, true,
                "BitLocker suspension call returned success, but WMI did not confirm ProtectionStatus=0 and SuspendCount=1.",
                verifiedProof);
        }

        log?.Invoke("[BitLocker] WMI confirmed protection is suspended for exactly one reboot.");
        return new(true, true, true,
            "Recovery protector proved and one-reboot BitLocker suspension confirmed.",
            verifiedProof);
    }

    public static BitLockerNativeResult ResumeSystemVolume(Action<string>? log = null) =>
        ResumeSystemVolume(LivePlatform, log);

    internal static BitLockerNativeResult ResumeSystemVolume(
        IBitLockerPlatform platform,
        Action<string>? log = null)
    {
        var before = platform.InspectSystemVolume();
        if (!before.ProbeSucceeded)
            return new(false, "System-volume protection state is unavailable: " + before.FailureCode, before.NativeError);
        if (!before.IsEncrypted || before.ProtectionStatus == 1)
            return new(true, "BitLocker protection is already active or not required.");

        var resumed = platform.ResumeSystemVolume();
        if (!resumed.Success)
            return resumed;
        var after = platform.InspectSystemVolume();
        if (!after.ProbeSucceeded || after.ProtectionStatus != 1)
            return new(false, "EnableKeyProtectors returned success, but WMI did not confirm ProtectionStatus=1.");
        log?.Invoke("[BitLocker] Protection resumed after the mutation was rolled back.");
        return new(true, "BitLocker protection resumed and verified.");
    }

    internal static string DescribeProof(BitLockerRecoveryProof proof)
    {
        var volume = proof.Volume;
        if (!volume.ProbeSucceeded)
            return $"BitLocker state unavailable ({volume.FailureCode ?? "unknown"}; native={FormatNative(volume.NativeError)}).";
        if (!volume.SystemVolumePresent || !volume.IsEncrypted)
            return "System volume is not BitLocker-encrypted.";
        if (!proof.DirectoryJoin.ProbeSucceeded)
            return $"Directory join state unavailable ({proof.DirectoryJoin.FailureCode ?? "unknown"}); recovery backup destination cannot be proven.";
        if (!volume.IsFullyEncrypted)
            return $"BitLocker conversion state {volume.ConversionStatus} is not stable (1=fully encrypted); mutation is blocked.";
        if (volume.ProtectionStatus == 2)
            return "BitLocker protection status is Unknown (2); mutation is blocked.";
        if (volume.RecoveryProtectorIds.Count == 0)
            return "BitLocker is active but no numerical-password recovery protector is present; mutation is blocked.";

        var destination = proof.DirectoryJoin.Kind switch
        {
            DirectoryJoinKind.ActiveDirectory => "Active Directory backup will be refreshed",
            DirectoryJoinKind.MicrosoftEntra => "Microsoft Entra backup will be refreshed",
            DirectoryJoinKind.Hybrid => "Active Directory and Microsoft Entra backups will be refreshed",
            _ => "device is not directory-joined"
        };
        return $"Recovery-password protector ID(s): {string.Join(", ", volume.RecoveryProtectorIds)}; {destination}.";
    }

    private static string FormatNative(uint? value) =>
        value is null ? "n/a" : "0x" + value.Value.ToString("X8", CultureInfo.InvariantCulture);
}

internal sealed class WmiBitLockerPlatform : IBitLockerPlatform
{
    private const string NamespacePath = @"root\cimv2\Security\MicrosoftVolumeEncryption";
    private const uint Success = 0;
    private const uint NumericalPasswordProtector = 3;

    public BitLockerVolumeEvidence InspectSystemVolume()
    {
        var mountPoint = NormalizeMountPoint(Environment.GetEnvironmentVariable("SystemDrive"));
        try
        {
            return WithSystemVolume(mountPoint, volume => InspectVolume(volume, mountPoint)) ??
                   new BitLockerVolumeEvidence
                   {
                       ProbeSucceeded = true,
                       SystemVolumePresent = false,
                       MountPoint = mountPoint
                   };
        }
        catch (ManagementException ex)
        {
            return FailedEvidence(mountPoint, "WmiManagementError", unchecked((uint)ex.ErrorCode));
        }
        catch (UnauthorizedAccessException)
        {
            return FailedEvidence(mountPoint, "AccessDenied", 5);
        }
        catch (Exception ex)
        {
            return FailedEvidence(mountPoint, ex.GetType().Name, null);
        }
    }

    public DirectoryJoinEvidence InspectDirectoryJoin()
    {
        bool adJoined;
        try
        {
            using var search = new ManagementObjectSearcher("SELECT PartOfDomain FROM Win32_ComputerSystem");
            using var results = WmiQueryHelper.ExecuteWithTimeout(search);
            var first = results.Cast<ManagementBaseObject>().FirstOrDefault();
            if (first is null)
                return new(false, DirectoryJoinKind.None, "ComputerSystemNotFound");
            adJoined = first["PartOfDomain"] is bool joined && joined;
        }
        catch (Exception ex)
        {
            return new(false, DirectoryJoinKind.None, "ActiveDirectoryProbe:" + ex.GetType().Name);
        }

        var entra = InspectMicrosoftEntraJoin();
        if (!entra.ProbeSucceeded)
            return new(false, DirectoryJoinKind.None, entra.FailureCode);
        bool entraJoined = entra.Joined;

        var kind = (adJoined, entraJoined) switch
        {
            (true, true) => DirectoryJoinKind.Hybrid,
            (true, false) => DirectoryJoinKind.ActiveDirectory,
            (false, true) => DirectoryJoinKind.MicrosoftEntra,
            _ => DirectoryJoinKind.None
        };
        return new(true, kind);
    }

    private static (bool ProbeSucceeded, bool Joined, string? FailureCode) InspectMicrosoftEntraJoin()
    {
        IntPtr joinInfo = IntPtr.Zero;
        try
        {
            int hresult = NetGetAadJoinInformation(null, out joinInfo);
            if (hresult < 0)
                return (false, false, $"EntraProbe:HRESULT_0x{unchecked((uint)hresult):X8}");
            if (joinInfo == IntPtr.Zero)
                return (true, false, null);

            var info = Marshal.PtrToStructure<DsregJoinInfo>(joinInfo);
            return (true, info.JoinType == DsregJoinType.DeviceJoin, null);
        }
        catch (Exception ex)
        {
            return (false, false, "EntraProbe:" + ex.GetType().Name);
        }
        finally
        {
            if (joinInfo != IntPtr.Zero)
                NetFreeAadJoinInformation(joinInfo);
        }
    }

    public BitLockerNativeResult BackupToActiveDirectory(string protectorId)
    {
        var id = NormalizeProtectorId(protectorId);
        if (id is null)
            return new(false, "Protector ID is not a GUID.");
        try
        {
            return WithSystemVolume(NormalizeMountPoint(Environment.GetEnvironmentVariable("SystemDrive")), volume =>
            {
                using var input = volume.GetMethodParameters("BackupRecoveryInformationToActiveDirectory");
                input["VolumeKeyProtectorID"] = id;
                using var output = volume.InvokeMethod("BackupRecoveryInformationToActiveDirectory", input, null);
                return NativeResult(output, "BackupRecoveryInformationToActiveDirectory");
            }) ?? new(false, "System BitLocker volume was not found.");
        }
        catch (Exception ex)
        {
            return new(false, "AD backup invocation failed: " + ex.GetType().Name);
        }
    }

    public BitLockerNativeResult BackupToMicrosoftEntra(string mountPoint, string protectorId)
    {
        var id = NormalizeProtectorId(protectorId);
        var mount = NormalizeMountPoint(mountPoint);
        if (id is null)
            return new(false, "Protector ID is not a GUID.");
        try
        {
            var script =
                $"BackupToAAD-BitLockerKeyProtector -MountPoint '{mount}' -KeyProtectorId '{id}' -Confirm:$false -ErrorAction Stop | Out-Null";
            var psi = new ProcessStartInfo("powershell.exe")
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            };
            psi.ArgumentList.Add("-NoProfile");
            psi.ArgumentList.Add("-NonInteractive");
            psi.ArgumentList.Add("-Command");
            psi.ArgumentList.Add(script);
            using var process = Process.Start(psi);
            if (process is null)
                return new(false, "powershell.exe could not start.");
            var stdout = process.StandardOutput.ReadToEndAsync();
            var stderr = process.StandardError.ReadToEndAsync();
            if (!process.WaitForExit(60000))
            {
                try { process.Kill(entireProcessTree: true); } catch { }
                return new(false, "Microsoft Entra backup timed out after 60 seconds.");
            }
            try { stdout.GetAwaiter().GetResult(); } catch { }
            try { stderr.GetAwaiter().GetResult(); } catch { }
            return process.ExitCode == 0
                ? new(true, "Microsoft Entra backup command succeeded.")
                : new(false, $"Microsoft Entra backup command exited {process.ExitCode}.");
        }
        catch (Exception ex)
        {
            return new(false, "Microsoft Entra backup invocation failed: " + ex.GetType().Name);
        }
    }

    public BitLockerNativeResult SuspendSystemVolumeForOneReboot()
    {
        try
        {
            return WithSystemVolume(NormalizeMountPoint(Environment.GetEnvironmentVariable("SystemDrive")), volume =>
            {
                using var input = volume.GetMethodParameters("DisableKeyProtectors");
                input["DisableCount"] = 1u;
                using var output = volume.InvokeMethod("DisableKeyProtectors", input, null);
                return NativeResult(output, "DisableKeyProtectors");
            }) ?? new(false, "System BitLocker volume was not found.");
        }
        catch (Exception ex)
        {
            return new(false, "DisableKeyProtectors invocation failed: " + ex.GetType().Name);
        }
    }

    public BitLockerNativeResult ResumeSystemVolume()
    {
        try
        {
            return WithSystemVolume(NormalizeMountPoint(Environment.GetEnvironmentVariable("SystemDrive")), volume =>
            {
                using var output = volume.InvokeMethod("EnableKeyProtectors", null, null);
                return NativeResult(output, "EnableKeyProtectors");
            }) ?? new(false, "System BitLocker volume was not found.");
        }
        catch (Exception ex)
        {
            return new(false, "EnableKeyProtectors invocation failed: " + ex.GetType().Name);
        }
    }

    private static BitLockerVolumeEvidence InspectVolume(ManagementObject volume, string mountPoint)
    {
        var conversion = InvokeConversionStatus(volume);
        if (!conversion.Success)
            return FailedEvidence(mountPoint, "GetConversionStatus", conversion.Error);
        var protection = InvokeScalar(volume, "GetProtectionStatus", "ProtectionStatus");
        if (!protection.Success)
            return FailedEvidence(mountPoint, "GetProtectionStatus", protection.Error);
        var protectors = InvokeRecoveryProtectors(volume);
        if (!protectors.Success && conversion.Value != 0)
            return FailedEvidence(mountPoint, "GetKeyProtectors", protectors.Error);

        uint? suspendCount = null;
        if (conversion.Value != 0 && protection.Value == 0)
        {
            var suspend = InvokeScalar(volume, "GetSuspendCount", "SuspendCount");
            if (suspend.Success)
                suspendCount = suspend.Value;
        }

        return new BitLockerVolumeEvidence
        {
            ProbeSucceeded = true,
            SystemVolumePresent = true,
            MountPoint = mountPoint,
            ConversionStatus = conversion.Value,
            ProtectionStatus = protection.Value,
            SuspendCount = suspendCount,
            RecoveryProtectorIds = protectors.Ids
        };
    }

    private static (bool Success, uint Value, uint? Error) InvokeConversionStatus(ManagementObject volume)
    {
        using var input = volume.GetMethodParameters("GetConversionStatus");
        input["PrecisionFactor"] = 0u;
        using var output = volume.InvokeMethod("GetConversionStatus", input, null);
        var error = ReturnValue(output);
        return error == Success
            ? (true, Convert.ToUInt32(output?["ConversionStatus"] ?? 0u, CultureInfo.InvariantCulture), null)
            : (false, 0, error);
    }

    private static (bool Success, uint Value, uint? Error) InvokeScalar(
        ManagementObject volume,
        string method,
        string outputName)
    {
        using var output = volume.InvokeMethod(method, null, null);
        var error = ReturnValue(output);
        return error == Success
            ? (true, Convert.ToUInt32(output?[outputName] ?? 0u, CultureInfo.InvariantCulture), null)
            : (false, 0, error);
    }

    private static (bool Success, IReadOnlyList<string> Ids, uint? Error) InvokeRecoveryProtectors(
        ManagementObject volume)
    {
        using var input = volume.GetMethodParameters("GetKeyProtectors");
        input["KeyProtectorType"] = NumericalPasswordProtector;
        using var output = volume.InvokeMethod("GetKeyProtectors", input, null);
        var error = ReturnValue(output);
        if (error != Success)
            return (false, Array.Empty<string>(), error);
        var ids = output?["VolumeKeyProtectorID"] is Array values
            ? values.Cast<object?>()
                .Select(value => NormalizeProtectorId(value?.ToString()))
                .Where(value => value is not null)
                .Cast<string>()
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray()
            : Array.Empty<string>();
        return (true, ids, null);
    }

    private static T? WithSystemVolume<T>(string mountPoint, Func<ManagementObject, T> action)
        where T : class
    {
        using var search = new ManagementObjectSearcher(
            NamespacePath,
            $"SELECT * FROM Win32_EncryptableVolume WHERE DriveLetter='{mountPoint}'");
        using var results = WmiQueryHelper.ExecuteWithTimeout(search);
        foreach (var raw in results)
        {
            if (raw is not ManagementObject volume) continue;
            using (volume)
                return action(volume);
        }
        return null;
    }

    private static BitLockerNativeResult NativeResult(ManagementBaseObject? output, string method)
    {
        var value = ReturnValue(output);
        return value == Success
            ? new(true, method + " succeeded.")
            : new(false, $"{method} returned 0x{value:X8}.", value);
    }

    private static uint ReturnValue(ManagementBaseObject? output)
    {
        try { return Convert.ToUInt32(output?["ReturnValue"] ?? uint.MaxValue, CultureInfo.InvariantCulture); }
        catch { return uint.MaxValue; }
    }

    private static BitLockerVolumeEvidence FailedEvidence(
        string mountPoint,
        string code,
        uint? nativeError) => new()
    {
        ProbeSucceeded = false,
        MountPoint = mountPoint,
        FailureCode = code,
        NativeError = nativeError
    };

    internal static string NormalizeMountPoint(string? mountPoint)
    {
        var value = string.IsNullOrWhiteSpace(mountPoint) ? "C:" : mountPoint.Trim();
        if (value.Length >= 2 && char.IsLetter(value[0]) && value[1] == ':')
            return char.ToUpperInvariant(value[0]) + ":";
        return "C:";
    }

    internal static string? NormalizeProtectorId(string? protectorId)
    {
        if (!Guid.TryParse(protectorId?.Trim().Trim('{', '}'), out var guid))
            return null;
        return "{" + guid.ToString("D").ToUpperInvariant() + "}";
    }

    private enum DsregJoinType
    {
        UnknownJoin = 0,
        DeviceJoin = 1,
        WorkplaceJoin = 2
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct DsregJoinInfo
    {
        public DsregJoinType JoinType;
        public IntPtr JoinCertificate;
        public IntPtr DeviceId;
        public IntPtr IdentityProviderDomain;
        public IntPtr TenantId;
        public IntPtr JoinUserEmail;
        public IntPtr TenantDisplayName;
        public IntPtr MdmEnrollmentUrl;
        public IntPtr MdmTermsOfUseUrl;
        public IntPtr MdmComplianceUrl;
        public IntPtr UserSettingSyncUrl;
        public IntPtr UserInfo;
    }

    [DllImport("netapi32.dll", CharSet = CharSet.Unicode)]
    private static extern int NetGetAadJoinInformation(
        string? tenantId,
        out IntPtr joinInfo);

    [DllImport("netapi32.dll")]
    private static extern void NetFreeAadJoinInformation(IntPtr joinInfo);
}
