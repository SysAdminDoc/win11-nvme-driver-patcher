# NVMe Driver Patcher — Roadmap

Living document — **incomplete work only**. Shipped items are deleted (git history + [CHANGELOG.md](CHANGELOG.md) are the record). Blocked items live in [Roadmap_Blocked.md](Roadmap_Blocked.md). Current ship: **v5.0.0**.

**Scope rule:** every item must improve the core function — enabling, disabling, verifying, or rolling back Microsoft's native NVMe driver swap on Windows 11. No external integrations, no general-purpose storage tools, no theme/UI-locale polish. If an idea drifts into "separate tool that happens to live in the same exe," it doesn't belong here. Priority is by user impact / regret cost, not effort; S/M/L/XL are rough effort estimates.

---

## Research-Driven Additions

### P2 — Safety and UX

### P2 — Safety and compat (research pass 4, 2026-06-20)

### P3 — Quality and distribution (research pass 4, 2026-06-20)





### P2 - Truth and distribution guardrails


### P3 - Test toolchain hygiene

- [ ] P3 - Refresh test infrastructure package pins without changing runtime packages
  Why: The local suite is the release trust gate, and `dotnet list package --outdated --include-transitive` shows newer Microsoft.NET.Test.Sdk, test platform, xUnit analyzer, and Newtonsoft.Json test dependencies.
  Evidence: local NuGet outdated scan; `tests/NVMeDriverPatcher.Tests/NVMeDriverPatcher.Tests.csproj`; `https://www.nuget.org/packages/Microsoft.NET.Test.Sdk/18.7.0`.
  Touches: `tests/NVMeDriverPatcher.Tests/NVMeDriverPatcher.Tests.csproj`, test runner configuration, package audit notes.
  Acceptance: Test-only package pins are current where compatible, the suite still runs locally on .NET 10, the xUnit v2 runner decision is preserved or explicitly migrated, and no runtime project package changes are included in the same commit.
  Complexity: S
