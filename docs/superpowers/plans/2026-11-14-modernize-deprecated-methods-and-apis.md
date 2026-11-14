# Modernize Deprecated Roblox APIs, Client Techniques, .NET 10 Code & Dependencies Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Eliminate retired/deprecated Roblox Web APIs (especially before the December 11, 2026 Avatar v1–v3 sunset), update Roblox FPS/version/GeoIP/VC++ client handling, modernize remaining .NET 10 WinForms/P-Invoke/obsolete leftovers, and upgrade NuGet dependencies safely while preserving every architecture budget and coverage floor.

**Architecture:** Execute in three risk-ordered waves:
1. **Wave 1 — Breaking Roblox Web APIs (Tasks 1–3):** Retire dead `v1/account/pin` HTTP calls first to recover ~80 lines of headroom in `RobloxSettingsApiService.cs` before migrating Avatar updates to `PATCH avatar.roblox.com/v4/avatar`, and move missing-asset lookup/purchase + place fallback off deprecated `economy.roblox.com` routes onto `catalog.roblox.com` / `apis.roblox.com/marketplace-sales`.
2. **Wave 2 — Roblox Client, Windows Shell & Code Hygiene (Tasks 4–7):** Switch version checks to `clientsettings` `v2`, migrate FPS unlocking from Allowlist-blocked `DFIntTaskSchedulerTargetFps` FFlag to native `GlobalBasicSettings_13.xml` `FramerateCap`, enforce HTTPS GeoIP (`https://ipwho.is/<ip>`), update `PrerequisiteService` VC++ Redist check/URL (`vs/18/vc_redist.x64.exe`), harden Win11 tray sweeping (`DesktopWindowContentBridge` + honest `forceOpenFlyout`), surface Anti-AFK focusless limitations, and clean dead `[Obsolete]`, `[DllImport]`, `4.2.0` version fallback, WinForms `BeginInvoke`/`InvokeIfRequired`, and legacy launch strings.
3. **Wave 3 — Dependencies (Tasks 8–9):** Apply low-risk NuGet upgrades (`WebView2`, `ProtectedData`, `Tmds.DBus.Protocol`, `Microsoft.NET.Test.Sdk`, `NSubstitute`) with lockfile regeneration first, leaving breaking major upgrades (`xunit` 2.9.3 -> `xunit.v3` and `Avalonia` 11 -> 12.1.6 gated on `LucideAvalonia`) isolated in Task 9.

**Tech Stack:** C# (latest), .NET 10 (`net10.0-windows10.0.19041.0`, `win-x64`), Avalonia `11.3.22` (target `12.1.6` in Task 9B), `System.Text.Json` source generation (`ShiroAppJson`), xUnit + NSubstitute, `Directory.Packages.props` (Central Package Management + `RestorePackagesWithLockFile=true`).

**Verified Baseline (2026-11-14):**
- `dotnet build Shiro.sln --no-restore --nologo`: `0 Warning(s), 0 Error(s)`.
- `dotnet test Shiro.sln --no-build --nologo --filter "FullyQualifiedName~ArchitectureTests"`: `Passed: 51, Failed: 0, Skipped: 0`.
- `dotnet list Shiro.sln package --vulnerable`: `0` vulnerable packages; `--deprecated` reports `xunit 2.9.3` (`Legacy`, alt `xunit.v3`).

---

### Global Constraints

1. **Source File & Class Budgets (`tests/Shiro.Tests/ArchitectureTests.Budgets.cs`):**
   - Every C# file MUST remain `<= 485` lines (`NoSourceCodeFiles_ShouldExceed500Lines`).
   - Every test file MUST remain `<= 550` lines (`NoTestFiles_ShouldExceed550Lines`).
   - Default single-class total budget across partials is `<= 1150` lines (`NoClass_ShouldExceedLineBudget_AcrossPartials`).
   - `RobloxSettingsApiService.cs` is currently `464` lines: **Task 1 MUST execute before Task 2**, and Task 2 MUST extract `AvatarV4PayloadMapper.cs` so `RobloxSettingsApiService.cs` stays comfortably below `485` lines.
2. **Code Hygiene & Domain Purity (`tests/Shiro.Tests/ArchitectureTests.Hygiene.cs`):**
   - `TreatWarningsAsErrors=true` in `src/Shiro/Shiro.csproj`.
   - `UntypedCatchBlocks=0` (never write bare `catch { }` or `catch (Exception)` without typed handling or explicit logging per existing style).
   - `CSharpComments_ShouldNotContainVietnameseDiacritics`: **ALL C# comments, docstrings, and log messages MUST be strict ASCII English.**
   - `DomainFolder_ShouldNotDependOn_ViewModelsOrAvaloniaOrWin32Registry`.
   - `FastJson_ShouldHaveSingleDefinition`; register all new DTOs with `[JsonSerializable(...)]` in `src/Shiro/Services/ShiroAppJson.cs` (trimming/AOT-safe source generation).
   - No new async `void` handlers in Views (`AsyncVoidHandlers_InViews_ShouldNotExceedBaseline == 0`).
3. **Branch Coverage Floors (`tests/coverage-floors.json`):**
   - `Services/Network/RobloxSettingsApiService.cs`: `>= 87%` branch coverage
   - `AccountManager.cs`: `>= 85%` branch coverage
   - `Services/Network/RobloxServerQueryService.cs`: `>= 81%` branch coverage
   - `Services/Network/AccountApiDispatcher.Endpoints.cs`: `>= 77%` branch coverage
   - `Services/Launch/AccountLaunchService.cs`: `>= 73%` branch coverage
4. **Localization Contract (`tests/Shiro.Tests/LocalizationServiceTests.cs`):**
   - Line 295 asserts `[InlineData("Set_UnlockFpsTip")]` is non-empty in both `en.json` and `vi.json`. Update its text rather than removing/renaming the key; keep `en.json` and `vi.json` keys 1:1 symmetric.
5. **NuGet Lockfiles:**
   - `RestorePackagesWithLockFile=true`: any change to `Directory.Packages.props` MUST be followed by `dotnet restore Shiro.sln --force-evaluate` so `src/Shiro/packages.lock.json` and `tests/Shiro.Tests/packages.lock.json` stay in sync.

### Review Focus

- **Live Roblox Contract Verification Before DTO Freeze:** Tasks 2 and 3 MUST verify live response/request schemas (`avatar.roblox.com/docs/json/v4`, `v1/avatar-rules`, `catalog.roblox.com/v1/catalog/items/details`, `apis.roblox.com/marketplace-sales`) in Step 1 before committing `ShiroAppJson.cs` payload types.
- **No Unwanted UI Flyout Popups:** Task 6 MUST NOT call UI automation or simulate clicks that pop open the Windows 11 tray overflow flyout during background cleanup when `forceOpenFlyout == false`.
- **Keep Working Fragile Mechanisms Intact:** `RobloxSingletonService` (`ROBLOX_singletonMutex` / `Event`) and `RobloxDirectLauncher` (`RobloxCookies.dat` DPAPI injection) still work on current Windows Roblox Player; do NOT remove them in this plan.

### File Structure Overview

- **Create:**
  - `src/Shiro/Services/Network/AvatarV4PayloadMapper.cs` (maps saved Shiro outfit JSON + brick colors/scales/assets -> Roblox Avatar `v4` `PATCH /v4/avatar` request payload)
  - `tests/Shiro.Tests/Network/AvatarV4PayloadMapperTests.cs` (unit tests for v4 mapping & edge cases)
