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

    public string StatusLabel => Status switch
    {
        CheckStatus.Pass => "Ready",
        CheckStatus.Warning => "Review",
        CheckStatus.Fail => "Blocked",
        CheckStatus.Info => "Info",
        _ => "Checking"
    };
}

public class DriveRowVM
{
    public string Name { get; set; } = "";
    public string Size { get; set; } = "";
    public string BusType { get; set; } = "";
    public bool IsBoot { get; set; }
    public bool IsNativeDrive { get; set; }
    public bool ShowDriverBadge { get; set; }

    public string DriverBadgeText => IsNativeDrive ? "NATIVE" : "LEGACY";
}
