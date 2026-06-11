using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32;

namespace NVMeDriverPatcher.Services;

public class FeatureStoreWriteResult
{
    public bool Success { get; set; }
    public string Summary { get; set; } = string.Empty;
    public int[] AppliedIds { get; set; } = Array.Empty<int>();
}

/// <summary>Decoded state of one feature ID in one configuration store.</summary>
public sealed record FeatureConfigState(
    int FeatureId,
    bool Found,
    int EnabledState,   // 0 = Default, 1 = Disabled, 2 = Enabled
    int Priority,       // 0..15; ViVeTool user overrides land at 8 (User)
    string Store)       // "Boot" or "Runtime"
{
    public bool IsEnabled => Found && EnabledState == 2;
}

// Native feature-configuration access via the same ntdll APIs ViVeTool uses
// (RtlQueryFeatureConfiguration / RtlSetFeatureConfigurations — see
// thebookisclosed/ViVe NativeMethods.Ntdll.cs + NativeStructs.cs). This replaces the
// earlier plan to reverse-engineer the FeatureStore registry blob: the blob format is
// undocumented, but the Rtl API surface is stable since Win10 2004 and is exactly what
// the kernel itself consults.
//
//   - Query path (read-only, no admin): per-ID enabled-state/priority for the Boot and
//     Runtime stores. Powers HasFallbackEvidence and the CLI `featurestore` report.
//   - Write path (EXPERIMENTAL, admin): sets the same configuration ViVeTool's /enable
//     sets (priority User, state Enabled, both stores). Exposed only behind the explicit
//     CLI `featurestore --write-native` switch — the ViVeTool download remains the
//     default fallback route until this path has soaked.
//   - Blob export is kept for support bundles; the blob scan remains only as a last-resort
//     evidence heuristic when the Rtl API is unavailable.
public static class FeatureStoreWriterService
{
    public const string FeatureStoreSubkey =
        @"SYSTEM\CurrentControlSet\Control\FeatureManagement\Overrides";

    public const string DataValueName = "FeatureStore";

    // Union of every known fallback set — evidence written by ANY known set (or by a
    // user running ViVeTool by hand from a forum guide) must still be recognized.
    public static readonly int[] PostBlockFeatureIds =
        Models.FallbackFeatureCatalog.AllKnownIds.ToArray();

    #region ntdll interop (mirrors ViVe's NativeStructs/NativeMethods)

    private enum ConfigurationType : uint { Boot = 0, Runtime = 1 }

    private const int StatusSuccess = 0;

    [StructLayout(LayoutKind.Sequential)]
    private struct RTL_FEATURE_CONFIGURATION
    {
        public uint FeatureId;
        // Bitfield: Priority:4 | EnabledState:2 | IsWexpConfiguration:1 | HasSubscriptions:1
        //         | Variant:6 | VariantPayloadKind:2 | (reserved)
        public uint CompactState;
        public uint VariantPayload;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct RTL_FEATURE_CONFIGURATION_UPDATE
    {
        public uint FeatureId;
        public uint Priority;            // 8 = User (what ViVeTool /enable writes)
        public uint EnabledState;        // 0 Default / 1 Disabled / 2 Enabled
        public uint EnabledStateOptions; // 1 = WexpConfig used by some tooling; 0 here
        public uint Variant;
        public uint VariantPayloadKind;
        public uint VariantPayload;
        public uint Operation;           // 1 FeatureState | 2 VariantState (combined: 3)
    }

    [DllImport("ntdll.dll")]
    private static extern int RtlQueryFeatureConfiguration(
        uint featureId, ConfigurationType configurationType, ref ulong changeStamp,
        out RTL_FEATURE_CONFIGURATION configuration);

    [DllImport("ntdll.dll")]
    private static extern int RtlSetFeatureConfigurations(
        ref ulong changeStamp, ConfigurationType configurationType,
        [In] RTL_FEATURE_CONFIGURATION_UPDATE[] updates, uint updateCount);

    #endregion

