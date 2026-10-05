using System.IO;
using System.Security.Cryptography;

namespace NVMeDriverPatcher.Services;

public class CompatChecksumResult
{
    public bool ShippedDefault { get; set; }
    public string Sha256 { get; set; } = string.Empty;
    public string ShippedSha256 { get; set; } = string.Empty;
    public string Summary { get; set; } = string.Empty;
}

// SHA-256 of the loaded compat.json compared against the SHA-256 of the shipped default
// (app base dir). Flags when the user has edited their local copy so support bundles carry
// a "this user is running a custom compat table" breadcrumb.
public static class CompatChecksumService
{
    /// <param name="embeddedFileName">When set and no loose shipped copy exists (a bare single-file
    /// download), the copy embedded in Core stands in as the shipped default.</param>
    public static CompatChecksumResult Verify(
        string? workingDirCompatPath,
        string shippedCompatPath,
        string? embeddedFileName = null)
    {
        var result = new CompatChecksumResult();
        try
        {
            bool localExists = !string.IsNullOrEmpty(workingDirCompatPath) && File.Exists(workingDirCompatPath);
            bool shippedExists = File.Exists(shippedCompatPath);
            string? embeddedSha = !shippedExists && !string.IsNullOrEmpty(embeddedFileName)
                ? BundledDataFileService.EmbeddedSha256(embeddedFileName)
                : null;
            if (!localExists && !shippedExists && embeddedSha is null)
            {
                result.Summary = "No compat.json present.";
                return result;
            }
            result.Sha256 = localExists
                ? HashFile(workingDirCompatPath!)
                : shippedExists ? HashFile(shippedCompatPath) : embeddedSha!;
            result.ShippedSha256 = shippedExists ? HashFile(shippedCompatPath) : embeddedSha ?? string.Empty;
            result.ShippedDefault = !string.IsNullOrEmpty(result.ShippedSha256) &&
                string.Equals(result.Sha256, result.ShippedSha256, StringComparison.OrdinalIgnoreCase);
            result.Summary = result.ShippedDefault
                ? "compat.json matches the shipped default."
                : "compat.json has been customized locally.";
        }
        catch (Exception ex)
        {
            result.Summary = $"Checksum failed: {ex.Message}";
        }
        return result;
    }

    private static string HashFile(string path)
    {
        using var fs = File.OpenRead(path);
        using var sha = SHA256.Create();
        var bytes = sha.ComputeHash(fs);
        var hex = new System.Text.StringBuilder(bytes.Length * 2);
        foreach (var b in bytes) hex.Append(b.ToString("x2"));
        return hex.ToString();
    }
}
