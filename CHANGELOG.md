# Changelog

All notable changes to win11-nvme-driver-patcher will be documented in this file.

## [v4.0.0] - %Y->- (HEAD -> main, origin/main, origin/HEAD)

- NVMe Driver Patcher v4.0.0 -- C# WPF port with telemetry, tuning, and charts
- Speed up preflight: drop CIM cache, use fast service checks, lazy health
- Verbose preflight logging with CIM query timing
- Cache CIM queries in preflight: eliminates ~40s of redundant WMI calls
- Adopt Zinc palette from LibreSpot for consistent dark theme
- Fixed: Fix DarkColorTable assembly resolution for PS7
- Fixed: Fix light scrollbar: apply SetWindowTheme DarkMode_Explorer to form
- Strip all DWM dark mode hacks -- restore original v3.0.0 theme behavior
- Force PS 5.1 for GUI: re-launch from pwsh.exe to powershell.exe
- Triple dark mode application: HandleCreated + Load + pre-ShowDialog
