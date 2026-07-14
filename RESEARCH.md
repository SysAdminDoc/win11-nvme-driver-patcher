# Research — NVMe Driver Patcher

## Executive Summary
NVMe Driver Patcher v5.0.0 is a local Windows 11 safety-and-recovery layer for enabling, verifying, and reversing Microsoft's still-experimental client `nvmedisk.sys` path. Its strongest shape is breadth around a narrow job: GUI/CLI/tray/watchdog surfaces, build-rule and firmware evidence, BitLocker/WinRE precautions, reversible registry and FeatureStore routes, a build-gated fallback ID catalog, diagnostic exports, and a large local test suite. The highest-value direction is not more storage features; it is making every elevated mutation truthful, durable, and trustworthy end-to-end. The prior pass's five P0s (raise the SQLite floor + per-connection defenses; gate apply/fallback/restart on trusted build rules; durably checkpoint fallback across reboot; preserve pre-existing SafeBoot state; make remove/auto-revert residue-verified) remain the load-bearing work and are already on the roadmap. This pass adds concrete, verified net-new gaps: (1) the ViVeTool fallback downloads and executes an **unverified** third-party binary as admin; (2) `ConfigService.Save` uses a fixed-name temp file that races across the four processes sharing `%ProgramData%`; (3) BitLocker suspension covers only the system drive, so protected NVMe *data* volumes re-lock after reboot with no warning; (4) the `compare-benchmarks` CLI compares the baseline to itself and can never report a regression; (5) the manual watchdog `/install` verb registers a malformed `sc.exe binpath=`; plus static-data drift in `windows_build_rules.json`/`compat.json`. Confidence: Verified unless labeled otherwise.

## Product Map
- Core workflows: assess readiness; choose Safe/Full or build-gated FeatureStore fallback; apply; reboot; prove driver binding; monitor storage events; remove/auto-revert; export recovery and support evidence.
- User personas: storage enthusiasts, workstation/homelab administrators, fleet operators using the CLI/PowerShell/ADMX, and support engineers diagnosing failed driver swaps.
- Platforms and distribution: Windows 11 24H2/25H2 x64 mutation path; diagnostic-only ARM64 builds; Server 2025 as the supported reference; portable EXEs, MSI, winget/Scoop/Chocolatey manifests, PowerShell module, ADMX, and Intune assets.
- Key integrations and data flows: 64-bit HKLM feature/SafeBoot state, Rtl Feature Store APIs, WMI/CIM/PnP evidence, BitLocker and WinRE, Event Log/watchdog state, `%ProgramData%\NVMePatcher` config + SQLite history, curated `windows_build_rules.json`/`compat.json`, and checksummed release assets.

