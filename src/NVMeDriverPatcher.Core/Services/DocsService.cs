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
  bypassio         DirectStorage / BypassIO gaming impact.
  vivetool         The post-block ViVeTool fallback path.
  buildrules       Windows build support matrix.
  firmware         Controller/firmware compatibility hints.
  gpo              Group Policy / ADMX deployment for fleets.
  portable         Portable-mode deployment.
  telemetry        The opt-in compat telemetry payload.
  featureflags     The native Feature flags page on Windows 11 26300+.
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
4. Driver method (force-loaded). If nvmedisk.sys was forced via Device Manager or PnPUtil
   (no registry keys / fallback flags — `status` shows enablement source 'untracked'), the
   .reg/.bat will NOT revert it. Revert in Device Manager: Disk drives > your NVMe > Update
   driver > Browse > Let me pick > select the Standard NVM Express Controller / stornvme.
",
        ["watchdog"] = @"
The post-patch watchdog counts storage-stack distress signals (Storport 129, disk 51/153,
BugCheck 1001, Kernel-Power 41) inside a user-configurable window (default 48h). If the
count crosses the revert threshold AND AutoRevertEnabled is true, the next-boot
AutoRevertService stages an uninstall. Tune via HKLM Policies or the CLI's
`register-tasks` + watchdog.json.

Storport Event ID 129 means a command timeout / device reset. Treat repeated
command timeout (Storport 129) events as a strong revert signal, especially when paired
with disk 51/153 paging or reset events.
",
        ["bypassio"] = @"
nvmedisk.sys vetoes BypassIO, so DirectStorage titles such as Ratchet & Clank: Rift Apart,
Forspoken, Forza Motorsport, and Horizon Forbidden West can fall back to legacy I/O with
higher CPU use or stutter. Keep game-library drives on stornvme.sys with per-drive scope
when gaming performance matters. EasyAntiCheat's EOSSys.sys can also veto BypassIO
independently, so an EOSSys.sys blocker is separate from the storage-driver choice.
",
        ["vivetool"] = @"
Microsoft silently neutered the FeatureManagement override path on early 2026 Insider
builds. The fallback writes build-specific feature IDs to FeatureStore instead:
24H2 post-block builds use 60786016 + 48433719, while 25H2 26200 builds below
UBR 8524 use 55369237 + 48433719 + 49453572. Build 26200.8524+, 26201-26299,
and 26300+ have no known registry or fallback route that binds GenNvmeDisk; treat
this tool as verify/monitor/rollback-only there. `featurestore` probes whether the
fallback has been applied.
",
        ["buildrules"] = @"
The app and CLI load windows_build_rules.json at runtime before recommending an enablement
path. Current client buckets:

* 24H2 26100.0-26100.3774: registry override route on known builds.
* 24H2 26100.3775+: FeatureManagement keys may write but not bind; use fallback and verify.
* 25H2 26200.0-26200.8523: registry override is blocked; use the 25H2 FeatureStore fallback.
* 25H2 26200.8524+ and 26201-26299: verify/monitor/rollback only; no known bind path.
* 26300+: check Settings > Windows Update > Windows Insider Program > Feature flags first;
  registry and fallback routes are not expected to bind.

Server 2025 has a separate official opt-in path. Run `status` or preflight on the target
machine and prefer that result over static docs if Microsoft changes build behavior.
",
        ["firmware"] = @"
compat.json ships a curated `{controller, firmware} -> {Good, Caution, Bad, Unknown}`
map. Preflight surfaces hits. Drop a custom compat.json at
%ProgramData%\NVMePatcher\ to override the shipped defaults. Use `firmware` CLI to
inspect the active DB; `compat-checksum` flags when a local copy differs from shipped.
",
        ["gpo"] = @"
packaging/admx/NVMeDriverPatcher.admx + en-US/*.adml provide Group Policy templates.
Copy the ADMX to C:\Windows\PolicyDefinitions\ (the ADML to the en-US\ subfolder).
Policies land under HKLM\SOFTWARE\Policies\SysAdminDoc\NVMeDriverPatcher and override
shared config.json. Six policies: PatchProfile, IncludeServerKey, SkipWarnings,
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
        ["featureflags"] = @"
Starting with Insider build 26300.8155, Windows 11 has a built-in 'Feature flags' page
under Settings > Windows Update > Windows Insider Program. If you are on build 26300 or
newer, check there FIRST: Microsoft may expose native NVMe as an official, supported toggle.
An official toggle is always preferable to this tool's overrides — on these builds the
registry and ViVeTool routes do not bind the driver anyway (the GenNvmeDisk compatible ID
was removed). If a native NVMe flag appears on that page, use it and treat this tool as a
verify/monitor/rollback helper rather than the enabler.
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
