# NVMe Driver Patcher for Windows 11

A GUI + CLI tool to enable the experimental Windows Server 2025 Native NVMe driver (nvmedisk.sys) on Windows 11, replacing the legacy SCSI translation layer for improved NVMe performance.

![PowerShell](https://img.shields.io/badge/PowerShell-5.1+-blue?logo=powershell)
![Windows 11](https://img.shields.io/badge/Windows-11-0078D4?logo=windows11)
![License](https://img.shields.io/badge/License-MIT-green)

![NVMe Driver Patcher Screenshot](screenshot.png)

## Quick Start

**One-line install** (Run as Administrator in PowerShell):

```powershell
irm https://github.com/SysAdminDoc/win11-nvme-driver-patcher/releases/latest/download/NVMe_Driver_Patcher.ps1 -OutFile NVMe_Driver_Patcher.ps1; .\NVMe_Driver_Patcher.ps1
```

Or download `NVMe_Driver_Patcher.ps1` from [Releases](https://github.com/SysAdminDoc/win11-nvme-driver-patcher/releases) and right-click > **Run with PowerShell**.

## What Does This Do?

Windows Server 2025 introduced a new **Native NVMe driver** that eliminates the legacy SCSI translation layer, allowing direct communication with NVMe drives. This driver is available in Windows 11 (24H2+) but disabled by default.

**This tool enables it via 5 registry components:**

| Component | Purpose |
|-----------|---------|
| Feature Flag `735209102` | NativeNVMeStackForGeClient - Primary driver enable |
| Feature Flag `1853569164` | UxAccOptimization - Extended functionality |
| Feature Flag `156965516` | Standalone_Future - Performance optimizations |
| SafeBoot Minimal Key | Prevents INACCESSIBLE_BOOT_DEVICE BSOD in Safe Mode |
| SafeBoot Network Key | Safe Mode with Networking support |

Optional: Feature Flag `1176759950` (Microsoft Official Server 2025 key) can be included via checkbox.

> **Important:** The SafeBoot keys are critical. Without them, your system cannot boot into Safe Mode after enabling Native NVMe. Many manual guides omit these keys -- this tool includes them automatically.

## Features

- **Dark/Light theme** auto-detected from Windows settings
- **Async preflight checks** -- scans system without freezing the GUI
- **NVMe health badges** -- temperature and wear % per drive via StorageReliabilityCounter
- **Before/after comparison** -- shows exactly what changed after patch/unpatch
- **System tray minimize** -- double-click to restore, context menu to exit
- **Toast notifications** -- Windows balloon tips for patch results
- **Activity log** with right-click context menu (copy, save, clear)
- **Registry backup** export + system restore point creation
- **Diagnostics export** -- full system report for troubleshooting
- **Post-reboot verification script** -- auto-generated to confirm patch after restart
- **Windows Event Log** integration for audit trails
- **BitLocker detection** with recovery key warning
- **Third-party driver detection** (Samsung, WD, Intel RST, AMD, etc.)
- **BypassIO/DirectStorage** status check with gaming impact warning
- **Single-instance mutex** -- prevents running multiple copies
- **Silent/CLI mode** for scripting and automation

## CLI Usage

All CLI operations require Administrator privileges.

```powershell
# Check patch status (exit code: 0=applied, 1=not applied, 2=partial)
.\NVMe_Driver_Patcher.ps1 -Silent -Status

# Apply the patch silently without restart prompt
.\NVMe_Driver_Patcher.ps1 -Silent -Apply -NoRestart

# Apply with force (skip NVMe drive check)
.\NVMe_Driver_Patcher.ps1 -Silent -Apply -Force

# Remove the patch silently
.\NVMe_Driver_Patcher.ps1 -Silent -Remove

# Export system diagnostics report
.\NVMe_Driver_Patcher.ps1 -ExportDiagnostics

# Generate post-reboot verification script
.\NVMe_Driver_Patcher.ps1 -GenerateVerifyScript
```

**Exit Codes (Silent Mode):**

| Code | Meaning |
|------|---------|
| 0 | Success / Patch Applied |
| 1 | Failure / Patch Not Applied |
| 2 | Partial / No NVMe drives |
| 3 | Invalid parameters |
| 4 | Elevation required |

## Requirements

| Requirement | Details |
|-------------|---------|
| **OS** | Windows 11 Build 22000+ (24H2 or 25H2 recommended) |
| **Privileges** | Administrator (auto-elevation prompt) |
| **Hardware** | NVMe SSD using Windows inbox driver (`StorNVMe.sys`) |
| **Update** | October 2025 cumulative update or newer |

## Windows Version Compatibility

| Windows 11 Version | Build | Support |
|--------------------|-------|---------|
| 25H2 | 26200+ | Full support, best performance |
| 24H2 | 26100 | Full support, recommended minimum |
| 23H2 | 22631 | Partial — feature flags apply but driver may not activate |
| 22H2 | 22621 | Not recommended — driver not present in base image |
| 21H2 | 22000 | Unsupported |

> The tool will warn you if your build is below 26100 but will not block the patch.

## Hardware Compatibility

The patch works with any NVMe drive using the Windows inbox `StorNVMe.sys` driver. Drives using vendor-specific drivers are unaffected.

**Confirmed working:**

| Brand | Models |
|-------|--------|
| Samsung | 970 Evo/Plus, 980, 980 Pro, 990 Pro (when using inbox driver) |
| WD | SN570, SN580, SN770, SN850X (when using inbox driver) |
| Crucial | P3, P3 Plus, P5, P5 Plus |
| SK Hynix | Platinum P41, Gold P31 |
| Kingston | NV2, KC3000 |
| Sabrent | Rocket 4 Plus |
| Generic/OEM | Any drive using StorNVMe.sys |

**Not compatible (uses vendor driver by default):**

| Brand | Notes |
|-------|-------|
| Samsung (with Samsung NVMe Driver) | Uses `samsungnvmedriver.sys` — patch has no effect |
| WD (with WD Dashboard driver) | Uses proprietary driver |

To check which driver your drive uses: **Device Manager → Disk drives → [Your NVMe] → Properties → Driver → Driver Files**

## Performance Expectations

Microsoft's benchmarks show **~80% IOPS improvement** on Windows Server 2025 with enterprise NVMe drives. Real-world results on Windows 11 consumer hardware are more modest:

| Scenario | Expected Improvement |
|----------|---------------------|
| Server (synthetic benchmarks) | ~80% IOPS, ~45% CPU reduction |
| Desktop (real-world workloads) | 10-15% improvement |
| Gaming | Minimal noticeable difference |
| Large file transfers | Modest improvement |

The biggest gains are in high-queue-depth random I/O operations.

## Scope

**This patch affects ALL NVMe drives** in your system that use the Windows inbox driver (`StorNVMe.sys`), not just the OS drive.

**Exception:** Drives using vendor-specific drivers (e.g., Samsung's proprietary driver) are not affected.

## Known Compatibility Issues

Some third-party software may have issues with Native NVMe:

| Software | Issue |
|----------|-------|
| Samsung Magician | May not detect drives |
| SSD vendor tools | Firmware update tools may fail |
| Hardware monitoring | Some disk monitoring utilities |
| Backup software | Rare issues with disk enumeration |
| DirectStorage games | BypassIO not supported -- higher CPU usage |

If you experience problems, use the **Remove Patch** button (or `-Silent -Remove`) and restart.

## Troubleshooting

### "No NVMe drives detected"
- Your drives may use vendor-specific drivers
- Check Device Manager > Disk drives > Properties > Driver
- If using Samsung/WD proprietary drivers, this patch won't help

### System won't boot after patch
1. Boot into Windows Recovery Environment
2. Open Command Prompt
3. Run the following commands to remove all 5 patch components:
```cmd
reg delete "HKLM\SYSTEM\CurrentControlSet\Policies\Microsoft\FeatureManagement\Overrides" /v 735209102 /f
reg delete "HKLM\SYSTEM\CurrentControlSet\Policies\Microsoft\FeatureManagement\Overrides" /v 1853569164 /f
reg delete "HKLM\SYSTEM\CurrentControlSet\Policies\Microsoft\FeatureManagement\Overrides" /v 156965516 /f
reg delete "HKLM\SYSTEM\CurrentControlSet\Policies\Microsoft\FeatureManagement\Overrides" /v 1176759950 /f
reg delete "HKLM\SYSTEM\CurrentControlSet\Control\SafeBoot\Minimal\{75416E63-5912-4DFA-AE8F-3EFACCAFFB14}" /f
reg delete "HKLM\SYSTEM\CurrentControlSet\Control\SafeBoot\Network\{75416E63-5912-4DFA-AE8F-3EFACCAFFB14}" /f
```
4. Restart

### Can't boot into Safe Mode
This shouldn't happen if you used this tool (SafeBoot keys are included). If it does:
1. Boot from Windows installation media
2. Open Command Prompt
3. Run the same 6 commands from the section above
4. Restart

## Credits

- **Ghacks** - [This Registry Hack Unlocks a Faster NVMe Driver in Windows 11](https://www.ghacks.net/2025/12/26/this-registry-hack-unlocks-a-faster-nvme-driver-in-windows-11/)
- **Microsoft TechCommunity** -- [Native NVMe announcement](https://techcommunity.microsoft.com/blog/windowsservernewsandbestpractices/announcing-native-nvme-in-windows-server-2025-ushering-in-a-new-era-of-storage-p/4477353)
- **Whirlpool Forums** -- Discovery of missing SafeBoot keys ([whrl.pl/RgTgVj](https://whrl.pl/RgTgVj))

## Disclaimer

This tool modifies system registry settings. While safety measures are included (restore points, registry backups, rollback), use at your own risk. Always ensure you have backups before making system changes.

---

<p align="center">
  <b>Made with coffee by <a href="https://github.com/SysAdminDoc">SysAdminDoc</a></b>
</p>
