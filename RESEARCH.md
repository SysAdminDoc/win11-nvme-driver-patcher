# Research — NVMe Driver Patcher

Date: 2026-08-11 — replaces all prior research (the previous v5.0.0 point-in-time snapshot).
Repo state at capture: v5.7.0, HEAD `394e2b8`.

## Executive Summary

NVMe Driver Patcher is a local Windows 11 safety-and-recovery layer for enabling, verifying, and
reversing Microsoft's still-experimental client `nvmedisk.sys` path (GUI + CLI + tray + watchdog +
MSI/winget/Intune/ADMX). Its strongest shape is unusual: after ~397 commits and a drained deep-audit
backlog, the *safety* machinery (mutation ledger, SafeBoot journal, control-set mirroring, residue
probe, recovery kit, build-action policy, watchdog auto-revert) is materially better than anything
else in the space — there is no second application-grade competitor. The weakness is now on the
opposite side: **the feature-ID and build data the whole tool acts on is hand-curated from press and
forum reports, while a per-build, per-branch primary source exists and is not being used.** That
data gap, plus a servicing gap in the self-contained .NET runtime, is where the highest value sits.

Top opportunities, priority order:

1. **Ship on .NET 10.0.11.** Released 2026-08-11 with 10 CVEs (2 RCE, 3 EoP). Every published
   artifact is self-contained single-file, so Windows Update never services them — this repo owns
   .NET patching outright, in a `requireAdministrator` process. Verified.
2. **The registry-override path writes three feature IDs that map to no feature name on any current
   branch.** `735209102` / `1853569164` / `156965516` are absent from the 26100, 26404, and 29531
   velocity dumps; the same three feature *names* now carry different numbers. Verified from primary
   dumps.
3. **`Standalone_Future: 49453572` is Always Enabled on all three sampled branches** — the fallback
   set applies an override for a feature Windows already forces on, needlessly widening the
   boot-critical mutation and restore surface. Verified.
4. **A second gate exists and is untracked: `NativeNVMeStackEnableForClientOS: 48613417`**, present
   on the Rubidium branch alongside `NativeNVMeStackForGeClient`. Plausibly the "another feature ID
   in the mix" ViVe's maintainer named when closing issue #164 — the very issue this repo's
   `26200-bind-blocked` rule cites as evidence there is *no* known route. Verified (existence);
   Needs live validation (causality).
5. **Adopt per-build velocity dumps as the curated data source** for IDs *and* default-state
   (`Disabled By Default` = overridable vs `Always Disabled` = not), replacing `SelectForBuild`'s
   `buildNumber >= 26200` heuristic.
6. **ViVeTool has been dead since 2025-03-10**; its GUI front-end repo is deleted and its bundled
   dictionary stops at build 26236. The fallback's secondary path rests on abandonware.
7. **Locale trap, C# twin:** `BypassIoInspectorService` regex-parses English `fsutil` output — the
   identical defect already logged against the legacy script, never checked in C#.
8. **Recovery is a release behind the OS.** Point-in-Time Restore went GA mid-2026 and Quick Machine
   Recovery is off by default on Pro/Enterprise; the tool still gates on `Checkpoint-Computer` and
   has zero references to either.
9. **The most-asked community question is unanswered:** benchmarks run one fixed profile
   (`-t4 -o16 -b4K` ≈ QD64). Measured gains are ~+65% 4K random read at high QD, **−2.6% on write**,
   and near-zero at QD1–QD2 — i.e. typical desktop use.
10. **Dependency currency and audit gating** — no `NuGetAudit` config, no lock files, an
    orphaned `System.Threading.AccessControl` (NU1510), and two upgrade traps documented below.

## Product Map

- **Core workflows:** assess readiness (≈26 preflight checks) → choose Safe/Full profile or the
  build-gated FeatureStore fallback → apply (BitLocker suspend, restore point, mutation ledger,
  SafeBoot journal, control-set mirroring) → reboot → prove driver binding → watchdog-monitor →
  remove / auto-revert with residue proof → export recovery kit and support bundle.
