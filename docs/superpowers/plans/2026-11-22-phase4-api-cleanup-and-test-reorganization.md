# Phase 4 — Breaking Dead API/Parameter Cleanup, Flake Hardening & `tests/Shiro.Tests/` Reorganization Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use `superpowers:subagent-driven-development` to implement this plan task-by-task in isolated git worktree `.worktrees/phase4-api-cleanup-test-reorg`.

**Goal:** Remove verified breaking dead APIs/parameters deferred from Phases 1–3 (`IAssetCommerce.PurchaseAsync(..., assetId, productId)`, `Account.HasExpiredShortLivedCookie()`, `preferChrome`/`preferChromeHome`, `Internal`/`internalCall`, `skipPinCheck`, `RobloxProcessOptimizer.ShouldAssignToJobObject(..., cpuCapPercent)`, `ClientSettingsPatcher.ApplyFpsLimitToDirectory`, and `IniSection.Set(..., comment)`), resolve parked Phase 3 minors and cross-collection test flakes (`RobloxProcessServiceLifecycleBehaviorTests`, `RobloxServerQueryServiceBehaviorTests`), and organize all 221 root `.cs` files in `tests/Shiro.Tests/` into clean domain subfolders enforced by an `ArchitectureTests` guardrail.

**Scope Boundary:** Major dependency upgrades (`Avalonia 12`, `LucideAvalonia`, `xunit.v3`) are deferred cleanly to Phase 5.

**Baseline (`main`):** `375505f123f5a10a051dffab9ca708dc59387e48`

---

## Global Constraints

- **Architecture Guardrails (enforced by `ArchitectureTests*.cs` & `P3DomainPurityTests.cs`):**
  - Every `.cs` file in `src/Shiro/` MUST stay `<= 485` lines (`NoSourceCodeFiles_ShouldExceed500Lines`).
  - Every `.cs` file in `tests/Shiro.Tests/` MUST stay `<= 550` lines (`NoTestFiles_ShouldExceed550Lines`).
  - Every `partial class` total across files MUST stay `<= 1150` lines (`NoClass_ShouldExceedLineBudget_AcrossPartials`).
  - `Account*.cs` partials (excl. `AccountJson.cs` & `AccountSearchHelper.cs`) MUST stay strictly `< 950` lines (`P3DomainPurityTests.AccountPartials_TotalLines_IsStrictlyBelow950_AndWithinGlobalBudget`).
  - Zero compiler warnings (`-warnaserror`), zero empty/untyped `catch` (except `AppLogger.cs`), zero Vietnamese diacritics in C# comments/strings outside `LocalizationService*`, zero `.GetAwaiter().GetResult()`, zero `[DllImport]`, zero `DispatcherFrame`/`PushFrame`.
  - `.Wait(TimeSpan...)` allowed ONLY in `AccountManager.Lifecycle.cs` and `Services/Storage/AppLogger.cs`; `Thread.Sleep` allowed ONLY in `StoragePaths.VaultKey.cs`.
  - `en.json` and `vi.json` MUST remain 100% key-symmetric with matching `{N}` placeholders.
  - Every `[Fact]`/`[Theory]` MUST assert concrete values.
- **Build & Test Notes (.NET 10 SDK):**
  - `--no-incremental` is valid ONLY on `dotnet build`, NEVER on `dotnet test`.
  - Do **NOT** terminate any live `Shiro.exe` or `RobloxPlayerBeta.exe` process on the host machine.
- **Coverage Floors (`tests/coverage-floors.json`):** Maintain per-file branch coverage >= floor on all 25 existing hotspots (including `RobloxSettingsApiService.cs >= 87%`, `RobloxProcessService.cs`, `RobloxProcess.cs >= 48%`, `RobloxWatcher.cs >= 20%`).

---

### Task 1: Remove Dead Commerce & Session Health APIs + Atomic `SuppressDeferredSaveScope`

