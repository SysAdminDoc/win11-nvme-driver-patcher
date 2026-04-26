using System.Text.RegularExpressions;
using NVMeDriverPatcher.Models;

namespace NVMeDriverPatcher.ViewModels;

// Item-view-models shown in the MainWindow's ItemsControls. Each is a lightweight projection
// — no INPC because the collection is rebuilt on every refresh and never mutated in place.
// Factored out of MainViewModel.cs to keep that file focused on the workspace viewmodel
// itself instead of leaf presentation types that WPF only reads via property bindings.

public class PreflightCheckVM
{
    public string Label { get; set; } = "";
    public CheckStatus Status { get; set; }
    public string Message { get; set; } = "";
    public string? Tooltip { get; set; }
    public string DetailTooltip => string.IsNullOrWhiteSpace(Tooltip) ? Message : $"{Message}\n\n{Tooltip}";

    public string Color => Status switch
    {
        CheckStatus.Pass => "Green",
        CheckStatus.Warning => "Yellow",
        CheckStatus.Fail => "Red",
        CheckStatus.Info => "Accent",
        _ => "TextDim"
    };

    public string StatusLabel => Status switch
    {
        CheckStatus.Pass => "Ready",
        CheckStatus.Warning => "Review",
        CheckStatus.Fail => "Blocked",
        CheckStatus.Info => "Info",
        _ => "Checking"
    };
}

public class RegistryFlagVM
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public bool IsSet { get; set; }
    public bool IsOptional { get; set; }

    public string DotColor => IsSet ? "Green" : IsOptional ? "TextDim" : "Red";
    public string StatusLabel => IsSet ? "Present" : IsOptional ? "Optional" : "Missing";
}

public class AttentionNoteVM
{
    public string Title { get; set; } = "";
    public string Detail { get; set; } = "";
    public string ToneColor { get; set; } = "Yellow";
}

public class ChangePlanStepVM
{
    public string Title { get; set; } = "";
    public string Detail { get; set; } = "";
    public string ToneColor { get; set; } = "Accent";
}

public class DriveRowVM
{
    private static readonly Regex RxDigitsOnly = new(@"[^0-9]", RegexOptions.Compiled);

    public string Name { get; set; } = "";
    public string Size { get; set; } = "";
    public string BusType { get; set; } = "";
    public bool IsNVMe { get; set; }
    public bool IsBoot { get; set; }
    public string Temperature { get; set; } = "N/A";
    public string Wear { get; set; } = "N/A";
    public string SmartTooltip { get; set; } = "";
    public string Firmware { get; set; } = "";
    public bool ShowFirmware => !string.IsNullOrEmpty(Firmware) && IsNVMe;
    public bool IsNativeDrive { get; set; }
    public bool ShowDriverBadge { get; set; }

    public string DotColor => IsNVMe ? "Green" : "TextDim";
    public string BusPillBg => IsNVMe ? "AccentBg" : "SurfaceInset";
    public string BusPillFg => IsNVMe ? "Accent" : "TextDim";
    public bool ShowTemp => Temperature != "N/A" && IsNVMe;
    public bool ShowWear => Wear != "N/A" && IsNVMe;
    public string TempColor
    {
        get
        {
            if (int.TryParse(RxDigitsOnly.Replace(Temperature, ""), out int val))
                return val >= 70 ? "Red" : val >= 50 ? "Yellow" : "Green";
            return "Green";
        }
    }
    public string WearColor
    {
        get
        {
            if (int.TryParse(RxDigitsOnly.Replace(Wear, ""), out int val))
                return val <= 20 ? "Red" : val <= 50 ? "Yellow" : "Green";
            return "Green";
        }
    }
    public string DriverBadgeText => IsNativeDrive ? "NATIVE" : "LEGACY";
    public string DriverBadgeBg => IsNativeDrive ? "GreenBg" : "YellowBg";
    public string DriverBadgeFg => IsNativeDrive ? "Green" : "Yellow";
}