- **Modify (by Task):**
  - **Task 1:** `src/Shiro/Services/Network/RobloxSettingsApiService.cs`, `src/Shiro/Domain/Account.SettingsApi.cs`, `src/Shiro/Domain/Account.SocialAndFriendsApi.cs`, `src/Shiro/Services/Auth/IRobloxSessionService.cs`, `src/Shiro/Services/Auth/RobloxSessionService.cs`, `src/Shiro/Services/Launch/AccountLaunchService.cs`, `src/Shiro/Services/Network/RobloxSocialApiService.cs`, `src/Shiro/Services/ShiroAppJson.cs`, `src/Shiro/ViewModels/Dialogs/AccountUtilsViewModel.cs`, `src/Shiro/AvaloniaUI/Views/AccountUtilsDialog.axaml`, `src/Shiro/Resources/Strings/{en,vi}.json`, and test files `RobloxSettingsApiServiceTests.cs`, `RobloxSettingsAndQueryServiceBehaviorTests.cs`, `ServerListAndAccountSettingsTests.cs`, `AccountDecouplingTests.cs`, `JsonSerializationTests.cs`, `AccountUtilsViewModelTests.cs`, `AccountLaunchServiceBranchTests.Fallback.cs`.
  - **Task 2:** `src/Shiro/Services/Network/RobloxSettingsApiService.cs`, `src/Shiro/Services/Network/AvatarV4PayloadMapper.cs` (new), `src/Shiro/Services/ShiroAppJson.cs`, `tests/Shiro.Tests/Network/RobloxSettingsApiServiceTests.cs`, `tests/Shiro.Tests/Network/AvatarV4PayloadMapperTests.cs` (new).
  - **Task 3:** `src/Shiro/Services/Network/RobloxApiClient.cs`, `src/Shiro/Services/Network/AssetCommerceService.cs`, `src/Shiro/ViewModels/Dialogs/AssetDetailsParser.cs`, `src/Shiro/ViewModels/Dialogs/MissingAssetsViewModel.cs`, `src/Shiro/Services/Network/GameInfoService.cs`, `src/Shiro/Services/ShiroAppJson.cs`, `src/Shiro/Resources/Strings/{en,vi}.json`, `tests/Shiro.Tests/Dialogs/MissingAssetsViewModelTests.cs`, plus AssetCommerce/GameInfo unit tests.
  - **Task 4:** `src/Shiro/Services/Network/GameInfoService.cs`, `src/Shiro/Services/Launch/ClientSettingsPatcher.cs`, `src/Shiro/AvaloniaUI/Views/SettingsView.axaml`, `src/Shiro/ViewModels/Dialogs/SettingsViewModel.cs`, `src/Shiro/Resources/Strings/{en,vi}.json`, `tests/Shiro.Tests/ClientSettingsPatcherTests.cs`, `tests/Shiro.Tests/ClientSettingsAndAntiAfkTests.cs`, `tests/Shiro.Tests/Dialogs/SettingsViewModelTests.cs`.
  - **Task 5:** `src/Shiro/AccountManager.cs`, `src/Shiro/Services/Launch/PrerequisiteService.cs`, `tests/Shiro.Tests/AccountManagerBehaviorTests.cs` (if asserting default URL) + Prerequisite tests.
  - **Task 6:** `src/Shiro/Services/Windows/Win32TrayHelper.cs`, `src/Shiro/Services/Windows/AntiAfkService.cs`, `src/Shiro/Resources/Strings/{en,vi}.json`, `tests/Shiro.Tests/ClientSettingsAndAntiAfkTests.cs` (or dedicated Tray/AntiAfk unit tests).
  - **Task 7:** `src/Shiro/Services/Windows/MemoryOptimizer.cs`, `src/Shiro/Services/Windows/RobloxAudioService.cs`, `src/Shiro/AvaloniaUI/Views/MessageBoxDialog.axaml.cs`, `src/Shiro/Services/Network/UpdateCheckService.cs`, `src/Shiro/AccountManager.cs`, `src/Shiro/AccountManager.RecentGamesAndSettings.cs`, `src/Shiro/Services/Launch/LaunchCoordinator.Models.cs`, `src/Shiro/Services/Launch/LaunchCoordinator.cs`, `src/Shiro/Services/Launch/AccountLaunchService.cs`, `src/Shiro/Services/Launch/AccountLaunchService.Home.cs`, `tests/Shiro.Tests/ArchitectureTests.SourceInvariants.cs`, `tests/Shiro.Tests/AccountManagerBehaviorTests.cs`, `tests/Shiro.Tests/LaunchResultTests.cs`.
  - **Task 8:** `Directory.Packages.props`, `src/Shiro/packages.lock.json`, `tests/Shiro.Tests/packages.lock.json`.
  - **Task 9:** `Directory.Packages.props`, `tests/Shiro.Tests/Shiro.Tests.csproj`, `src/Shiro/packages.lock.json`, `tests/Shiro.Tests/packages.lock.json`, and any incompatible test/Avalonia 12/Lucide callers discovered in the Task 9 verification gate.

---

## Wave 1: Breaking Roblox Web API Migrations

### Task 1: Retire Dead `v1/account/pin` & `/unlock` HTTP Calls and Remove Legacy PIN Unlock UI

**Context:** Roblox discontinued the 4-digit Account PIN feature in favor of parent-managed Parental Controls and 2SV. Currently `RobloxSettingsApiService.CheckPinAsync` calls `GET https://auth.roblox.com/v1/account/pin/` BEFORE `SetFollowPrivacyAsync`, `LogOutOfOtherSessionsAsync`, `SetDisplayNameAsync`, `RobloxSocialApiService` friend/unfriend calls, `Account.SocialAndFriendsApi.cs`, and `AccountLaunchService.cs:365`. When Roblox returns `404`/`410`, `CheckPinAsync` returns `false`, breaking all downstream settings/social/join flows whenever `PinUnlocked` is not set in the future. Removing these dead calls now also frees ~80 lines in `RobloxSettingsApiService.cs` (currently `464/485` lines) ahead of Task 2.

**Interfaces:**
- Removes `UnlockPinAsync` from `IRobloxSessionService` (`src/Shiro/Services/Auth/IRobloxSessionService.cs:16`), `RobloxSessionService` (`src/Shiro/Services/Auth/RobloxSessionService.cs:39-42`), `Account` (`src/Shiro/Domain/Account.SettingsApi.cs:15-16`), and `RobloxSettingsApiService` (`src/Shiro/Services/Network/RobloxSettingsApiService.cs:106-141`).
- Removes `RobloxPinInfo` (`RobloxSettingsApiService.cs:16-23`, `ShiroAppJson.cs:26`) and `PinUnlockPayload` (`ShiroAppJson.cs:108, 127`).
- Converts or removes `CheckPinAsync`: replace `CheckPinAsync` HTTP gate with an authenticated-session guard (`EnsureAuthenticatedSession(Account? account)` checking `!string.IsNullOrWhiteSpace(account?.SecurityToken)`), removing `CheckPinAsync` from `Account.SettingsApi.cs`, `RobloxSettingsApiService.cs`, `Account.SocialAndFriendsApi.cs`, `RobloxSocialApiService.cs`, and `AccountLaunchService.cs:365` (so settings/social requests rely directly on 401/403 HTTP status handling from the target endpoint).
- Removes PIN Unlock UI controls from `src/Shiro/AvaloniaUI/Views/AccountUtilsDialog.axaml` (~lines 70-100) and `Pin` / `UnlockPinCommand` / `UnlockPinAsync` / `IsValidPin` from `src/Shiro/ViewModels/Dialogs/AccountUtilsViewModel.cs`.

**Steps:**
- [ ] **Step 1: Verify all remaining references and design exact replacement**
  Run `git grep -n -E "PinUnlocked|PinMustBeFourDigits|UnlockPin|CheckPinAsync" "src/Shiro" "tests"` to confirm every reference across views, domain, services, localization, and tests. Decide whether `Account.PinUnlocked` persisted property stays unused for backward JSON deserialization or gets marked/ignored cleanly in `Account` without breaking old `AccountData.json` files.