- **`src/Shiro/Services/Network/AssetCommerceService.cs` (`L15, 53-62`):**
  Remove dead `long assetId` and `long productId` from `IAssetCommerce.PurchaseAsync` and `AssetCommerceService.PurchaseAsync` -> `PurchaseAsync(Account a, long expectedPrice, long sellerId, string? collectibleItemId, string? collectibleProductId, string sellerType = "User", CancellationToken ct = default)`.
- **`src/Shiro/ViewModels/Dialogs/MissingAssetsViewModel.cs` (`L316-325`):** Drop `item.AssetId` and `item.ProductId` from `_commerce.PurchaseAsync(...)`.
- **`src/Shiro/Domain/Account.cs` (`L240-281`):**
  Remove `HasExpiredShortLivedCookie()` (which always returned `!hasToken`). Inline `hasToken` directly into `IsShortLivedSession` (`return hasToken && payloadLen is >= 500 and < 850;`) and `SessionHealthStatus` (`if (!Valid || !hasToken) return SessionHealth.Expired;`). (`Account*.cs` drops from `949` to `~932 < 950`.)
- **`src/Shiro/Domain/Account.SessionState.CookieRotation.cs` (`L80-90`):** Replace `private bool _disposed;` with `private int _disposed;` and guard `Dispose()` via `if (Interlocked.Exchange(ref _disposed, 1) != 0) return;` (`0` net lines).
- **`src/Shiro/Services/Storage/AccountRepository.cs` (`L244-248`):** Remove unreachable `else if (acc.Valid && acc.HasExpiredShortLivedCookie())` branch (`L240` already sets `Valid = false` when `SecurityToken` is whitespace).
- **Tests to update:**
  - `tests/Shiro.Tests/Dialogs/MissingAssetsViewModelTests.cs` (9 call/setup sites)
  - `tests/Shiro.Tests/Network/AssetCommerceServiceTests.cs` (2 call sites)
  - `tests/Shiro.Tests/BugHuntRound2Tests.cs` (`L239`)
  - `tests/Shiro.Tests/P0AsyncViewModelConcurrencyTests.cs` (`L148`)
  - `tests/Shiro.Tests/P1NetworkRegressionTests.cs` (`L238, 245, 250`)
  - `tests/Shiro.Tests/Phase3NetworkAndIntegrationBugfixTests.cs` (`L116, 286`)
  - `tests/Shiro.Tests/SecurityHardeningTests.cs` (`L274, 300-347` — assert `SessionHealthStatus` & `IsShortLivedSession` cached metadata behavior cleanly without decrypting)
  - `tests/Shiro.Tests/SessionHealthTests.cs` (`L38-61` — verify `SessionHealthStatus` / `IsShortLivedSession` directly without removed `HasExpiredShortLivedCookie()`)
- **Verification:** `dotnet build "Shiro.sln" --no-incremental -warnaserror`, targeted tests across all 8 modified test files + `P3DomainPurityTests` + `Shiro.Tests.ArchitectureTests` + `P3CompositionAndArchitectureTests`.

---

### Task 2: Remove Dead `preferChrome`, `Internal`/`internalCall`, and `skipPinCheck` Parameters

- **Remove `preferChrome` / `preferChromeHome` / `PreferChromeHome`:**
  - `src/Shiro/Domain/Account.Launcher.cs` (`L19-23`)
  - `src/Shiro/Services/Launch/AccountLaunchService.Home.cs` (`L42, 45, 51`)
  - `src/Shiro/Services/Launch/BrowserService.cs` (`L61, 67, 72, 78`)
  - `src/Shiro/Services/Launch/AccountLaunchBatchService.cs` (`L257, 280`)
  - `src/Shiro/Services/Launch/LaunchCoordinator.Models.cs` (`L17, 26, 34`)
  - `src/Shiro/AccountManager.Launch.cs` (`L24, 28, 40`)
  - `src/Shiro/AccountManager.ModernActions.cs` (`L16, 46`)
- **Remove `Internal` / `internalCall`:**
  - `src/Shiro/Domain/Account.SettingsApi.cs` (`L20-21`)
  - `src/Shiro/Services/Network/RobloxSettingsApiService.cs` (`L186`)
  - `src/Shiro/Domain/Account.Launcher.cs` (`L12-17`)
  - `src/Shiro/Services/Launch/AccountLaunchService.cs` (`L215, 224, 235`)
