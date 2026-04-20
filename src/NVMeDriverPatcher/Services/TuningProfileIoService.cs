using System.IO;
using System.Text;
using System.Text.Json;
using NVMeDriverPatcher.Models;

namespace NVMeDriverPatcher.Services;

public class TuningProfileBundle
{
    public int SchemaVersion { get; set; } = 1;
    public string Name { get; set; } = "Custom";
    public string ExportedAt { get; set; } = DateTime.UtcNow.ToString("o");
    public TuningProfile? Profile { get; set; }
}

// Named-profile import/export so sysadmins can share a curated tuning config across machines.
// Complements TuningService which already reads/writes live registry parameters. Closes ROADMAP §1.2.
public static class TuningProfileIoService
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    public static string Export(string name, TuningProfile profile, string outputPath)
    {
        var bundle = new TuningProfileBundle
        {
            Name = string.IsNullOrWhiteSpace(name) ? "Custom" : name,
            Profile = profile
        };
        var dir = Path.GetDirectoryName(outputPath);
        if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir)) Directory.CreateDirectory(dir);
        var tmp = outputPath + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(bundle, JsonOptions), new UTF8Encoding(false));
        File.Move(tmp, outputPath, overwrite: true);
        return outputPath;
    }

    public static (TuningProfile? Profile, string Summary) Import(string inputPath)
    {
        try
        {
            if (!File.Exists(inputPath)) return (null, "Profile file not found.");
            var json = File.ReadAllText(inputPath);
            var bundle = JsonSerializer.Deserialize<TuningProfileBundle>(json);
            if (bundle?.Profile is null) return (null, "Profile file is empty or unparseable.");
            return (bundle.Profile, $"Imported profile '{bundle.Name}' (schema {bundle.SchemaVersion}).");
        }
        catch (Exception ex)
        {
            return (null, $"Import failed: {ex.GetType().Name}: {ex.Message}");
        }
    }
}
