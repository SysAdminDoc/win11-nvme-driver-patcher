# Research: NVMe Driver Patcher

Pass date: 2026-10-06. Repo at `82038af` on `main`, version strings 5.7.0, newest published release v5.6.0 (2026-08-08). This replaces the 2026-08-11 pass.

Labels: **Verified** means I read it at the primary source or reproduced it. **Likely** means a secondary source, or a primary source I couldn't read in full. **Assumption** is my own inference. **Needs live validation** means only a test machine can settle it.

## Executive Summary

1. **On every current retail client build, the tool has no route it will apply.** `windows_build_rules.json` (reviewed 2026-10-05) resolves 24H2 26100.x, 25H2 26200.8524 and later, and 26H2 26300.x to `expectedPath: none-known`. On 24H2 the forum's three-value set (3244671118, 1853569164, 156965516) does bind, as the 2026-10-05 VM test showed. But it needs 156965516, the value that makes `DISM /ScanHealth` report component store corruption (#19), and Windows deletes the tool's own primary value 735209102 at boot. So for most users today the product is verify, monitor and roll back. Verified (rules file, VM log).
2. **A third bind route has surfaced, and it skips feature IDs entirely.** Two authors describe it independently: revoconner (blog, 2026-09-22, reported working on 24H2 26100.8875 and 26100.9168) and St1cky (repo with reverse-engineering notes, 2026-10-05, found on 25H2 26200.9168). `storport!RaDriverAddDevice` reads a per-controller `EnableNVMeInterface` DWORD from `HKLM\SYSTEM\CurrentControlSet\Enum\<stornvme controller>\Device Parameters\StorPort` after the feature decision, and checks a global `HKLM\SYSTEM\CurrentControlSet\Control\StorPort\DisableNativeNVMeStack` first, which wins. Likely: two independent write-ups, no repro of ours, no license on either repo. This matters three ways. Neither value is read anywhere in `src/` today, so a leftover from those tools makes our verdicts wrong without naming the cause. The kill switch could become a one-value offline revert that works for every route. And the per-controller value may be the first working route on 25H2 and 26H2, where this tool is verify-only.
3. **.NET 10.0.12 (2026-09-08) fixes six CVEs, and taking it means leaving the 10.0.3xx SDK band.** The embedded-runtime floor is 10.0.11 (`Directory.Build.props:10`, `scripts/Validate-ReleaseAssets.ps1:24`) and `global.json` pins SDK 10.0.303. That band never got a 10.0.12 SDK; 10.0.401 and 10.0.112 carry it. The app runs elevated, and the 10.0.11 list it already took had elevation-of-privilege and RCE entries. Verified.
4. **Smart App Control is the install risk for an unsigned tool.** Since April 2026 a 25H2 user can turn SAC back on after turning it off. It blocks unsigned `.exe`, `.msi` and `.ps1` files that carry Mark of the Web, and the block dialog has no override. Nothing in the repo detects SAC or tells users what to do. The GUI exe is what people download (1,072 downloads of v5.6.0's `NVMeDriverPatcher.exe` against 235 for the MSI), so it's the file that gets blocked. Verified for SAC behavior; Needs live validation for this app.
5. **Release binaries are unsigned and no roadmap item tracks signing.** `Validate-ReleaseAssets.ps1 -ExpectSigned` already exists, but `Build-ReleaseArtifacts.ps1` has no signing step. Azure Artifact Signing (about $9.99 a month, individuals in the US and Canada) is the route that fits, because it signs from a local `signtool` run. SignPath Foundation is free for open source, but it requires origin verification from a trusted CI build system, and this repo doesn't build in CI. An EV certificate no longer skips SmartScreen reputation. Verified.
6. **The in-app updater's only integrity check is a SHA-256 file from the same release.** `AutoUpdaterService` accepts only `IntegritySignal.Sha256Sidecar`; anyone who can replace the exe on the release can replace its sidecar too. It does refuse equal or older versions (`UpdateService.cs:88`). A manifest signed with a key whose public half ships inside the app (NetSparkle's model) closes that gap without buying a certificate. Verified (code), Likely (NetSparkle docs).
7. **26H2 reached general availability on 2026-09-29** as enablement package KB5121794 (26300.x) on the shared 26100 servicing core. The `26300-feature-flags-page` rule still calls 26300 "26H2 Experimental". The existing P3 build-rules item already covers the enablement-package fact and now has primary evidence. Verified for the KB and date, Likely for the shared branch.
8. **DiskSpd 2.3 (2026-09-04) changed two defaults** (P-cores ordered before E-cores, buffers separated by cache line) and added BypassIO and IoRing modes. The tool pins v2.2 by hash (`BenchmarkService.cs:28`), which keeps runs comparable, but results don't record which DiskSpd produced them, so a later bump would mix incomparable runs without warning. Verified.
9. **The MSI is 211 MB because it carries the .NET runtime four times.** v5.6.0's MSI and Intune zip are both 211 MB. The GUI (89 MB), Tray (57 MB), CLI (44 MB) and Watchdog (44 MB) are each published self-contained and single-file, so each embeds its own runtime. An MDL user complained about the size in 2026 and another had to explain it. Publishing the four into one directory that shares a single runtime copy keeps the no-prerequisite install and should cut most of that. Verified for sizes, Assumption for the saving.
10. **v5.7.0 was never published.** The CHANGELOG has `## [5.7.0] - 2026-08-11` and every version string says 5.7.0, but the newest tag and GitHub release is v5.6.0. The repo's working notes and the vault hub both claim 5.7.0 shipped. Verified (`gh release list`).