- [ ] **Step 2: Write failing tests first (TDD Red)**
  - In `tests/Shiro.Tests/Network/RobloxSettingsApiServiceTests.cs`: update `SetFollowPrivacy_ReturnsFalseOn401` (lines 90-110) so it asserts the call goes directly to `user-settings` (and/or fails cleanly when `SecurityToken` is empty) with NO `v1/account/pin` request made; remove `UnlockPin_SendsPinInBody_AndHandlesEnabledUnlockedUntil` (lines 112-150) and `CheckPinAsync_VariousStates_EvaluatesExpectedly` (lines 180-225), and add `SetFollowPrivacy_EmptySecurityToken_ReturnsFalseWithoutHttpRequest` & `LogOutOfOtherSessions_EmptySecurityToken_ReturnsFalseWithoutHttpRequest` to preserve `>= 87%` branch coverage on `RobloxSettingsApiService.cs`.
  - In `tests/Shiro.Tests/RobloxSettingsAndQueryServiceBehaviorTests.cs` (lines 33-110), `tests/Shiro.Tests/ServerListAndAccountSettingsTests.cs` (lines 226-240), `tests/Shiro.Tests/AccountDecouplingTests.cs` (line 50), `tests/Shiro.Tests/JsonSerializationTests.cs` (lines 108-123), `tests/Shiro.Tests/Dialogs/AccountUtilsViewModelTests.cs` (lines 18-85): remove obsolete PIN tests.
  - In `tests/Shiro.Tests/AccountLaunchServiceBranchTests.Fallback.cs` (lines 263-273): remove `if (url.Contains("v1/account/pin"))` mock branch and assert VIP join directly inspects `user-settings`.
- [ ] **Step 3: Run targeted tests and verify expected Red compilation/test failures**
  Run `dotnet test Shiro.sln --no-restore --nologo --filter "FullyQualifiedName~RobloxSettingsApiServiceTests|FullyQualifiedName~AccountUtilsViewModelTests|FullyQualifiedName~AccountLaunchServiceBranchTests"` and observe failures due to existing `v1/account/pin` calls / missing new behavior.
- [ ] **Step 4: Implement PIN retirement across Services, Domain, ViewModel, UI, and Locales**
  - Edit `src/Shiro/Services/Network/RobloxSettingsApiService.cs`: remove `RobloxPinInfo`, `CheckPinAsync`, `UnlockPinAsync`; add concise `private bool HasValidSession(Account? account, [CallerMemberName] string caller = "")` helper replacing `CheckPinAsync` gates in `SetFollowPrivacyAsync`, `ChangePasswordAsync`, `ChangeEmailAsync`, `LogOutOfOtherSessionsAsync`, and `SetDisplayNameAsync` (note: check `ChangePasswordAsync` & `ChangeEmailAsync` around lines 200-260 to confirm uniform session check).
  - Edit `src/Shiro/Services/Auth/IRobloxSessionService.cs`, `src/Shiro/Services/Auth/RobloxSessionService.cs`, `src/Shiro/Domain/Account.SettingsApi.cs`, `src/Shiro/Domain/Account.SocialAndFriendsApi.cs`, `src/Shiro/Services/Network/RobloxSocialApiService.cs`, `src/Shiro/Services/Launch/AccountLaunchService.cs` (around line 365: handle `403` / settings update failure without `CheckPinAsync`).
  - Edit `src/Shiro/Services/ShiroAppJson.cs`: remove `RobloxPinInfo` (line 26) and `PinUnlockPayload` (lines 108, 127).
  - Edit `src/Shiro/ViewModels/Dialogs/AccountUtilsViewModel.cs` and `src/Shiro/AvaloniaUI/Views/AccountUtilsDialog.axaml`: remove the PIN unlock block/command; remove unused `AccountUtils.PinMustBeFourDigits`, `AccountUtils.UnlockPin*` keys cleanly from both `src/Shiro/Resources/Strings/en.json` and `vi.json` (verify parity in `LocalizationServiceTests`).
- [ ] **Step 5: Run full test & architecture suite + verify branch coverage**
  Run:
  ```powershell
  dotnet test Shiro.sln --no-restore --nologo --filter "FullyQualifiedName~RobloxSettings|FullyQualifiedName~AccountUtils|FullyQualifiedName~ServerListAndAccountSettings|FullyQualifiedName~AccountDecoupling|FullyQualifiedName~JsonSerialization|FullyQualifiedName~AccountLaunchServiceBranch|FullyQualifiedName~LocalizationService|FullyQualifiedName~ArchitectureTests"
  ```
  Verify `RobloxSettingsApiService.cs` line count is `<= 400` lines and passes `ArchitectureTests`.
- [ ] **Step 6: Commit Task 1**
  `git commit -m "refactor(auth): retire dead Roblox v1/account/pin API and legacy PIN unlock UI"`

---

### Task 2: Migrate Avatar Customization to `PATCH https://avatar.roblox.com/v4/avatar` (Before Dec 11, 2026 Cutoff)

**Context:** Roblox announced deprecation of legacy Avatar v1–v3 mutation endpoints (`/v1/avatar/set-player-avatar-type`, `/v1/avatar/set-scales`, `/v1/avatar/set-body-colors`, `/v2/avatar/set-wearing-assets`) effective **December 11, 2026**, consolidating them into `PATCH https://avatar.roblox.com/v4/avatar` (accepting `updateTypes` and `avatarDefinition`). Currently `RobloxSettingsApiService.SetAvatarAsync` (`lines 374-462`) fires 3–4 separate v1/v2 calls and triggers `OpenMissingAssetsDialog` when wearing assets return `invalidAssetIds`.

**Interfaces:**
- **New:** `internal static class AvatarV4PayloadMapper` in `src/Shiro/Services/Network/AvatarV4PayloadMapper.cs`
  - **Consumes:** Saved outfit JSON (`string avatarJsonData`) from `AccountApiDispatcher.Endpoints.cs:472` (`playerAvatarType`, `scales`, `bodyColors` / `bodyColor3s`, `assets`).
  - **Produces:** Strongly typed Avatar v4 PATCH DTO (`AvatarV4PatchRequest` serialized via `ShiroAppJson.cs`) containing `updateTypes` + `avatarDefinition`, plus parsed missing asset ID list from v4 error/partial response.
- **Modified:** `RobloxSettingsApiService.SetAvatarAsync(Account account, string avatarJsonData, CancellationToken cancellationToken = default)` (invoked via `Account.SetAvatarAsync` and `AccountApiDispatcher.Endpoints.cs:472`).

**Steps:**
- [ ] **Step 1: Verification Gate — Inspect Live Roblox Avatar v4 OpenAPI Schema & BrickColor Palette**
  Run `Invoke-RestMethod` or `curl` against:
  - `https://avatar.roblox.com/docs/json/v4` (extract exact property names, enum string values for `updateTypes`, `avatarType` `R6`/`R15`, `scales`, `bodyColor3s` format e.g. `#RRGGBB` vs hex without `#`, `assets` item shape `{ id, meta }`, and exact response/error schema for unowned/invalid assets!).
  - `https://avatar.roblox.com/v1/avatar-rules` (fetch `bodyColorsPalette` mapping of legacy integer `brickColorId` -> `hexColor` if stored outfits use legacy `brickColorId` integers in `bodyColors` instead of `bodyColor3s` hex strings!).
  Record the exact verified field names in `AvatarV4PayloadMapper.cs` docstrings/comments (strict ASCII English).
