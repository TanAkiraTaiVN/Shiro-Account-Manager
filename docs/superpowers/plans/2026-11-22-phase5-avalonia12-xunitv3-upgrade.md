# Phase 5 — Major Ecosystem Upgrade (Avalonia 12.1.6, Froststrap.LucideAvalonia 2.0.1 & xUnit v3) Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use `superpowers:subagent-driven-development` to implement this plan task-by-task in isolated git worktree `.worktrees/phase5-avalonia12-xunitv3`. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Upgrade Shiro Account Manager from Avalonia `11.3.22` + `LucideAvalonia 1.6.2` + legacy `xunit 2.9.3` to `Avalonia 12.1.6` + `Froststrap.LucideAvalonia 2.0.1` + `xunit.v3 3.2.2` + `xunit.runner.visualstudio 3.1.5` + `Microsoft.Web.WebView2 1.0.4370.26` + `coverlet.collector 10.1.1` with zero warnings, zero vulnerable/deprecated packages, full single-file win-x64 publishability, and all `>= 2619` tests + coverage gates passing.

**Architecture:** Replace deprecated `xunit 2.9.3` with `xunit.v3 3.2.2` (matching `Avalonia.Headless.XUnit 12.1.6`'s `xunit.v3.extensibility.core 3.2.2` dependency) in VSTest bridge mode (`<OutputType>Exe</OutputType>`, `<IsTestingPlatformApplication>false</IsTestingPlatformApplication>`) so `coverlet.collector 10.1.1` and `.github/workflows/*.yml` stay green without pipeline rewrites. Swap `LucideAvalonia 1.6.2` (Avalonia 11.1 XamlIl only) for its MIT Avalonia 12 drop-in `Froststrap.LucideAvalonia 2.0.1` (`LucideAvalonia.dll`, `LucideAvalonia.Lucide`, `LucideIconNames`), update Avalonia 12 window chrome/VisualRoot APIs, sync UI/docs strings, and close the two non-blocking Phase 4 minor test cleanups.

**Tech Stack:** .NET 10 (`net10.0-windows10.0.19041.0`, `win-x64`), C# latest, Avalonia UI `12.1.6`, `Froststrap.LucideAvalonia 2.0.1`, `Microsoft.Web.WebView2 1.0.4370.26`, `xunit.v3 3.2.2`, `xunit.runner.visualstudio 3.1.5`, `coverlet.collector 10.1.1`, `NSubstitute 6.2.0`, `NetArchTest.eNhancedEdition 1.4.5`.

**Baseline (`main`):** `d56cefbe698b580eeb99dce08fd8db42e86ffff7`

## Global Constraints

- Every `.cs` file in `src/Shiro/` MUST stay `<= 485` lines; every `.cs` file in `tests/Shiro.Tests/` MUST stay `<= 550` lines; `partial class <= 1150` lines; `Account*.cs` total `< 950` lines; `Batch.cs` zero `Task.Run`.
- Zero compiler warnings (`-warnaserror`); zero warnings suppressed in `src/Shiro/`; zero root `.cs` files in `tests/Shiro.Tests/`.
- Zero vulnerable packages (`dotnet list "Shiro.sln" package --vulnerable --include-transitive`) and zero deprecated packages (`--deprecated --include-transitive`).
- Keep application version at `5.0.0` (`ArchitectureTests.VersionMetadata_ShouldBeSynchronizedAcrossProjectAndDocs`).
- `en.json` and `vi.json` MUST remain 100% key-symmetric with matching `{N}` placeholders; zero Vietnamese diacritics in C# comments/strings outside `LocalizationService*`.
- Never run overlapping `dotnet test` processes simultaneously; never pass `--no-incremental` to `dotnet test`; never kill live `Shiro.exe` or `RobloxPlayerBeta.exe`.
- Total executed tests MUST remain `>= 2619` (`Failed: 0, Skipped: 0`); `Check-Coverage.ps1` must exit `0` (`>= 75%` line / `>= 60%` branch / `>= 92%` Core + 25/25 hotspot floors).

## Review Focus

- `MainWindow.axaml` custom titlebar chrome under Avalonia 12 (`WindowDecorations="BorderOnly"` replacing `ExtendClientAreaChromeHints="NoChrome"`) preserves headless construction and icon stroke/visibility assertions.
- `WebView2Control.cs` scaling lookup (`TopLevel.GetTopLevel(this)?.RenderScaling ?? 1.0` replacing `VisualRoot?.RenderScaling`).
- `Froststrap.LucideAvalonia 2.0.1` (`Lucide.IconSource` -> `DrawingImage` -> `DrawingGroup`) preserves every `LucideIconNames` enum member and brush rendering in `[AvaloniaFact]` tests and single-file Release publish.
- `xunit.v3 3.2.2` + `xunit.runner.visualstudio 3.1.5` executes all `>= 2619` `[Fact]`/`[Theory]`/`[AvaloniaFact]`/`[AvaloniaTheory]` tests via `dotnet test` and generates valid `coverage.cobertura.xml` with `coverlet.collector 10.1.1`.
- Strict LIFO `AppServices.Override` scoping and `HttpClient`/`RestClient` disposal in `AssetCommerceServiceTests` and `BatchBehaviorTests`.

---

### Task 1: Package Graph Upgrade, Lockfiles & Avalonia 12 Production Migration

**Files:**
- Modify: `Directory.Packages.props`
- Modify: `src/Shiro/Shiro.csproj`
- Modify: `tests/Shiro.Tests/Shiro.Tests.csproj`
- Modify: `src/Shiro/packages.lock.json`, `tests/Shiro.Tests/packages.lock.json`
- Modify: `src/Shiro/AvaloniaUI/Views/MainWindow.axaml:5`
- Modify: `src/Shiro/AvaloniaUI/Controls/WebView2Control.cs:60,420`

- [ ] **Step 1: Update central package versions and project files**
  - Update `Directory.Packages.props` (`Avalonia*` -> `12.1.6`, `LucideAvalonia` -> `Froststrap.LucideAvalonia 2.0.1`, `Microsoft.Web.WebView2` -> `1.0.4370.26`, `xunit` -> `xunit.v3 3.2.2`, `xunit.runner.visualstudio` -> `3.1.5`, `coverlet.collector` -> `10.1.1`).
  - Swap `LucideAvalonia` for `Froststrap.LucideAvalonia` in `src/Shiro/Shiro.csproj`.
  - Add `<OutputType>Exe</OutputType>`, `<IsTestingPlatformApplication>false</IsTestingPlatformApplication>` and swap `xunit` for `xunit.v3` in `tests/Shiro.Tests/Shiro.Tests.csproj`.
- [ ] **Step 2: Regenerate lockfiles and audit NuGet graph**
  - Run `dotnet restore "Shiro.sln" --runtime win-x64 /p:SelfContained=true --force-evaluate` then `--locked-mode`.
  - Verify `dotnet list "Shiro.sln" package --vulnerable --include-transitive` and `--deprecated --include-transitive` report zero findings.
- [ ] **Step 3: Migrate Avalonia 12 breaking APIs in `src/Shiro/`**
  - In `src/Shiro/AvaloniaUI/Views/MainWindow.axaml:5`, replace `ExtendClientAreaChromeHints="NoChrome"` with `WindowDecorations="BorderOnly"`.
  - In `src/Shiro/AvaloniaUI/Controls/WebView2Control.cs:60,420`, replace `VisualRoot?.RenderScaling ?? 1.0` with `TopLevel.GetTopLevel(this)?.RenderScaling ?? 1.0`.
- [ ] **Step 4: Build `src/Shiro/Shiro.csproj` cleanly**
  - Run `dotnet build "src/Shiro/Shiro.csproj" --configuration Release --no-restore --no-incremental -warnaserror` -> `0 Warning(s), 0 Error(s)`.
- [ ] **Step 5: Commit & review (`phase5-implementer` -> `phase5-reviewer`)**

### Task 2: xUnit v3 Test Suite Migration, `xunit.analyzers` Compliance & Headless UI Verification

**Files:**
- Modify: `tests/Shiro.Tests/Ui/ListScalingPerformanceTests.cs:7`
- Modify: `tests/Shiro.Tests/Shiro.Tests.csproj` (only if high-churn analyzer IDs such as `xUnit1051` require scoped test-project `<NoWarn>`)
- Modify: any `tests/Shiro.Tests/**/*.cs` flagged by `xunit.v3` / `xunit.analyzers` / Avalonia 12 `-warnaserror`

- [ ] **Step 1: Remove `using Xunit.Abstractions;` in `tests/Shiro.Tests/Ui/ListScalingPerformanceTests.cs:7`**
- [ ] **Step 2: Build full solution (`dotnet build "Shiro.sln" --configuration Release --no-restore -warnaserror`) and resolve test errors/warnings**
- [ ] **Step 3: Verify `ArchitectureTests` and all `[AvaloniaFact]` / `[AvaloniaTheory]` headless UI tests pass under `Avalonia.Headless.XUnit 12.1.6` + `Froststrap.LucideAvalonia 2.0.1`**
- [ ] **Step 4: Commit & review (`phase5-implementer` -> `phase5-reviewer`)**

### Task 3: UI/Localization/Docs Sync (`Avalonia UI 11` -> `Avalonia UI 12`) & Phase 4 Minor Test Hygiene

**Files:**
- Modify: `src/Shiro/AvaloniaUI/Views/MainWindow.axaml:4,14`, `src/Shiro/AvaloniaUI/Views/AboutView.axaml:22`, `src/Shiro/Program.cs:144`
- Modify: `src/Shiro/Resources/Strings/en.json:3,7`, `src/Shiro/Resources/Strings/vi.json:3,7`
- Modify: `src/Shiro/Services/Ui/AppDispatcher.cs:10`, `src/Shiro/Services/Windows/ClipboardHelper.cs:8`
- Modify: `README.md`, `NOTICE.md`, `docs/architecture.md`
- Modify: `tests/Shiro.Tests/Ui/P2CleanupAndUiWiringRegressionTests.cs:202`, `tests/Shiro.Tests/Ui/SettingsAndWorkflowTask3Tests.cs:61`
- Modify: `tests/Shiro.Tests/Launch/BatchBehaviorTests.cs:50-79`, `tests/Shiro.Tests/Network/AssetCommerceServiceTests.cs:17-85`

- [ ] **Step 1: Sync all `Avalonia UI 11` user-facing strings, logs, localization entries, comments, docs, and `P2CleanupAndUiWiringRegressionTests` assertions to `Avalonia UI 12`**
- [ ] **Step 2: Apply Phase 4 Minor test cleanups (resource disposal & cache cleanup in `BatchBehaviorTests`, strict `using` override scopes in `AssetCommerceServiceTests`)**
- [ ] **Step 3: Build & run targeted regression/localization/architecture tests**
- [ ] **Step 4: Commit & review (`phase5-implementer` -> `phase5-reviewer`)**

### Task 4: Release Gate, CI Coverage Gate, Single-File Publish Check & Whole-Branch Final Review

- [ ] **Step 1: Clean Release/coverage/publish dirs; `dotnet restore --locked-mode`; verify `--vulnerable` & `--deprecated` = 0; `dotnet build --no-incremental -warnaserror` = `0W/0E`**
- [ ] **Step 2: Run Full Release Test Suite ALONE -> `Failed: 0, Skipped: 0, Total >= 2619`**
- [ ] **Step 3: Run CI Coverage Gate ALONE (`--collect:"XPlat Code Coverage"` + `scripts/Check-Coverage.ps1`) -> exit `0`**
- [ ] **Step 4: Verify `dotnet publish` single-file win-x64 (`build/Release/Shiro.exe` present & non-empty) and delete transient dirs**
- [ ] **Step 5: Run Whole-Branch Final Review (`Model: "pro"`) on `d56cefb..HEAD`, clean `.superpowers/sdd/phase5`, and present `finishing-a-development-branch` menu**
