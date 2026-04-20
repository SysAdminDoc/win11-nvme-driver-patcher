using System.Text;

namespace NVMeDriverPatcher.Services;

// Offline doc reference — `NVMeDriverPatcher.Cli docs [topic]` prints a focused section so
// a sysadmin working on a locked-down box without browser access can still answer their
// own questions. Content is curated, not generated — each topic is a short, stable answer
// to one recurring question from the support queue.
public static class DocsService
{
    private static readonly Dictionary<string, string> Topics = new(StringComparer.OrdinalIgnoreCase)
    {
        ["index"] = @"
Available topics:
  overview         What this tool does and why.
  profiles         Safe vs Full patch profiles.
  recovery         How to recover if the system won't boot.
  watchdog         The post-patch stability watchdog + auto-revert.
  vivetool         The post-block ViVeTool fallback path.
  firmware         Controller/firmware compatibility hints.
  gpo              Group Policy / ADMX deployment for fleets.
  portable         Portable-mode deployment.
  telemetry        The opt-in compat telemetry payload.
  uninstall        Removing the app cleanly.
",
        ["overview"] = @"
NVMe Driver Patcher enables the Server 2025 Native NVMe driver (nvmedisk.sys) on
Windows 11 by writing three feature-management override keys and two SafeBoot keys.
The patch swaps the storage stack from the legacy stornvme.sys to the new native path,
which delivers large gains on 4K random I/O and sequential reads. BypassIO support is
lost in exchange, which affects DirectStorage-aware games.
",
        ["profiles"] = @"
Safe profile (default)    Writes only the primary feature flag (735209102) + both
                          SafeBoot keys. Community BSOD reports cluster on the two
                          extended flags — Safe mode avoids them.
Full profile              Adds UxAccOptimization (1853569164) and Standalone_Future
                          (156965516). Higher peak performance on some drives; higher
                          BSOD correlation per early 2026 community threads.

Flip via `apply --safe` / `apply --full` or the GUI's Install Mode radio.
",
        ["recovery"] = @"
1. Recovery Kit (recommended). Generate one before applying; copy it to a USB stick.
   Boot to WinRE, run Remove_NVMe_Patch.bat from the USB. See also: CLI `winpe`.
2. Offline reg edit from WinRE. See the README's 'System won't boot' section for the
   full reg load / reg delete / reg unload sequence.
3. Auto-disable. Windows disables the native driver after 2-3 consecutive failed boots.
",
        ["watchdog"] = @"
The post-patch watchdog counts storage-stack distress signals (Storport 129, disk 51/153,
BugCheck 1001, Kernel-Power 41) inside a user-configurable window (default 48h). If the
count crosses the revert threshold AND AutoRevertEnabled is true, the next-boot
AutoRevertService stages an uninstall. Tune via HKLM Policies or the CLI's
`register-tasks` + watchdog.json.
",
        ["vivetool"] = @"
Microsoft silently neutered the FeatureManagement override path on early 2026 Insider
builds. The ViVeTool fallback writes IDs 60786016 + 48433719 to FeatureStore instead.
Use `NVMeDriverPatcher.Cli fallback` to download ViVeTool, apply, and flag verification
pending. `featurestore` CLI probes whether the fallback has been applied.
",
        ["firmware"] = @"
compat.json ships a curated `{controller, firmware} -> {Good, Caution, Bad, Unknown}`
map. Preflight surfaces hits. Drop a custom compat.json at
%LocalAppData%\NVMePatcher\ to override the shipped defaults. Use `firmware` CLI to
inspect the active DB; `compat-checksum` flags when a local copy differs from shipped.
",
        ["gpo"] = @"
packaging/admx/NVMeDriverPatcher.admx + en-US/*.adml provide Group Policy templates.
Copy the ADMX to C:\Windows\PolicyDefinitions\ (the ADML to the en-US\ subfolder).
Policies land under HKLM\SOFTWARE\Policies\SysAdminDoc\NVMeDriverPatcher and override
per-user config.json. Six policies: PatchProfile, IncludeServerKey, SkipWarnings,
WatchdogAutoRevert, WatchdogWindowHours, CompatTelemetryEnabled.
",
        ["portable"] = @"
Create portable.flag beside the exe (or run `portable-enable`) and the working dir
redirects to Data\ beside the exe. Lets field techs carry the patcher on a USB stick
without installer state. `portable-disable` removes the flag.
",
        ["telemetry"] = @"
Opt-in. Build with `telemetry`; submit with `telemetry --endpoint=<https url>`. No
serials, machine names, drive letters, or user names are sent. Payload:
anonId (per-install GUID), appVersion, osBuild, CPU vendor/family, controllers
(model, firmware, migrated), profile, verification outcome, watchdog counts,
reliability delta, benchmark delta. Server-side reference receiver lives in
packaging/telemetry-receiver/ (Cloudflare Worker).
",
        ["uninstall"] = @"
1. Remove the patch:   `NVMeDriverPatcher.Cli remove`  (restart required)
2. Clean local data:   `NVMeDriverPatcher.Cli clean-data`  (removes logs, ETL, backups, DB)
3. Unregister tasks:   `NVMeDriverPatcher.Cli unregister-tasks`
4. If installed via MSI: use Programs and Features. Otherwise just delete the exe.
"
    };

    public static string Render(string topic)
    {
        if (string.IsNullOrWhiteSpace(topic)) topic = "index";
        if (Topics.TryGetValue(topic, out var content))
            return "# " + topic + Environment.NewLine + content.TrimEnd();
        var sb = new StringBuilder();
        sb.AppendLine($"Unknown topic '{topic}'. Available topics:");
        sb.AppendLine(Topics["index"].TrimEnd());
        return sb.ToString();
    }
}