- [ ] **Step 2: Write failing tests first (TDD Red)**
  - Create `tests/Shiro.Tests/Network/AvatarV4PayloadMapperTests.cs`:
    - Maps `playerAvatarType`, `scales`, `bodyColors` (both hex `bodyColor3s` and legacy integer `brickColorId` fields), and `assets` (`id`, optional `meta`) into the verified `AvatarV4PatchRequest` JSON structure with the exact `updateTypes` list.
    - Omits sections absent from `avatarJsonData` cleanly without emitting null overwrite blocks.
    - Handles empty/malformed/array JSON safely without throwing.
  - Update `tests/Shiro.Tests/Network/RobloxSettingsApiServiceTests.cs` (`SetAvatarAsync_AppliesAttributesAndHandlesMissingAssets`, lines 376-408):
    - Assert `SetAvatarAsync` sends `PATCH` to `https://avatar.roblox.com/v4/avatar` with `X-CSRF-TOKEN` retry-on-403 via `ExecuteAccountRestAsync`.
    - Assert missing/unowned asset error response parses unowned asset IDs and invokes `IDialogService.OpenMissingAssetsDialog(acc, missingIds)`.
    - Add failure branch test (null account, CSRF acquire failure, HTTP 400 non-asset error) so `RobloxSettingsApiService.cs` stays `>= 87%` branch coverage.
- [ ] **Step 3: Run targeted tests and verify TDD Red failures**
  Run `dotnet test Shiro.sln --no-restore --nologo --filter "FullyQualifiedName~AvatarV4PayloadMapperTests|FullyQualifiedName~SetAvatarAsync|FullyQualifiedName~SetAvatar"` and confirm expected failure because `AvatarV4PayloadMapper` does not exist and `SetAvatarAsync` still hits v1/v2 routes.
- [ ] **Step 4: Implement `AvatarV4PayloadMapper`, `ShiroAppJson` DTOs, and `SetAvatarAsync`**
  - Add `[JsonSerializable]` attributes and sealed record/class DTOs (`AvatarV4PatchRequest`, definition sub-records, missing-assets error DTO) in `src/Shiro/Services/ShiroAppJson.cs`; remove `SetPlayerAvatarTypePayload` (lines 111, 130) once unused.
  - Create `src/Shiro/Services/Network/AvatarV4PayloadMapper.cs` (keep `<= 250` lines; include fallback table from verified `v1/avatar-rules` `brickColorsPalette` so older saved JSON using numeric BrickColor IDs converts seamlessly to `hexColor` required by v4 `bodyColor3s`).
  - Rewrite `RobloxSettingsApiService.SetAvatarAsync` (`src/Shiro/Services/Network/RobloxSettingsApiService.cs`): build single `PATCH v4/avatar` `RestRequest` (handling CSRF via `ExecuteAccountRestAsync` without extra redundant sequential calls), inspect response for unowned/invalid asset IDs (supporting both v4 field shape verified in Step 1 and legacy `invalidAssetIds` if returned), and trigger `AppServices.Dialogs.OpenMissingAssetsDialog`.
- [ ] **Step 5: Run tests & ArchitectureChecks**
  Run `dotnet test Shiro.sln --no-restore --nologo --filter "FullyQualifiedName~AvatarV4PayloadMapperTests|FullyQualifiedName~RobloxSettingsApiServiceTests|FullyQualifiedName~ServerListAndAccountSettingsTests|FullyQualifiedName~ArchitectureTests"`.
  Verify `RobloxSettingsApiService.cs` is `<= 485` lines and `0` warnings.
- [ ] **Step 6: Commit Task 2**
  `git commit -m "feat(avatar): migrate SetAvatarAsync to unified Roblox Avatar v4 PATCH endpoint"`

---

### Task 3: Migrate Asset Purchase, Catalog Lookup & Place Fallback Off Deprecated `economy.roblox.com`

**Context:** Roblox retired `POST https://economy.roblox.com/v1/purchases/products/{productId}` and deprecated `GET https://economy.roblox.com/v2/assets/{id}/details` (`AssetCommerceService.cs:20, 37-41` and `GameInfoService.cs:311` fallback). Missing-item purchasing in `MissingAssetsViewModel` and fallback game title resolution in `GameInfoService` fail or hit harsh legacy rate limits against these endpoints.

**Interfaces:**
- **Modified:** `IRobloxApiClient` / `RobloxApiClient` (`src/Shiro/Services/Network/RobloxApiClient.cs`): add `RestClient Catalog { get; }` pointing to `"https://catalog.roblox.com/"` (compatible with all `Substitute.For<IRobloxApiClient>()` tests).
- **Modified:** `IAssetCommerce` / `AssetCommerceService` (`src/Shiro/Services/Network/AssetCommerceService.cs`) and `AssetDetailsParser` (`src/Shiro/ViewModels/Dialogs/AssetDetailsParser.cs`) + `MissingAssetsViewModel` (`src/Shiro/ViewModels/Dialogs/MissingAssetsViewModel.cs`).
- **Modified:** `GameInfoService.cs:309-325` place title fallback.

**Steps:**
- [ ] **Step 1: Verification Gate — Inspect Live Roblox Catalog Batch Details & Marketplace Purchase Contract**
  Verify against live Roblox endpoints (safe read-only `GET`/`POST` schema lookup):
  - `POST https://catalog.roblox.com/v1/catalog/items/details` with `{ "items": [{ "itemType": "Asset", "id": <sampleAccessoryId> }] }` (note whether `X-CSRF-TOKEN` is required even on POST lookup and exact field names in `data[0]`: `id`, `name`, `price`, `lowestPrice`, `productId`, `collectibleItemId`, `collectibleProductId`, `creatorTargetId`, `isPurchasable` / `priceStatus`).
  - Confirm purchase endpoint used for UGC/Collectible vs standard catalog items (`https://apis.roblox.com/marketplace-sales/v1/item/{collectibleItemId}/purchase-item` payload: `collectibleItemId`, `expectedPrice`, `expectedPurchaserId`, `expectedSellerId`, `idempotencyKey`, `collectibleProductId`), AND identify what to do when a legacy classic asset has no `collectibleItemId` (`purchase fallback` -> open `https://www.roblox.com/catalog/{assetId}/` in browser with clear localized prompt rather than silently failing on dead `v1/purchases/products`).
  - Check whether `catalog.roblox.com/v1/catalog/items/details` or `https://games.roblox.com/v1/games/multiget-place-details?placeIds={placeId}` works unauthenticated for `GameInfoService.cs:309-325` (remember `GameInfoService` fetches titles using plain `HttpClient` without a user cookie!).
- [ ] **Step 2: Write failing tests first (TDD Red)**
  - In `tests/Shiro.Tests/Dialogs/MissingAssetsViewModelTests.cs`:
    - Update JSON fixtures (`Load_SumsOnlyPositiveCosts_AndPopulatesFreeAndOffSale`, etc.) to test `AssetDetailsParser.Parse` on modern `catalog.roblox.com/v1/catalog/items/details` `data[0]` JSON (plus backward compatibility if needed during transition), including `collectibleItemId` / `collectibleProductId` / `priceStatus: "Off Sale"` / `isPurchasable: false`.
    - Add test verifying `BuyCommand` handles collectible purchase metadata AND surfaces clean browser-link / unpurchasable guidance when an item lacks `collectibleItemId` / valid purchase product ID.
  - Add/update tests for `AssetCommerceService` (using `Account.TestHttpClientFactory` + `AppServices.Override<IRobloxApiClient>(new RobloxApiClient(handler))`):
    - Asserts `GetAssetDetailsAsync` calls `POST https://catalog.roblox.com/v1/catalog/items/details` (retrying once on 403 `x-csrf-token` if returned!).
    - Asserts `PurchaseAsync` calls `POST https://apis.roblox.com/marketplace-sales/v1/item/{collectibleItemId}/purchase-item` with `expectedPurchaserId = account.UserID` and deterministic GUID `idempotencyKey` (avoiding deprecated `v1/purchases/products`).
  - Add unit test covering `GameInfoService` fallback path using the verified non-`economy.roblox.com` route.
- [ ] **Step 3: Run targeted tests and verify TDD Red failures**
  Run `dotnet test Shiro.sln --no-restore --nologo --filter "FullyQualifiedName~MissingAssetsViewModelTests|FullyQualifiedName~GameInfoService"` and confirm failures before implementation.
