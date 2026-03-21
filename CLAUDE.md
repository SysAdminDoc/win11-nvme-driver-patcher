# NVMe Driver Patcher

## Overview
GUI + CLI tool to enable the experimental Windows Server 2025 Native NVMe driver (nvmedisk.sys) on Windows 11.

## Tech Stack
- **v4.0.0**: C# WPF/.NET 9, CommunityToolkit.Mvvm, EF Core SQLite, System.Management
- **v3.5.2 (legacy)**: PowerShell 5.1+, WPF GUI, single monolithic script

## Current Version: v4.0.0

## Project Structure
```
NVMeDriverPatcher.sln
src/
  NVMeDriverPatcher/           (.NET 9 WPF GUI)
    Models/                    AppConfig, PreflightCheck, DriveInfo, PatchSnapshot, BenchmarkResult
    ViewModels/                MainViewModel (MVVM + RegistryFlagVM, PreflightCheckVM, DriveRowVM)
    Views/                     MainWindow.xaml/.cs, ThemedDialog.xaml/.cs
    Services/                  ConfigService, EventLogService, ToastService, RegistryService,
                               DriveService, PreflightService, PatchService, BenchmarkService,
                               DiagnosticsService, RecoveryKitService, UpdateService
    Converters/                StatusToColorConverter, BoolToVisibilityConverter, SettingsToggleConverter,
                               StringToColorConverter, StringToBrushConverter
    Themes/                    DarkTheme.xaml (zinc-950 palette, blue accent)
    GlobalUsings.cs            Disambiguates WPF vs WinForms types
    nvme.ico                   App icon (blue N on dark background)
    app.manifest               requireAdministrator
  NVMeDriverPatcher.Cli/       (thin CLI)
    Program.cs                 --status, --apply, --remove, --diagnostics, --recovery-kit, --verify
NVMe_Driver_Patcher.ps1        (kept as-is, frozen at v3.5.2)
```

## Architecture
- MVVM: MainViewModel with CommunityToolkit.Mvvm [ObservableProperty] + [RelayCommand]
- Dialog delegates: ConfirmDialog/InfoDialog set by MainWindow.xaml.cs (decoupled from VM)
- ThemedDialog: Dark-themed WPF dialog with OK/YesNo buttons, vector icons (ports Show-ThemedDialog)
- app.manifest with requireAdministrator (no elevation dance)
- System.Management.ManagementObjectSearcher for WMI/CIM (same WQL as PS1)
- Microsoft.Win32.Registry for registry operations
- Task.Run() for async preflight/benchmark (replaces PS1 runspaces)
- System.Text.Json for backward-compatible config.json and benchmark_results.json
- DarkTheme.xaml ResourceDictionary with full dark styles
- StringToBrushConverter for all dynamic color bindings (avoids SolidColorBrush in DataTemplates)
- UseWindowsForms=true for NotifyIcon toasts + FolderBrowserDialog

## Build Commands
```bash
dotnet build NVMeDriverPatcher.sln                    # Debug build
dotnet publish src/NVMeDriverPatcher -c Release -r win-x64 --self-contained -p:PublishSingleFile=true  # Single exe (~171MB)
```

## Key Decisions
- PS1 frozen at v3.5.2, exe is primary going forward
- Same config.json schema for backward compat
- Same benchmark_results.json format
- Same recovery kit format (.reg + .bat + README)
- CLI maps to same exit codes as PS1 silent mode (0/1/2/3)

## Port Mapping (49 PS1 functions -> C# services)
All 49 functions ported. Full confirm dialog flow with warnings (VeraCrypt block, BitLocker, compat, laptop, BypassIO). Post-operation restart dialog. Before/after snapshot comparison.

## Known Issues / TODO
- Inno Setup installer not yet created
- Framework-dependent publish option for smaller exe (~5MB vs 171MB)

## Version History
- v4.0.0: Complete C# WPF port. ThemedDialog, Registry Components UI, confirm dialogs, app icon, benchmark comparison, log context menu, compat tooltips.
- v3.5.2: Last PowerShell version (frozen).

## Gotchas
- UseWindowsForms + UseWPF requires GlobalUsings.cs to disambiguate Application, Clipboard, MessageBox, Path, File, Directory, Brush
- System.Windows.Shapes.Path conflicts with global using Path = System.IO.Path -- use `WpfPath` alias in ThemedDialog.xaml.cs
- System.Drawing.Color/ColorConverter conflicts -- use fully qualified System.Windows.Media.Color in converters
- WMI queries use same WQL but ManagementObjectSearcher requires System.Management NuGet
