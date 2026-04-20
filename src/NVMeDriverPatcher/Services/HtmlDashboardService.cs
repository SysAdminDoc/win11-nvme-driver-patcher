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
        sb.AppendLine("body{font:14px/1.5 -apple-system,Segoe UI,Roboto,sans-serif;color:#e6e8eb;background:#0b1118;margin:0;padding:24px;max-width:980px;margin:auto}");
        sb.AppendLine("h1{color:#72aeea;margin:0 0 8px;font-size:22px}h2{color:#72aeea;margin-top:32px;font-size:16px;border-bottom:1px solid #2d3d51;padding-bottom:6px}");
        sb.AppendLine(".meta{color:#8694a8;font-size:12px;margin-bottom:24px}");
        sb.AppendLine("table{border-collapse:collapse;width:100%;margin-top:6px}td,th{text-align:left;padding:6px 10px;border-bottom:1px solid #202d3c}");
        sb.AppendLine("th{color:#aab6c8;font-weight:600;font-size:12px;text-transform:uppercase;letter-spacing:.04em}");
        sb.AppendLine(".ok{color:#6fd3a5}.warn{color:#e3be79}.err{color:#e49c9c}.muted{color:#8694a8}");
        sb.AppendLine("code{background:#141e2b;padding:2px 6px;border-radius:3px;font-family:Cascadia Code,Consolas,monospace;font-size:12px}");
        sb.AppendLine(".card{background:#141e2b;border:1px solid #202d3c;border-radius:8px;padding:16px;margin-top:12px}");
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
        File.WriteAllText(path, html, new UTF8Encoding(false));
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