- [ ] **Step 4: Implement Catalog Details, Marketplace-Sales Purchase, and Place Title Fallback**
  - Edit `src/Shiro/Services/Network/RobloxApiClient.cs`: expose `RestClient Catalog { get; }` -> `"https://catalog.roblox.com/"` in both constructors.
  - Edit `src/Shiro/Services/ShiroAppJson.cs`: replace `PurchaseProductPayload` (lines 106, 125) with `CatalogItemDetailsBatchRequest`, `MarketplacePurchasePayload`, and response models cleanly registered with `[JsonSerializable]`.
  - Edit `src/Shiro/ViewModels/Dialogs/AssetDetailsParser.cs` (`AssetDetails` record extended with `string? CollectibleItemId`, `string? CollectibleProductId`, and `Parse(string json)` supporting `catalog.roblox.com/v1/catalog/items/details` `data[0]` schema).
  - Edit `src/Shiro/Services/Network/AssetCommerceService.cs` & `src/Shiro/ViewModels/Dialogs/MissingAssetsViewModel.cs`:
    - `GetAssetDetailsAsync`: POST `v1/catalog/items/details` via `AppServices.RobloxApi.Catalog` (handling CSRF automatically via `ExecuteAccountRestAsync`).
    - `PurchaseAsync` (or updated `IAssetCommerce` signature passing `collectibleItemId` & `collectibleProductId` alongside `productId`, `expectedPrice`, `sellerId`): purchase via `apis.roblox.com/marketplace-sales/v1/item/{collectibleItemId}/purchase-item`; when `collectibleItemId` is absent/non-collectible legacy asset, open catalog page fallback or show clear localized status via `en.json` & `vi.json`.
  - Edit `src/Shiro/Services/Network/GameInfoService.cs:309-325`: replace `https://economy.roblox.com/v2/assets/{placeId}/details` with the verified unauthenticated catalog/place endpoint (no `economy.roblox.com` string left in `GameInfoService.cs`).
- [ ] **Step 5: Run tests & ArchitectureChecks**
  Run `dotnet test Shiro.sln --no-restore --nologo --filter "FullyQualifiedName~MissingAssets|FullyQualifiedName~GameInfo|FullyQualifiedName~LocalizationService|FullyQualifiedName~ArchitectureTests"`.
- [ ] **Step 6: Commit Task 3**
  `git commit -m "feat(commerce): migrate asset lookup/purchase and place fallback off deprecated economy.roblox.com"`

---

## Wave 2: Roblox Client, Windows Shell & Code Hygiene

### Task 4: Switch Client Version to `clientsettings` `v2` & Migrate FPS Unlocking to Native `FramerateCap`

**Context:**
1. `GameInfoService.LoadClientVersionAsync` (`lines 402-423`) queries `https://clientsettings.roblox.com/v1/client-version/WindowsPlayer` (legacy v1 route; current route is `/v2/client-version/WindowsPlayer`, with CDN mirror `clientsettingscdn.roblox.com/v2/...`).
2. `ClientSettingsPatcher.cs` (`line 52` `FpsFlagKey = "DFIntTaskSchedulerTargetFps"`) writes `DFIntTaskSchedulerTargetFps` into `%LOCALAPPDATA%\Roblox\Versions\<ver>\ClientSettings\ClientAppSettings.json`. Roblox's FastFlag Allowlist silently ignores/blocks `DFIntTaskSchedulerTargetFps` on modern clients, whereas Roblox natively honors `<int name="FramerateCap">...</int>` in `%LOCALAPPDATA%\Roblox\GlobalBasicSettings_13.xml` (up to `240` FPS in Roblox Studio/Player settings).

**Interfaces:**
- **Modified:** `GameInfoService.LoadClientVersionAsync` (`src/Shiro/Services/Network/GameInfoService.cs:402-423`).
- **Modified:** `ClientSettingsPatcher` (`src/Shiro/Services/Launch/ClientSettingsPatcher.cs`):
  - `PatchSettings()` / `ApplyFpsLimit(bool unlockFps, int targetFps)` updates `<int name="FramerateCap">` inside `GlobalBasicSettings_13.xml` via `UpdateBasicSettingsXmlContent` (`lines 198-232`) AND strips any stale `"DFIntTaskSchedulerTargetFps"` key left in existing `ClientAppSettings.json` files.
- **Modified (UI/Locale):** `SettingsView.axaml:113` (`Maximum="240"` or documented clamp if `FramerateCap` caps cleanly), `en.json` & `vi.json` (`Set_UnlockFpsTip` updated without removing the key required by `LocalizationServiceTests.cs:295`).

**Steps:**
- [ ] **Step 1: Verify `v2/client-version/WindowsPlayer` live response & Roblox `FramerateCap` XML node**
  Confirm live JSON shape from `https://clientsettings.roblox.com/v2/client-version/WindowsPlayer` still exposes `clientVersionUpload` (`version-...`) and check whether `%LOCALAPPDATA%\Roblox\GlobalBasicSettings_13.xml` (if present on machine, or existing tests in `ClientSettingsPatcherTests.cs`) places `<int name="FramerateCap">` inside `<Properties>` of `UserGameSettings` just like `SavedQualityLevel`.
- [ ] **Step 2: Write failing tests first (TDD Red)**
  - In `tests/Shiro.Tests/ClientSettingsPatcherTests.cs`:
    - Replace expectations asserting `"DFIntTaskSchedulerTargetFps": 144` in `ClientAppSettings.json` with assertions verifying `UpdateBasicSettingsXmlContent` / `ApplyFpsLimit` writes `<int name="FramerateCap">144</int>` into `GlobalBasicSettings_13.xml` when `unlockFps == true` (clamping `<= 240` or handling valid positive range cleanly) and restores default (`60` or removes custom override) while preserving custom XML settings and scrubbing legacy `"DFIntTaskSchedulerTargetFps"` from existing `ClientAppSettings.json` without losing `"FStringCustom": "Test"`.
  - In `tests/Shiro.Tests/ClientSettingsAndAntiAfkTests.cs` (lines 38-55): update to match `FramerateCap` XML patching + legacy `DFIntTaskSchedulerTargetFps` cleanup.
  - Add unit test for `GameInfoService.LoadClientVersionAsync` / v2 endpoint fallback (`clientsettings.roblox.com/v2/...` -> `clientsettingscdn.roblox.com/v2/...`).
- [ ] **Step 3: Run targeted tests and verify TDD Red failures**
  Run `dotnet test Shiro.sln --no-restore --nologo --filter "FullyQualifiedName~ClientSettings|FullyQualifiedName~GameInfoService|FullyQualifiedName~SettingsViewModelTests"`.
- [ ] **Step 4: Implement `clientsettings` `v2`, `FramerateCap` XML patching, and updated locale tooltips**
  - Edit `src/Shiro/Services/Network/GameInfoService.cs:402-423`: query `https://clientsettings.roblox.com/v2/client-version/WindowsPlayer` with fallback to `https://clientsettingscdn.roblox.com/v2/client-version/WindowsPlayer`.
  - Edit `src/Shiro/Services/Launch/ClientSettingsPatcher.cs`: extend `UpdateBasicSettingsXmlContent` & `ConfigureGlobalBasicSettings` to support `int? framerateCap` (`<int name="FramerateCap">`); remove `DFIntTaskSchedulerTargetFps` writing while keeping a lightweight `ScrubLegacyFpsFastFlag(...)` cleanup on `ClientAppSettings.json` during `ApplyFpsLimit` / `PatchSettings`.
  - Check `src/Shiro/AvaloniaUI/Views/SettingsView.axaml:113` & `SettingsViewModel.cs` (`MaxFPSValue`): clamp cleanly to Roblox supported native cap (`60..240` or adjust `Maximum="240"` if appropriate without breaking `SettingsViewModelTests.cs:122` which expects `144m`).
  - Update `Set_UnlockFpsTip` and `Set_TargetFpsTip` in `src/Shiro/Resources/Strings/en.json` and `vi.json` (`line 1108, 1119`) to describe `FramerateCap` in `GlobalBasicSettings_13.xml` (removing mention of `DFIntTaskSchedulerTargetFps` while keeping both JSON keys intact!).
