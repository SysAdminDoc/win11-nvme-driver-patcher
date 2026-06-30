namespace NVMeDriverPatcher.Cli;

public enum CommandGroup
{
    Lifecycle,
    Recovery,
    Diagnostics,
    StoragePerformance,
    ConfigData,
    FleetAdmin,
    Advanced,
}

public enum RiskLevel
{
    Normal,
    Caution,
    Experimental,
}

public sealed record CliCommandDescriptor(
    string Name,
    string[] Aliases,
    CommandGroup Group,
    string Summary,
    RiskLevel Risk = RiskLevel.Normal,
    string? Options = null);

public static class CliCommandRegistry
{
    public static readonly CliCommandDescriptor[] All =
    {
        // ── Lifecycle ──
        new("status", [], CommandGroup.Lifecycle,
            "Check patch status (exit: 0=applied, 1=not, 2=partial)"),
        new("apply", ["install"], CommandGroup.Lifecycle,
            "Apply the NVMe driver patch (--dry-run to preview, --safe/--full, --force, --no-restart, --unattended)"),
        new("remove", ["uninstall"], CommandGroup.Lifecycle,
            "Remove the NVMe driver patch (--no-restart)"),
        new("disable-for-update", ["disable-for-firmware"], CommandGroup.Lifecycle,
            "Revert to the legacy stack so vendor tools can update SSD firmware (remembers the active profile)",
            RiskLevel.Caution),
        new("re-enable-after-update", ["reenable-after-update"], CommandGroup.Lifecycle,
            "Re-apply the remembered profile after a firmware update (pairs with disable-for-update)"),
        new("dry-run", ["preview"], CommandGroup.Lifecycle,
            "Show exactly what apply would change — no registry writes"),
        new("verify", [], CommandGroup.Lifecycle,
            "Generate post-reboot verification script"),
        new("recovery-proof", [], CommandGroup.Recovery,
            "Check recovery infrastructure readiness (exit: 0=ready, 1=not ready)"),

        // ── Recovery ──
        new("recovery-kit", ["export-recovery-kit"], CommandGroup.Recovery,
            "Generate WinRE recovery kit (.reg + .bat + README)"),
        new("kit-freshness", ["recovery-kit-freshness"], CommandGroup.Recovery,
            "Check recovery kit age and freshness status"),
        new("upgrade-safeboot", ["safeboot-upgrade"], CommandGroup.Recovery,
            "Add KB5079391 service-name SafeBoot entries missing from pre-v4.6.1 patches"),
        new("fallback", ["vivetool-fallback", "apply-fallback"], CommandGroup.Recovery,
            "Apply native FeatureStore fallback first; use ViVeTool only if native write fails"),
        new("winre-inject", ["inject-winre"], CommandGroup.Recovery,
            "Preview or --apply the guarded DISM injection of stornvme.inf into the WinRE image"),

        // ── Diagnostics ──
        new("diagnostics", ["export-diagnostics"], CommandGroup.Diagnostics,
            "Export system diagnostics report (.txt)"),
        new("bundle", ["export-bundle", "support-bundle"], CommandGroup.Diagnostics,
            "Export shareable support bundle (.zip: report + config + crash + regs + db)"),
        new("watchdog", [], CommandGroup.Diagnostics,
            "Read watchdog verdict (exit: 0=healthy, 1=unstable, 2=warning; --auto-revert to arm)"),
        new("watchdog-service", ["service-status"], CommandGroup.Diagnostics,
            "Report real-time service state (exit: 0=running, 2=stopped, 3=not installed)"),
        new("reliability", [], CommandGroup.Diagnostics,
            "Pull Win32_ReliabilityStabilityMetrics, correlate with patch timestamp"),
        new("minidump", ["triage"], CommandGroup.Diagnostics,
            "Scan C:\\Windows\\Minidump for NVMe-stack-referencing dumps since the patch"),
        new("guardrails", [], CommandGroup.Diagnostics,
            "Show AppLocker/SRP, WDAC, HVCI, and VROC guardrail state"),
        new("controllers", ["per-controller"], CommandGroup.Diagnostics,
            "Per-controller NVMe driver audit (bound driver, queue depth)"),
        new("tail", ["events-tail"], CommandGroup.Diagnostics,
            "Live event-log tail (Storport/nvmedisk/disk IDs)"),
        new("physical-disks", [], CommandGroup.Diagnostics,
            "MSFT_PhysicalDisk + StorageReliabilityCounter telemetry"),
        new("bypassio", [], CommandGroup.Diagnostics,
            "Per-volume BypassIO/DirectStorage status with named-game gaming impact (--history for pre/post diff)"),
        new("apst", [], CommandGroup.Diagnostics,
            "APST power-state inspector and current override state"),
        new("identify", [], CommandGroup.Diagnostics,
            "NVMe Identify Controller dump (vendor, model, firmware, features)"),
        new("scope", [], CommandGroup.Diagnostics,
            "List per-drive include/exclude decisions from drive_scope.json"),

        // ── Storage & Performance ──
        new("etw", [], CommandGroup.StoragePerformance,
            "Capture a 60s ETW storage trace (wpr.exe) to %ProgramData%\\NVMePatcher\\etl"),
        new("firmware", ["compat"], CommandGroup.StoragePerformance,
            "List bundled controller/firmware compat entries (compat.json)"),
        new("compare-benchmarks", [], CommandGroup.StoragePerformance,
            "Compare before/after benchmark JSON (--threshold=N%, default 15)"),
        new("compat-checksum", [], CommandGroup.StoragePerformance,
            "Compute and display compat.json integrity checksum"),

        // ── Config & Data ──
        new("config-export", [], CommandGroup.ConfigData,
            "Export current config to --export=<path>"),
        new("config-import", [], CommandGroup.ConfigData,
            "Import config from --import=<path>"),
        new("tuning-export", [], CommandGroup.ConfigData,
            "Export StorNVMe tuning profile to --export=<path>"),
        new("tuning-import", [], CommandGroup.ConfigData,
            "Import StorNVMe tuning profile from --import=<path>"),
        new("clean-data", [], CommandGroup.ConfigData,
            "Purge stale logs, orphaned DBs, and temp files from the data directory",
            RiskLevel.Caution),
        new("verify-backup", [], CommandGroup.ConfigData,
            "Verify a registry backup file's integrity (--import=<path>)"),

        // ── Fleet & Admin ──
        new("register-tasks", [], CommandGroup.FleetAdmin,
            "Register scheduled-task jobs (benchmark regression, firmware nudge)"),
        new("unregister-tasks", [], CommandGroup.FleetAdmin,
            "Remove all NVMe Patcher scheduled tasks"),
        new("policy-install", [], CommandGroup.FleetAdmin,
            "Install ADMX/ADML Group Policy templates into PolicyDefinitions (--source=<dir>, --central-store=<dir>)"),
        new("policy-uninstall", [], CommandGroup.FleetAdmin,
            "Remove the installed ADMX/ADML policy templates (--central-store=<dir>)"),
        new("portable-enable", [], CommandGroup.FleetAdmin,
            "Enable portable mode (data beside the exe, for USB deployment)"),
        new("portable-disable", [], CommandGroup.FleetAdmin,
            "Disable portable mode (revert to ProgramData storage)"),
        new("winpe", [], CommandGroup.FleetAdmin,
            "Build a WinPE recovery USB tree/ISO from the current Recovery Kit (--output=<dir>)"),
        new("winre", [], CommandGroup.FleetAdmin,
            "Probe WinRE BCD and SafeBoot readiness"),
        new("telemetry", [], CommandGroup.FleetAdmin,
            "Build compat report; optional --endpoint=<url> to submit anonymized payload"),
        new("dashboard", ["html-report"], CommandGroup.FleetAdmin,
            "Generate an HTML dashboard report of the current system state"),
        new("fw-nudge", ["firmware-nudge"], CommandGroup.FleetAdmin,
            "Check NVMe firmware versions against known-good baselines"),
        new("maintenance-window", ["window"], CommandGroup.FleetAdmin,
            "Show or configure the maintenance window for scheduled operations"),
        new("update-check", [], CommandGroup.FleetAdmin,
            "Check GitHub releases for a newer version"),
        new("safemode-verify", [], CommandGroup.FleetAdmin,
            "Generate and display a Safe Mode diagnostic script"),

        // ── Advanced / Experimental ──
        new("featurestore", ["feature-store"], CommandGroup.Advanced,
            "Probe, write (--write-native), or undo (--reset-native) native FeatureStore fallback overrides",
            RiskLevel.Experimental),
        new("verifier-on", [], CommandGroup.Advanced,
            "Enable Driver Verifier stress checks on nvmedisk/stornvme/disk (reboot required)",
            RiskLevel.Caution),
        new("verifier-off", [], CommandGroup.Advanced,
            "Disable Driver Verifier on the NVMe stack (reboot required)",
            RiskLevel.Caution),
        new("verifier-status", [], CommandGroup.Advanced,
            "Report whether Driver Verifier is active on the NVMe stack"),
        new("docs", ["help-topic"], CommandGroup.Advanced,
            "Show built-in documentation by topic (e.g. docs safeboot, docs bypassio)"),
        new("accessibility", ["a11y"], CommandGroup.Advanced,
            "Report accessibility/high-contrast/text-scale state"),
    };