- **Personas:** storage enthusiasts; workstation/homelab admins; fleet operators (CLI, PowerShell
  module, ADMX, Intune); support engineers diagnosing a failed swap. **New in 2026:** the tool is
  mirrored by MajorGeeks at v5.6.0, which adds a non-enthusiast audience that did not read the
  README.
- **Platforms/distribution:** Windows 11 24H2/25H2 x64 mutation path; diagnostic-only ARM64;
  Server 2025 as the supported reference. Portable EXEs, MSI, winget/Scoop/Chocolatey manifests,
  PowerShell module, ADMX/ADML, Intune assets. No GitHub Actions (deliberate repo policy).
- **Integrations/data flows:** 64-bit HKLM feature + SafeBoot state, Rtl Feature Store APIs,
  WMI/CIM/PnP evidence, BitLocker, WinRE/`reagentc`, System/Application event logs, `wpr` ETW,
  `%ProgramData%\NVMePatcher` config + SQLite history, curated `windows_build_rules.json` /
  `compat.json`, checksummed release assets, optional Cloudflare Worker telemetry receiver.

## Competitive Landscape

There is no second application-grade competitor. The relevant field is data sources, scripts, and
adjacent OS features.

- **phantomofearth/windows-velocity-feature-lists** — per-build, per-branch feature name→ID dumps
  *with default-state sections*, continuing Rivera's mach2 work. **Learn:** this is ground truth for
  every ID decision the tool makes today by inference; sections `Always Enabled / Enabled By Default
  / Disabled By Default / Always Disabled` directly answer "is this build overridable at all".
  **Avoid:** the repo carries **no LICENSE** — transcribe rows into the existing curated JSON with
  `sourceUrl` + `lastReviewed`, do not vendor the files, and do not auto-download (both would break
  this repo's own curated-data principle).
- **thebookisclosed/ViVe (ViVeTool)** — the fallback's secondary substrate. **Learn:** issue #164's
  closing comment ("another feature ID or registry key in the mix") is a maintainer-level hint that
  matches `48613417`; issue #166 shows `Policies\...\Overrides` values owned by TrustedInstaller
  break `vivetool /fullreset` — *this tool writes exactly there.* **Avoid:** depending on it. Zero
  commits since 2025-03-10; bundled dictionary stops at build 26236; `PheeL-Pheel/ViVeTool-GUI`
  404s; `riverar/mach2` is archived.
- **FR33THYFR33THY/Ultimate** (631★) — ships a 5th override value `3244671118` this tool does not
  know, and its revert does `reg delete HKLM\SYSTEM\CurrentControlSet\Policies\Microsoft /f`,
  destroying the whole policy subtree. **Learn:** detect-and-repair this state; it is a support-load
  generator, not a competitor. **Avoid:** everything about that revert.
- **TheBeardofKnowledge `nvmeSPEEDtweak.bat`** — best preflight narrative of the script tier. **Learn:**
  it reads `HKLM\SYSTEM\CurrentControlSet\Services\storport\Parameters\EnableBypassIO` from the
  registry and checks device binding via `DEVPKEY_Device_Service` — locale-independent where this
  tool parses English `fsutil` text. **Avoid:** no rollback proof, no post-reboot bind verification.
- **jhochwald/PowerShell-collection** — ships Intune **Check/Remediate proactive-remediation pairs**
  for exactly this tweak. **Learn:** the pair *is* the Intune-native contract; this repo ships a
  detection script and an MSI bundle but no remediation half. **Avoid:** its unguarded apply.
- **GEAnalyticsLabs/native-nvme** — architecturally the closest clone (ProgramData `state.json` +
  ONLOGON scheduled task for reboot-resume, `manage-bde` suspend, USB recovery kit). **Learn:** the
  three-phase workflow rendered as a *visible* cross-reboot state machine. **Avoid:** dead since
  2025-12; Win11 Pro 25H2-only gate; no bind proof.
- **ken-yossy/nvmetool-win** — raw NVMe admin/IO pass-through (Identify, log pages, SMART, self-test)
  through the inbox driver. **Learn:** a richer inbox-IOCTL health path than this repo's `identify`
  subset, which matters when vendor tools stop seeing drives post-swap. **Avoid:** dormant 17 months;
  it is a diagnostic library, not an enabler.
- **Windows Point-in-Time Restore / Quick Machine Recovery** (OS features, 2026) — the OS's own
  answers to "snapshot before" and "can't boot after". **Learn:** PiTR captures user files/apps/certs
  where a restore point does not; `reagentc /SetRecoveryTestmode` proves the recovery path *before*
  touching the storage stack. **Avoid:** treating them as replacements for the recovery kit — they
  are additional evidence, and QMR is off by default on Pro/Enterprise.

## Security, Privacy, and Reliability

- **Verified — unserviced .NET in an elevated process.** `global.json` pins SDK **10.0.301**;
  installed SDK is 10.0.302 / runtime **10.0.10**. .NET **10.0.11** shipped 2026-08-11 with ten CVEs
  including two RCE (CVE-2026-70354, CVE-2026-62897) and three EoP. Because all four exes publish
  `SelfContained` + `PublishSingleFile` (`src/NVMeDriverPatcher/NVMeDriverPatcher.csproj`), users
  receive **no** .NET servicing from Windows Update — and both GUI and CLI carry
  `requireAdministrator`, so a runtime memory-safety defect is a privilege-boundary crossing.
  Runtime currency must be a per-release gate, not a chore.
- **Verified (primary dumps) — the primary path's feature IDs are stale.** `AppConfig.RegistryPath`
  writes `SYSTEM\CurrentControlSet\Policies\Microsoft\FeatureManagement\Overrides` with
  `735209102`, `1853569164`, `156965516` (README's 5-component table). None of the three appears in
  the 26100.8687, 26404.5000, or 29531.1000 dumps; the same feature *names* now carry
  `NativeNVMeStackForGeClient: 60786016` (26100) / `55369237` (26404, 29531),
  `UxAccOptimization: 48433719`, `Standalone_Future: 49453572`. The tool already knows the current
  numbers — but only on the FeatureStore fallback path. Whether writing current IDs to the Policies
  path binds is **Needs live validation**; reporting the mismatch is shippable now and is a strictly
  more honest `status` than "no known route".
- **Verified — a needless boot-critical mutation.** `Standalone_Future: 49453572` sits under
  `## Always Enabled:` in all three sampled dumps, yet `FallbackFeatureCatalog.NativeNvmeStack25H2`
  applies it. It cannot change behavior, but it enlarges the FeatureStore write, the ledger baseline,
  and the restore obligation — including the priority-8 reset defect already on the roadmap.
- **Verified (existence) / Needs live validation (causality) — untracked second gate.**
  `NativeNVMeStackEnableForClientOS: 48613417` is `Disabled By Default` on 29531.1000 and has zero
  references anywhere in this repo. `windows_build_rules.json`'s `26200-bind-blocked` and
  `post-26200-trains-bind-blocked` rules both cite ViVe #164 as evidence of no route; #164's own
  closing comment points at a missing extra ID.
- **Verified — supply/maintenance risk in the fallback's secondary path.** ViVeTool: last commit
  2025-03-10, dictionary current only to build 26236, GUI front-end deleted. `ViVeToolService`
  correctly gates on a SHA-256 manifest, so this is not an integrity hole — it is a dead-upstream
  risk that the fallback UI does not disclose.
- **Verified — this tool can wedge the user's own escape hatch.** ViVe issue #166: values under
  `Policies\Microsoft\FeatureManagement\Overrides` owned by TrustedInstaller make `vivetool
  /fullreset` fail access-denied. That is precisely the key `PatchService` writes.
- **Verified — locale trap, C# twin.** `Services/BypassIoInspectorService.cs:55-96` regex-matches
  English `fsutil bypassio state` output (`RxBypassEnabled`, `RxStorageStack`). On non-English
  Windows `Enabled` is always false and the gaming-impact warning silently degrades. The identical
  defect in `NVMe_Driver_Patcher.ps1:986-1006` is already on the roadmap; the C# site is not — the
  same "nobody checked the twin" pattern the repo logged on 2026-08-11 for the ACL predicate.
- **Verified — no dependency-audit gate.** `Directory.Build.props` sets no `NuGetAuditMode` /
  `NuGetAuditLevel`, and there are no `packages.lock.json` files.
  `dotnet list package --vulnerable --include-transitive` is clean today; nothing *fails a build*
  when it stops being clean.
- **Verified — upgrade traps that would pass the suite while breaking the pin.**
  (a) `SQLitePCLRaw.bundle_e_sqlite3` **3.0.5 switches its native dependency to a different package
  id** (`SQLite` 3.53.4) — the repo's direct `SourceGear.sqlite3` pin would silently stop overriding
  anything and ship a second native `e_sqlite3`, and `SqliteVersionTests` (a runtime version-string
  check) would still pass. Go to **3.0.4**, not 3.0.5. (b) LiveCharts 2.0.5 declares SkiaSharp
  2.88.9 / Views.WPF 3.119.0; the repo force-resolves 4.148.0, and SkiaSharp 4.150.0 promoted
  pre-v4 obsolete APIs to errors — a removed member surfaces only as a runtime
  `MissingMethodException` on a path `ChartingSmokeTests` may not hit.
- **Verified — bundled native C libraries are current at the pin.** libpng 1.6.58, freetype 2.14.3,
  libwebp 1.6.0 are all upstream-current in SkiaSharp 4.148.0; the only native delta to 4.151.1 is
  HarfBuzz 14.2.0→14.2.1. The one gap, libexpat 2.8.1 vs 2.8.2 (13 CVEs), is **not** fixed by any
  SkiaSharp release and is unreachable here (no SVG/XML parsed through Skia). The security case for
  bumping SkiaSharp is nil; the *support-tier* case is real (4.148 was dropped from stable support).
- **Verified — telemetry receiver drifted.** `packaging/telemetry-receiver/` declares no `wrangler`
  dependency and has no lockfile, uses the deprecated `[[unsafe.bindings]]` rate-limiter surface
  (stable `[[ratelimits]]` since wrangler 4.36.0), and pins `compatibility_date = 2026-04-19`.
- **Verified — privacy posture remains correct.** Compat telemetry is explicit, anonymized,
  GPO-controllable, with no default receiver. Do not turn curated compat/build data into an unsigned
  auto-update channel.

## Architecture Assessment

- **Data model, not code, is the bottleneck.** `FallbackFeatureCatalog.SelectForBuild` decides by
  `buildNumber >= 26200`, and `windows_build_rules.json` encodes verdicts (`none-known`) derived
  from press and forum reports. Both should consume one curated table carrying, per branch and
  build: feature *name*, numeric ID, and default-state class. Default-state is the missing axis —
  `Always Disabled` is the only honest basis for "no route", and no sampled branch shows it.
- **`AppConfig.RegistryPath` and `FeatureStoreWriterService`'s
  `Control\FeatureManagement\Overrides` are two different hives with two different ID sets.** They
  should share one resolved-per-build ID list so the registry and FeatureStore routes cannot drift
  apart again.
- **`BypassIoInspectorService` should be evidence-based, not text-based:** read
  `Services\storport\Parameters\EnableBypassIO` from the registry, and check whether the bound
  driver's INF declares `STORAGE_SUPPORTED_FEATURES_BYPASS_IO` — that converts the gaming-impact
  warning from heuristic to proof.
- **Recovery evidence should include OS-native rollback.** `RecoveryProofGateService` currently
  hard-gates on System Protection being on; Point-in-Time Restore is a stronger, separate signal and
  Quick Machine Recovery is testable pre-mutation via `reagentc /SetRecoveryTestmode`.
- **Benchmarking is one fixed profile.** `BenchmarkService.CreateDiskSpdArguments` is
  `-c128M -d30 -t4 -o16 -b4K -r -Sh -L`. A second QD1/QD2 desktop profile (and a 128K sequential
  pass) is what turns "does this help me?" into an answer.
- **Watchdog evidence is inferential.** `EventLogWatchdogService.WatchEvents` already includes the
  classic `nvmedisk` source at ID 129 — good — but the driver also has a dedicated ETW provider
  (`Microsoft-Windows-NvmeDisk`, `{9799276c-fb04-47e8-845e-36946045c218}`), which `EtwTraceService`
  (a generic `wpr` profile wrapper) does not target.
- **Test/doc gaps:** no gate asserts the runtime version embedded in a published artifact (the
  existing `Validate-ReleaseAssets.ps1` MSI-SummaryInfo read is the pattern to copy); no gate covers
  dependency audit; `Roadmap_Blocked.md` describes editing `.github/workflows/release.yml`, which
  does not exist in this repo (build CI is banned by policy) — the item's mechanism is wrong.

## Rejected Ideas

- **Vendor the velocity dump files directly** — Source: phantomofearth repo. Reason: no LICENSE on
  that repo, and auto-downloading unreviewed feature data contradicts this repo's curated-data
  principle. Transcribe with citation instead.
- **Vendor the FeatureStore RPC/ABI to drop ViVeTool entirely** — Source: ViVe dormancy analysis.
  Reason: already largely moot — `FeatureStoreWriterService` is the primary path and ViVeTool is
  only the secondary; taking full ownership of an undocumented, version-sensitive ABI on a
  boot-critical store buys little and costs the third-party cross-check.
- **Migrate WPF → WinUI 3 / Windows App SDK** — Source: platform survey. Reason: nothing in 2025–26
  gives WinUI 3 a storage/recovery API WPF lacks, and it would cost the self-contained single-file,
  no-prerequisite elevated-launch story the tool depends on.
- **Warn on Data Deduplication, Storage Spaces, WSL2, Intel VMD/RST** — Source: community failure
  reports. Reason: **already implemented** (`DriveService.cs:756, 785-816, 875`); no new work.
- **Rebuild build-aware per-branch ID selection** — Source: ID-drift reports. Reason: already
  implemented in `FallbackFeatureCatalog`; only the data behind it needs correcting.
- **Automate the custom-INF / test-signing workaround** — Source: ViVe #164. Reason: resigning inbox
  storage drivers falls outside the rollback model; the issue's owner closed it with the same
  warning.
- **Adopt WiX 6/7** — Source: WiX releases. Reason: v7 requires OSMF EULA acceptance (build error
  WIX7015) with fees above $10k/yr revenue; `heat` removal would be a rewrite for no user benefit.
  Stay on 5.0.2 and accept it is frozen.
- **Firmware flashing, secure erase, SMART prediction, general driver-store cleanup, full rescue
  imaging, NVMe-oF / NVMe 2.2 tooling, plugin ecosystem, mobile, multi-user server control, full
  product i18n** — Source: vendor tools, Macrium/Veeam, Server vNext previews, repo scope rule.
  Reason: none improves enable/verify/rollback. (i18n narrowly excepted: *locale-independent
  probing* is a correctness fix and is on the roadmap; translating the product is not.)
- **Accessibility rework** — Source: category sweep. Reason: `AccessibilityService`,
  `HighContrastTheme.xaml`, `ThemeContrastTests`, `AccessibilitySmokeTests` and reduced-motion
  support already exist; the one live gap (ThemedDialog accessible title) is already on the roadmap.
  No new a11y items justified by evidence.

## Sources

Microsoft / platform:
- https://github.com/dotnet/core/blob/main/release-notes/10.0/10.0.11/10.0.11.md
- https://techcommunity.microsoft.com/blog/windows-itpro-blog/point-in-time-restore-for-windows-11-is-now-generally-available/4508101
- https://learn.microsoft.com/en-us/windows/configuration/cloud-rebuild/
- https://learn.microsoft.com/en-us/windows-hardware/drivers/ifs/bypassio
- https://learn.microsoft.com/en-us/windows-hardware/drivers/ddi/storport/ns-storport-stormq_miniport_interface
- https://techcommunity.microsoft.com/blog/windows-itpro-blog/what-to-know-about-windows-11-version-26h1/4491941
- https://support.microsoft.com/en-us/topic/windows-11-version-25h2-update-history-99c7f493-df2a-4832-bd2d-6706baa0dec0
- https://github.com/MicrosoftDocs/windows-driver-docs/blob/staging/windows-driver-docs-pr/install/system-defined-device-setup-classes-available-to-vendors.md

Feature-ID ground truth:
- https://github.com/phantomofearth/windows-velocity-feature-lists
- https://raw.githubusercontent.com/phantomofearth/windows-velocity-feature-lists/main/rs_prerelease/amd64/29531.1000.txt
- https://raw.githubusercontent.com/phantomofearth/windows-velocity-feature-lists/main/ge_prerelease_im/amd64/26100.8687.txt
- https://raw.githubusercontent.com/phantomofearth/windows-velocity-feature-lists/main/ge_prerelease/amd64/26404.5000.txt

Competitors and community:
- https://github.com/thebookisclosed/ViVe/issues/164
- https://github.com/thebookisclosed/ViVe/issues/166
- https://github.com/thebookisclosed/ViVe/releases
- https://github.com/FR33THYFR33THY/Ultimate/blob/main/8%20Advanced/19%20NVME%20Faster%20Driver.ps1
- https://github.com/jhochwald/PowerShell-collection
- https://github.com/TheBeardofKnowledge/Scripts-from-my-videos
- https://github.com/GEAnalyticsLabs/native-nvme
- https://github.com/ken-yossy/nvmetool-win
- https://www.storagereview.com/review/windows-server-native-nvme
- https://winraid.level1techs.com/t/discussion-microsofts-native-nvme-disk-drive-support/113111
- https://www.tomshardware.com/software/windows/microsoft-blocks-the-registry-hack-trick-that-unlocked-native-nvme-performance-on-windows-11
- https://www.majorgeeks.com/files/details/nvme_driver_patcher_for_windows_11.html

Recovery and diagnostics:
- https://4sysops.com/archives/quick-machine-recovery-in-windows-11/
- https://4sysops.com/archives/configure-windows-11-point-in-time-restore/
- https://github.com/libyal/winevt-kb/blob/main/docs/sources/eventlog-providers/Provider-Microsoft-Windows-NvmeDisk.md

Dependencies:
- https://github.com/mono/SkiaSharp/releases/tag/v4.150.0
- https://github.com/mono/SkiaSharp/pull/4502
- https://blog.hartwork.org/posts/expat-2-8-2-released/
- https://www.sqlite.org/changes.html
- https://developers.cloudflare.com/workers/runtime-apis/bindings/rate-limit/
- https://github.com/wixtoolset/issues/issues/9196

## Open Questions

- **Does writing the current-branch IDs to `Policies\Microsoft\FeatureManagement\Overrides` bind the
  driver on 26200+?** The IDs are now known; whether the Policies hive is neutered independently of
  ID rotation is not. Blocks the choice between "correct the registry path" and "report the mismatch
  and stay verify-only". Requires a live 26200.8xxx machine.
- **Is `NativeNVMeStackEnableForClientOS: 48613417` a required co-gate on 26200+?** Its existence is
  verified; its necessity is inferred from ViVe #164's closing comment. Blocks whether it becomes an
  applied ID or a probe-only evidence field.
- **Samsung 990 Pro firmware truth** (carried forward, still unresolved): community sources call
  `4B2QJXD7` the bad revision fixed by `6B2QJXD7`; `compat.json` cautions `7B2QJXD7` and recommends
  updating *to* `4B2QJXD7`. Needs first-party Samsung confirmation before editing. Blocks correct
  `compat.json` data only.
- Hardware-only validation (WinRE inject `--commit`, ARM64 launch, debloat/LTSC bind reproduction,
  Windows Sandbox package lifecycle, OEM VMD/RAID WinPE media) remains isolated in
  `Roadmap_Blocked.md`.
