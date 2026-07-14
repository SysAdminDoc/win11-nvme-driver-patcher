<p align="center">
  <img src="https://github.com/user-attachments/assets/68963fd7-fc98-4d91-bd83-b13768fff161" alt="NVMe Driver Patcher" width="300">
</p>

# NVMe Driver Patcher for Windows 11

A GUI + CLI tool to enable the experimental Windows Server 2025 Native NVMe driver (nvmedisk.sys) on Windows 11, replacing the legacy SCSI translation layer for improved NVMe performance.

![Version](https://img.shields.io/badge/Version-5.0.0-blue)
![.NET](https://img.shields.io/badge/.NET-10.0-512BD4?logo=dotnet)
![Windows 11](https://img.shields.io/badge/Windows-11-0078D4?logo=windows11)
![License](https://img.shields.io/badge/License-MIT-green)

## Quick Start

**GUI (recommended)** — download [`NVMeDriverPatcher.exe`](https://github.com/SysAdminDoc/win11-nvme-driver-patcher/releases/latest/download/NVMeDriverPatcher.exe) from the latest release and run it. Administrator elevation is automatic; no install or prerequisites needed (self-contained single file).

**CLI (automation/fleets)** — download [`NVMeDriverPatcher.Cli.exe`](https://github.com/SysAdminDoc/win11-nvme-driver-patcher/releases/latest/download/NVMeDriverPatcher.Cli.exe):

```powershell
.\NVMeDriverPatcher.Cli.exe status
.\NVMeDriverPatcher.Cli.exe apply --safe
```

**Windows on ARM** releases also ship `*-win-arm64.exe` portable builds for GUI, CLI, tray,
and watchdog. These are diagnostic/status builds until Microsoft ships an ARM64 `nvmedisk.sys`;
use the x64 assets under emulation if you need the current native-NVMe enablement path.

**MSI (managed deployment)** — `NVMeDriverPatcher-<version>.msi` from the release installs GUI + CLI + tray per-machine; the real-time watchdog service is an opt-in feature (`ADDLOCAL=WatchdogService`).

<details>
<summary><b>Legacy PowerShell script (deprecated)</b></summary>

```powershell
irm https://github.com/SysAdminDoc/win11-nvme-driver-patcher/releases/latest/download/NVMe_Driver_Patcher.ps1 -OutFile NVMe_Driver_Patcher.ps1; .\NVMe_Driver_Patcher.ps1
```

> **Deprecated — limited to pre-March-2026 builds.** The script writes only the registry
> override keys, which Microsoft neutered on newer builds: it reports success while the
> driver never binds. It has none of the v4.4+ capabilities:
>
> | Capability | Legacy script | GUI / CLI |
> |---|---|---|
> | Registry override patch + SafeBoot keys | ✅ | ✅ |
> | Native FeatureStore fallback for post-block builds (ViVeTool only after native failure) | ❌ | ✅ |
> | Post-reboot bind verification (honest status) | ❌ | ✅ |
> | Watchdog auto-revert, minidump triage, reliability correlation | ❌ | ✅ |
> | Global-scope warning, dry-run preview, recovery USB builder | ❌ | ✅ |
>
> It remains in releases for air-gapped/legacy environments only.
</details>

### Verify the download (recommended)

Every release artifact ships with a matching `<asset>.sha256` sidecar and a combined
`SHA256SUMS.txt`. Verify before running — this catches a tampered CDN, a corrupted
download, or a masquerading file under the same name:

```powershell
# Verify any single artifact against its .sha256 sidecar
$file = 'NVMe_Driver_Patcher.ps1'
$expected = (Invoke-WebRequest -UseBasicParsing -Uri "https://github.com/SysAdminDoc/win11-nvme-driver-patcher/releases/latest/download/$file.sha256").Content.Split(' ')[0].Trim()
$actual = (Get-FileHash -Algorithm SHA256 -Path $file).Hash.ToLower()
if ($expected -eq $actual) { "OK: $file" } else { "MISMATCH: expected $expected, got $actual" }
```

The GUI's in-app auto-updater (**Help → Check for updates**) performs the same
SHA-256 sidecar check automatically at the original GitHub release asset URL
before falling back to any redirected CDN URL. It refuses to stage any binary
that either fails the hash or has no sidecar / Authenticode signature available
— this is load-bearing supply-chain defense, not just UI polish.

## What Does This Do?

Windows Server 2025 introduced a new **Native NVMe driver** that eliminates the legacy SCSI translation layer, allowing direct communication with NVMe drives. This driver is available in Windows 11 (24H2+) but disabled by default. Microsoft has stated they are ["absolutely exploring"](https://techcommunity.microsoft.com/blog/windowsservernewsandbestpractices/announcing-native-nvme-in-windows-server-2025-ushering-in-a-new-era-of-storage-p/4477353) bringing it broadly to the entire Windows codebase.

**This tool enables it via 5 registry components:**

| Component | Purpose |
|-----------|---------|
| Feature Flag `735209102` | NativeNVMeStackForGeClient - Primary driver enable |
| Feature Flag `1853569164` | UxAccOptimization - Extended functionality |
| Feature Flag `156965516` | Standalone_Future - Performance optimizations |
| SafeBoot Minimal Key | Prevents INACCESSIBLE_BOOT_DEVICE BSOD in Safe Mode |
| SafeBoot Network Key | Safe Mode with Networking support |

Optional: Feature Flag `1176759950` (Microsoft Official Server 2025 key) can be included via checkbox. **Recommended** -- without it, the new I/O scheduler may not activate and results can be inconsistent.

> **Important:** The SafeBoot keys are critical. Without them, your system cannot boot into Safe Mode after enabling Native NVMe. Many manual guides omit these keys -- this tool includes them automatically.

## Features

**Safety & Compatibility**
- **VeraCrypt hard block** -- detects system encryption and refuses to patch ([breaks boot entirely](https://github.com/veracrypt/VeraCrypt/issues/1640))
- **Typed critical environment gate** -- administrator, VeraCrypt, Intel RST/VMD, BitLocker, and SafeBoot probes return `Pass`, `Fail`, or `Unknown` with reason code, native error, evidence, and timestamp; `Fail`/`Unknown` cannot be forced past
- **Proved BitLocker recovery + suspension** -- requires a numerical recovery-password protector, surfaces only its safe-to-match ID, refreshes AD/Entra escrow on joined devices, and WMI-confirms an exact one-reboot suspension before mutation
- **Crash-consistent, access-controlled mutation ledger** -- durably captures the first clean registry, SafeBoot, and FeatureStore baseline before mutation under `%ProgramData%\NVMePatcher\State`; the protected administrator/SYSTEM-only DACL, owner/reparse/hard-link checks, and atomic publication prevent standard-user pre-creation or replacement. Interrupted work and uninstall restore that exact state, and restart is not offered until the reboot checkpoint is durable
- **Fail-closed startup recovery** -- an incomplete interrupted-ledger restore, FeatureStore recovery, or watchdog auto-revert disables Apply, reinstall, fallback, SafeBoot upgrade, and hot-swap for the rest of the process. Removal, recovery exports, verification, and diagnostics remain available with the exact failure recorded in support evidence
- **Durable registry commit proof** -- every feature override and SafeBoot value must complete `SetValue`, a successful `Flush`, and readback through a newly opened HKLM64 handle before the ledger can advance to Applied or a restart can be offered; any failed key restores the exact baseline
- **Versioned history database upgrades** -- legacy v1 and formerly-unversioned v2 SQLite files are detected from their real schema, quick-checked, copied through SQLite Online Backup, upgraded transactionally, and revalidated before history is served. Corrupt, incomplete, or newer schemas remain untouched and surface an exact backup/recovery path instead of appearing as empty history
- **Durable shared configuration** -- GUI, CLI, Tray, and Watchdog configuration access fails closed on cross-process lock contention; validated, flushed staging files publish atomically while retaining a validated `config.json.bak`, and corrupt primary/backup evidence is preserved before safe defaults are used
- **Authenticated ViVeTool fallback** -- the signed app embeds exact v0.3.4 x64/ARM64 archive and member hashes; an unlisted release, wrong architecture, missing/extra/nested file, modified companion DLL/data file, or tampered cache is rejected before installation and rechecked before every elevated launch
- **Verified non-boot hot-swap transaction** -- a live swap aborts before dismount if any volume flush fails, uses the controller's documented SetupAPI property-state change, honors restart flags, and reports success only after the exact controller driver/service and every original volume mount are independently proved
- **Comprehensive software detection** -- warns about Intel RST (BSOD risk), Intel VMD (boot failures), Hyper-V/WSL2 (40% I/O regression), Storage Spaces (array degradation), Veeam, Acronis, Macrium, UrBackup, NinjaOne, Paragon, Samsung Magician, WD Dashboard, Crucial Storage Executive, CrystalDiskInfo, Data Deduplication
- **Laptop/power warning** -- detects laptops and warns about APST battery regression (~15% impact)
- **Rollback on partial failure** -- restores pre-existing values from the durable baseline instead of assuming every touched value was absent
- **Registry backup** export + system restore point creation before any changes
- **Third-party driver detection** (Samsung, WD, Intel RST, AMD, SK Hynix, Crucial, Phison)
- **Custom INF / TESTSIGNING warning** -- flags test-signed native NVMe driver-store workarounds that the registry rollback cannot remove
- **Recovery Kit generation** -- creates .reg + .bat files for offline WinRE recovery (auto-detects WinRE, loads offline registry hive)

**Diagnostics & Benchmarking**
- **Built-in DiskSpd benchmark** -- 4K random read/write test targeting NVMe drive with before/after comparison (auto-downloads [Microsoft DiskSpd](https://github.com/microsoft/diskspd))
- **11 async preflight checks** run in a background thread without freezing the GUI
- **NVMe health badges** -- temperature, wear %, firmware, power-on hours, media errors (hover for SMART details)
- **Per-drive NATIVE/LEGACY badges** -- shows whether each NVMe drive migrated to `nvmedisk.sys` or remains on `stornvme.sys`
- **Post-reboot drive migration verification** -- per-drive confirmation of which drives moved to "Storage disks"
- **BypassIO/DirectStorage** status check with named-game gaming impact warning
- **Before/after comparison** -- shows exactly what changed after patch/unpatch
- **Diagnostics export** -- full system report with SMART health, compat software, migration status, benchmark history, and rules/compat DB provenance (source, schema, SHA-256, review freshness)
- **GitHub update check** with clickable badge in title bar
- **Windows Event Log** integration for audit trails

**UI/UX**
- **WPF dark theme** -- zinc-950 palette with blue accent, custom title bar, drop shadow
- **Resizable window** with grip handle, clamped to work area (no off-screen at high DPI)
- **Toast notifications** -- Windows balloon tips for patch results
- **Activity log** with right-click context menu (Copy Selection, Select All, Copy All, Clear)
- **Collapsible Settings panel** -- Auto-save, Toasts, Event Log, Restart Delay, Open Data Folder
- **Benchmark IOPS display** in patch status card
- **Refresh button** -- re-run all preflight checks without restarting
- **Skip warnings checkbox** -- for experienced users who don't need confirmation dialogs
- **Silent/CLI mode** for scripting and automation

**v4.4 — Stability, correlation, enterprise (new)**
- **Post-patch event-log watchdog** -- scans `System` channel for Storport ID 129 command timeouts, disk ID 51/153, Kernel-Power 41, and BugCheck 1001 inside a configurable window (default 48h). Crosses the revert threshold? Stages an auto-revert on next boot. Missing state or Event Log evidence is reported as unavailable rather than healthy; the optional LocalService restarts after its first two failures and runs with only the state-directory and System-log access it needs.
- **Separated privileged/service state** -- standard users receive read-only access to shared status/configuration, boot-critical state remains administrator/SYSTEM-only even in portable mode, and the restricted LocalService watchdog can modify only `%ProgramData%\NVMePatcher\Watchdog`.
- **Reliability Monitor correlation** -- pulls `Win32_ReliabilityStabilityMetrics`, overlays the patch-apply timestamp, reports pre/post stability averages with delta.
- **Minidump triage** -- scans `C:\Windows\Minidump` for dumps newer than the patch and flags any that reference `nvmedisk.sys`, `stornvme.sys`, `storport.sys`, `disk.sys`.
- **Firmware + controller compat JSON** -- shipped `compat.json` maps `{controller, firmware}` → `{Good, Caution, Bad}` and flags power-loss-risk entries such as Phison E18/E26. Preflight consults it before proceeding.
- **Honest machine-wide scope** -- the registry and FeatureStore routes affect Windows driver selection for every eligible NVMe drive/controller. Legacy `drive_scope.json` preferences are detected and reported as unenforced; the tool does not claim a drive can stay independently on `stornvme.sys`.
- **Dry-run preview** (`--dry-run` / "Preview Changes") -- prints every registry write the patch would perform, without touching the registry.
- **ETW storage trace** (`etw`) -- wraps `wpr.exe` for 60-second pre/post captures; ETL files land in `%ProgramData%\NVMePatcher\etl\`.
- **Controller-complete WinPE recovery USB builder** (`winpe`) -- detects the Windows ADK + WinPE add-on, inventories every present hardware-backed storage controller, exports each bound OEM package once, injects signed packages into `boot.wim`, and retains the same INFs for manual `drvload`. The published tree/ISO includes a verified Recovery Kit, controller coverage report, custom `startnet.cmd`, and final SHA-256 inventory. `winpe-freshness` verifies that media and reports stale when the app, Recovery Kit, rollback script, WinRE image, or controller INF/version has changed.
- **Opt-in compatibility telemetry** -- build an anonymized `{controller, firmware, OS build, profile, verification, watchdog, reliability delta}` JSON and optionally `POST` it to a user-configured HTTPS endpoint. No serials, machine names, drive letters, or user names.
- **Driver Verifier harness** (`verifier-on` / `-off` / `-status`) -- dev/tester-mode wrapper around `verifier.exe` for kernel-level stress checks on the NVMe stack.
- **GPO / ADMX templates** (`packaging/admx/`) -- pin Safe/Full profile, IncludeServerKey, SkipWarnings, watchdog behavior, and telemetry across a fleet via `HKLM\SOFTWARE\Policies\SysAdminDoc\NVMeDriverPatcher`. Policy overrides local config.
- **Intune source bundle** (`NVMeDriverPatcher.Intune-<version>.zip`) -- release builds package the MSI and detection script with a versioned, per-file SHA-256 manifest before upload or `.intunewin` wrapping.
- **winget manifest** (`packaging/winget/SysAdminDoc.NVMeDriverPatcher.yaml`) -- `winget install SysAdminDoc.NVMeDriverPatcher`.
- **Non-admin status tray agent** (`NVMeDriverPatcher.Tray`) -- separate exe, no UAC. Shows patch state + watchdog verdict from the system tray; right-click → "Open Main App (elevated)" for the admin GUI.
- **Rotating logs** -- `crash.log`, `activity.log`, `watchdog.log`, `diagnostics.log` rotate at 5MB each with 5 generations retained.

## CLI Usage

CLI operations use the product-wide Administrator manifest. `verify-payload` is read-only and
does not initialize application config, mutation recovery, policy, or Event Log state.

```powershell
# Check patch status (exit code: 0=applied, 1=not applied, 2=partial)
# Shows driver status, migration, compat warnings, laptop detection
.\NVMe_Driver_Patcher.ps1 -Silent -Status

# Apply the patch silently without restart prompt
.\NVMe_Driver_Patcher.ps1 -Silent -Apply -NoRestart

# Apply with force (skip NVMe drive check and preflight)
.\NVMe_Driver_Patcher.ps1 -Silent -Apply -Force

# Remove the patch silently
.\NVMe_Driver_Patcher.ps1 -Silent -Remove

# Export system diagnostics report
.\NVMe_Driver_Patcher.ps1 -ExportDiagnostics

# Generate post-reboot verification script
.\NVMe_Driver_Patcher.ps1 -GenerateVerifyScript

# Generate WinRE-compatible recovery kit
.\NVMe_Driver_Patcher.ps1 -ExportRecoveryKit
```

### Extended CLI (C# binary — 60 commands)

Run `NVMeDriverPatcher.Cli help` for the full grouped command reference.

```powershell
# Lifecycle
NVMeDriverPatcher.Cli status                              # Patch state + build rule (exit: 0/1/2)
NVMeDriverPatcher.Cli apply --safe                         # Apply with Safe Mode profile
NVMeDriverPatcher.Cli apply --dry-run                      # Preview registry changes only
NVMeDriverPatcher.Cli remove                               # Undo the patch

# Recovery
NVMeDriverPatcher.Cli recovery-kit                         # Generate WinRE recovery kit
NVMeDriverPatcher.Cli verify-payload --input=<dir-or-zip>  # Verify the complete generated payload
NVMeDriverPatcher.Cli winpe-freshness [--input=<tree>]     # Media integrity/freshness (exit: 0 fresh, 1 stale/missing, 2 unknown)
NVMeDriverPatcher.Cli recovery-proof [--json]              # Prove recovery infrastructure and BitLocker protector state
NVMeDriverPatcher.Cli upgrade-safeboot                     # Add KB5079391 SafeBoot entries

# Diagnostics
NVMeDriverPatcher.Cli preflight [--json]                   # Typed critical probes (exit: 0 pass, 1 blocked, 2 unknown)
NVMeDriverPatcher.Cli watchdog                             # Stability verdict (exit: 0/1/2)
NVMeDriverPatcher.Cli watchdog-service                     # Real-time service state
NVMeDriverPatcher.Cli reliability                          # Reliability Monitor correlation
NVMeDriverPatcher.Cli minidump                             # NVMe-stack crash scan
NVMeDriverPatcher.Cli controllers [--json]                 # Bound driver plus read-only candidate/rank evidence
NVMeDriverPatcher.Cli bypassio                             # Per-volume BypassIO + named-game gaming impact
NVMeDriverPatcher.Cli bypassio --history                   # Pre/post patch BypassIO comparison
NVMeDriverPatcher.Cli apst                                 # APST state + battery impact estimate
NVMeDriverPatcher.Cli identify                             # NVMe Identify Controller dump
NVMeDriverPatcher.Cli bundle                               # Export support bundle (.zip)
NVMeDriverPatcher.Cli diagnostics                          # Export diagnostics report (.txt)

# Storage & Performance
NVMeDriverPatcher.Cli etw                                  # 60s ETW storage trace
NVMeDriverPatcher.Cli firmware                             # Compat.json entries
NVMeDriverPatcher.Cli compare-benchmarks --threshold=15    # Before/after benchmark diff

# Fleet & Admin
NVMeDriverPatcher.Cli telemetry --endpoint=<url>           # Submit anonymized compat report
NVMeDriverPatcher.Cli dashboard                            # Generate HTML dashboard
NVMeDriverPatcher.Cli winpe --output=E:\                   # WinPE recovery USB
NVMeDriverPatcher.Cli config-export --export=<path>        # Export config bundle
NVMeDriverPatcher.Cli config-import --import=<path>        # Import config bundle
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
| **OS** | Windows 11 Build 22000+ (24H2 or 25H2 recommended when the bundled build-rule check reports a working path) |
| **Privileges** | Administrator (auto-elevation prompt) |
| **Hardware** | NVMe SSD using Windows inbox driver (`StorNVMe.sys`) |
| **Update** | KB5066835 (October 2025 cumulative update) or newer |

## Windows Version Compatibility

| Windows 11 Version | Build | Support |
|--------------------|-------|---------|
| 25H2 pre-26200.8524 | 26200.0-26200.8523 | Registry override is blocked; the build-specific FeatureStore fallback is the expected path. |
| 25H2 26200.8524+ | 26200.8524+ | Verify / monitor / rollback only. No known registry or fallback route binds `GenNvmeDisk` on this branch. |
| 24H2 evidenced fallback | 26100.8106 | Exact community-evidenced FeatureStore fallback interval; adjacent UBRs are not inferred. |
| Other 24H2 builds | 26100.x / 26101-26199 | Verify / monitor / rollback only until the exact build and UBR have a sourced working path. |
| 26300+ Insider | 26300+ | Check the native Settings Feature flags page first; registry and fallback routes are not expected to bind. |
| Pre-24H2 client | 26099 and below | Verify / monitor / rollback only; no sourced working enablement interval. |

> The app and CLI use `src/NVMeDriverPatcher.Core/windows_build_rules.json` at runtime. Trust `status`
> and preflight output over this static table when Microsoft changes Insider or cumulative-update behavior.

## Hardware Compatibility

The patch works with any NVMe drive using the Windows inbox `StorNVMe.sys` driver. Drives using vendor-specific drivers are unaffected.

**Confirmed working:**

| Brand | Models |
|-------|--------|
| Samsung | 970 Evo/Plus, 980, 980 Pro, 990 Pro on known-good firmware (when using inbox driver) |
| WD | SN570 and compat-cleared SN580/SN770/SN850X firmware (when using inbox driver) |
| Crucial | P3, P3 Plus, P5, P5 Plus, T705 |
| SK Hynix | Gold P31, Platinum P41 with preflight performance caution |
| Kingston | NV2, KC3000 with Phison E18 power-loss caution |
| Sabrent | Rocket 4 Plus with Phison power-loss caution |
| Solidigm | P5316 (enterprise, Server 2025 tested) |
| Generic/OEM | Any drive using StorNVMe.sys |

Run preflight before enabling the patch. The bundled `compat.json` flags known risky controller/firmware combinations including WD/SanDisk 2TB HMB BSOD firmware, WD SN850X Critical Failure reports, Samsung 990 Pro 2TB `7B2QJXD7`, SK hynix Platinum P41 mixed performance, and Phison E18/E26 power-loss risk.

**Not compatible (uses vendor driver by default):**

| Brand | Notes |
|-------|-------|
| Samsung (with Samsung NVMe Driver) | Uses `samsungnvmedriver.sys` -- patch has no effect |
| WD (with WD Dashboard driver) | Uses proprietary driver |

To check which driver your drive uses: **Device Manager > Disk drives > [Your NVMe] > Properties > Driver > Driver Files**

## Performance Benchmarks

The native NVMe driver delivers significant gains by eliminating the SCSI translation layer. Results vary by SSD model, controller, and workload type.

### Independent Benchmark Results

| Source | Test | Improvement |
|--------|------|-------------|
| [Tom's Hardware](https://www.tomshardware.com/pc-components/ssds/new-windows-native-nvme-driver-benchmarks-reveal-transformative-performance-gains-up-to-64-89-percent-lightning-fast-random-reads-and-breakthrough-cpu-efficiency) | 4K random read (StorageReview) | **+64.89%** |
| [Tom's Hardware](https://www.tomshardware.com/pc-components/ssds/windows-11-rockets-ssd-performance-to-new-heights-with-hacked-native-nvme-driver-up-to-85-percent-higher-random-workload-performance-in-some-tests) | Random workloads (consumer SSD) | **Up to +85%** |
| [StorageReview](https://www.storagereview.com/review/windows-server-native-nvme) | 64K random read latency | **-38.46%** (faster) |
| [NotebookCheck](https://www.notebookcheck.net/Windows-11-hack-Higher-SSD-speeds-with-new-Microsoft-NVMe-driver.1190489.0.html) | Sequential read / write (PCIe 4.0) | +23% / +30% |
| [Microsoft](https://techcommunity.microsoft.com/blog/windowsservernewsandbestpractices/announcing-native-nvme-in-windows-server-2025-ushering-in-a-new-era-of-storage-p/4477353) | 4K random read IOPS (Server) | **+80%**, -45% CPU |
| [Overclock.net](https://www.overclock.net/threads/enable-native-nvme-driver-in-windows-11-24h2-25h2-with-last-update.1818467/) | IOPS 512b-8KB (Samsung OEM) | **+167%** |
| CrystalDiskMark (Reddit) | 4K-64Thrd random read/write | +22% / +85% |

### What to Expect

| Workload | Expected Gains |
|----------|---------------|
| Random 4K read/write (high queue depth) | **+20% to +85%** -- biggest wins |
| Sequential read/write | +10% to +30% |
| Desktop responsiveness (app launches, boot) | Single-digit % (desktop I/O runs at QD1-2) |
| Gaming (load times) | Minimal difference |
| DirectStorage games | **May be worse** (BypassIO not supported) |

> The biggest gains are in high-queue-depth random I/O. Sequential transfers and gaming see more modest improvements. Desktop usage operates at QD1-2 where gains are ~2%. Run your own benchmarks with the built-in DiskSpd or [CrystalDiskMark](https://crystaldiskmark.org/) before and after.

## Known Compatibility Issues

The tool automatically detects and warns about all of these. VeraCrypt is a hard block.

| Software | Issue | Severity | Auto-Detected |
|----------|-------|----------|---------------|
| **VeraCrypt** (system encryption) | [Breaks boot entirely](https://github.com/veracrypt/VeraCrypt/issues/1640) | **Critical** | Yes (blocks patch) |
| **BitLocker** | May trigger recovery key prompt | High | Yes (requires recovery-password protector, refreshes directory escrow, verifies one-reboot suspension) |
| **Intel RST** | Conflicts with nvmedisk.sys, BSOD risk | High | Yes (warns) |
| **Intel VMD** | Boot failures on VMD-configured systems | High | Yes (warns) |
| **Hyper-V / WSL2** | WSL2 disk I/O ~40% slower (no paravirt) | Medium | Yes (warns) |
| **Storage Spaces** | Arrays may degrade or disappear | High | Yes (warns) |
| **Acronis True Image** | Disk-ID change moves the drive under Storage disks — backup jobs lose it; re-register after the swap | High | Yes (warns) |
| **Veeam Backup** | Disk-ID change → agent stops detecting the drive; re-add it to the job after the swap | High | Yes (warns) |
| **Macrium Reflect** | May need update for compatibility | Medium | Yes (warns) |
| **UrBackup / NinjaOne / Paragon** | Backup image-mount driver may be blocked by Windows CodeIntegrity after KB5083769 | Medium | Yes (service + event-log evidence) |
| **Samsung Magician** | Cannot detect drives (SCSI pass-through) | Low | Yes (warns) |
| **WD Dashboard** | Cannot detect drives (SCSI pass-through) | Low | Yes (warns) |
| **Crucial Storage Executive** | Cannot detect drives (SCSI pass-through) | Low | Yes (warns) |
| **CrystalDiskInfo** | SMART monitoring may stop reading NVMe health (SCSI pass-through) | Medium | Yes (warns) |
| **Data Deduplication** | Microsoft confirms incompatibility | High | Yes (warns) |
| **Laptop / Battery** | APST broken, ~15% battery life reduction | Medium | Yes (warns) |
| DirectStorage games | BypassIO not supported, higher CPU | Low-Medium | Yes (warns) |

If you experience problems, use the **Remove Patch** button (or `-Silent -Remove`) and restart.

## Recovery Kit

The tool can generate a **WinRE-compatible Recovery Kit** -- a folder containing:

- **`Remove_NVMe_Patch.bat`** -- canonical entry point; verifies exact file count, byte lengths, and SHA-256 before any registry mutation
- **`Apply_Recovery_Mutation.bat`** -- guarded removal logic that auto-detects WinRE vs Windows, loads the offline SYSTEM hive, and removes the patch from all ControlSets
- **`NVMe_Remove_Patch.reg`** -- manual fallback after independent payload verification
- **`ARTIFACT-MANIFEST.json`** -- schema/tool version plus role, byte length, and SHA-256 for every required file
- **`README.txt`** -- step-by-step instructions for both Windows and WinRE recovery

A recovery kit is **automatically generated** after each successful patch installation. You can also create one manually via the **RECOVERY KIT** button or `.\NVMe_Driver_Patcher.ps1 -ExportRecoveryKit`.

**Copy this folder to a USB drive** before rebooting to have an offline recovery option if the system won't boot. Run `NVMeDriverPatcher.Cli verify-payload --input=<copied-folder>` after copying when a Windows support station is available; the recovery batch also fails closed on missing, extra, truncated, or modified required files.

Advanced hardening: `NVMeDriverPatcher.Cli.exe winre-inject` previews the DISM plan to inject `stornvme.inf` into the local WinRE image. `winre-inject --apply` backs up `winre.wim`, logs original/backup/final SHA-256 hashes, mounts to an app-owned temp folder, injects the driver, and commits or discards cleanly. After applying, boot into WinRE once and confirm the system volume is accessible.

## Scope

**This patch affects ALL NVMe drives** in your system that use the Windows inbox driver (`StorNVMe.sys`), not just the OS drive.

**Exception:** Drives using vendor-specific drivers (e.g., Samsung's proprietary driver) are not affected.

## Troubleshooting

### "No NVMe drives detected"
- Your drives may use vendor-specific drivers
- Check Device Manager > Disk drives > Properties > Driver
- If using Samsung/WD proprietary drivers, this patch won't help

### System won't boot after patch

**Option 1: Use the Recovery Kit (recommended)**
1. If you saved the Recovery Kit to USB before rebooting:
2. Boot to WinRE (hold Shift + Restart, or use Windows install USB > "Repair")
3. Open Command Prompt
4. Navigate to USB (try `D:`, `E:`, `F:`)
5. Run `NVMe_Recovery_Kit\Remove_NVMe_Patch.bat`
6. Restart

**Option 2: Manual WinRE removal**
1. Boot to WinRE > Troubleshoot > Advanced options > Command Prompt
2. Load the offline registry:
```cmd
reg load HKLM\OFFLINE C:\Windows\System32\config\SYSTEM
```
(If C: doesn't work, try D: or E: -- drive letters differ in WinRE)
3. Remove the patch:
```cmd
for /L %N in (1,1,9) do reg delete "HKLM\OFFLINE\ControlSet00%N\Policies\Microsoft\FeatureManagement\Overrides" /v 735209102 /f
for /L %N in (1,1,9) do reg delete "HKLM\OFFLINE\ControlSet00%N\Policies\Microsoft\FeatureManagement\Overrides" /v 1853569164 /f
for /L %N in (1,1,9) do reg delete "HKLM\OFFLINE\ControlSet00%N\Policies\Microsoft\FeatureManagement\Overrides" /v 156965516 /f
for /L %N in (1,1,9) do reg delete "HKLM\OFFLINE\ControlSet00%N\Policies\Microsoft\FeatureManagement\Overrides" /v 1176759950 /f
for /L %N in (1,1,9) do reg delete "HKLM\OFFLINE\ControlSet00%N\Control\SafeBoot\Minimal\{75416E63-5912-4DFA-AE8F-3EFACCAFFB14}" /f
for /L %N in (1,1,9) do reg delete "HKLM\OFFLINE\ControlSet00%N\Control\SafeBoot\Network\{75416E63-5912-4DFA-AE8F-3EFACCAFFB14}" /f
reg unload HKLM\OFFLINE
```
4. Restart

**Option 3: Wait for auto-recovery**
Windows automatically disables the native NVMe driver after 2-3 consecutive failed boots and reverts to the legacy stack.

### Custom/test-signed native NVMe workaround detected
Some community workarounds force `nvmedisk.sys` with a custom OEM INF and BCD TESTSIGNING. This tool warns on that evidence but will not automate or remove the route because it changes driver-store state outside the registry/FeatureStore rollback model. Capture evidence with `pnputil /enum-drivers /files`, then revert through Device Manager or remove the confirmed package with `pnputil /delete-driver <oem#.inf> /uninstall` only after you know which INF owns the binding.

### Can't boot into Safe Mode
This shouldn't happen if you used this tool (SafeBoot keys are included). If it does, follow the WinRE steps above.

### SSD vendor tools stopped working
Samsung Magician, WD Dashboard, Crucial Storage Executive, and CrystalDiskInfo use legacy SCSI pass-through to communicate with drives. The native NVMe driver doesn't implement this interface. Use Windows built-in tools (Device Manager, `Get-PhysicalDisk`, `Get-StorageReliabilityCounter`) for health monitoring instead, or remove the patch to restore compatibility.

## Credits & Sources

- **Microsoft TechCommunity** -- [Announcing Native NVMe in Windows Server 2025](https://techcommunity.microsoft.com/blog/windowsservernewsandbestpractices/announcing-native-nvme-in-windows-server-2025-ushering-in-a-new-era-of-storage-p/4477353)
- **Tom's Hardware** -- [Native NVMe Driver Benchmarks: Up to 64.89% Gains](https://www.tomshardware.com/pc-components/ssds/new-windows-native-nvme-driver-benchmarks-reveal-transformative-performance-gains-up-to-64-89-percent-lightning-fast-random-reads-and-breakthrough-cpu-efficiency)
- **Tom's Hardware** -- [Up to 85% Higher Random Workload Performance](https://www.tomshardware.com/pc-components/ssds/windows-11-rockets-ssd-performance-to-new-heights-with-hacked-native-nvme-driver-up-to-85-percent-higher-random-workload-performance-in-some-tests)
- **StorageReview** -- [Windows Server 2025 Native NVMe Benchmarks](https://www.storagereview.com/review/windows-server-native-nvme)
- **NotebookCheck** -- [Higher SSD Speeds with New Microsoft NVMe Driver](https://www.notebookcheck.net/Windows-11-hack-Higher-SSD-speeds-with-new-Microsoft-NVMe-driver.1190489.0.html)
- **Ghacks** -- [This Registry Hack Unlocks a Faster NVMe Driver in Windows 11](https://www.ghacks.net/2025/12/26/this-registry-hack-unlocks-a-faster-nvme-driver-in-windows-11/)
- **XDA Developers** -- [Windows 11 Free NVMe Speed Boost](https://www.xda-developers.com/windows-11-nvme-owners-free-speed-boost-enable/)
- **Overclock.net** -- [Community Testing Thread](https://www.overclock.net/threads/enable-native-nvme-driver-in-windows-11-24h2-25h2-with-last-update.1818467/)
- **Win-Raid Level1Techs** -- [Native NVMe Discussion](https://winraid.level1techs.com/t/discussion-microsofts-native-nvme-disk-drive-support/113111)
- **VeraCrypt** -- [Issue #1640: Breaks boot with native NVMe driver](https://github.com/veracrypt/VeraCrypt/issues/1640)
- **4sysops** -- [Windows Server 2025 Native NVMe Support](https://4sysops.com/archives/windows-server-2025-introduces-native-nvme-support-with-performance-gains-of-up-to-80-percent/)
- **StarWind** -- [Windows Server 2025 Native NVMe Support](https://www.starwindsoftware.com/blog/windows-server-native-nvme-support/)
- **Thomas-Krenn Wiki** -- [Activation of Native NVMe Driver](https://www.thomas-krenn.com/en/wiki/Activation_of_native_NVME_driver_in_Windows_Server_2025)

## Disclaimer

This tool modifies system registry settings to enable an **experimental, unsupported** feature on Windows 11. While safety measures are included (VeraCrypt detection, proved BitLocker recovery and one-reboot suspension, restore points, registry backups, rollback on failure, recovery kit generation), use at your own risk. The native NVMe driver is only officially supported on Windows Server 2025. Always ensure you have backups before making system changes.

---

<p align="center">
  <b>Made with coffee by <a href="https://github.com/SysAdminDoc">SysAdminDoc</a></b>
</p>