- **Remove `skipPinCheck`:**
  - `src/Shiro/Services/Network/RobloxSocialApiService.cs` (`L148, 181-182, 214, 225`) -> `BlockUserIdAsync(Account account, string userId, bool unblock = false, CancellationToken cancellationToken = default)` & `UnblockUserIdAsync(Account account, string userId, CancellationToken cancellationToken = default)` (also check `Account.SocialAndFriendsApi.cs` callers if any).
- **Tests to update:** `AccountLaunchServiceBehaviorTests.cs`, `AccountLauncherAndHelperBehaviorTests.cs`, `LaunchCoordinatorTests.cs`, `P0CookieGuardAndLaunchCoordinatorTests.cs`, `ValidateSessionTests.cs`, `Network/BugHuntRound2NetworkTests.cs` (+ any other caller revealed by build).
- **Verification:** `dotnet build "Shiro.sln" --no-incremental -warnaserror`, targeted tests + `P3DomainPurityTests` + `Shiro.Tests.ArchitectureTests` + `P3CompositionAndArchitectureTests`. Preserve `RobloxSettingsApiService.cs >= 87%` branch coverage.

---

### Task 3: Remove Dead Windows & Storage Parameters (`ShouldAssignToJobObject`, `ApplyFpsLimitToDirectory`, `Set(..., comment)`)

- **`src/Shiro/Services/Windows/RobloxProcessOptimizer.cs` (`L76-80, 110`):** Simplify `ShouldAssignToJobObject(int pid)` (drop unused `int cpuCapPercent`), update call site at `L110`, and update `tests/Shiro.Tests/ProcessAndSingletonOptimizationTests.cs` (`L16-18`).
- **`src/Shiro/Services/Launch/ClientSettingsPatcher.cs` (`L103, 108-111`):** Remove dead `ApplyFpsLimitToDirectory(string versionFolder, bool unlockFps, int targetFps)` wrapper (or simplify to `ScrubLegacyFpsFastFlag(folder.FullName)` at `L103`), and update `ClientSettingsAndAntiAfkTests.cs`, `ClientSettingsAndUtilitiesBehaviorTests.cs`, `ClientSettingsAndWindowTilingTests.cs`, `ClientSettingsPatcherTests.cs`.
- **Remove dead `string? comment = null` parameter:**
  - `src/Shiro/Services/Storage/ISettingsService.cs` (`L17`)
  - `src/Shiro/Services/Storage/IniFile.cs` (`L206`)
  - `src/Shiro/Services/Storage/SettingsDocument.cs` (`L68, 73`)
  - `src/Shiro/Services/Storage/SettingsService.cs` (`L41, 44`)
  - `src/Shiro/Services/AppServices.cs` (`L148`)
  - `src/Shiro/Services/Composition/AppBootstrap.cs` (`L414, 435`)
  - `src/Shiro/AccountManager.cs` (`L159` — drop 3rd arg comment string)
  - Update `tests/Shiro.Tests/SettingsServiceTests.cs` (`L149`) and `tests/Shiro.Tests/ClientSettingsAndUtilitiesBehaviorTests.cs` (`L122`).
- **Verification:** `dotnet build "Shiro.sln" --no-incremental -warnaserror`, targeted tests + `Shiro.Tests.ArchitectureTests` + `P3CompositionAndArchitectureTests`.

---

### Task 4: Phase 3 Parked Minors, `FlagIconService` Split, Async Ratchet Tightening & Flake Hardening