## What the last pass changed

Most of the 2026-08-11 pass's top items have shipped (CHANGELOG `[5.7.0]` and `[Unreleased]`): the .NET 10.0.11 floor and its gate, the curated `feature_ids.json` with default-state classes, BypassIO read from registry and PnP evidence, Point-in-Time Restore and Quick Machine Recovery as recovery evidence, the NvmeDisk ETW profile, the QD1 benchmark, ViVeTool disclosure, the Intune Check/Remediate pair, 3244671118 detection, the NuGet audit gate, SQLitePCLRaw 3.0.4 and the telemetry receiver fixes. The Samsung 990 Pro firmware question was settled in `compat.json` on 2026-10-05. Two of its open questions are still open (see the end of this file).

## Product Map

- **Core workflow:** assess (about 26 preflight checks, build rules, `compat.json`), then pick Safe, Full (156965516 is now its own opt-in) or the build-gated FeatureStore route. Apply suspends BitLocker, takes a restore point, writes the mutation ledger and a schema 2 SafeBoot journal, and mirrors control sets. After the reboot it proves the bind; the watchdog and persistence guard keep watching. Remove and auto-revert prove there's no residue. Around that sit the recovery kit, the WinPE builder, WinRE stornvme injection and the support bundle.
- **Personas:** enthusiasts and gamers (the MajorGeeks audience), workstation and homelab admins, fleet admins (CLI, PowerShell module, ADMX, Intune), and whoever ends up diagnosing a failed swap.
- **Where it works:** Server 2025 is the supported reference. On client, see Executive Summary item 1: every current retail build is verify-only. 26100.8106 and 25H2 builds below 8524 keep their ViVeTool-route rules. ARM64 is diagnostic only.
- **Distribution:** portable GUI and CLI exes, MSI (WiX 5.0.2), PowerShell module, ADMX/ADML, an Intune bundle (Detect plus a Check/Remediate pair), Chocolatey nupkg and Scoop manifest (built, never published), and the in-app updater. No CI builds, by policy.
- **Data flows:** HKLM FeatureManagement overrides and SafeBoot keys, the Rtl Feature Store, WMI/CIM/PnP, BitLocker, `reagentc` and WinRE, event logs and the NvmeDisk ETW provider, `%ProgramData%\NVMePatcher` config and SQLite history, curated JSON (build rules, feature IDs, compat), the GitHub releases API, and an optional Cloudflare Worker telemetry receiver.

## Competitive Landscape

There's still no second application that pairs a ledger-backed rollback with a post-reboot bind proof. What changed since August is that the tweak itself has spread into general tweak packs and single-purpose scripts, which means more users arrive with someone else's leftovers on the machine.

