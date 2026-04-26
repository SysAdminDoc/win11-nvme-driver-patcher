using System.IO;
using System.Text;
using NVMeDriverPatcher.Models;

namespace NVMeDriverPatcher.Services;

// Composes a single-file HTML dashboard from the current state of every diagnostic service.
// Unlike the support-bundle ZIP (machine-readable, for triage) this is human-friendly —
// a shareable snapshot suitable for attaching to an email or uploading to a ticketing system.
public static class HtmlDashboardService
{
    public static string Render(
        AppConfig config,
        PreflightResult preflight,
        VerificationReport? verification,
        WatchdogReport? watchdog,
        ReliabilityCorrelationReport? reliability,
        MinidumpTriageReport? minidump,
        SystemGuardrailsReport? guardrails,
        PerControllerAuditReport? controllers)
    {
        var sb = new StringBuilder();
        sb.AppendLine("<!doctype html><html><head><meta charset=\"utf-8\">");
        sb.AppendLine("<title>NVMe Driver Patcher — Diagnostics Snapshot</title>");
        sb.AppendLine("<style>");
        sb.AppendLine(":root{color-scheme:dark light;--bg:#0d0f13;--surface:#14181e;--inset:#101318;--border:#2a3038;--fg:#f6f9ff;--secondary:#d5deeb;--muted:#aab6c8;--dim:#8694a8;--accent:#7ab8ff;--ok:#7ad7ae;--warn:#e4bd73;--err:#f0a1a1;--shadow:rgba(0,0,0,.18)}");
        sb.AppendLine("@media (prefers-color-scheme:light){:root{--bg:#f7fafe;--surface:#fff;--inset:#f2f5fa;--border:#d5dce6;--fg:#0b1220;--secondary:#1e2a3b;--muted:#4a5668;--dim:#6b7788;--accent:#2563eb;--ok:#047857;--warn:#b45309;--err:#b91c1c;--shadow:rgba(82,96,112,.12)}}");
        sb.AppendLine("@media (prefers-contrast:more){:root{--bg:#000;--surface:#050505;--inset:#000;--border:#fff;--fg:#fff;--secondary:#f2f2f2;--muted:#e0e0e0;--dim:#cfcfcf;--accent:#66d9ff;--ok:#6dffb1;--warn:#ffe066;--err:#ff8a8a;--shadow:transparent}}");
        sb.AppendLine("*{box-sizing:border-box}html{background:var(--bg)}body{font:13px/1.45 -apple-system,BlinkMacSystemFont,Segoe UI,Roboto,sans-serif;color:var(--secondary);background:var(--bg);margin:0 auto;padding:22px;max-width:1040px}");
        sb.AppendLine("h1{color:var(--fg);margin:0 0 6px;font-size:22px;line-height:1.18;font-weight:650}h2{color:var(--fg);margin:24px 0 8px;font-size:14px;line-height:1.25;font-weight:650;border-bottom:1px solid var(--border);padding-bottom:7px}");
        sb.AppendLine(".meta{color:var(--dim);font-size:12px;margin-bottom:18px}.card{background:var(--surface);border:1px solid var(--border);border-radius:8px;box-shadow:0 14px 34px var(--shadow);padding:14px;margin-top:10px}");
        sb.AppendLine("table{border-collapse:collapse;width:100%;margin-top:2px}td,th{text-align:left;padding:7px 9px;border-bottom:1px solid var(--border);vertical-align:top}tr:last-child td{border-bottom:0}th{color:var(--muted);font-weight:650;font-size:11px;text-transform:uppercase;letter-spacing:.04em}");
        sb.AppendLine(".ok{color:var(--ok)}.warn{color:var(--warn)}.err{color:var(--err)}.muted{color:var(--dim)}strong{color:var(--fg)}code{color:var(--fg);background:var(--inset);border:1px solid var(--border);padding:2px 5px;border-radius:4px;font-family:Cascadia Code,Consolas,monospace;font-size:12px}");
        sb.AppendLine("</style></head><body>");
        sb.AppendLine($"<h1>NVMe Driver Patcher — diagnostics snapshot</h1>");
        sb.AppendLine($"<div class=\"meta\">Generated {DateTime.UtcNow:u} · app v{AppConfig.AppVersion} · profile {config.PatchProfile}</div>");

        Section(sb, "Verification", () =>
        {
            if (verification is null) { sb.AppendLine("<div class=\"muted\">No verification data.</div>"); return; }
            sb.AppendLine($"<div class=\"card\"><strong>Outcome:</strong> {verification.Outcome}<br>{WebEscape(verification.Summary)}<br><span class=\"muted\">{WebEscape(verification.Detail)}</span></div>");
        });

        Section(sb, "Watchdog", () =>
        {
            if (watchdog is null) { sb.AppendLine("<div class=\"muted\">Watchdog idle.</div>"); return; }
            sb.AppendLine($"<div class=\"card\"><strong>Verdict:</strong> {watchdog.Verdict} — {WebEscape(watchdog.Summary)}");
            sb.AppendLine("<table><tr><th>Source/ID</th><th>Count</th><th>Latest</th></tr>");
            foreach (var c in watchdog.Counts.Where(c => c.Count > 0))
                sb.AppendLine($"<tr><td><code>{WebEscape(c.Source)}/{c.Id}</code> — {WebEscape(c.Description)}</td><td>{c.Count}</td><td class=\"muted\">{c.LatestOccurrence:u}</td></tr>");
            sb.AppendLine("</table></div>");
        });

        Section(sb, "Reliability correlation", () =>
        {
            if (reliability?.DataAvailable != true) { sb.AppendLine("<div class=\"muted\">No Reliability Monitor data.</div>"); return; }
            sb.AppendLine($"<div class=\"card\">{WebEscape(reliability.Summary)}</div>");
        });

        Section(sb, "Minidump triage", () =>
        {
            if (minidump is null) { sb.AppendLine("<div class=\"muted\">Not scanned.</div>"); return; }
            sb.AppendLine($"<div class=\"card\">{WebEscape(minidump.Summary)}</div>");
        });

        Section(sb, "System guardrails", () =>
        {
            if (guardrails is null) { sb.AppendLine("<div class=\"muted\">Not evaluated.</div>"); return; }
            sb.AppendLine("<div class=\"card\"><table><tr><th>Name</th><th>Severity</th><th>Detail</th></tr>");
            foreach (var f in guardrails.Findings)
                sb.AppendLine($"<tr><td>{WebEscape(f.Name)}</td><td class=\"{SeverityClass(f.Severity)}\">{f.Severity}</td><td>{WebEscape(f.Detail)}</td></tr>");
            sb.AppendLine("</table></div>");
        });

        Section(sb, "Per-controller audit", () =>
        {
            if (controllers is null || controllers.Controllers.Count == 0) { sb.AppendLine("<div class=\"muted\">No controllers visible.</div>"); return; }
            sb.AppendLine("<div class=\"card\"><table><tr><th>Controller</th><th>Driver</th><th>Status</th></tr>");
            foreach (var c in controllers.Controllers)
                sb.AppendLine($"<tr><td>{WebEscape(c.FriendlyName)}</td><td><code>{WebEscape(c.BoundDriver)}</code></td><td class=\"{(c.IsNative ? "ok" : "warn")}\">{(c.IsNative ? "native" : "legacy")}</td></tr>");
            sb.AppendLine("</table></div>");
        });

        sb.AppendLine("</body></html>");
        return sb.ToString();
    }

