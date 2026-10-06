using System.IO;
using System.Text;
using System.Text.Json;
using NVMeDriverPatcher.Models;

namespace NVMeDriverPatcher.Services;

public sealed class FirmwareUpdatePendingState
{
    public string Profile { get; set; } = nameof(PatchProfile.Safe);
    public string DisabledAt { get; set; } = string.Empty;
    // Null in markers written before these were recorded; re-enable then keeps the config's values.
    public bool? IncludeServerKey { get; set; }
    public bool? IncludeStandaloneFuture { get; set; }
}

// Guides the "temporarily disable for firmware update" workflow. Vendor SSD tools (Samsung
// Magician, WD Dashboard, Crucial Storage Executive) can't see drives while nvmedisk.sys is
// active because the disk identity changes (GenNvmeDisk vs GenDisk). disable-for-update reverts
// to the legacy stack and remembers the active profile in a marker file; re-enable-after-update
// re-applies that exact profile and clears the marker.
public static class FirmwareUpdateWorkflowService
{
    private const string MarkerFile = "firmware_update_pending.json";
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    public static string MarkerPath(AppConfig config)
    {
        var dir = string.IsNullOrWhiteSpace(config.WorkingDir) ? AppConfig.GetWorkingDir() : config.WorkingDir;
        return Path.Combine(dir, MarkerFile);
    }

    public static void WriteMarker(
        AppConfig config,
        PatchProfile profile,
        string disabledAtIso,
        bool? includeServerKey = null,
        bool? includeStandaloneFuture = null)
    {
        var path = MarkerPath(config);
        var dir = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);

        var state = new FirmwareUpdatePendingState
        {
            Profile = profile.ToString(),
            DisabledAt = disabledAtIso,
            IncludeServerKey = includeServerKey ?? config.IncludeServerKey,
            IncludeStandaloneFuture = includeStandaloneFuture ?? config.IncludeStandaloneFuture
        };
        var json = JsonSerializer.Serialize(state, JsonOptions);
        var tmp = path + ".tmp";
        using (var fs = new FileStream(tmp, FileMode.Create, FileAccess.Write, FileShare.None))
        using (var sw = new StreamWriter(fs, new UTF8Encoding(false)))
        {
            sw.Write(json);
            sw.Flush();
            fs.Flush(flushToDisk: true);
        }
        File.Move(tmp, path, overwrite: true);
    }

    public static FirmwareUpdatePendingState? ReadMarker(AppConfig config)
    {
        try
        {
            var path = MarkerPath(config);
            if (!File.Exists(path)) return null;
            return JsonSerializer.Deserialize<FirmwareUpdatePendingState>(File.ReadAllText(path));
        }
        catch { return null; }
    }

    public static void ClearMarker(AppConfig config)
    {
        try
        {
            var path = MarkerPath(config);
            if (File.Exists(path)) File.Delete(path);
        }
        catch { /* best-effort */ }
    }

    /// <summary>
    /// Pure: which profile to re-apply on re-enable. A valid marker wins; otherwise fall back to
    /// the config's current profile (manual re-enable with no marker). Returns whether a marker
    /// was honored so the caller can tell the user it's re-applying the current profile.
    /// </summary>
    public static (PatchProfile Profile, bool HadMarker) ResolveReEnableProfile(
        FirmwareUpdatePendingState? marker, AppConfig config)
    {
        if (marker is not null
            && Enum.TryParse<PatchProfile>(marker.Profile, ignoreCase: true, out var parsed)
            && Enum.IsDefined(typeof(PatchProfile), parsed))
            return (parsed, true);
        return (config.PatchProfile, false);
    }

    /// <summary>
    /// What's installed right now, read from the override values that are set. Settings can change
    /// after an apply without another apply (the GUI saves a checkbox on click), so the marker
    /// records this instead. With the primary value absent there's nothing installed to go by,
    /// and the settings are the best answer.
    /// </summary>
    public static (PatchProfile Profile, bool IncludeServerKey, bool IncludeStandaloneFuture) InstalledSelection(
        IReadOnlyCollection<string> setValueNames, bool serverKeySet, AppConfig config)
    {
        if (!setValueNames.Contains(AppConfig.PrimaryFeatureID))
            return (config.PatchProfile, config.IncludeServerKey, config.IncludeStandaloneFuture);
        bool full = setValueNames.Contains(AppConfig.ExtendedFeatureIDs[0]);
        return (full ? PatchProfile.Full : PatchProfile.Safe, serverKeySet,
                full && setValueNames.Contains(AppConfig.StandaloneFutureFeatureID));
    }

    /// <summary>Puts back the optional keys recorded at disable time, so re-enable writes the same set.</summary>
    public static void RestoreMarkedOptions(FirmwareUpdatePendingState? marker, AppConfig config)
    {
        if (marker?.IncludeServerKey is bool server) config.IncludeServerKey = server;
        if (marker?.IncludeStandaloneFuture is bool future) config.IncludeStandaloneFuture = future;
    }

    /// <summary>
    /// Pure rendering of the disable-time instructions: per detected NVMe drive, the vendor's
    /// firmware-update guide/tool link. Empty drive list still produces a clear message.
    /// </summary>
    public static string BuildDisableInstructions(IReadOnlyList<FirmwareUpdateNudge> nudges)
    {
        var sb = new StringBuilder();
        sb.AppendLine("Native NVMe (nvmedisk.sys) is now disabled. Your drives are back on the legacy");
        sb.AppendLine("stack so vendor tools can detect them. Update firmware, then run 're-enable-after-update'");
        sb.AppendLine("to restore the same profile.");
        if (nudges.Count == 0)
        {
            sb.AppendLine("  No NVMe drives detected to map to a vendor tool.");
            return sb.ToString().TrimEnd();
        }
        foreach (var n in nudges)
        {
            var fw = string.IsNullOrWhiteSpace(n.CurrentFirmware) ? "firmware unknown" : n.CurrentFirmware;
            sb.AppendLine($"  {n.DriveModel} ({fw})");
            sb.AppendLine($"    Vendor: {n.Vendor}");
            sb.AppendLine($"    Firmware update guide / tool: {(string.IsNullOrWhiteSpace(n.HowToUpdateUrl) ? "(no vendor page mapped)" : n.HowToUpdateUrl)}");
        }
        return sb.ToString().TrimEnd();
    }
}
