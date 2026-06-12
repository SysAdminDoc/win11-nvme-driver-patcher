using System.IO;
using System.Security.Cryptography;
using System.Text.Json;
using NVMeDriverPatcher.Models;

namespace NVMeDriverPatcher.Services;

public static class DataFileProvenanceService
{
    public const int DefaultStaleAfterDays = 30;

    public static List<DataFileProvenance> InspectAll(string? workingDir = null, int staleAfterDays = DefaultStaleAfterDays)
    {
        return
        [
            InspectWindowsBuildRules(workingDir, staleAfterDays),
            InspectFirmwareCompat(workingDir, staleAfterDays)
        ];
    }

    public static DataFileProvenance InspectWindowsBuildRules(string? workingDir = null, int staleAfterDays = DefaultStaleAfterDays) =>
        Inspect("Windows build rules", "windows_build_rules.json", workingDir, AppContext.BaseDirectory, staleAfterDays);

    public static DataFileProvenance InspectFirmwareCompat(string? workingDir = null, int staleAfterDays = DefaultStaleAfterDays) =>
        Inspect("Firmware compatibility DB", "compat.json", workingDir, AppContext.BaseDirectory, staleAfterDays);

    internal static DataFileProvenance Inspect(
        string name,
        string fileName,
        string? workingDir,
        string? shippedDir,
        int staleAfterDays)
    {
        var shippedPath = string.IsNullOrWhiteSpace(shippedDir) ? string.Empty : Path.Combine(shippedDir, fileName);
        var localPath = string.IsNullOrWhiteSpace(workingDir) ? string.Empty : Path.Combine(workingDir, fileName);
        var activePath = !string.IsNullOrWhiteSpace(localPath) && File.Exists(localPath) ? localPath : shippedPath;
        var sourceKind = string.Equals(activePath, localPath, StringComparison.OrdinalIgnoreCase)
            ? "local override"
            : "bundled default";

        var result = new DataFileProvenance
        {
            Name = name,
            FileName = fileName,
            ActivePath = activePath,
            SourceKind = sourceKind,
            Exists = !string.IsNullOrWhiteSpace(activePath) && File.Exists(activePath),
            StaleAfterDays = staleAfterDays
        };

        if (!result.Exists)
        {
            result.Summary = $"{name}: missing {fileName}.";
            return result;
        }

        try
        {
            result.Sha256 = HashFile(activePath);
            if (!string.IsNullOrWhiteSpace(shippedPath) && File.Exists(shippedPath))
                result.ShippedSha256 = HashFile(shippedPath);
            result.IsCustomized = !string.IsNullOrWhiteSpace(result.ShippedSha256) &&
                !string.Equals(result.Sha256, result.ShippedSha256, StringComparison.OrdinalIgnoreCase);

            using var doc = JsonDocument.Parse(File.ReadAllText(activePath));
            var root = doc.RootElement;
            if (root.TryGetProperty("schemaVersion", out var schema) && schema.ValueKind == JsonValueKind.Number)
                result.SchemaVersion = schema.GetInt32();
            if (root.TryGetProperty("updated", out var updated) && updated.ValueKind == JsonValueKind.String)
                result.Updated = updated.GetString() ?? string.Empty;

            result.NewestLastReviewed = NewestDate(EnumerateDateStrings(root, "lastReviewed").Append(result.Updated));
            result.IsStale = IsStale(result.NewestLastReviewed, staleAfterDays);
            result.Summary = BuildSummary(result);
        }
        catch (Exception ex)
        {
            result.Summary = $"{name}: provenance read failed ({ex.GetType().Name}).";
        }

        return result;
    }

    public static string DescribeForPreflight(IEnumerable<DataFileProvenance> files)
    {
        var list = files.ToList();
        if (list.Count == 0)
            return "No data-file provenance available";

        var stale = list.Where(f => f.IsStale || !f.Exists).ToList();
        if (stale.Count > 0)
            return string.Join("; ", stale.Select(f => f.Summary));

        return string.Join("; ", list.Select(f =>
            $"{f.FileName}: {f.SourceKind}, schema {f.SchemaVersion}, reviewed {DisplayDate(f.NewestLastReviewed)}, sha256 {ShortHash(f.Sha256)}"));
    }

    public static string RenderForDiagnostics(IEnumerable<DataFileProvenance> files, bool includePath)
    {
        var lines = new List<string>();
        foreach (var f in files)
        {
            lines.Add($"{f.Name} ({f.FileName})");
            lines.Add($"  Source: {f.SourceKind}");
            if (includePath)
                lines.Add($"  Active path: {f.ActivePath}");
            lines.Add($"  Schema: {f.SchemaVersion}");
            lines.Add($"  Updated: {DisplayDate(f.Updated)}");
            lines.Add($"  Newest reviewed: {DisplayDate(f.NewestLastReviewed)}");
            lines.Add($"  SHA-256: {f.Sha256}");
            if (!string.IsNullOrWhiteSpace(f.ShippedSha256))
                lines.Add($"  Shipped SHA-256: {f.ShippedSha256}");
            lines.Add($"  Customized: {(f.IsCustomized ? "yes" : "no")}");
            lines.Add($"  Freshness: {(f.IsStale ? $"STALE (>{f.StaleAfterDays} days)" : "fresh")}");
        }
        return string.Join(Environment.NewLine, lines);
    }

    private static string BuildSummary(DataFileProvenance file)
    {
        var freshness = file.IsStale
            ? $"STALE: reviewed {DisplayDate(file.NewestLastReviewed)} (>{file.StaleAfterDays} days)"
            : $"fresh: reviewed {DisplayDate(file.NewestLastReviewed)}";
        var custom = file.IsCustomized ? ", customized" : string.Empty;
        return $"{file.FileName}: {file.SourceKind}{custom}, schema {file.SchemaVersion}, {freshness}, sha256 {ShortHash(file.Sha256)}.";
    }

    private static bool IsStale(string date, int staleAfterDays)
    {
        if (!DateTime.TryParse(date, out var parsed))
            return true;

        var age = DateTime.UtcNow.Date - parsed.ToUniversalTime().Date;
        return age.TotalDays > staleAfterDays;
    }

    private static IEnumerable<string> EnumerateDateStrings(JsonElement element, string propertyName)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            foreach (var property in element.EnumerateObject())
            {
                if (property.NameEquals(propertyName) && property.Value.ValueKind == JsonValueKind.String)
                    yield return property.Value.GetString() ?? string.Empty;

                foreach (var value in EnumerateDateStrings(property.Value, propertyName))
                    yield return value;
            }
        }
        else if (element.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in element.EnumerateArray())
            {
                foreach (var value in EnumerateDateStrings(item, propertyName))
                    yield return value;
            }
        }
    }

    private static string NewestDate(IEnumerable<string> dates)
    {
        DateTime? newest = null;
        foreach (var date in dates)
        {
            if (!DateTime.TryParse(date, out var parsed))
                continue;

            var utc = parsed.ToUniversalTime();
            if (newest is null || utc > newest.Value)
                newest = utc;
        }

        return newest?.ToString("yyyy-MM-dd") ?? string.Empty;
    }

    private static string HashFile(string path)
    {
        using var fs = File.OpenRead(path);
        using var sha = SHA256.Create();
        return Convert.ToHexString(sha.ComputeHash(fs)).ToLowerInvariant();
    }

    private static string DisplayDate(string? date) =>
        string.IsNullOrWhiteSpace(date) ? "unknown" : date;

    private static string ShortHash(string? hash) =>
        string.IsNullOrWhiteSpace(hash) ? "missing" : hash[..Math.Min(12, hash.Length)];
}
