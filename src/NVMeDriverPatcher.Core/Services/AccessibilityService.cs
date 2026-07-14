using Microsoft.Win32;
using System.Runtime.InteropServices;

namespace NVMeDriverPatcher.Services;

public class AccessibilitySnapshot
{
    public bool HighContrastActive { get; set; }
    public bool NarratorInstalled { get; set; }
    public bool ReducedMotion { get; set; }
    public double TextScalePercent { get; set; } = 100;
    public string Summary { get; set; } = string.Empty;
}

// Detects OS-level accessibility settings for CLI/diagnostic reporting. The WPF client enforces
// the live client-area animation preference through MotionPreferenceService.
public static class AccessibilityService
{
    private const uint SpiGetClientAreaAnimation = 0x1042;

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

        bool? clientAreaAnimations = TryGetClientAreaAnimation();
        string? minAnimate = null;
        try
        {
            using var hkcu = RegistryKey.OpenBaseKey(RegistryHive.CurrentUser, RegistryView.Default);
            using var anim = hkcu.OpenSubKey(@"Control Panel\Desktop\WindowMetrics");
            minAnimate = anim?.GetValue("MinAnimate")?.ToString();
        }
        catch { }
        snap.ReducedMotion = ResolveReducedMotion(clientAreaAnimations, minAnimate);

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

    internal static bool ResolveReducedMotion(bool? clientAreaAnimationsEnabled, string? minAnimate) =>
        clientAreaAnimationsEnabled.HasValue
            ? !clientAreaAnimationsEnabled.Value
            : string.Equals(minAnimate?.Trim(), "0", StringComparison.Ordinal);

    private static bool? TryGetClientAreaAnimation()
    {
        try
        {
            return SystemParametersInfo(SpiGetClientAreaAnimation, 0, out int enabled, 0)
                ? enabled != 0
                : null;
        }
        catch { return null; }
    }

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SystemParametersInfo(
        uint uiAction,
        uint uiParam,
        out int pvParam,
        uint fWinIni);
}