- **St1ckyNew/25H2-NVMe-Native-Stack-Support** (PowerShell/WinForms, 0 stars, created 2026-10-05, no license). The StorPort route as a GUI. **Learn:** it walks the disk's PnP parent chain to the devnode whose service is `stornvme` before writing anything, it dedupes controllers that expose several namespaces, it tells people to try a secondary drive before the boot drive, and it proves the result by `Class = NvmeDisk`, `Service = nvmedisk` and a `GenNvmeDisk` hardware ID. **Avoid:** no ledger, no SafeBoot handling, no rollback beyond "delete the value and reboot". Verified (README read).
- **revoconner/Windows-11-24h2-nvmedriver** and the 2026-09-22 blog (0 stars, no license). The first public write-up of `EnableNVMeInterface`. **Learn:** a plain "don't do this if" list (VeraCrypt system encryption, Intel VMD/RST, Storage Spaces, backup tools keyed to disk number or PnP path, no backup), all of which this tool already checks. It also says Windows now ships the SafeBoot class GUID key itself, which matches the 2026-10-05 VM. **Avoid:** manual `reg delete` rollback, debug scripts only. Likely.
- **Oblivionevil/nvme-stack-pilot** (.NET Framework 4.8.1, German UI, 1 star, last push 2026-08-02, no license). The closest feature match: it rolls back a partial failure, removes known older IDs, reads the actual stack and verifies SafeBoot. **Learn:** nothing this tool lacks. **Avoid:** its 24H2-only scope and the Framework runtime. Likely (repo metadata and README via search).
- **FR33THYFR33THY/Ultimate**, `8 Advanced/19 NVME Faster Driver.ps1` (20 stars; the repo was recreated 2026-09-16, so the 631 stars cited in August belonged to the old one). Still writes 735209102, 3244671118, 1853569164 and 156965516 plus the SafeBoot GUID keys, and still reverts by deleting `HKLM\SYSTEM\CurrentControlSet\Policies\Microsoft` outright. **Learn:** nothing new. The tool already detects every piece of that state. Verified.
- **Espionage724/Windows**, `Enable Native NVMe.bat` (235 stars, last push 2026-07-22). The most-starred script. Its author reported Safe Mode working on 26100.8457 with the three-value set and no extra keys. **Avoid:** no verification, no rollback. Likely.
- **synoxvf/NOVA** (26 stars, pushed 2026-10-06) and **isleap9/Akari-Toolbox** (pushed 2026-10-04). General tweak packs with a native NVMe toggle. **Learn:** the tweak is becoming a checkbox in debloat suites, so detection of foreign state is worth more every month. Likely (not read in depth).
- **bbmaster123/FWFU** (28 stars, pushed 2026-09-30, no license). Feature ID lists for 26300.8068, 26300.8376, 26340.9233, 27982 and 28000. **Learn:** it's the only source I found with 26H2-era IDs; phantomofearth's velocity dumps have no 26300 file and no commits after 2026-06-15. **Avoid:** vendoring it, for the same license reason. Verified (listing).
- **thebookisclosed/ViVe** (7,872 stars). No release since v0.3.4 (2025-03-10). Issue #164 (2026-06-01) is the record of the override and ViVeTool routes failing on 26200.8524; the maintainer's 2026-06-28 reply points at "another feature ID or registry key in the mix", which the StorPort value may turn out to be. Verified.
- **Windows tweakers: Sophia Script 7.3.0 (2026-09-05), Win11Debloat 2026.08.24, winutil 26.09.29.** Sophia pairs every tweak with a function that restores the default. Win11Debloat points at a wiki for undo, and its #576 ("Lost my restore points", 2026-05-08) shows why System Restore alone isn't a rollback plan. winutil has no undo and a run of Windows Update complaints (#3214, #3651, #3755). **Learn:** this tool's ledger and byte-exact restore are already ahead of all three. **Avoid:** presets without proof. Verified (READMEs and issue titles).
- **Windows itself.** Server 2025 has the official opt-in. The Feature Preview MSI's GPO only writes `1176759950`, the Server key (MDL, 2026-03-25). 26H2 shipped with no client toggle. Point-in-Time Restore reached all Windows 11 PCs in July 2026 and can use up to 50 GB. Likely for the PiTR size, Verified for the rest.