    // --- CompactState decoding (internal for tests) ---
    internal static int DecodePriority(uint compactState) => (int)(compactState & 0xF);
    internal static int DecodeEnabledState(uint compactState) => (int)((compactState >> 4) & 0x3);

    /// <summary>
    /// Read-only query of one feature ID in one store via the Rtl API. Returns Found=false
    /// (not an exception) when the feature has no configuration there.
    /// </summary>
    public static FeatureConfigState QueryConfiguration(int featureId, bool bootStore)
    {
        var store = bootStore ? "Boot" : "Runtime";
        try
        {
            ulong changeStamp = 0;
            int status = RtlQueryFeatureConfiguration(
                unchecked((uint)featureId),
                bootStore ? ConfigurationType.Boot : ConfigurationType.Runtime,
                ref changeStamp,
                out var cfg);
            if (status != StatusSuccess)
                return new FeatureConfigState(featureId, false, 0, 0, store);
            return new FeatureConfigState(
                featureId, true,
                DecodeEnabledState(cfg.CompactState),
                DecodePriority(cfg.CompactState),
                store);
        }
        catch
        {
            // Export missing (very old Windows) or marshalling failure — treat as not found;
            // callers fall back to the blob heuristic.
            return new FeatureConfigState(featureId, false, 0, 0, store);
        }
    }

    /// <summary>Both-store query for every known fallback ID — feeds CLI/diagnostics.</summary>
    public static IReadOnlyList<FeatureConfigState> QueryAllKnownConfigurations()
    {
        var list = new List<FeatureConfigState>();
        foreach (var id in PostBlockFeatureIds)
        {
            list.Add(QueryConfiguration(id, bootStore: true));
            list.Add(QueryConfiguration(id, bootStore: false));
        }
        return list;
    }

    /// <summary>
    /// EXPERIMENTAL native write path — sets the same configuration ViVeTool's
    /// /enable writes (priority User=8, state Enabled, FeatureState+VariantState
    /// operation) in the Runtime AND Boot stores. Requires admin. Callers must keep
    /// this behind an explicit opt-in switch until the path has soaked.
    /// </summary>
    public static FeatureStoreWriteResult WriteOverrides(IEnumerable<int> featureIds)
    {
        var ids = featureIds.Distinct().ToArray();
        if (ids.Length == 0)
            return new FeatureStoreWriteResult { Success = false, Summary = "No feature IDs supplied." };

        var updates = BuildEnableUpdates(ids);
        try
        {
            foreach (var type in new[] { ConfigurationType.Runtime, ConfigurationType.Boot })
            {
                ulong changeStamp = 0;
                int status = RtlSetFeatureConfigurations(ref changeStamp, type, updates, (uint)updates.Length);
                if (status != StatusSuccess)
                {
                    return new FeatureStoreWriteResult
                    {
                        Success = false,
                        Summary = $"RtlSetFeatureConfigurations({type}) failed with NTSTATUS 0x{status:X8}. " +
                                  "Run elevated; if this persists, use the ViVeTool fallback instead.",
                    };
                }
            }
        }
        catch (Exception ex)
        {
            return new FeatureStoreWriteResult
            {
                Success = false,
                Summary = $"Native feature-configuration write unavailable: {ex.Message}. Use the ViVeTool fallback.",
            };
        }

        // Verify what the store now reports before claiming success.
        var notEnabled = ids.Where(id => !QueryConfiguration(id, bootStore: false).IsEnabled).ToArray();
        if (notEnabled.Length > 0)
        {
            return new FeatureStoreWriteResult
            {
                Success = false,
                Summary = "Write call succeeded but verification shows ID(s) not enabled: " +
                          string.Join(", ", notEnabled) + ". Use the ViVeTool fallback and report this.",
                AppliedIds = ids.Except(notEnabled).ToArray(),
            };
        }

        return new FeatureStoreWriteResult
        {
            Success = true,
            Summary = $"Enabled feature ID(s) {string.Join(", ", ids)} via native Rtl API (Runtime + Boot stores, priority User). Restart to take effect.",
            AppliedIds = ids,
        };
    }

