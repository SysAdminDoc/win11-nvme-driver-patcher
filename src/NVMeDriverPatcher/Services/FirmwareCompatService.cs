using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using NVMeDriverPatcher.Models;

namespace NVMeDriverPatcher.Services;

public enum FirmwareCompatLevel
{
    Unknown,
    Good,
    Caution,
    Bad
}

public class FirmwareCompatEntry
{
    [JsonPropertyName("controller")]
    public string Controller { get; set; } = string.Empty;

    // Firmware versions are matched case-insensitively. A "*" entry applies to any firmware.
    [JsonPropertyName("firmware")]
    public string Firmware { get; set; } = "*";

    [JsonPropertyName("level")]
    public FirmwareCompatLevel Level { get; set; } = FirmwareCompatLevel.Unknown;

    [JsonPropertyName("note")]
    public string Note { get; set; } = string.Empty;
}

public class FirmwareCompatDatabase
{
    [JsonPropertyName("schemaVersion")]
    public int SchemaVersion { get; set; } = 1;

    [JsonPropertyName("updated")]
    public string Updated { get; set; } = string.Empty;

    [JsonPropertyName("entries")]
    public List<FirmwareCompatEntry> Entries { get; set; } = new();
}

public class FirmwareCompatFinding
{
    public string DriveModel { get; set; } = string.Empty;
    public string Firmware { get; set; } = string.Empty;
    public FirmwareCompatLevel Level { get; set; } = FirmwareCompatLevel.Unknown;
    public string Note { get; set; } = string.Empty;
}

// Maps {controller model, firmware version} → {Good, Caution, Bad, Unknown} against a
// shipped compat.json (refreshable via GitHub). Feeds the preflight card so users on a
// firmware known to BSOD under nvmedisk.sys get blocked (or at least warned loudly)
// before they apply.
public static class FirmwareCompatService
{
    private const string BundledCompatFile = "compat.json";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        Converters = { new JsonStringEnumConverter() }
    };

    public static FirmwareCompatDatabase LoadDatabase(string? workingDir = null)
    {
        // Resolve order:
        //   1. %LocalAppData%\NVMePatcher\compat.json (user-editable, takes precedence)
        //   2. <app exe dir>\compat.json (shipped default)
        //   3. Empty DB with a "loaded fallback" note so callers can render an honest UI.
        var candidates = new List<string>();
        var workDir = workingDir ?? AppConfig.GetWorkingDir();
        if (!string.IsNullOrEmpty(workDir)) candidates.Add(Path.Combine(workDir, BundledCompatFile));
        try
        {
            var appDir = AppContext.BaseDirectory;
            if (!string.IsNullOrEmpty(appDir)) candidates.Add(Path.Combine(appDir, BundledCompatFile));
        }
        catch { }

        foreach (var path in candidates)
        {
            try
            {
                if (!File.Exists(path)) continue;
                var json = File.ReadAllText(path);
                if (string.IsNullOrWhiteSpace(json)) continue;
                var db = JsonSerializer.Deserialize<FirmwareCompatDatabase>(json, JsonOptions);
                if (db is not null && db.Entries.Count > 0) return db;
            }
            catch { /* try next candidate */ }
        }

        return new FirmwareCompatDatabase
        {
            SchemaVersion = 1,
            Updated = DateTime.UtcNow.ToString("yyyy-MM-dd"),
            Entries = new List<FirmwareCompatEntry>()
        };
    }

    /// <summary>
    /// Match a drive's (model, firmware) against the compat DB. Returns an Unknown finding
    /// if no entry matches — which is the right default, since absence of evidence is not
    /// evidence of absence for obscure OEM drives.
    /// </summary>
    public static FirmwareCompatFinding Lookup(FirmwareCompatDatabase db, string driveModel, string firmware)
    {
        var normalizedDriveModel = driveModel ?? string.Empty;
        var normalizedFirmware = firmware ?? string.Empty;
        var finding = new FirmwareCompatFinding
        {
            DriveModel = normalizedDriveModel,
            Firmware = normalizedFirmware,
            Level = FirmwareCompatLevel.Unknown,
            Note = string.Empty
        };

        if (db?.Entries is null || db.Entries.Count == 0)
            return finding;

        // Prefer exact firmware match over wildcard. "Bad" beats "Caution" beats "Good" when
        // multiple entries match the same firmware (defense in depth — the worst match wins).
        FirmwareCompatEntry? best = null;
        int bestScore = -1;
        foreach (var entry in db.Entries)
        {
            if (!MatchesController(entry.Controller ?? string.Empty, normalizedDriveModel)) continue;
            if (!MatchesFirmware(entry.Firmware ?? string.Empty, normalizedFirmware)) continue;
            int score = ScoreEntry(entry, normalizedFirmware);
            if (score > bestScore)
            {
                bestScore = score;
                best = entry;
            }
        }
        if (best is not null)
        {
            finding.Level = best.Level;
            finding.Note = best.Note;
        }
        return finding;
    }

    internal static bool MatchesController(string pattern, string model)
    {
        if (string.IsNullOrWhiteSpace(pattern) || pattern == "*") return true;
        if (string.IsNullOrWhiteSpace(model)) return false;
        // Loose contains-match lowercased. Controllers have inconsistent naming
        // (e.g. "Samsung SSD 990 PRO 2TB" vs "SAMSUNG MZ-V9P2T0"), so contains is the
        // pragmatic choice. Patterns with a leading "=" are treated as exact.
        if (pattern.StartsWith("="))
            return string.Equals(pattern[1..], model, StringComparison.OrdinalIgnoreCase);
        return model.Contains(pattern, StringComparison.OrdinalIgnoreCase);
    }

    internal static bool MatchesFirmware(string pattern, string firmware)
    {
        if (string.IsNullOrWhiteSpace(pattern) || pattern == "*") return true;
        if (string.IsNullOrWhiteSpace(firmware)) return false;
        if (pattern.StartsWith("="))
            return string.Equals(pattern[1..], firmware, StringComparison.OrdinalIgnoreCase);
        return string.Equals(pattern, firmware, StringComparison.OrdinalIgnoreCase);
    }

    internal static int ScoreEntry(FirmwareCompatEntry entry, string firmware)
    {
        // Exact firmware match scores higher than wildcard. Within the same match quality,
        // the severity ranking ensures the worst level wins ties.
        int firmwareScore = string.IsNullOrWhiteSpace(firmware) ? 0
            : string.Equals(entry.Firmware, firmware, StringComparison.OrdinalIgnoreCase) ? 10 : 0;
        int severityScore = entry.Level switch
        {
            FirmwareCompatLevel.Bad => 3,
            FirmwareCompatLevel.Caution => 2,
            FirmwareCompatLevel.Good => 1,
            _ => 0
        };
        return firmwareScore * 10 + severityScore;
    }
}