## Reported Issues

The repo's own tracker on 2026-10-06: two open issues, five closed, ten Dependabot PRs (all closed; the bot isn't allowed here any more) and two discussions with no replies since 2026-07-30. 76 stars, 3 forks, 192 clones in the last 14 days. v5.6.0 downloads: GUI exe 1,072, MSI 235, CLI 193, PowerShell module 95, legacy script 83.

- **#18, Intel RST false block (2026-09-27, open).** An AMD B650 machine was blocked as "Intel RST/VMD driver evidence is present", and a second user confirmed it. Windows ships Intel's `iaStorAVC` on every PC whether or not it's used, and the check counted it. Fixed in Unreleased: the block now needs the driver loaded or set to start at boot. Waiting on a release and the reporter's word.
- **#19, component store corruption (2026-10-02, open).** Reproduced on 26100.9550: override 156965516 makes `DISM /ScanHealth` report payload corruption in WinSxS rollback copies, SFC stays clean, and removing the value clears it. Fixed in Unreleased by making 156965516 a separate opt-in that's off by default. MDL users reproduced the DISM report on their own (posts #216 on 2026-09-26 and #218 on 2026-09-30), but nobody there isolated which ID causes it.
- **#15, chkdsk and Bootstat.dat (closed 2026-08-08).** A boot-time `chkdsk /f` or a missing `Bootstat.dat` puts the machine back on `disk.sys`. MDL had the same report on 2026-03-07. It led to `PersistenceGuardService`.
- **#13, SafeBoot key access denied (closed 2026-07-29).** The VM later showed why: on recent builds Windows ships the SafeBoot class GUID keys itself, owned by TrustedInstaller.
- **#12 (MSI placeholder text), #14 (blank support bundle path), #1 (SafeBoot)**, all closed.