    // Internal for tests: the exact update payload an enable writes.
    internal static (uint FeatureId, uint Priority, uint EnabledState, uint Operation)[] DescribeEnableUpdates(int[] ids)
        => BuildEnableUpdates(ids).Select(u => (u.FeatureId, u.Priority, u.EnabledState, u.Operation)).ToArray();

    private static RTL_FEATURE_CONFIGURATION_UPDATE[] BuildEnableUpdates(int[] ids) =>
        ids.Select(id => new RTL_FEATURE_CONFIGURATION_UPDATE
        {
            FeatureId = unchecked((uint)id),
            Priority = 8,        // User — matches ViVeTool /enable
            EnabledState = 2,    // Enabled
            EnabledStateOptions = 0,
            Variant = 0,
            VariantPayloadKind = 0,
            VariantPayload = 0,
            Operation = 1 | 2,   // FeatureState | VariantState
        }).ToArray();

    /// <summary>
    /// True when any known fallback ID is configured Enabled. Prefers the Rtl query API;
    /// falls back to the legacy FeatureStore blob scan if the native query finds nothing
    /// (covers exotic states the query can't see, and pre-2004 Windows).
    /// </summary>
    public static bool HasFallbackEvidence()
    {
        try
        {
            foreach (var id in PostBlockFeatureIds)
            {
                if (QueryConfiguration(id, bootStore: false).IsEnabled) return true;
                if (QueryConfiguration(id, bootStore: true).IsEnabled) return true;
            }
        }
        catch { }

        return HasBlobEvidence();
    }

    /// <summary>
    /// Legacy heuristic: scans the FeatureStore blob for little-endian occurrences of each
    /// known post-block feature ID.
    /// </summary>
    internal static bool HasBlobEvidence()
    {
        try
        {
            using var hklm = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64);
            using var key = hklm.OpenSubKey(FeatureStoreSubkey);
            if (key is null) return false;
            var blob = key.GetValue(DataValueName) as byte[];
            if (blob is null || blob.Length == 0) return false;
            foreach (var id in PostBlockFeatureIds)
            {
                var bytes = BitConverter.GetBytes(id);
                if (IndexOfBytes(blob, bytes) >= 0) return true;
            }
        }
        catch { }
        return false;
    }

    /// <summary>
    /// Exports the current FeatureStore blob to a .bin file beside the requested path.
    /// Lets users capture evidence before experimenting with the fallback path, and feeds
    /// the support bundle with a snapshot of pre-tweak state.
    /// </summary>
    public static string? ExportBlob(string outputPath)
    {
        try
        {
            using var hklm = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64);
            using var key = hklm.OpenSubKey(FeatureStoreSubkey);
            var blob = key?.GetValue(DataValueName) as byte[];
            if (blob is null || blob.Length == 0) return null;
            var dir = Path.GetDirectoryName(outputPath);
            if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir)) Directory.CreateDirectory(dir);
            File.WriteAllBytes(outputPath, blob);
            // Also emit an ASCII hex dump next to the .bin for eyeball inspection.
            var dumpPath = outputPath + ".hex.txt";
            File.WriteAllText(dumpPath, HexDump(blob), new UTF8Encoding(false));
            return outputPath;
        }
        catch { return null; }
    }

    internal static int IndexOfBytes(byte[] haystack, byte[] needle)
    {
        if (needle.Length == 0 || haystack.Length < needle.Length) return -1;
        for (int i = 0; i <= haystack.Length - needle.Length; i++)
        {
            bool match = true;
            for (int j = 0; j < needle.Length; j++)
            {
                if (haystack[i + j] != needle[j]) { match = false; break; }
            }
            if (match) return i;
        }
        return -1;
    }

    private static string HexDump(byte[] data)
    {
        var sb = new StringBuilder(data.Length * 4);
        for (int i = 0; i < data.Length; i += 16)
        {
            sb.Append(i.ToString("x8")).Append("  ");
            for (int j = 0; j < 16 && i + j < data.Length; j++)
                sb.Append(data[i + j].ToString("x2")).Append(' ');
            sb.AppendLine();
        }
        return sb.ToString();
    }
}