- [ ] **Step 5: Run tests & ArchitectureChecks**
  Run `dotnet test Shiro.sln --no-restore --nologo --filter "FullyQualifiedName~ClientSettings|FullyQualifiedName~SettingsViewModelTests|FullyQualifiedName~LocalizationService|FullyQualifiedName~ArchitectureTests"`.
- [ ] **Step 6: Commit Task 4**
  `git commit -m "fix(client): use clientsettings v2 and native GlobalBasicSettings FramerateCap instead of blocked FFlag"`

---

### Task 5: Enforce HTTPS GeoIP Default & Modernize `PrerequisiteService` VC++ Redist Check/URL

**Context:**
1. `AccountManager.cs:175` initializes `IPApiLink = "http://ip-api.com/json/<ip>"` (cleartext HTTP + commercial restriction), even though `RobloxServerQueryService.cs:58-63, 254` already supports `country_code` (snake_case from `ipwho.is`) and falls back to `"https://ipwho.is/<ip>"`.
2. `PrerequisiteService.cs:18, 27-39` points to `https://aka.ms/vs/17/release/vc_redist.x86.exe` (VS 2022 32-bit x86), while `Shiro.csproj` and modern `RobloxPlayerBeta.exe` both run on **x64** (`win-x64`) and Microsoft's current evergreen VC++ runtime channel is `https://aka.ms/vs/18/release/vc_redist.x64.exe`.

**Interfaces:**
- **Modified:** `AccountManager.IPApiLink` (`src/Shiro/AccountManager.cs:175`).
- **Modified:** `PrerequisiteService` (`src/Shiro/Services/Launch/PrerequisiteService.cs:18, 27-45`).

**Steps:**
- [ ] **Step 1: Verification Gate — Check VC++ `vs/18` x64 registry footprint & callers of `IPApiLink`**
  - Verify `https://aka.ms/vs/18/release/vc_redist.x64.exe` resolves cleanly (HTTP `HEAD` redirect check) and writes/updates `HKLM\SOFTWARE\Microsoft\VisualStudio\14.0\VC\Runtimes\X64` (and/or `WOW6432Node\...\X64`) with `Installed = 1` + `Bld` / `Version` check.
  - Check `git grep -n "IPApiLink|VcRedistUrl|NeedsVCRedist" "src/Shiro" "tests"` to see every reference and test expectation (e.g., if `SettingsView` or `AccountManager` binds/persists `"http://ip-api.com/..."` cleanly or migrates unmodified legacy defaults on load).
- [ ] **Step 2: Write failing tests first (TDD Red)**
  - In `tests/Shiro.Tests/AccountManagerBehaviorTests.cs` (or dedicated server/prerequisite test file): assert `AccountManager` / default GeoIP endpoint uses `"https://ipwho.is/<ip>"` (HTTPS) and upgrade migration logic cleanly replaces uncustomized legacy `"http://ip-api.com/json/<ip>"` if stored unmodified, without overwriting user-customized endpoints.
  - Extract a pure static evaluator in `PrerequisiteService` (e.g., `internal static bool IsVcRuntimeSatisfied(int? installedValue, string? archKeyVersionOrMajor)`) and write unit tests verifying x64 runtime detection alongside existing `VerifyMicrosoftSignature` tests.
- [ ] **Step 3: Run targeted tests and verify TDD Red failures**
  Run `dotnet test Shiro.sln --no-restore --nologo --filter "FullyQualifiedName~AccountManagerBehaviorTests|FullyQualifiedName~Prerequisite"` before editing production code.
- [ ] **Step 4: Implement GeoIP HTTPS switch & `vs/18/vc_redist.x64.exe` update**
  - Edit `src/Shiro/AccountManager.cs:175`: set default `IPApiLink` to `"https://ipwho.is/<ip>"` and update `ServerRegionFormat` comment if still referencing `ip-api` HTTP. If `GeneralSettings` persisted the exact old default `"http://ip-api.com/json/<ip>"` (checked on load), upgrade it to `"https://ipwho.is/<ip>"`.
  - Edit `src/Shiro/Services/Launch/PrerequisiteService.cs:18-45`: change `VcRedistUrl` to `"https://aka.ms/vs/18/release/vc_redist.x64.exe"` and update `NeedsVCRedist()` to inspect x64 registry keys (`SOFTWARE\Microsoft\VisualStudio\14.0\VC\Runtimes\X64` + `SOFTWARE\WOW6432Node\Microsoft\VisualStudio\14.0\VC\Runtimes\X64` via `RegistryView.Registry64` and default view), preserving `VerifyMicrosoftAuthenticodeCore` / `IsMicrosoftSigner` security checks intact.
- [ ] **Step 5: Run tests & ArchitectureChecks**
  Run `dotnet test Shiro.sln --no-restore --nologo --filter "FullyQualifiedName~AccountManager|FullyQualifiedName~RobloxServerQuery|FullyQualifiedName~Prerequisite|FullyQualifiedName~ArchitectureTests"`.
- [ ] **Step 6: Commit Task 5**
  `git commit -m "fix(infra): switch GeoIP default to HTTPS ipwho.is and update VC++ Redist to vs/18 x64"`

---

### Task 6: Harden Windows 11 Tray Sweep (`Win32TrayHelper`) & Anti-AFK Focusless Guidance (`AntiAfkService`)

**Context:**
1. In `Win32TrayHelper.cs:90-92`, `EnumChildWindowsCallback` matches only legacy Win10 tray classes (`ToolbarWindow32`, `SysPager`, `TrayNotifyWnd`), missing Windows 11's XAML Island host (`Windows.UI.Composition.DesktopWindowContentBridge`). Meanwhile, `ThoroughCleanNotificationArea(bool forceOpenFlyout = false)` (`lines 250-253`) accepts `forceOpenFlyout` without honoring it, and `RefreshNotificationArea()` skips hidden overflow XAML islands (`IsWindowVisible(hTop)`).
2. In `AntiAfkService.cs:204-260`, background `PostMessage(WM_KEYDOWN/UP, VK_F15)` + `WM_MOUSEMOVE` is ignored by Roblox's modern RawInput/`GetAsyncKeyState` loop unless `AntiAfkForegroundFallback` (`default: false`) is enabled. Users enabling Anti-AFK on minimized/unfocused windows currently receive no warning explaining why idle kicks still happen.

**Interfaces:**
- **Modified:** `Win32TrayHelper` (`src/Shiro/Services/Windows/Win32TrayHelper.cs`):
  - Extract pure `internal static bool IsTrayNotificationChildClass(ReadOnlySpan<char> className)` helper.
  - Honor `forceOpenFlyout` safely inside `ThoroughCleanNotificationArea` (never popping open UI flyouts during silent background sweeps when `forceOpenFlyout == false`).
- **Modified:** `AntiAfkService` (`src/Shiro/Services/Windows/AntiAfkService.cs`) + `en.json` / `vi.json` (diagnostic log / one-time UI toast or setting tip clarifying focusless RawInput behavior and the `AntiAfkForegroundFallback` option).

**Steps:**
- [ ] **Step 1: Write failing tests first (TDD Red)**
  - In `tests/Shiro.Tests/ClientSettingsAndAntiAfkTests.cs` (or new cohesive unit tests keeping file `<= 550` lines):
    - Assert `Win32TrayHelper.IsTrayNotificationChildClass` returns `true` for `"ToolbarWindow32"`, `"SysPager"`, `"TrayNotifyWnd"`, AND `"Windows.UI.Composition.DesktopWindowContentBridge"`, and `false` for unrelated window classes (`"Button"`, `""`).
    - Assert `AntiAfkService` logs a single structured warning/advisory per cycle/enablement when `AntiAfkForegroundFallback == false` and target window is backgrounded without spamming every tick.
