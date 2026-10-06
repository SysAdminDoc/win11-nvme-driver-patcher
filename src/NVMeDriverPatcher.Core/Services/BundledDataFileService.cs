using System.IO;
using System.Security.Cryptography;
using System.Text;

namespace NVMeDriverPatcher.Services;

/// <summary>
/// The curated data files (build rules, feature IDs, firmware compat) ship twice: loose beside the
/// executable, where the MSI installs them and an admin can read them, and embedded in Core. A
/// single-file publish never bundles the loose copies, so the bare exe handed out by Scoop,
/// Chocolatey and a direct download would otherwise run with no build rules, an empty feature
/// catalog and no compat list. Loaders keep their precedence (admin override, then the file beside
/// the exe) and use the embedded copy only when neither is usable.
/// </summary>
public static class BundledDataFileService
{
    public const string EmbeddedSourceKind = "embedded default";

    internal static string ResourceName(string fileName) => "NVMeDriverPatcher." + fileName;

    /// <summary>What provenance output shows in place of a file path for the embedded copy.</summary>
    public static string EmbeddedDisplayPath(string fileName) =>
        $"(built into the app) {fileName}";

    public static byte[]? ReadEmbeddedBytes(string fileName)
    {
        try
        {
            using var stream = typeof(BundledDataFileService).Assembly
                .GetManifestResourceStream(ResourceName(fileName));
            if (stream is null) return null;
            using var buffer = new MemoryStream();
            stream.CopyTo(buffer);
            return buffer.ToArray();
        }
        catch
        {
            return null;
        }
    }

    public static string? ReadEmbeddedText(string fileName)
    {
        var bytes = ReadEmbeddedBytes(fileName);
        if (bytes is null) return null;
        // Same BOM handling as File.ReadAllText, so the loose and embedded copies parse alike.
        using var reader = new StreamReader(new MemoryStream(bytes), Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
        return reader.ReadToEnd();
    }

    public static string? EmbeddedSha256(string fileName)
    {
        var bytes = ReadEmbeddedBytes(fileName);
        return bytes is null ? null : Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
    }
}