    private static readonly HashSet<string> _allTokens = BuildTokenSet();

    private static HashSet<string> BuildTokenSet()
    {
        var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var d in All)
        {
            set.Add(d.Name);
            foreach (var a in d.Aliases) set.Add(a);
        }
        return set;
    }

    public static bool IsKnown(string command) => _allTokens.Contains(command);

    public static CliCommandDescriptor? Find(string command)
    {
        foreach (var d in All)
        {
            if (d.Name.Equals(command, StringComparison.OrdinalIgnoreCase)) return d;
            foreach (var a in d.Aliases)
                if (a.Equals(command, StringComparison.OrdinalIgnoreCase)) return d;
        }
        return null;
    }

    private static readonly (CommandGroup Group, string Label)[] GroupLabels =
    {
        (CommandGroup.Lifecycle, "Lifecycle"),
        (CommandGroup.Recovery, "Recovery"),
        (CommandGroup.Diagnostics, "Diagnostics"),
        (CommandGroup.StoragePerformance, "Storage & Performance"),
        (CommandGroup.ConfigData, "Config & Data"),
        (CommandGroup.FleetAdmin, "Fleet & Admin"),
        (CommandGroup.Advanced, "Advanced"),
    };

    public static string RenderUsage(string version)
    {
        var sb = new System.Text.StringBuilder();
        sb.AppendLine($"NVMe Driver Patcher CLI v{version}");
        sb.AppendLine();
        sb.AppendLine("Usage: NVMeDriverPatcher.Cli <command> [options]");

        foreach (var (group, label) in GroupLabels)
        {
            var cmds = All.Where(d => d.Group == group).ToArray();
            if (cmds.Length == 0) continue;
            sb.AppendLine();
            sb.AppendLine($"  {label}:");
            foreach (var d in cmds)
            {
                var name = d.Name;
                var suffix = d.Risk switch
                {
                    RiskLevel.Caution => " [!]",
                    RiskLevel.Experimental => " [experimental]",
                    _ => ""
                };
                if (name.Length >= 22)
                {
                    sb.AppendLine($"    {name}");
                    sb.AppendLine($"    {"",22}{d.Summary}{suffix}");
                }
                else
                {
                    sb.AppendLine($"    {name,-22}{d.Summary}{suffix}");
                }
                if (d.Aliases.Length > 0)
                    sb.AppendLine($"    {"",22}Aliases: {string.Join(", ", d.Aliases)}");
            }
        }

        sb.AppendLine();
        sb.AppendLine("  help, version        Show this help or print the CLI version");

        sb.AppendLine();
        sb.AppendLine("Global options:");
        sb.AppendLine("  --force, -f                Skip overridable safety checks (VeraCrypt remains blocked)");
        sb.AppendLine("  --no-restart               Don't prompt for restart after apply/remove");
        sb.AppendLine("  --safe                     Safe Mode: write primary flag only (735209102) — recommended");
        sb.AppendLine("  --full                     Full Mode: write all three flags (higher perf, higher risk)");
        sb.AppendLine("  --include-server-key       Force the optional Server 2025 key on for this run");
        sb.AppendLine("  --no-server-key            Force the optional Server 2025 key off for this run");
        sb.AppendLine("  --dry-run, --preview       Preview changes without applying them (works with 'apply')");
        sb.AppendLine("  --unattended               No prompts, auto-reboot, non-zero exit on any blocker");
        sb.AppendLine("  --json                     Emit machine-readable JSON (status, watchdog, controllers, recovery-proof, bypassio)");

        sb.AppendLine();
        sb.AppendLine("Exit codes:");
        sb.AppendLine("  0  success / patch applied (status)");
        sb.AppendLine("  1  failure / patch not applied (status)");
        sb.AppendLine("  2  partial state / no NVMe drives");
        sb.AppendLine("  3  unknown command or no args");
        sb.AppendLine("  4  Administrator privileges required");
        sb.AppendLine("  99 unhandled error");

        return sb.ToString();
    }
}