- [ ] **Step 2: Run targeted tests and verify TDD Red failures**
  Run `dotnet test Shiro.sln --no-restore --nologo --filter "FullyQualifiedName~ClientSettingsAndAntiAfkTests"`.
- [ ] **Step 3: Implement `Win32TrayHelper` XAML Bridge matching & `AntiAfkService` one-shot advisory**
  - Edit `src/Shiro/Services/Windows/Win32TrayHelper.cs`: add `"Windows.UI.Composition.DesktopWindowContentBridge"` matching inside `IsTrayNotificationChildClass`, use it in `EnumChildWindowsCallback`, and pass `forceOpenFlyout` cleanly so hidden `TopLevelWindowForOverflowXamlIsland` hosts are still enumerated only when requested/appropriate without popping the flyout window unexpectedly during silent routine cleanup.
  - Edit `src/Shiro/Services/Windows/AntiAfkService.cs` (`lines 204-260`): track whether the focusless-RawInput warning has already been logged in the current session, emit a clear English warning/tip referencing `AntiAfkForegroundFallback` when running in background-only mode, and ensure any user-visible string keys added to `en.json` / `vi.json` stay symmetric.
- [ ] **Step 4: Run tests & ArchitectureChecks**
  Run `dotnet test Shiro.sln --no-restore --nologo --filter "FullyQualifiedName~ClientSettingsAndAntiAfkTests|FullyQualifiedName~LocalizationService|FullyQualifiedName~ArchitectureTests"`.
- [ ] **Step 5: Commit Task 6**
  `git commit -m "fix(windows): support Win11 DesktopWindowContentBridge in tray helper and clarify Anti-AFK focusless behavior"`

---

### Task 7: Clean Up .NET 10 / C# Obsolete APIs, P/Invoke `[LibraryImport]`, Version Fallback & WinForms/Legacy Shims

**Context:** Several low-risk code-level items remain from earlier WinForms/.NET migrations:
- Dead `[Obsolete]` methods with 0 callers: `MemoryOptimizer.NotifyForegroundChanged` (`lines 89-90`), `RobloxAudioService.NotifyForegroundChanged` (`lines 80-81`).
- Last `[DllImport]` attribute instead of source-generated `[LibraryImport]`: `MessageBoxDialog.axaml.cs:137-138` (`MessageBoxDialog` is already `public partial class MessageBoxDialog : Window` at line 39).
- Outdated hardcoded version fallback: `UpdateCheckService.cs:82` (`new Version(4, 2, 0)` vs `<Version>5.0.0</Version>` in `Shiro.csproj`).
- WinForms `Control` compatibility wrappers in `AccountManager.cs:28-43` (`IsHandleCreated`, `BeginInvoke`, `InvokeIfRequired`) used at 4 sites in `AccountManager.RecentGamesAndSettings.cs:44, 47, 66, 80`.
- Legacy string adapters on `LaunchResult` (`LaunchCoordinator.Models.cs:68-81`: `ToLegacyString()`, `FromLegacy(...)`) still wrapping `AccountLaunchService.cs:265`, `AccountLaunchService.Home.cs:127`, and `LaunchCoordinator.cs:46-54`.

**Interfaces:**
- **Removed:** `MemoryOptimizer.NotifyForegroundChanged`, `RobloxAudioService.NotifyForegroundChanged`, `AccountManager.IsHandleCreated` / `BeginInvoke` / `InvokeIfRequired`, `LaunchResult.ToLegacyString` / `FromLegacy`, `LaunchCoordinator(Func<LaunchRequest, CancellationToken, Task<string>>)`.
- **Modified:** `AccountLaunchService` legacy string return points upgraded to return `Task<LaunchResult>` directly if/when all `LaunchCoordinator` callers accept `LaunchResult`. *(Note: verify all callers of `JoinServerAsync` returning `string` before removing `ToLegacyString()` — if WebSocket/HTTP external dispatcher (`AccountApiDispatcher`) relies on `string` status from `Account`, convert `LaunchResult.Message` directly at the API boundary rather than keeping `ToLegacyString()` on `LaunchResult`.)*

**Steps:**
- [ ] **Step 1: Verify every caller of `BeginInvoke`, `InvokeIfRequired`, `IsHandleCreated`, `ToLegacyString`, `FromLegacy`**
  Run `git grep -n -E "BeginInvoke|InvokeIfRequired|IsHandleCreated|ToLegacyString|FromLegacy|new Version\(4" "src/Shiro" "tests"` to confirm zero hidden callers outside the mapped files. Check `AccountManager.cs` test coverage paths (`coverage-floors.json` floor: `85%` branch coverage on `AccountManager.cs`).
- [ ] **Step 2: Write failing tests / architecture guard first (TDD Red)**
  - In `tests/Shiro.Tests/ArchitectureTests.SourceInvariants.cs`: add `Source_ShouldNotUseLegacyDllImport` invariant asserting zero `[DllImport` or `[System.Runtime.InteropServices.DllImport` occurrences exist in `src/Shiro` (`MessageBoxDialog.axaml.cs` will fail this Red test until converted!).
  - Update `tests/Shiro.Tests/AccountManagerBehaviorTests.cs` (`lines 219, 377-383`): remove assertions against deleted `IsHandleCreated`/`BeginInvoke`/`InvokeIfRequired` and ensure direct coverage of `AccountManager` branches remains `>= 85%`.
  - Update `tests/Shiro.Tests/LaunchResultTests.cs` (`lines 1-77`): replace `FromLegacy` / `ToLegacyString` assertions with direct `LaunchResult.Success()`, `LaunchResult.BrowserHome()`, `LaunchResult.Fail(...)` checks (including rate-limited / message / null normalization checks).
- [ ] **Step 3: Run targeted tests and verify TDD Red failures**
  Run `dotnet test Shiro.sln --no-restore --nologo --filter "FullyQualifiedName~ArchitectureTests|FullyQualifiedName~LaunchResultTests|FullyQualifiedName~AccountManagerBehaviorTests"`.
- [ ] **Step 4: Implement cleanups**
  - Delete `NotifyForegroundChanged` in `src/Shiro/Services/Windows/MemoryOptimizer.cs:88-93` and `src/Shiro/Services/Windows/RobloxAudioService.cs:79-84`.
  - Convert `src/Shiro/AvaloniaUI/Views/MessageBoxDialog.axaml.cs:137-138` from `[DllImport(...)]` to `[LibraryImport("user32.dll", EntryPoint = "MessageBoxW", StringMarshalling = StringMarshalling.Utf16)] private static partial int MessageBoxWin32(...)` (verify `using System.Runtime.InteropServices;` at top of file).
  - Edit `src/Shiro/Services/Network/UpdateCheckService.cs:82`: update fallback `new Version(4, 2, 0)` to `new Version(5, 0, 0)` matching `<Version>5.0.0</Version>` in `Shiro.csproj`.
  - Edit `src/Shiro/AccountManager.cs:28-43` and `src/Shiro/AccountManager.RecentGamesAndSettings.cs:44, 47, 66, 80`: remove WinForms `Control` wrappers and dispatch cleanly via `AppServices.HostUiBridge.InvokeOnUiThread(...)` (or `Dispatcher.UIThread.InvokeIfRequired` in `Utilities.cs:46`).
  - Edit `src/Shiro/Services/Launch/LaunchCoordinator.Models.cs:68-81`, `LaunchCoordinator.cs:46-54`, `AccountLaunchService.cs:265`, `AccountLaunchService.Home.cs:127`: return `LaunchResult` directly without `ToLegacyString()` / `FromLegacy(...)`.