- **`src/Shiro/Services/Ui/FlagIconService.cs` + `src/Shiro/Services/Ui/FlagIconService.Archive.cs`:** Extract ZIP archive loading/opening into `FlagIconService.Archive.cs` (`using MemoryStream` disposal on `CopyTo` failure), un-compact `FlagIconService.cs` formatting cleanly (`<= 455/485`, partial total `<= 1030/1150`).
- **`src/Shiro/ViewModels/ServerList/ServerBrowserTabViewModel.PlayerSearch.cs` (`L127`):** Initialize `long lastStatusPostTicks = long.MinValue;` instead of `0`.
- **`tests/Shiro.Tests/ArchitectureTests.Ratchets.cs` (`L103-116`):** Tighten regexes to match `(?:static\s+)?async\b` and `async\s+(?:static\s+)?void\b`, with concrete unit tests in `Phase3AsyncSafetyAndDialogTests.cs` verifying detection.
- **`tests/Shiro.Tests/RobloxProcessServiceLifecycleBehaviorTests.cs` (`L369-405`):** Reset `ProcessExtensions.TestCommandLineOverride = null;` before step 3; wait `pBeta3.WaitForExit(3000)`, call `InvalidateSnapshotCache()`, and isolate step 4 via `RobloxProcessService.TestGetRunningRobloxPidsOverride = Array.Empty<int>` in `try/finally` so live host `RobloxPlayerBeta.exe` or slow `pBeta3` exit never flakes `CleanZombieProcesses()`; safely kill/wait all 4 temp test processes in `finally`.
- **`tests/Shiro.Tests/Network/RobloxServerQueryServiceBehaviorTests.cs` (`L13`) & `tests/Shiro.Tests/Phase3ServerBrowserAndSearchTests.cs` (`L101-266`):** Move `RobloxServerQueryServiceBehaviorTests` to `[Collection("SerialGlobalState")]`; wrap the 2 remaining search tests in `Phase3ServerBrowserAndSearchTests.cs` in `using (LocalizationService.OverrideLanguageForTest(AppLanguage.English))`.
- **Verification:** Clean build `-warnaserror`, `RobloxProcessServiceLifecycleBehaviorTests` (x3 consecutive), `FlagIconServiceTests|Phase3BuildAssetsAndFlagTests`, `Phase3AsyncSafetyAndDialogTests`, `RobloxServerQueryServiceBehaviorTests|Phase3ServerBrowserAndSearchTests`, `Shiro.Tests.ArchitectureTests`, and `P3CompositionAndArchitectureTests`.

---

### Task 5: Reorganize `tests/Shiro.Tests/` Root `.cs` Files — Wave 1 (Architecture, Domain, Auth, Storage, Network, Launch, Windows)

- Pure `git mv` ONLY (no namespace edits, no C# modifications) organizing single-domain root test files into:
  - `Architecture/` (12 files)
  - `Domain/` (8 files)
  - `Auth/` (19 files)
  - `Storage/` (28 files)
  - `Network/` (+27 files -> 36 total)
  - `Launch/` (+35 files -> 37 total)
  - `Windows/` (28 files)
- **Verification:** `dotnet build "Shiro.sln" --no-incremental -warnaserror`, `Shiro.Tests.ArchitectureTests`, `P3CompositionAndArchitectureTests`, `P3DomainPurityTests`, and `git diff --summary BASE..HEAD` shows pure renames (`create`/`delete` 0 content changes).

---

### Task 6: Reorganize `tests/Shiro.Tests/` Root `.cs` Files — Wave 2 (Ui, TestInfrastructure, Regressions) + Root Guardrail

- Pure `git mv` of all remaining root `.cs` files into:
  - `TestInfrastructure/` (+1 file `StoragePathsTestCollection.cs` -> 3 total)
  - `Ui/` (+38 files -> 46 total)
  - `Regressions/` (25 cross-cutting / legacy sweep test files + any straggler leaving **0** `.cs` files in `tests/Shiro.Tests/` root)
- Add `[Fact] public void TestFiles_ShouldBeOrganizedInSubfolders()` in `tests/Shiro.Tests/Architecture/ArchitectureTests.Hygiene.cs` (`<= 550` lines) enforcing `0` `.cs` files in `tests/Shiro.Tests/` root.
- **Verification:** `dotnet build "Shiro.sln" --no-incremental -warnaserror`, `Shiro.Tests.ArchitectureTests`, `P3CompositionAndArchitectureTests`, `P3DomainPurityTests`, `LocalizationIntegrityTests`, confirm `0` root `.cs` files and `252` total `.cs` files under `tests/Shiro.Tests/`.