    public static string SaveTo(AppConfig config, string html, string? customPath = null)
    {
        var dir = string.IsNullOrWhiteSpace(config.WorkingDir) ? AppConfig.GetWorkingDir() : config.WorkingDir;
        var path = customPath ?? Path.Combine(dir, $"nvme_dashboard_{DateTime.UtcNow:yyyyMMddHHmmss}.html");

        // Ensure the output directory exists (custom paths may point at a folder that hasn't
        // been created yet). Then write atomically via a `.tmp` sibling so a crash or power
        // loss between bytes 0 and N never leaves a half-rendered dashboard for the user to
        // open. Same pattern as ConfigService.Save / DiagnosticsService.ExportBundle.
        var outDir = Path.GetDirectoryName(Path.GetFullPath(path));
        if (!string.IsNullOrEmpty(outDir))
            Directory.CreateDirectory(outDir);

        var tempPath = path + ".tmp";
        try
        {
            using (var fs = new FileStream(tempPath, FileMode.Create, FileAccess.Write, FileShare.None))
            using (var sw = new StreamWriter(fs, new UTF8Encoding(false)))
            {
                sw.Write(html);
                sw.Flush();
                fs.Flush(flushToDisk: true);
            }
            File.Move(tempPath, path, overwrite: true);
        }
        catch
        {
            try { if (File.Exists(tempPath)) File.Delete(tempPath); } catch { }
            throw;
        }
        return path;
    }

    private static void Section(StringBuilder sb, string name, Action body)
    {
        sb.AppendLine($"<h2>{WebEscape(name)}</h2>");
        body();
    }

    private static string SeverityClass(GuardrailSeverity sev) => sev switch
    {
        GuardrailSeverity.Blocker => "err",
        GuardrailSeverity.Warning => "warn",
        _ => "ok"
    };

    private static string WebEscape(string s) =>
        (s ?? string.Empty)
            .Replace("&", "&amp;")
            .Replace("<", "&lt;")
            .Replace(">", "&gt;")
            .Replace("\"", "&quot;");
}