- [ ] **Step 5: Run full Release build + full test suite + coverage floor check**
  ```powershell
  dotnet build Shiro.sln --no-restore -c Release --nologo
  dotnet test Shiro.sln --no-build -c Release --nologo
  ```
  Verify `0 Warning(s), 0 Error(s)` and `AccountManager.cs` branch coverage `>= 85%`.
- [ ] **Step 6: Commit Task 7**
  `git commit -m "refactor(cleanup): remove obsolete methods, convert remaining DllImport, and drop WinForms/legacy shims"`

---

## Wave 3: NuGet Dependency Upgrades

### Task 8: Apply Compatible NuGet Package Upgrades & Refresh Lockfiles (`Directory.Packages.props`)

**Context:** Upgrading compatible minor/patch and Windows/test tooling dependencies first delivers immediate bugfixes cleanly without breaking Avalonia 12 or xUnit v3 test runners.

**Target Versions (verified via `dotnet list Shiro.sln package --outdated`):**
- `Microsoft.Web.WebView2`: `1.0.2903.40` -> `1.0.4314.35`
- `System.Security.Cryptography.ProtectedData`: `9.0.0` -> `11.0.0`
- `Tmds.DBus.Protocol`: `0.95.1` -> `0.95.2`
- `Microsoft.NET.Test.Sdk`: `17.13.0` -> `18.12.0`
- `NSubstitute`: `5.3.0` -> `6.2.0`

**Steps:**
- [ ] **Step 1: Inspect `Directory.Packages.props` and verify no NSubstitute 6.x analyzer/API breaks**
  Check `NSubstitute` 6.x release compatibility with `.NET 10` (`Substitute.For`, `Arg.Any`, `DidNotReceiveWithAnyArgs`) and `Microsoft.Web.WebView2` runtime loader behavior on `win-x64`.
- [ ] **Step 2: Update versions in `Directory.Packages.props`**
  Update only the 5 packages above in `d:\Dự Án Visual\Shiro-Account-Manager\Directory.Packages.props`. Do NOT bump `Avalonia*` or `xunit` in this task.
- [ ] **Step 3: Restore & evaluate lockfiles**
  Run:
  ```powershell
  dotnet restore Shiro.sln --force-evaluate
  ```
  Verify updated `src/Shiro/packages.lock.json` and `tests/Shiro.Tests/packages.lock.json` contain zero vulnerable/unexpected downgrade warnings.
- [ ] **Step 4: Build & run complete test suite (Release + Architecture + Functional)**
  ```powershell
  dotnet build Shiro.sln --no-restore -c Release --nologo
  dotnet test Shiro.sln --no-build -c Release --nologo
  ```
  Verify `0 Warning(s), 0 Error(s)` and 100% passing tests.
- [ ] **Step 5: Commit Task 8**
  `git commit -m "chore(deps): upgrade WebView2, ProtectedData, Tmds.DBus.Protocol, Test.Sdk, and NSubstitute"`

---

### Task 9: Isolated Major Upgrade Track — `xunit` -> `xunit.v3` & `Avalonia` 11 -> `12.1.6`

**Context:** Both `xunit 2.9.3 -> xunit.v3` (marked `Legacy`/`Deprecated` by NuGet) and `Avalonia 11.3.22 -> 12.1.6` carry major ecosystem breaking changes:
- `xunit.v3` converts the test assembly into an executable (`<OutputType>Exe</OutputType>` optional/MTP), changes `Avalonia.Headless.XUnit` runner expectations, and updates some assertion overloads.
- `Avalonia 12.1.6` has major XAML/control/theme/headless API changes AND third-party package **`LucideAvalonia` (`1.6.2`)** is referenced across 26 `.axaml`/`.cs` files in `src/Shiro` (23 files) and `tests/Shiro.Tests` (3 files) (`LucideIconNames`). If `LucideAvalonia` has not shipped an Avalonia 12-compatible release, bumping `Avalonia*` to `12.1.6` will fail runtime/compile bindings unless `LucideAvalonia` is updated or replaced.

**Split into two sub-tasks with hard verification gates:**

#### Sub-task 9A: Migrate `xunit` `2.9.3` -> `xunit.v3` + `xunit.runner.visualstudio` `4.0.1`
- [ ] **Step 1: Check `Avalonia.Headless.XUnit` `11.3.22` compatibility with `xunit.v3`**
  Verify on NuGet whether `Avalonia.Headless.XUnit` `11.3.22` depends strictly on `xunit.extensibility.core (>= 2.x < 3.0)` (if `Avalonia.Headless.XUnit 11.x` pins xunit v2, **STOP 9A and run 9A together with/after 9B once `Avalonia.Headless.XUnit 12.x` is verified**!).
- [ ] **Step 2: Update `Directory.Packages.props` and `tests/Shiro.Tests/Shiro.Tests.csproj`**
  Switch `xunit` -> `xunit.v3` (latest `3.x` compatible with `Avalonia.Headless.XUnit`), bump `xunit.runner.visualstudio` -> `4.0.1`, run `dotnet restore Shiro.sln --force-evaluate`.
- [ ] **Step 3: Fix any `xunit.v3` signature/async lifecycle breaks across `tests/Shiro.Tests`**
  Compile and run full test suite (`dotnet test Shiro.sln --no-restore -c Release --nologo`).
- [ ] **Step 4: Commit 9A**
  `git commit -m "chore(tests): migrate xunit 2.9.3 (legacy) to xunit.v3"`

#### Sub-task 9B: Spike & Migrate `Avalonia*` `11.3.22` -> `12.1.6`
- [ ] **Step 1: Verification Gate — Audit `LucideAvalonia` & Avalonia 12 Breaking Changes**
  Check `LucideAvalonia` package metadata on NuGet (whether an Avalonia 12 version exists or `1.6.2` targets Avalonia `11.x` only).
  - **If `LucideAvalonia` does NOT support Avalonia 12:** stop before changing `Avalonia*` versions and open a separate decision for replacing `LucideAvalonia` (e.g., native SVG/PathIcon resource dictionary preserving `LucideIconNames` or custom lightweight icon control) rather than shipping broken `.axaml` bindings.
- [ ] **Step 2: Upgrade `Avalonia*` (`Avalonia`, `.Desktop`, `.Fonts.Inter`, `.ReactiveUI`, `.Themes.Fluent`, `.Headless.XUnit`) to `12.1.6`**
  Update `Directory.Packages.props`, run `dotnet restore Shiro.sln --force-evaluate`, and capture every compile/XAML error under `TreatWarningsAsErrors=true`.
- [ ] **Step 3: Fix Avalonia 12 API changes & run full headless + architecture + Release tests**
  ```powershell
  dotnet build Shiro.sln --no-restore -c Release --nologo
  dotnet test Shiro.sln --no-build -c Release --nologo
  ```
- [ ] **Step 4: Commit 9B**
  `git commit -m "chore(ui): upgrade Avalonia to 12.1.6"`

---

## Final Verification Checklist

After completing Tasks 1–8 (and Task 9 if unblocked):
- [ ] `dotnet build Shiro.sln --no-restore -c Release --nologo` exits `0` with `0 Warning(s), 0 Error(s)`.
- [ ] `dotnet test Shiro.sln --no-build -c Release --nologo` passes all tests including `ArchitectureTests` (all 51+ budgets/hygiene/ratchets).
- [ ] Verify branch coverage against `tests/coverage-floors.json` (especially `RobloxSettingsApiService.cs >= 87%`, `AccountManager.cs >= 85%`, `AccountLaunchService.cs >= 73%`).
- [ ] Confirm `dotnet list Shiro.sln package --deprecated` reports zero deprecated packages (after Task 9A).
- [ ] Smoke-test live UI launch, Settings (`FramerateCap` written to `GlobalBasicSettings_13.xml`), Avatar import, and Missing Assets dialog.
