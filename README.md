# NVMe Driver Patcher for Windows 11

A tool to make it easier to enable the experimental Windows Server 2025 Native NVMe storage driver on Windows 11. 

![PowerShell](https://img.shields.io/badge/PowerShell-5.1+-blue?logo=powershell)
![Windows 11](https://img.shields.io/badge/Windows-11-0078D4?logo=windows11)
![License](https://img.shields.io/badge/License-MIT-green)

<img width="1390" height="944" alt="2025-12-29 21_15_09-NVMe Driver Patcher v2 6 3" src="https://github.com/user-attachments/assets/186c5157-56b5-4f97-94fc-ca5f55693dad" />

## Quick Start

**One-line install** (Run as Administrator in PowerShell):

```powershell
irm https://run.matt.care/nvmepatcher | iex
```

Or download `NVMe_Driver_Patcher.ps1` and right-click → **Run with PowerShell**.

## What Does This Do?

Windows Server 2025 introduced a new **Native NVMe driver** that eliminates the legacy SCSI translation layer, allowing direct communication with NVMe drives. This driver is available in Windows 11 (24H2+) but disabled by default.

**This tool enables it via 5 registry components:**

| Component | Purpose |
|-----------|---------|
| Feature Flag `735209102` | Primary driver enable |
| Feature Flag `1853569164` | Extended functionality |
| Feature Flag `156965516` | Performance optimizations |
| SafeBoot Minimal Key | Prevents boot failure in Safe Mode |
| SafeBoot Network Key | Safe Mode with Networking support |

> ⚠️ **Important:** The SafeBoot keys are critical. Without them, your system cannot boot into Safe Mode after enabling Native NVMe. Many manual guides omit these keys — this tool includes them automatically.

## Requirements

| Requirement | Details |
|-------------|---------|
| **OS** | Windows 11 Build 22000+ (24H2 or 25H2 recommended) |
| **Privileges** | Administrator (auto-elevation prompt) |
| **Hardware** | NVMe SSD using Windows inbox driver (`StorNVMe.sys`) |
| **Update** | October 2025 cumulative update or newer |

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

If you experience problems, use the **Remove Patch** button and restart.

## Troubleshooting

### "No NVMe drives detected"
- Your drives may use vendor-specific drivers
- Check Device Manager → Disk drives → Properties → Driver
- If using Samsung/WD proprietary drivers, this patch won't help

### System won't boot after patch
1. Boot into Windows Recovery Environment
2. Open Command Prompt
3. Run: `reg delete "HKLM\SYSTEM\CurrentControlSet\Policies\Microsoft\FeatureManagement\Overrides" /v 735209102 /f`
4. Repeat for `1853569164` and `156965516`
5. Restart

### Can't boot into Safe Mode
This shouldn't happen if you used this tool (SafeBoot keys are included). If it does:
1. Boot from Windows installation media
2. Open Command Prompt
3. Run: `reg delete "HKLM\SYSTEM\CurrentControlSet\Policies\Microsoft\FeatureManagement\Overrides" /v 735209102 /f`
4. Repeat for `1853569164` and `156965516`
5. Restart

## Credits

- **Ghacks** - [This Registry Hack Unlocks a Faster NVMe Driver in Windows 11](https://www.ghacks.net/2025/12/26/this-registry-hack-unlocks-a-faster-nvme-driver-in-windows-11/)
- **Microsoft TechCommunity** — [Native NVMe announcement](https://techcommunity.microsoft.com/blog/windowsservernewsandbestpractices/announcing-native-nvme-in-windows-server-2025-ushering-in-a-new-era-of-storage-p/4477353)
- **Whirlpool Forums** — Discovery of missing SafeBoot keys ([whrl.pl/RgTgVj](https://whrl.pl/RgTgVj))
- **Community Contributors** — Testing and feedback

## Disclaimer

This tool modifies system registry settings. While safety measures are included, use at your own risk. Always ensure you have backups before making system changes. The author is not responsible for any damage to your system.

---

<p align="center">
  <b>Made with ☕ by <a href="https://www.buymeacoffee.com/mattcreatingthings">SysAdminDoc</a></b>
</p>