## Competitive Landscape
- **GEAnalyticsLabs/native-nvme** (Python/Tkinter, v1.0.0 Dec 2025): a clean 3-phase Safety→Modify→Verify workflow with **reboot-resumable state** via a `%ProgramData%` state.json + an ONLOGON scheduled task, and a `manage-bde` poll-until-decrypted loop. Learn: adopt the scheduled-task ONLOGON resume as the durable pattern for the `PendingFallbackApplied` checkpoint. Avoid: its 25H2-Pro-only gate, no dry-run, no post-reboot bind proof.
- **1LUC1D4710N/nvme-performance-script** (PowerShell): ships a "nuclear cleanup" that removes the entire `Overrides` key structure, not just values. Learn: the residue probe on remove should also detect an emptied/deleted `Overrides` key. Avoid: restore-point-only recovery, no bind proof.
- **giosci1994/feature-overrides-registry** (PS+C#, Feb 2026): SHA256 integrity on release artifacts (now table stakes — this repo already does it). Avoid: only 3 IDs, no server key, no mitigation for the drive-duplication risk it documents.
- **ken-yossy/nvmetool-win** (C): reads NVMe Identify/log-page/SMART through the *inbox* driver IOCTLs. Learn: an inbox-IOCTL health path could keep SMART badges alive after vendor SCSI-passthrough tools break post-swap. Avoid: it is a diagnostic library, not an enabler.
- **thebookisclosed/ViVe (ViVeTool)**: the FeatureStore substrate the fallback shells out to; `Extra/FeatureDiction` is the community ID dictionary and the bellwether for ID drift. Issue #164 is the custom-INF/test-signing route this repo correctly refuses.
- **Driver Store Explorer / DiskSpd / CrystalDiskInfo**: keep the repo's bounded, itemized, honest-partial-failure model; do not expand into general driver-store management, a benchmark suite, or a SMART suite.
- **Macrium Reflect / Veeam Agent**: recovery-first UX and explicit destructive warnings are the lesson; full imaging/cloud rebuild stays outside remit (the recovery kit + blocked WinRE-inject item are the proportionate boundary).

## Security, Privacy, and Reliability
- Verified — **unverified elevated third-party execution**: the ViVeTool fallback downloads `ViVeTool.exe` with `RequireIntegrity = false` and `AllowAuthenticodeFallback = false` (`src/NVMeDriverPatcher.Core/Services/ViVeToolService.cs:287`), gated only by host allowlist + a size range cross-checked against the GitHub API's self-reported `size` (same channel), then executes it elevated (`:449`). A compromised release, repo transfer, or same-sized MITM payload passes every check. This is a strictly weaker trust link than the app's own updater (`RequireIntegrity = true`). No SHA-256 sidecar exists upstream, so there is currently *no* content verification on an admin-run binary.
- Verified — **cross-process config write race**: `ConfigService.Save` writes to a fixed `config.json.tmp` (`ConfigService.cs:207`) in shared `%ProgramData%`, which GUI, CLI, Tray, and Watchdog all target. Concurrent saves collide (`FileMode.Create`/`FileShare.None` throws for one writer; a `File.Move` can clobber the other's half-written temp), so the "atomic" write is not atomic across processes and a settings/state write can be silently dropped (only a Warning is logged).
- Verified — **BitLocker suspension is system-drive-only**: `SuspendBitLocker` acts on `%SystemDrive%` alone (`PatchService.cs:352`), yet the swap changes the driver stack for *all* NVMe controllers. A BitLocker-protected non-system NVMe volume without auto-unlock re-locks on the post-patch boot; unlike the HotSwap path (which warns via `DescribeBitLockerRisk`), apply neither warns nor suspends it. Access-loss, not data-loss.
- Verified — **Authenticode fallback checks validity, not identity**: `VerifiedDownloader.VerifyAuthenticode` runs `signtool verify /pa` (chain/policy only), and `AutoUpdaterService` sets `AllowAuthenticodeFallback = true`. With no `.sha256` sidecar present, any validly-signed binary at the asset URL is accepted for the in-place self-replace; there is no expected signer/thumbprint pin. Low real-world exposure because releases always ship sidecars, but the fallback is unpinned.
- Verified — **FeatureStore write lock is process-local**: `FeatureStoreWriterService.WriteLock` is a `SemaphoreSlim(1,1)` (`FeatureStoreWriterService.cs:54`), but `RtlSetFeatureConfigurations` mutates machine-global state. Two elevated processes can interleave the Runtime-then-Boot two-phase write; the boot-failure rollback only reverts this process's Runtime write, risking a split Runtime/Boot state the verifier then reports as partial.
- Verified — **pre-patch `.reg` backup cannot undo an install**: `RegistryService.ExportRegistryBackup` records only pre-existing values (`:152`); re-importing it never deletes the keys the patch later adds (a real revert needs `"id"=-` and `[-...GUID]` directives). Recovery messaging that points users at this backup (`PatchService.cs:316`) over-promises in exactly the incomplete-rollback path.
- Verified — **dead regression detector**: `CompareBenchmarksCommand` calls `AutoBenchmarkService.Compare(baseline, baseline, threshold)` (`Cli/Program.cs:494`); identical inputs always yield `Regressed = false` and exit 0, so a fleet script gating on this exit code gets false confidence.
- Verified — **malformed manual service install**: the watchdog's own `/install` verb passes `binpath=` + a pre-quoted path through `ProcessStartInfo.ArgumentList` (`Watchdog/Program.cs:67`), which re-escapes the embedded quotes; on any spaced install path the `ImagePath` registers wrong and the service won't start. The MSI is unaffected (it uses a WiX `<ServiceInstall>`), so this only hits the documented manual route.
- Verified — **restart timeout reported as success**: `InitiateRestart` returns `true` when `shutdown.exe` doesn't exit within 5s (`PatchService.cs:867`); in the `--unattended` CLI flow this suppresses the "run shutdown manually" hint, so a machine that never actually reboots is indistinguishable from a queued restart.
- Verified — privacy remains appropriately local-first: compat telemetry is explicit, anonymized, GPO-controllable, with no default receiver. Do not turn curated compat/build data into an unsigned auto-update channel.
- Verified — issue #12: the shipping MSI shows lorem ipsum; `packaging/wix/README.md:21` also misstates the watchdog account (LocalSystem vs the installed LocalService).
- Verified report; ACL cause needs live validation — issue #13 (build 26200.8737): the SafeBoot GUID keys already exist **in-box** with a named `NvmeDisk` REG_SZ value and deny writes. Microsoft is landing this state natively, so the write-then-delete-subtree model can erase OS-owned pre-state.

## Architecture Assessment
- Introduce one trusted action-policy boundary over `WindowsBuildRulesService`; GUI, CLI, fallback, dry-run, and restart decisions should consume the same disposition instead of re-interpreting advisory preflight strings.
- Make `PatchVerificationService.Evaluate()` a pure read; run fallback recovery through a one-shot coordinator that owns reset, persistence, retry state, and user-visible outcome (tray/dashboard polling must never mutate FeatureStore state).
- Give `ConfigService` an explicit persisted-state contract with round-trip + schema-parity tests, a per-process-unique temp name, and a cross-process mutex around the write.
- Register a `DbConnectionInterceptor` (sync + async open) in `AppDbContext.OnConfiguring()` so every EF connection receives the checked SQLite defenses; keep `EnsureCreated()` for schema/WAL init only.
- Treat SafeBoot edits as a reversible transaction: classify writable/already-correct/conflicting/denied GUID keys before writing, never take over ACLs, journal exact prior state durably, and restore byte-for-byte / delete only app-created state.
- Refactor removal into per-component outcomes plus a final residue probe (registry overrides, owned SafeBoot entries, fallback IDs); success only after a zero-residue re-read.
- Serialize the machine-global FeatureStore write with a named mutex, not a process-local semaphore.
- Static safety data (`windows_build_rules.json`, `compat.json`) drifts against a fast-moving target and needs a periodic, sourced review pass, not one-time authorship.
- Testing is broad; keep the fresh/stale provenance fixtures on an injected clock (already on the roadmap) so the canonical suite passes on any calendar date.

## Rejected Ideas
- Build-aware per-branch ViVeTool ID selection as *new* work — Source: 2026 ID-drift reports (elevenforum 46678, Win-Raid 113111). Reason: **already implemented** in `FallbackFeatureCatalog.SelectForBuild`; only incremental data refresh (e.g. evaluating the community `1409234060` client ID) is warranted, not a rebuild.
- Automate the custom-INF/test-signing workaround — Source: ViVe issue #164. Reason: modifies/resigns inbox storage-driver matching and may need test-signing/Secure Boot changes outside the rollback model.
- Integrate Windows Cloud rebuild as an app action — Source: Microsoft Cloud rebuild preview. Reason: it reformats the system disk; link only as last-resort recovery documentation.
- Add firmware flashing, secure erase, SMART prediction, or general driver cleanup — Source: Samsung Magician, Solidigm Storage Tool, Driver Store Explorer. Reason: vendor/general tools own these risky domains; only compatibility guidance fits.
- Build a full rescue-imaging product — Source: Macrium, Veeam. Reason: the recovery kit + blocked WinRE-inject item are the proportionate boundary.
- Auto-download unsigned build rules or promote raw telemetry into `compat.json` — Source: current curated data model. Reason: action gating must not trust unreviewed community state.
- Add NVMe-oF / NVMe 2.2 command tooling or device-fuzzing — Source: NVMe-oF Initiator Server preview, NVM Express 2.2. Reason: adjacent but unrelated to the Windows client class-driver swap; monitor as MS's investment direction only.
- Expand into plugin ecosystems, mobile, multi-user/server control, full i18n, or theme work — Source: repo scope rule. Reason: none improves enable/verify/rollback; MSI product strings can be localized without broad product localization.

## Sources
Official platform and standards:
- https://techcommunity.microsoft.com/blog/windowsservernewsandbestpractices/announcing-native-nvme-in-windows-server-2025-ushering-in-a-new-era-of-storage-p/4477353
- https://learn.microsoft.com/en-us/windows-insider/release-notes/experimental/preview-build-26300-8758
- https://learn.microsoft.com/en-us/windows-hardware/drivers/storage/stornvme-command-set-support
- https://learn.microsoft.com/en-us/windows-hardware/design/component-guidelines/power-management-for-storage-hardware-devices-nvme
- https://learn.microsoft.com/en-us/windows-hardware/drivers/ifs/bypassio
- https://learn.microsoft.com/en-us/windows-hardware/drivers/devtest/pnputil-command-syntax
- https://learn.microsoft.com/en-us/powershell/module/storage/get-physicaldisk?view=windowsserver2025-ps
- https://learn.microsoft.com/en-us/ef/core/logging-events-diagnostics/interceptors

Dependencies, security, and installer:
- https://sqlite.org/cves.html
- https://www.sqlite.org/releaselog/current.html
- https://www.nuget.org/packages/SourceGear.sqlite3/
- https://docs.firegiant.com/wix3/howtos/ui_and_localization/make_installer_localizable/

Native-NVMe ecosystem and community:
- https://github.com/SysAdminDoc/win11-nvme-driver-patcher/issues/13
- https://github.com/SysAdminDoc/win11-nvme-driver-patcher/issues/12
- https://github.com/thebookisclosed/ViVe/issues/164
- https://github.com/GEAnalyticsLabs/native-nvme
- https://github.com/1LUC1D4710N/nvme-performance-script
- https://github.com/giosci1994/feature-overrides-registry
- https://github.com/ken-yossy/nvmetool-win
- https://winraid.level1techs.com/t/discussion-microsofts-native-nvme-disk-drive-support/113111
- https://www.elevenforum.com/t/windows-11-25h2-nvmedisk-sys-driver-support.46678/
- https://www.overclock.net/threads/enable-native-nvme-driver-in-windows-11-24h2-25h2-with-last-update.1818467/
- https://www.tomshardware.com/software/windows/microsoft-blocks-the-registry-hack-trick-that-unlocked-native-nvme-performance-on-windows-11
- https://www.ghacks.net/2025/12/26/this-registry-hack-unlocks-a-faster-nvme-driver-in-windows-11/

Firmware/hardware advisories:
- https://www.heise.de/en/news/Against-blue-screens-Important-firmware-updates-for-Western-Digital-SSDs-9984513.html
- https://support-en.sandisk.com/app/answers/detailweb/a_id/51469
- https://eu.community.samsung.com/t5/computers-it/990-pro-2tb-disappearing-firmware-4b2qjxd7/td-p/12822796
- https://www.neowin.net/news/wd-sn850x-nvme-ssd-that-beat-samsung-seagate-crucial-hynix-bsoding-freezing-windows-11/
- https://rossmanngroup.com/problems/windows-11-24h2-ssd-missing
- https://community.intel.com/t5/Intel-Optane-Solid-State-Drives/Win11-newest-update-destroy-VMD-driver/m-p/1722976

## Open Questions
- Samsung 990 Pro firmware truth: community sources report `4B2QJXD7` as the *bad* disappearing-drive revision fixed by `6B2QJXD7`, while `compat.json` currently cautions `7B2QJXD7` and recommends updating *to* `4B2QJXD7`. Which revision is actually degraded for the native-stack case needs a first-party Samsung confirmation before editing the entry. (Blocks correct `compat.json` data only.)
- Exact registry-override block build: press reports the hard block at Insider `26100.8106`; `windows_build_rules.json` models it fuzzily around `26100`/`26200.8524+`. Confirming the precise UBR would let `status` be honest per-branch. (Data precision, not prioritization.)
- Hardware-only validation (WinRE inject `--commit`, ARM64 launch, debloat/LTSC bind reproduction) remains isolated in `Roadmap_Blocked.md`.
