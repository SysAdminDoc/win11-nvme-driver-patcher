using Microsoft.Win32;

namespace NVMeDriverPatcher.Services;

public class AccessibilitySnapshot
{
    public bool HighContrastActive { get; set; }
    public bool NarratorInstalled { get; set; }
    public bool ReducedMotion { get; set; }
    public double TextScalePercent { get; set; } = 100;
    public string Summary { get; set; } = string.Empty;
}

// Detects OS-level accessibility settings the WPF UI should respect. We don't enforce —
// only surface — so the ViewModel can log when the user is likely running with HighContrast
// on (our dark theme may not meet WCAG 2.2 AA contrast pairs) or an accessible text scale.
// Keeping the scope narrow so this doesn't drift into a full i18n/a11y effort.
public static class AccessibilityService
{
    public static AccessibilitySnapshot Probe()
    {
        var snap = new AccessibilitySnapshot();
        try
        {
            using var hkcu = RegistryKey.OpenBaseKey(RegistryHive.CurrentUser, RegistryView.Default);
            using var hc = hkcu.OpenSubKey(@"Control Panel\Accessibility\HighContrast");
            if (hc?.GetValue("Flags") is string flags)
            {
                // HCF_HIGHCONTRASTON = 0x1
                if (int.TryParse(flags, out var f)) snap.HighContrastActive = (f & 1) != 0;
            }
        }
        catch { }

        try
        {
            using var hklm = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64);
            using var narrator = hklm.OpenSubKey(@"SOFTWARE\Microsoft\Windows NT\CurrentVersion\Accessibility\ATs\Narrator");
            snap.NarratorInstalled = narrator is not null;
        }
        catch { }

        try
        {
            using var hkcu = RegistryKey.OpenBaseKey(RegistryHive.CurrentUser, RegistryView.Default);
            using var scale = hkcu.OpenSubKey(@"SOFTWARE\Microsoft\Accessibility");
            if (scale?.GetValue("TextScaleFactor") is int ts) snap.TextScalePercent = ts;
        }
        catch { }

        try
        {
            using var hkcu = RegistryKey.OpenBaseKey(RegistryHive.CurrentUser, RegistryView.Default);
            using var anim = hkcu.OpenSubKey(@"Control Panel\Desktop\WindowMetrics");
            // MinAnimate = 0 means "reduced motion preference" on Windows.
            if (anim?.GetValue("MinAnimate") is string a && a == "0") snap.ReducedMotion = true;
        }
        catch { }

        var flagsSummary = new List<string>();
        if (snap.HighContrastActive) flagsSummary.Add("HighContrast");
        if (snap.NarratorInstalled) flagsSummary.Add("Narrator-installed");
        if (snap.ReducedMotion) flagsSummary.Add("ReducedMotion");
        if (Math.Abs(snap.TextScalePercent - 100) > double.Epsilon) flagsSummary.Add($"TextScale={snap.TextScalePercent:0}%");
        snap.Summary = flagsSummary.Count == 0
            ? "No accessibility flags detected."
            : "Detected: " + string.Join(", ", flagsSummary);
        return snap;
    }
}
