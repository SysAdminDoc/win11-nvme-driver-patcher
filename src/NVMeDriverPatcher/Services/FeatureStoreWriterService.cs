using System.IO;
using System.Text;
using Microsoft.Win32;

namespace NVMeDriverPatcher.Services;

public class FeatureStoreWriteResult
{
    public bool Success { get; set; }
    public string Summary { get; set; } = string.Empty;
    public int[] AppliedIds { get; set; } = Array.Empty<int>();
}

// Native FeatureStore writer stub (ROADMAP §3.1). ViVeTool writes to an undocumented
// protobuf-ish blob under HKLM\...\FeatureManagement\FeatureStore. A full reimplementation
// is out of scope until we fully reverse-engineer the encoding; this service:
//
//   1. Surfaces the known FeatureStore registry path and the two fallback IDs (60786016,
//      48433719) so the rest of the app can reason about "did ViVeTool run?" without
//      reading ViVeTool's own on-disk state.
//   2. Provides a thin HasFallbackEvidence probe that checks whether the FeatureStore
//      blob contains either ID's little-endian representation — good-enough to render
//      "post-block fallback active" in the GUI without shelling out to ViVeTool.exe.
//
// When the native writer lands, `WriteOverrides` will grow the real encoder. For now it
// returns NotImplemented so callers have a clear seam to light up later.
public static class FeatureStoreWriterService
{
    public const string FeatureStoreSubkey =
        @"SYSTEM\CurrentControlSet\Control\FeatureManagement\Overrides";

    public const string DataValueName = "FeatureStore";

    public static readonly int[] PostBlockFeatureIds = { 60786016, 48433719 };

    public static FeatureStoreWriteResult WriteOverrides(IEnumerable<int> featureIds)
    {
        // Placeholder until the FeatureStore protobuf encoder lands. Keeping the method
        // signature stable so wiring elsewhere doesn't churn when the real implementation
        // arrives.
        return new FeatureStoreWriteResult
        {
            Success = false,
            Summary = "Native FeatureStore writer not yet implemented — use ViVeToolService.ApplyFallbackAsync.",
            AppliedIds = Array.Empty<int>()
        };
    }

    /// <summary>
    /// Scans the FeatureStore blob for little-endian occurrences of each of the known
    /// post-block feature IDs. Returns true if ANY are present — enough to distinguish
    /// "ViVeTool path was used at some point" from "override path is the only route in play."
    /// </summary>
    public static bool HasFallbackEvidence()
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