Four of the seven issues are about SafeBoot or recovery state (#1, #13, #15, #19). That's also where most fixes since 2026-08-11 landed: 42 of the 100 commits since then are plain `fix:`.

Outside the tracker (MDL thread pages 9 to 12, r/windows, ElevenForum, WinRAID):

- **"Does it still work?"** is the most common question, and the honest answer depends on the build. 25H2 26200.8106 stopped binding on 2026-03-13 and 26200.8116/8117 worked again by 2026-04-01 with the three-value set or ViVeTool IDs 60786016 and 48433719.
- **Safe Mode keys** were argued over from 2026-04-27 to 2026-05-23: the nvmedisk service key, the class GUID key, or both. The tool writes and journals both, and treats a GUID key Windows already owns as Windows'.
- **Is it worth it?** The 2026-08-30 r/windows thread (25 comments) says benchmark gains are real and daily use feels the same. Several people care more about stability than speed, and one says DirectStorage broke.
- **26H1 data damage**, a single report (2026-06-06): files with their first sector zeroed after enabling on 28000. Unverified, but it supports keeping 26H1 at `none-known`.
- **Mixed drivers.** One user (2026-10-05) says every SSD has to be on Microsoft's driver or performance drops. `DriveService` already flags third-party NVMe drivers.
- **This project at MDL.** Recommended on 2026-03-25 for its checks, backups and Safe Boot support; credited with fixing re-enable on 26100.7628 (2026-04-02); criticized for the installer's size (see Executive Summary item 9).

## Security, Privacy, and Reliability

- **Update trust.** Covered in Executive Summary item 6. Downgrade protection exists; a signature doesn't. Verified (`AutoUpdaterService.cs:140`, `UpdateService.cs:86-88`).
- **Unsigned code under Windows' own gates.** Smart App Control (item 4), SmartScreen reputation and App Control for Business all treat an unsigned file as unknown. App Control's script enforcement runs unallowed scripts in Constrained Language Mode rather than blocking them. I read the three Intune scripts against that: they use only types CLM allows, and the one that isn't (`[Console]::OutputEncoding` in `Remediate-NVMeDriverPatcher.ps1:69-72`) sits in a try/catch, so they degrade cleanly. The PowerShell module already exports functions by name (`NVMeDriverPatcher.psd1:14`), which App Control requires. The CLI exe those scripts call still needs an allow rule, and only a signature makes that a one-line policy. Verified (code), Likely (App Control docs).
- **Runtime CVEs.** Item 3. The 10.0.12 CVE IDs are CVE-2026-69439, 71328, 69522, 69304, 58649 and 69806; Microsoft hadn't typed them when I read the notes. Verified for IDs.
- **Foreign storage-stack state.** The tool already detects what FR33THY's script leaves behind (3244671118, a deleted `Policies\Microsoft` tree, orphaned SafeBoot entries). The StorPort values in item 2 are the next thing other tools will leave behind, and they're stronger than any override: a stray `DisableNativeNVMeStack=1` makes every route fail, and a stray `EnableNVMeInterface=1` keeps nvmedisk bound after our Remove. Likely.
- **Recovery.** The recovery kit and WinPE builder revert by removing override values offline. If `DisableNativeNVMeStack=1` really forces the legacy path ahead of every feature decision, it's a single value to set from WinRE no matter which route bound the drive. Needs live validation.
- **Locale.** Re-checked on 2026-10-06: `PerControllerAuditService` reads `pnputil` XML, `PreflightService` keys the `bcdedit` parse on the `testsigning` element name, and `WinReBcdPrepService` reads `reagentc` XML. No new locale traps. Verified.
- **Installer toolchain.** WiX 5.0.2 is the last 5.x; WiX 7.0.0 shipped 2026-04-06 with the OSMF EULA, and 6.0.2 is the last 6.x. A search result says v3 to v5 left community support on 2026-02-06; the FireGiant policy page doesn't give a v5 date. Likely.
- **Dependencies.** No advisories for SQLite, SourceGear, EF Core, CommunityToolkit.Mvvm or xunit as of 2026-10-06. SQLite 3.53.4 (2026-07-24) lists no security fixes over 3.53.3. SkiaSharp 4.153.1 (2026-09-30) is out; I couldn't find which libpng, freetype, harfbuzz and expat versions it bundles. Wrangler 4.148.0 deprecates nothing the receiver uses (KV, `[[ratelimits]]`). Verified for versions.
- **Benchmark provenance.** Item 8. The DiskSpd download is already hash-pinned and fails closed, which is the right bar for an exe the app runs elevated. Verified.
- **Privacy.** Nothing new. The support bundle and telemetry receiver were re-checked in the 2026-08 audit and nothing in this pass touches them.

## Architecture Assessment

- **Routes are modeled as machine-wide feature-ID sets.** `FallbackFeatureCatalog`, `AppConfig.RegistryPath`, the ledger and the SafeBoot journal all assume values under fixed machine-wide keys. A StorPort route is per controller: its key lives under that controller's `Enum` instance path, which changes if the drive moves to another slot or the controller re-enumerates. If that route lands, the ledger needs a device-scoped entry that records the controller's hardware ID and instance path, and Remove needs to find the controller by hardware ID when the path has moved. Assumption.
- **Detection lives in several places.** Leftover-state checks are in preflight, readiness, the dry run and the support bundle. Adding the two StorPort values should go through one reader that all four call, the way 3244671118 detection was done, so they can't disagree. Verified (code layout).
- **The build-rules data is now the product's front door.** With every current client build at `none-known`, the first screen most users see is a verify-only verdict. The existing P3 "first-run expectation gate" item gets more important for that reason.
- **The updater has check, download and hash verify, but no signature layer.** .NET's base library has ECDSA P-256 built in, so a signed manifest needs no new package. Assumption that the BCL in .NET 10 still has no Ed25519.
- **Benchmarks lack provenance fields.** A result should carry the DiskSpd version, its hash and the affinity and buffer flags in effect, and the compare view should refuse to diff runs from different DiskSpd versions.
- **Tests.** 1,719 discovered tests (`Validate-DocumentationFacts.ps1`, 2026-10-05). xunit 2.9.3 is deprecated; xunit.v3 4.0.1 (2026-09-12) and xunit.runner.visualstudio 4.0.0 are now stable, which the existing xunit v3 item can use.

## Prioritization

Fit, impact (1 to 5), effort (S, M, L, XL), risk and novelty for each new item. The roadmap holds the full entries.

| Bucket | Item | Impact | Effort | Risk | Novelty |
|---|---|---|---|---|---|
| Now | Detect the StorPort overrides | 4 | M | Low (read-only) | Ahead of the field; no other enabler reads them |
| Now | Test the StorPort route and kill switch in VMs | 5 | L | Contained to VMs | Would be the first tested route on 25H2/26H2 |
| Now | Ship on .NET 10.0.12 | 4 | S | Low | Table stakes |
| Next | Smart App Control detection and guidance | 4 | M | Low | No adjacent tool does this |
| Next | Sign release binaries (Azure Artifact Signing) | 4 | M | Needs the owner's enrollment and a monthly fee | Table stakes |
| Next | Signed update manifest | 3 | M | Key handling | Ahead of most single-dev tools |
| Next | Drop the winget channel | 2 | S | Low | Policy alignment |
| Next | Cumulative update test with 156965516 set | 4 | M | Contained to VMs | Settles whether 24H2 gets a route |
| Later | DiskSpd provenance in results | 2 | S | Low | Parity with good benchmark practice |
| Later | Identify and SMART reads under nvmedisk | 3 | S | Low | Fills a gap the vendor tools leave |
| Later | One shared runtime in the MSI | 3 | M | Medium (installer layout) | Answers a real complaint |
| Later | 5.7.0 CHANGELOG section with no release | 2 | S | Low | Hygiene |
| Later | Dependency refresh (SQLite, Test SDK, SkiaSharp, wrangler) | 2 | S | Low | Hygiene |
| Under consideration | WiX 5 exit | 2 | L | Medium | n/a |
| Under consideration | Listing on curated tool lists | 1 | S | Low | n/a |

Categories with no new item, and why: accessibility (the services, high-contrast theme and tests exist, and nothing new turned up), i18n (product translation stays rejected; I re-read the `pnputil`, `bcdedit` and `reagentc` parses and they key on XML or unlocalized element names, so there are no new locale traps), observability (the support bundle and ETW profile exist; the StorPort and Smart App Control items add fields to them), offline use (everything but the updater already works offline, and the kill-switch test is about offline recovery), migration and upgrade (the MSI item tests an upgrade over 5.6.0; config downgrade warnings are already on the roadmap), plugin ecosystem, mobile and multi-user (rejected below). "Listing on curated tool lists" stays under consideration because awesome-sysadmin's contribution rules couldn't be read, and promotion is the owner's call.

## Rejected Ideas

Carried forward, still valid:

- **Vendor the velocity dump files.** Source: phantomofearth repo. That repo has no license and gets no commits after 2026-06-15; transcribe rows with citations instead.
- **Own the FeatureStore RPC/ABI and drop ViVeTool.** Source: ViVe dormancy. `FeatureStoreWriterService` is already the primary path; owning an undocumented ABI on a boot-critical store buys little.
- **Move WPF to WinUI 3.** Source: platform survey. Nothing in 2026 gives WinUI 3 a storage or recovery API WPF lacks, and it'd cost the self-contained elevated-launch story.
- **Automate the custom-INF or test-signing workaround.** Source: ViVe #164. Re-signing inbox storage drivers falls outside the rollback model.
- **Firmware flashing, secure erase, SMART prediction, driver-store cleanup, rescue imaging, NVMe-oF tooling, a plugin ecosystem, mobile, multi-user control, full product translation.** Source: vendor tools and scope rule. None improves enable, verify or rollback. Locale-independent probing stays a correctness concern; translating the UI doesn't.

New this pass:

- **Bundle or adapt St1cky's script.** Source: that repo. It has no license, and the claims are unverified. Implement from the documented registry facts only after the VM test.
- **Sigstore or `gh attestation` for local builds.** Source: GitHub attestation docs. `gh attestation` only covers Actions builds (banned here), and cosign is one more tool users won't run. A signed manifest checked inside the app gives every user the check for free.
- **SignPath Foundation for code signing.** Source: SignPath origin-verification docs. Open-source signing there requires signing requests from a trusted CI build system, and this repo builds locally by policy. Azure Artifact Signing works from a local build.
- **Switch the updater to NetSparkle or Velopack.** Source: their docs. Only the signature idea is worth taking. Replacing a working updater adds a dependency, and I couldn't find Velopack's integrity docs.
- **Post-update drift detection.** Source: adjacent tool complaints (Win11Debloat #464, winutil #3755). Already built: `PersistenceGuardService` and `PatchVerificationService` both handle values Windows took away.
- **A VeraCrypt preflight.** Source: VeraCrypt #1640. Already a hard block in GUI and CLI (`MainViewModel.Commands.cs:47`, `Program.cs:1327`).
- **A vendor-tool warning before apply.** Source: borecraft and gigxp. Already there (`MainViewModel.cs:1317`, `DriveService.cs:815`), along with the firmware-update disable workflow.
- **Accessibility rework.** No new evidence. `AccessibilityService`, the high-contrast theme and its tests already exist.

## Sources

Microsoft and platform:
- https://support.microsoft.com/en-us/servicing/os/windows-11/2026/09/kb5124008-windows-11-24h2-25h2-security-update
- https://support.microsoft.com/en-us/help/5124010
- https://support.microsoft.com/ja-jp/servicing/os/windows/safeos-du/2026/08/kb5121002-windows-11-24h2-25h2-safeos-du
- https://learn.microsoft.com/en-us/windows-insider/release-notes/experimental/preview-build-26300-9032
- https://learn.microsoft.com/en-us/windows/win32/fileio/working-with-nvme-devices
- https://learn.microsoft.com/en-us/intune/device-management/actions/run-remediation
- https://learn.microsoft.com/en-us/windows/deployment/windows-autopatch/overview/windows-autopatch-faq
- https://learn.microsoft.com/en-us/windows/security/application-security/application-control/app-control-for-business/design/script-enforcement
- https://learn.microsoft.com/en-za/mem/intune/configuration/administrative-templates-import-custom
- https://support.microsoft.com/en-us/help/5080921
- https://learn.microsoft.com/en-us/windows/apps/package-and-deploy/code-signing-options
- https://textslashplain.com/2026/04/28/smart-app-control/
- https://docs.signpath.io/origin-verification

Windows release coverage:
- https://pureinfotech.com/windows-11-26h2-released-force-install/
- https://www.bleepingcomputer.com/news/microsoft/windows-11-2026-update-released-heres-everything-you-need-to-know/
- https://windowsreport.com/windows-11-kb5121003-kb5121000-and-kb5120240-august-2026-patch-tuesday-updates-released/
- https://windowslatest.com/2026/06/27/microsoft-warns-windows-11-recovery-feature-uses-up-to-50gb-of-storage-but-for-a-good-cause
- https://www.tomshardware.com/pc-components/ssds/new-windows-native-nvme-driver-benchmarks-reveal-transformative-performance-gains-up-to-64-89-percent-lightning-fast-random-reads-and-breakthrough-cpu-efficiency

Feature IDs and reverse engineering:
- https://github.com/St1ckyNew/25H2-NVMe-Native-Stack-Support
- https://revoconner.com/writing/windows-nvme-driver-workaround
- https://github.com/revoconner/Windows-11-24h2-nvmedriver
- https://github.com/phantomofearth/windows-velocity-feature-lists
- https://github.com/bbmaster123/FWFU
- https://borecraft.com/findings/Windows_Server_2025_NVMe_Driver.html
- https://gigxp.com/windows-11-native-nvme-driver/
- https://www.thomas-krenn.com/en/wiki/Activation_of_native_NVME_driver_in_Windows_Server_2025

Competitors and community:
- https://github.com/thebookisclosed/ViVe/issues/164
- https://github.com/Oblivionevil/nvme-stack-pilot
- https://github.com/FR33THYFR33THY/Ultimate
- https://github.com/Espionage724/Windows
- https://github.com/synoxvf/NOVA
- https://github.com/isleap9/Akari-Toolbox
- https://github.com/veracrypt/VeraCrypt/issues/1640
- https://forums.mydigitallife.net/threads/discussion-windows-11-26x00-native-nvme-driver-discussion.89933/
- https://www.reddit.com/comments/1w2ccs3
- https://winraid.level1techs.com/t/discussion-microsofts-native-nvme-disk-drive-support/113111
- https://www.elevenforum.com/t/windows-11-25h2-nvmedisk-sys-driver-support.46678/
- https://www.elevenforum.com/t/tweak-windows-11-to-speed-up-nvme-ssd-performance.48935/
- https://www.majorgeeks.com/files/details/nvme_driver_patcher_for_windows_11.html

Adjacent tools:
- https://github.com/farag2/Sophia-Script-for-Windows
- https://github.com/Raphire/Win11Debloat
- https://github.com/ChrisTitusTech/winutil
- https://github.com/microsoft/diskspd/releases

Dependencies, signing and distribution:
- https://github.com/dotnet/core/blob/main/release-notes/10.0/10.0.12/10.0.12.md
- https://github.com/dotnet/core/blob/main/release-notes/10.0/cve.md
- https://devblogs.microsoft.com/dotnet/dotnet-and-dotnet-framework-september-2026-servicing-updates/
- https://dotnet.microsoft.com/en-us/download/dotnet/10.0
- https://www.sqlite.org/changes.html
- https://xunit.net/docs/getting-started/v3/migration
- https://firegiant.com/blog/2025/2/6/wix-v3-and-wix-v4-are-no-longer-in-community-support
- https://github.com/NetSparkleUpdater/NetSparkle
- https://docs.github.com/en/actions/how-tos/secure-your-work/use-artifact-attestations/use-artifact-attestations
- https://developers.cloudflare.com/workers/wrangler/migration/deprecations/
- https://developers.cloudflare.com/workers/platform/changelog/
- https://docs.chocolatey.org/en-us/community-repository/moderation/
- https://github.com/ScoopInstaller/.github/blob/main/.github/CONTRIBUTING.md
- https://api.nuget.org/v3-flatcontainer/ (version lookups for every pinned package)

This repo:
- https://github.com/SysAdminDoc/win11-nvme-driver-patcher/issues/18
- https://github.com/SysAdminDoc/win11-nvme-driver-patcher/issues/19
- https://github.com/SysAdminDoc/win11-nvme-driver-patcher/issues/15
- https://github.com/SysAdminDoc/win11-nvme-driver-patcher/releases/tag/v5.6.0
- Local files cited inline by path, at `82038af`.

## Open Questions

1. **Does `EnableNVMeInterface=1` bind nvmedisk on retail 24H2, 25H2 and 26H2, and does it survive a cumulative update?** Blocks the StorPort route. revoconner says feature overrides stopped producing the native adapter on 24H2 from 8875, yet our VM bound with the three-value set on 26100.9550. That VM runs IoT Enterprise LTSC 2024, so the edition may matter. Needs live validation per build, on a retail edition.
2. **Does `DisableNativeNVMeStack=1` put a machine bound through feature overrides back on stornvme?** Blocks using it as the recovery kit's revert. Needs live validation on the 24H2 VM, where the three-value set binds.
3. **Does a cumulative update install cleanly while 156965516 is set?** DISM reports the component store as corrupt while it's set. If servicing still works, 24H2 can get an opt-in route with an honest warning; if not, it can't. Needs live validation.
4. **Do the current-branch IDs bind through the `Policies` path on 26200 and later, and is 48613417 a required co-gate?** Carried from 2026-08-11. The StorPort route may make both moot.
5. **When did WiX 5 leave community support, and what does WiX 6 require?** Blocks the installer decision. Needs a FireGiant answer.
6. **Does Smart App Control block an update the app downloads itself?** HttpClient downloads carry no Mark of the Web, but SAC checks code when it loads. Needs live validation on a SAC-enabled VM.
7. **Do `IOCTL_STORAGE_QUERY_PROPERTY` protocol-specific reads answer under nvmedisk?** Third-party teardown says nvmedisk doesn't clearly expose `IOCTL_STORAGE_PROTOCOL_COMMAND`; the tool uses the query path. Needs live validation on the 24H2 VM.
