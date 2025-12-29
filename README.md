# NVMe Driver Patcher for Windows 11

A PowerShell GUI tool to enable the experimental Windows Server 2025 NVMe storage driver on Windows 11.

![PowerShell](https://img.shields.io/badge/PowerShell-5.1+-blue?logo=powershell)
![Windows 11](https://img.shields.io/badge/Windows-11-0078D4?logo=windows11)
![License](https://img.shields.io/badge/License-MIT-green)

## Quick Start

Open **PowerShell as Administrator** and run:

```powershell
irm https://run.matt.care/nvmepatcher | iex
```

---

## Overview

Windows Server 2025 includes an updated NVMe storage driver (`stornvme.sys`) designed to reduce CPU overhead and improve IOPS performance. This tool enables that driver on Windows 11 by toggling the appropriate feature flags in the registry.

### Performance Expectations

| Environment | Claimed Improvement |
|-------------|---------------------|
| Windows Server 2025 | Up to 80% higher IOPS, ~45% lower CPU usage |
| Windows 11 (real-world) | **10-15% improvement** in storage-heavy workloads |

> **Note:** Microsoft's server-side claims do not translate directly to desktop usage. Community testing on Windows 11 25H2 shows more modest but still meaningful gains. Results vary by hardware and workload.

This patch enables existing but disabled functionality within Windows 11. It does not install new drivers or modify system files.

---

## Features

- ✅ **One-click patching** — Apply or remove the NVMe driver enhancement with a single button
- 🛡️ **Automatic restore points** — Creates a system restore point before any changes
- 💾 **Registry backup** — Backs up relevant registry keys before modification
- 📦 **Full HKLM backup option** — Export the entire system registry hive on demand
- 🔍 **Status detection** — Automatically detects if the patch is already applied
- 📋 **Activity logging** — Detailed, color-coded log with export capability
- 🎨 **Modern UI** — Windows 11 Fluent-inspired dark mode interface

---

## Screenshot

<img width="707" height="970" alt="2025-12-29 18_02_46-NVMe Driver Patcher v2 1 0" src="https://github.com/user-attachments/assets/d068dcd5-93e9-4f0c-9ed9-165aa1e89276" />

---

## Requirements

- **Windows 11** (Build 22000 or later, tested on 25H2)
- **PowerShell 5.1** or later (included with Windows)
- **Administrator privileges** (the tool will prompt for elevation)
- **NVMe SSD** (SATA drives are not affected by this patch)

---

## Installation

No installation required. Choose one of the following methods:

### Option 1: One-Line Install (Recommended)

Open **PowerShell as Administrator** and run:

```powershell
irm https://run.matt.care/nvmepatcher | iex
```

### Option 2: Manual Download

1. Download `NVMe_Driver_Patcher.ps1` from [this repository](https://github.com/SysAdminDoc/win11-nvme-driver-patcher/raw/refs/heads/main/NVMe_Driver_Patcher.ps1)
2. Right-click → **Run with PowerShell**
   - Or open PowerShell as Administrator and run:
     ```powershell
     Set-ExecutionPolicy -Scope Process -ExecutionPolicy Bypass
     .\NVMe_Driver_Patcher.ps1
     ```

---

## Usage

1. **Launch the tool** — It will automatically request administrator privileges
2. **Review status** — The tool displays current patch and Windows version status
3. **Apply Patch** — Click "APPLY PATCH" to enable the NVMe driver enhancements
4. **Restart** — Reboot your computer for changes to take effect
5. **Verify** — Open Device Manager; your NVMe drive should now appear under **"Storage Media"** instead of "Devices"
6. **Remove Patch** — Click "REMOVE PATCH" to revert to default behavior if needed

---

## What Does This Patch Do?

The tool modifies three feature flags in the Windows registry:

| Registry Path | Value |
|---------------|-------|
| `HKLM\SYSTEM\CurrentControlSet\Policies\Microsoft\FeatureManagement\Overrides` | |
| `735209102` | `1` (DWORD) |
| `1853569164` | `1` (DWORD) |
| `156965516` | `1` (DWORD) |

These flags enable the updated NVMe driver behavior from Windows Server 2025 on Windows 11.

---

## Safety Features

This tool prioritizes system safety:

| Feature | Description |
|---------|-------------|
| **System Restore Point** | Automatically created before any registry changes |
| **Registry Backup** | Exports the specific registry path before modification |
| **Full Backup Option** | One-click export of entire HKLM hive to Desktop |
| **Confirmation Dialogs** | All destructive operations require confirmation |
| **Admin Verification** | Ensures proper privileges before running |
| **Detailed Logging** | All operations are logged with timestamps |

Backups are saved to: `Desktop\NVMe Patcher\`

---

## Reverting Changes

You can undo the patch at any time:

1. **Using the tool** — Click "REMOVE PATCH" to delete the registry entries
2. **System Restore** — Use the restore point created before patching
3. **Manual removal** — Delete the three values from the registry path above

---

## Troubleshooting

| Issue | Solution |
|-------|----------|
| Script won't run | Use the one-liner: `irm https://run.matt.care/nvmepatcher \| iex` in an Admin PowerShell |
| Script won't run | Enable script execution: `Set-ExecutionPolicy -Scope Process -ExecutionPolicy Bypass` |
| Access denied | Right-click and select "Run as Administrator" |
| Patch shows partial | Click "APPLY PATCH" to complete the installation |
| No performance change | Ensure you rebooted after applying the patch |
| Samsung Magician broken | This is a known issue; see Known Issues section below |
| Drive not in "Storage Media" | Your hardware may not be compatible with the new driver |

---

## FAQ

**Q: Is this safe?**  
A: The tool only modifies three registry values that enable existing Windows functionality. A restore point is created automatically, and you can revert changes at any time.

**Q: Will this survive Windows Updates?**  
A: Major Windows updates may reset these registry values. Simply re-run the tool after major updates if needed. Future builds could also disable this behavior without notice.

**Q: How do I know if it's working?**  
A: After rebooting, open **Device Manager** and check your NVMe drive. If the patch is active, your drive will appear under **"Storage Media"** instead of **"Devices"**. This confirms the newer driver is loaded.

**Q: What performance gains should I expect?**  
A: Community testing shows **10-15% improvement** in storage-intensive workloads on Windows 11. Results vary by hardware. Microsoft's 80% IOPS claims apply to server workloads only.

**Q: Does this work on Windows 10?**  
A: No, this tool is designed for Windows 11 only (Build 22000+, tested on 25H2).

---

## Known Issues

| Issue | Details |
|-------|---------|
| **Samsung Magician** | May fail to detect drives or display incorrect information after enabling the driver |
| **SSD Management Tools** | Other vendor utilities (firmware updaters, health monitors) may have compatibility issues |
| **Device Monitoring** | Some third-party disk monitoring software may not recognize drives correctly |

If you rely on these tools, consider whether the performance gains outweigh the compatibility trade-offs. You can always revert by clicking "REMOVE PATCH" and rebooting.

---

## Building / Development

This is a single-file PowerShell script with no build process required.

To modify:
1. Edit `NVMe_Driver_Patcher.ps1` in any text editor
2. Test by running with PowerShell

The script uses Windows Forms for the GUI and requires no external dependencies.

---

## License

MIT License — See [LICENSE](LICENSE) for details.

---

## Disclaimer

**USE AT YOUR OWN RISK.** This tool modifies system registry settings to enable functionality that Microsoft does not officially support on consumer Windows. While safety measures are in place, the author is not responsible for any damage, data loss, or system instability that may occur.

This is best viewed as an **experimental optimization** rather than a guaranteed upgrade. Gains vary by workload, and future Windows updates could disable this behavior without notice.

Always ensure you have proper backups before making system changes.

This project is not affiliated with or endorsed by Microsoft.

## Credits

- https://www.ghacks.net/2025/12/26/this-registry-hack-unlocks-a-faster-nvme-driver-in-windows-11/
- Inspired by community research into Windows feature flags
- UI design follows Windows 11 Fluent Design guidelines
