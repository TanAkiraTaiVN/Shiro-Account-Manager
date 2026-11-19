# P1 Network Gateway, Discord RPC, ServerQuery & UI Stability Fixes Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.
> **Synced against live working tree (`107a1d7` + working edits):** Builds cleanly on completed WT items (`DiscordRpcService` `_workerGeneration`, `SettingsViewModel.SaveAdvanced()` order + 6-arg `ComputeWebServerAction`, `MainViewModel`/`ServerListViewModel` `_lastDefaultReadyStatus`, `AccountApiDispatcher` case-insensitive `Group` + deferred settings save, `StreamerModeService.Shutdown()`, `ToastService.AutoDismissCts`). Already-done items are marked `[x]`.

**Goal:** Eliminate remaining P1 gateway security & API parity gaps (`WebAuthPolicy`, `KestrelWebServer`, `AccountApiDispatcher`, `ApiDocumentationService`, `RAMAccount.lua`), Discord RPC fake ClientId / handshake timeout / CLOSE opcode bugs, `RobloxServerQueryService` join-game CSRF retry & GeoIP SSRF guards, `RobloxModerationService` per-probe timeouts, settings save & live-apply gaps (`MultiRoblox`, `EnableAntiAfk`, `EnableBackgroundThrottling`), remaining ViewModel/avatar/workflow races, Streamer Mode coverage across all 9 dialogs, and `RelayCommand` / theme / UI-thread stalls.

**Architecture:** Derive OpenAPI documentation from one typed route table verified against `AccountApiDispatcher` and `RAMAccount.lua`. Keep heavyweight helpers outside tight partials: **`MainViewModel` has ONLY `23` lines headroom (`3,027/3,050`)**, **`ServerListViewModel` has `15` lines (`1,625/1,640`)**, **`SettingsViewModel.cs` has `9` lines (`476/485` — place new logic in `.Advanced.cs`!)**, and **`FastLoginDialog` has `2` lines (`1,148/1,150`)**. Thumbnail disk cache lives at **`src/Shiro/Services/Network/GameThumbnailService.cs` (`225/485`)**, NOT `Services/Ui/`.
- **Conflict #3 Ruling (`WebAuthPolicy`):** Enforce `MinExternalPasswordLength = 12` when `settings.AllowExternal` is true (`CanListenExternally` & `Evaluate`), keep `MinPasswordLength = 6` for localhost, allow remote `?token=` ONLY for `AuthScope.WebSocket` through brute-force IP lockout + 12-char check, and update `tests/Shiro.Tests/WebAuthPolicyTests.cs:17-18,23-24,87-88`.
- **Conflict #4 Ruling (`DiscordRpcService`):** Reset `DiscordRpcService.DefaultClientId = string.Empty` (require explicit user configuration) and update `tests/Shiro.Tests/DiscordRpcServiceBehaviorTests.cs:14-17`.
- **Policy 2 Ruling (`RelayCommand`):** Marshal `RaiseCanExecuteChanged` via `AppDispatcher.Post` and attach optional unhandled-exception Toast/log handler to `AsyncRelayCommand` without double-toasting commands that catch locally.

**Tech Stack:** .NET 10 (`win-x64`), C# 14, ASP.NET Core Kestrel / WebSockets, Named Pipes, Avalonia 11, Lua (`RAMAccount.lua`), xUnit, NSubstitute.

**Spec:** System Audit Report (`C:/Users/lenovo/.gemini/antigravity/brain/d3d9db7e-c20c-4d8a-b056-391721b18014/system-audit-report.md`) — Sections 2.2 (#3–#5, #8, #10, #12), 3.5 (#22, #26–#27), 3.6 (#30–#31), 7.1.

---

## Global Constraints

- `TreatWarningsAsErrors=true`, `<Nullable>enable</Nullable>`, strict ASCII C# comments & logs.
- Budgets (`ArchitectureTests.Budgets.cs`): per-file `<= 485`; `MainViewModel <= 3050` (`3027`), `ServerListViewModel <= 1640` (`1625`), `SettingsViewModel.cs <= 485` (`476`), `FastLoginDialog <= 1150` (`1148`).
- Coverage floors (`tests/coverage-floors.json`): `RobloxSettingsApiService.cs >= 87%`, `AccountApiDispatcher.Endpoints.cs >= 77%`, `RobloxServerQueryService.cs >= 81%`, `RobloxModerationService.cs >= 67%`, plus `WebAuthPolicy.cs` / `KestrelWebServer.cs` in 92% Core gate.

---

### Task 1: Harden `WebAuthPolicy` (Conflict #3), `KestrelWebServer`, `AccountApiDispatcher`, OpenAPI & `RAMAccount.lua`

**Files:**
- Done in WT: `src/Shiro/Services/Network/AccountApiDispatcher.Endpoints.cs:56-57` (case-insensitive `Group`) & `:344,371` (`QueueDeferredSaveSettings()` — `P0AuditRemediationTests.cs:128-192`).
- Modify: `src/Shiro/Services/Auth/WebAuthPolicy.cs:38-39,142-146,275-327` (`329/485`)
- Modify: `src/Shiro/Services/Network/KestrelWebServer.cs:59,67-76,165,215,316-318,431-451`
- Modify: `src/Shiro/Services/Network/AccountApiDispatcher.cs:55,146-147` & `.Endpoints.cs:240-285,366-432` (`>= 77%`)
- Modify: `src/Shiro/Services/Network/RobloxSettingsApiService.cs:320` (`SetAvatarAsync` -> `Task<bool>`, `>= 87%`)
- Modify: `src/Shiro/Services/Network/ServerBrowserStateService.cs` (track `CachedServersPlaceId` alongside `CachedServers`)
- Modify: `src/Shiro/Services/Network/ApiDocumentationService.cs:15-272`
- Modify: `src/Shiro/AccountManager.Lifecycle.cs:219` (await clean `AltManagerWS.DisposeAsync()` shutdown)
- Modify: `RAMAccount.lua:123,182-185`
- Update Conflict #3: `tests/Shiro.Tests/WebAuthPolicyTests.cs:17-18,23-24,83-89`
- Add tests: `tests/Shiro.Tests/KestrelWebServerTests.cs`, `WebSocketGatewayTests.cs`, `P1GatewayRegressionTests.cs`, `AccountApiDispatcherEndpointsBehaviorTests.cs`

**Interfaces:**
- **Added const (`WebAuthPolicy.cs`):** `public const int MinExternalPasswordLength = 12;` (`CanListenExternally` returns `settings.AllowExternal && IsStrongExternalPassword(settings.ConfiguredPassword)`; `Evaluate` rejects external requests when password `< 12` chars; remote `?token=` allowed ONLY when `request.Scope == AuthScope.WebSocket`).
- **Modified (`RobloxSettingsApiService.cs:320`):** `public Task<bool> SetAvatarAsync(...)` returning server accept status so `/SetAvatar` reports accurate JSON status.
- **Added (`ServerBrowserStateService.cs`):** `public long CachedServersPlaceId { get; set; }` validated before `/SetRecommendedServer` consumes `CachedServers`.

- [x] Case-insensitive `Group` query & `QueueDeferredSaveSettings()` in `AccountApiDispatcher.Endpoints.cs` — *Verified Done in WT*
- [ ] **Step 1: Update Conflict #3 in `WebAuthPolicyTests.cs:17-89` & write failing gateway tests (TDD Red)**
  - In `WebAuthPolicyTests.cs`: update `"secret1"` (`line 24`) to `"secret123456"` and `"123456"` (`lines 87-88`) to `"123456789012"`; add explicit assertions that 6–11 char external passwords return `CanListenExternally == false` / `InvalidPassword`, AND `AuthScope.WebSocket` with remote `?token="secret123456"` returns `Allow` while `AuthScope.Api` with remote `?token=` returns `InvalidPassword`.
  ```csharp
  [Theory]
  [InlineData("12345678901", false)]
  [InlineData("123456789012", true)]
  public void CanListenExternally_Enforces12CharMinimum(string pass, bool expected)
      => Assert.Equal(expected, WebAuthPolicy.CanListenExternally(new AuthSettings(true, false, pass)));
  ```
  Plus failing tests for `KestrelWebServer` fail-closed default `_authValidator`, `/v2/` unknown payload envelope serialization, HTTP `/events` 400 JSON parity, `SetProxy` rejecting remote `?Proxy=`, `LaunchAccount`/`FollowUser` entering cookie guard scope, `SetRecommendedServer` checking `CachedServersPlaceId`, `ApiDocumentationService` matching `AccountApiDispatcher` routes & excluding fake endpoints, and `RAMAccount.lua` passing `self.Username` to `ImportCookie`.
- [ ] **Step 2: Run targeted gateway tests expecting Red**
  `dotnet test Shiro.sln -c Release --nologo --filter "FullyQualifiedName~WebAuthPolicyTests|FullyQualifiedName~KestrelWebServerTests|FullyQualifiedName~WebSocketGatewayTests|FullyQualifiedName~P1GatewayRegressionTests|FullyQualifiedName~AccountApiDispatcher"`
- [ ] **Step 3: Implement gateway, `WebAuthPolicy`, Dispatcher, OpenAPI & `RAMAccount.lua` fixes**
  - `WebAuthPolicy.cs` & `KestrelWebServer.cs`: Enforce `MinExternalPasswordLength = 12`; fail closed (`_authValidator ??= _ => false` regardless of `_allowExternal`); close WebSocket with lock-timeout hygiene (`401`/`429` tracking via `RecordAuthResult` inside `HandleWebSocketEndpointAsync`); handle unknown broadcast payloads cleanly; align `/events` & `/v2/*` status codes (`400`/`401`/`429`).
  - `AccountApiDispatcher*.cs`: Support `/v2/` + trailing slash + case-insensitive routes uniformly; block remote `?Proxy=` exposure on `SetProxy`; route `LaunchAccount`/`FollowUser` through `LaunchCoordinator` / `BeginCookieGuardScope`; verify `CachedServersPlaceId` in `SetRecommendedServer`; await `SetAvatarAsync` boolean.
  - `ApiDocumentationService.cs`: Build OpenAPI 3.0 paths from one static route descriptor array covering actual `/v2/*` and legacy endpoints.
  - `AccountManager.Lifecycle.cs:219`: Await bounded `AltManagerWS.DisposeAsync()` cleanup on shutdown.
  - `RAMAccount.lua:123,182-185`: Pass `self.Username` to `POST('ImportCookie', self.Username, ...)`; check `>= 12` for external WS password or document local/external bounds accurately.
- [ ] **Step 4: Run targeted tests + branch floors (`AccountApiDispatcher.Endpoints >= 77%`, `RobloxSettingsApiService >= 87%`) + `ArchitectureTests`**
  `dotnet test Shiro.sln -c Release --nologo --filter "FullyQualifiedName~WebAuthPolicy|FullyQualifiedName~KestrelWebServer|FullyQualifiedName~WebSocketGateway|FullyQualifiedName~P1GatewayRegression|FullyQualifiedName~AccountApiDispatcher|FullyQualifiedName~ArchitectureTests"`
- [ ] **Step 5: Stage & commit Task 1**
  `git add src/Shiro/Services/Auth/WebAuthPolicy.cs src/Shiro/Services/Network/KestrelWebServer.cs src/Shiro/Services/Network/AccountApiDispatcher*.cs src/Shiro/Services/Network/RobloxSettingsApiService.cs src/Shiro/Services/Network/ServerBrowserStateService.cs src/Shiro/Services/Network/ApiDocumentationService.cs src/Shiro/AccountManager.Lifecycle.cs RAMAccount.lua tests/Shiro.Tests/WebAuthPolicyTests.cs tests/Shiro.Tests/KestrelWebServerTests.cs tests/Shiro.Tests/WebSocketGatewayTests.cs tests/Shiro.Tests/P1GatewayRegressionTests.cs tests/Shiro.Tests/AccountApiDispatcherEndpointsBehaviorTests.cs`
  `git commit -m "fix(gateway): enforce 12-char external passwords, fail-closed Kestrel defaults, and RAMAccount parity"`

---

### Task 2: Complete `DiscordRpcService` (Conflict #4), `RobloxServerQueryService` CSRF/SSRF & `RobloxModerationService` Timeouts

**Files:**
- Done in WT: `DiscordRpcService` `_workerGeneration` race guard (`DiscordRpcService.cs:63,92-202`, `P1AuditRemediationTests.cs:178-200`).
- Modify: `src/Shiro/Services/Network/DiscordRpcService.cs:50,241,372-378` & `.Pipes.cs:105-145`
- Modify: `src/Shiro/Services/Network/RobloxServerQueryService.cs:88-93,204-295` (`>= 81%`)
- Modify: `src/Shiro/Services/Network/RobloxModerationService.cs:41` (`>= 67%`)
- Update Conflict #4: `tests/Shiro.Tests/DiscordRpcServiceBehaviorTests.cs:14-17`
- Add tests: `tests/Shiro.Tests/Network/RobloxServerQueryServiceBehaviorTests.cs`, `P1AuditRemediationTests.cs`, `P0AuthNetworkAndModerationTests.cs`

**Interfaces:**
- **Modified const (`DiscordRpcService.cs:50`):** `public const string DefaultClientId = "";` (`string.Empty` — never ships a hardcoded placeholder ID).
- **New internal guard (`RobloxServerQueryService.cs`):** `internal static bool TryValidatePublicTargetIpAndGeoUrl(string ip, string url, out IPAddress? parsedIp)` rejecting loopback, RFC1918 private, link-local (`169.254/16`), CGNAT, and non-`https://` templates.

- [x] `DiscordRpcService` `_workerGeneration` stale-cleanup guard — *Verified Done in WT*
- [ ] **Step 1: Update Conflict #4 in `DiscordRpcServiceBehaviorTests.cs:14-17` & write failing tests (TDD Red)**
  ```csharp
  [Fact]
  public void DefaultClientId_IsEmptyByDefault()
      => Assert.Equal(string.Empty, DiscordRpcService.DefaultClientId);

  [Theory]
  [InlineData("127.0.0.1", "https://ipwho.is/127.0.0.1", false)]
  [InlineData("10.0.0.1", "https://ipwho.is/10.0.0.1", false)]
  [InlineData("1.1.1.1", "http://ipwho.is/1.1.1.1", false)]
  [InlineData("1.1.1.1", "https://ipwho.is/1.1.1.1", true)]
  public void ServerQuery_TryValidatePublicTargetIpAndGeoUrl_BlocksSsrf(string ip, string url, bool expected)
      => Assert.Equal(expected, RobloxServerQueryService.TryValidatePublicTargetIpAndGeoUrl(ip, url, out _));
  ```
  Plus failing tests asserting: Discord IPC handshake aborts when `Opcode == 2` (CLOSE frame) or read exceeds `3000ms` timeout, `UpdatePresence` uses active cached game title when `placeId > 0`, `RobloxServerQueryService` retries `v1/join-game-instance` via `RobloxCsrfRetryHelper` on 403 CSRF, and `RobloxModerationService` gives each fallback probe its own timeout CTS instead of one shared 2.5s budget.
- [ ] **Step 2: Run targeted RPC/ServerQuery/Moderation tests expecting Red**
  `dotnet test Shiro.sln -c Release --nologo --filter "FullyQualifiedName~DiscordRpcServiceBehaviorTests|FullyQualifiedName~RobloxServerQueryServiceBehaviorTests|FullyQualifiedName~P0AuthNetworkAndModerationTests|FullyQualifiedName~P1AuditRemediationTests"`
- [ ] **Step 3: Implement RPC, ServerQuery & Moderation fixes**
  - `DiscordRpcService.cs` & `.Pipes.cs`: Empty `DefaultClientId`; wrap handshake `ReadFrameAsync` in 3s linked timeout CTS + reject Opcode `2` CLOSE frames; look up cached/current place title in `UpdateCurrentPresence` (`line 377`).
  - `RobloxServerQueryService.cs`: Execute `v1/join-game-instance` through `RobloxCsrfRetryHelper`; validate IP & GeoIP URL via `TryValidatePublicTargetIpAndGeoUrl` before ping/GeoIP fetches.
  - `RobloxModerationService.cs:41`: Give each probe its own per-probe `CancellationTokenSource` linked with caller `ct`.
- [ ] **Step 4: Run targeted tests + branch floors (`RobloxServerQueryService >= 81%`, `RobloxModerationService >= 67%`) + `ArchitectureTests`**
  `dotnet test Shiro.sln -c Release --nologo --filter "FullyQualifiedName~DiscordRpc|FullyQualifiedName~RobloxServerQuery|FullyQualifiedName~Moderation|FullyQualifiedName~ArchitectureTests"`
- [ ] **Step 5: Stage & commit Task 2**
  `git add src/Shiro/Services/Network/DiscordRpcService*.cs src/Shiro/Services/Network/RobloxServerQueryService.cs src/Shiro/Services/Network/RobloxModerationService.cs tests/Shiro.Tests/DiscordRpcServiceBehaviorTests.cs tests/Shiro.Tests/Network/RobloxServerQueryServiceBehaviorTests.cs tests/Shiro.Tests/P1AuditRemediationTests.cs tests/Shiro.Tests/P0AuthNetworkAndModerationTests.cs`
  `git commit -m "fix(network): remove placeholder Discord ClientId, harden ServerQuery SSRF/CSRF, and split moderation probe timeouts"`

---

### Task 3: Fix Live Settings Wiring (`MultiRoblox`, `EnableAntiAfk`, `BackgroundThrottling`), `AccountWorkflowService` & ViewModel Races

**Files:**
- Done in WT: `SettingsViewModel.cs:365-378` + `.Advanced.cs:66-78` (`SaveAdvanced()` before 6-arg `ComputeWebServerAction`, `P0AuditRemediationTests.cs:78-125`), `_lastDefaultReadyStatus`, & `MainViewModel.Features.cs:38` PotatoMode deferred save.
- Modify: `src/Shiro/Services/Ui/ISettingsSideEffects.cs:19-39,79` (`SetAntiAfk` persists `"EnableAntiAfk"`; add `UpdateMultiRoblox()` & `ConfigureBackgroundThrottling(bool enabled, int targetFps)`)
- Modify: `src/Shiro/ViewModels/Dialogs/SettingsViewModel.cs:324-325` (`476/485` — add minimal 1–2 line call only) & `.Advanced.cs:80-162` (`182/485` — place `EnableBackgroundThrottling` & `BackgroundTargetFps` props/load/save here!) + `src/Shiro/AccountManager.Lifecycle.cs`
- Modify: `src/Shiro/ViewModels/ServerListViewModel.cs:365-374`, `.PlayerSearch.cs:176-177`, `.Watcher.cs:18,104` (align `NoConnectionTimeout` default `20`; marshal `PlayerSearchStatus` via `AppDispatcher.Post`; reset `_lastLoadedPlaceId = 0` on catch — keep total `<= 1,640`!)
- Modify: `src/Shiro/ViewModels/AccountListFilterController.cs:104,135` (case-insensitive group compare + unmasked sort key), `AccountViewModel.Avatar.cs:16,134,239-248` (`_avatarCacheGeneration` check + safe disposal on clear), `CookieImportViewModel.cs:72-81`, `AvaloniaUI/Views/AccountCardView.axaml.cs:60-64`
- Modify: `src/Shiro/ViewModels/MainViewModel.cs:323,351,401-405` (`3027/3050` — 0+ net line budget!), `.Filtering.cs:26-41`, `.UiUpgrades.cs:109-158`, & `src/Shiro/Services/Storage/AccountWorkflowService.cs:146-176,292,389-457` (`RebuildLookupIndex` sync + fail-closed `ReLoginWithCookieAsync`)
- Test: `tests/Shiro.Tests/P1WindowsAndUiRegressionTests.cs`, `ServerListViewModelBehaviorTests.cs`, `MainViewModelStatusAndCountsTests.cs`, `AccountWorkflowServiceBehaviorTests.cs`

**Interfaces:**
- **Added to `ISettingsSideEffects`:** `void UpdateMultiRoblox();` & `void ConfigureBackgroundThrottling(bool enabled, int targetFps);`.
- **Modified (`AccountWorkflowService.ReLoginWithCookieAsync`):** Restores `acc.Model.SecurityToken = previousToken` when `ok == false` EVEN when `!wasValid` (never writes an unverified cookie to disk), and calls `RebuildLookupIndex` cleanly when username/UserId updates.

- [x] `SettingsViewModel.SaveAdvanced()` order + 6-arg `ComputeWebServerAction`, `_lastDefaultReadyStatus`, & PotatoMode deferred save — *Verified Done in WT*
- [ ] **Step 1: Write failing ViewModel, settings & workflow tests (TDD Red)**
  ```csharp
  [Fact]
  public void ServerListAndFilter_AlignDefaultTimeout20_AndCaseInsensitiveGroup()
  {
      using var serverVm = new ServerListViewModel();
      Assert.Equal(20m, serverVm.ConnectionTimeoutSec);
      var filtered = AccountListFilterController.Apply(accounts, "farm", string.Empty, AccountFilterTab.All, AccountSortMode.Default);
      Assert.Contains(filtered, a => string.Equals(a.Group, "Farm", StringComparison.OrdinalIgnoreCase));
  }
  ```
  Plus failing tests verifying `_lastLoadedPlaceId` resets to `0` on fetch exception and `AccountWorkflowService.ReLoginWithCookieAsync` preserves `previousToken` (never persists an unverified cookie) even if `wasValid == false`.
- [ ] **Step 2: Run targeted tests expecting Red**
  `dotnet test Shiro.sln -c Release --nologo --filter "FullyQualifiedName~P1WindowsAndUiRegressionTests|FullyQualifiedName~ServerListViewModelBehaviorTests|FullyQualifiedName~MainViewModelStatusAndCountsTests|FullyQualifiedName~AccountWorkflowServiceBehaviorTests"`
- [ ] **Step 3: Implement live settings wiring & ViewModel fixes respecting tight line caps**
  - Wire `UpdateMultiRoblox()`, `"EnableAntiAfk"` INI persistence, and `EnableBackgroundThrottling` + `BackgroundTargetFps` in `SettingsViewModel.Advanced.cs`, `ISettingsSideEffects.cs`, and `AccountManager.Lifecycle.cs`.
  - Fix `ServerListViewModel.Watcher.cs:18,104` default `20`, dispatch `PlayerSearchStatus` onto UI thread, reset `_lastLoadedPlaceId = 0` on catch; fix case-insensitive group compare, unmasked sort key, `_avatarCacheGeneration` guard, empty `CookieImportViewModel` reset, `AccountCardView` double-tap debounce, and `AccountWorkflowService` token rollback + index rebuild.
- [ ] **Step 4: Run targeted tests + `ArchitectureTests` (check `MainViewModel <= 3050`, `ServerListViewModel <= 1640`, `SettingsViewModel.cs <= 485`)**
  `dotnet test Shiro.sln -c Release --nologo --filter "FullyQualifiedName~ViewModel|FullyQualifiedName~AccountWorkflowService|FullyQualifiedName~P1WindowsAndUiRegressionTests|FullyQualifiedName~ArchitectureTests"`
- [ ] **Step 5: Stage & commit Task 3**
  `git add src/Shiro/Services/Ui/ISettingsSideEffects.cs src/Shiro/Services/Storage/AccountWorkflowService.cs src/Shiro/AccountManager.Lifecycle.cs src/Shiro/ViewModels/ tests/Shiro.Tests/P1WindowsAndUiRegressionTests.cs tests/Shiro.Tests/ServerListViewModelBehaviorTests.cs tests/Shiro.Tests/MainViewModelStatusAndCountsTests.cs tests/Shiro.Tests/AccountWorkflowServiceBehaviorTests.cs`
  `git commit -m "fix(ui): wire live MultiRoblox/AntiAfk/Throttling settings and close ViewModel/workflow state races"`

---

### Task 4: Extend Streamer Mode to All 9 Dialogs, Harden `RelayCommand` Error Surface (Policy 2), Fix Contrast & Offload Media/Theme I/O

**Files:**
- Done in WT: `StreamerModeService.Shutdown()` + duplicate window guard (`StreamerModeService.cs:141-152,336-345`) & `ToastService.AutoDismissCts` timer coalescing (`ToastService.cs:54,243-252`).
- Modify all 9 dialogs (`AboutDialog`, `AccountFieldsDialog`, `AccountUtilsDialog`, `BulkImportDialog`, `FastLoginDialog` **`1,148/1,150` — call one shared registration helper in `WindowBackdropService` or extract code out of `FastLoginDialog` first!**), `InputDialog`, `MessageBoxDialog`, `MissingAssetsDialog`, `ProxyManagerDialog` + `CookieImportViewModel` / `FastLoginDialog.axaml:144`.
- Modify: `AccountViewModel.cs:119,121-131,327-345`, `DashboardView.axaml:350,352`, `AccountFieldsDialog.axaml.cs:49`, `AccountUtilsViewModel.cs:29-30,59`, `MissingAssetsViewModel.cs:47,291`, `FastLoginQueue.cs:49-52`, `SettingsView.axaml:87-88` (bind `"HideUsernames"` & mask PII `Alias`/`UserId`/`JobId`/`Robux` uniformly under Streamer Mode).
- Modify (Policy 2): `src/Shiro/ViewModels/RelayCommand.cs:75,102,176` + `src/Shiro/AvaloniaUI/App.axaml.cs` (dispatch `RaiseCanExecuteChanged` via `AppDispatcher.Post`; wire `AsyncRelayCommand.DefaultUnhandledErrorHandler` to structured logging + safe non-duplicating Toast notification).
- Modify XAML contrast: `SettingsView.axaml:300`, `Styles/ViewStyles.axaml:165-252`, `AccountCardView.axaml:60,64-65`, `ServerListView.axaml:30-31`, `FastLoginDialog.axaml:210-213`, `BulkImportDialog.axaml:82-85`, `ProxyManagerDialog.axaml:41-44,122-125`.
- Modify: `src/Shiro/Services/Ui/AccentThemeService.cs:159-187`, `src/Shiro/Services/Ui/LocalizationService.cs:128-133` (swap UI dictionaries synchronously before firing `ThemeChanged`/`LanguageChanged`), **`src/Shiro/Services/Network/GameThumbnailService.cs:71-94`** (memory-only check in `TryGetCachedBitmap`; off-UI disk decode in `GetThumbnailBitmapAsync`) + `MainViewModel.UiUpgrades.cs:147`, `AvaloniaUI/Controls/WebView2Control.cs:77-213` (delete app-owned `Shiro_<guid>` profile dir on teardown/reset).
- Test: `tests/Shiro.Tests/MvvmFoundationTests.cs`, `tests/Shiro.Tests/Ui/DialogSmokeTests.Extended.cs`, `tests/Shiro.Tests/P1WindowsAndUiRegressionTests.cs`, `tests/Shiro.Tests/GameThumbnailServiceTests.cs`

**Interfaces (Policy 2):**
- **Added on `AsyncRelayCommand` (`RelayCommand.cs`):** `public static Action<Exception>? DefaultUnhandledErrorHandler { get; set; }` invoked (plus structured log) ONLY on uncaught asynchronous task failures when per-instance handler is omitted, keeping local `try/catch` commands free of duplicate toasts.

- [x] `StreamerModeService.Shutdown()` + duplicate window guard & `ToastService.AutoDismissCts` coalescing — *Verified Done in WT*
- [ ] **Step 1: Write failing tests for all 9 dialogs, Streamer Mode masking, Policy 2 `RelayCommand`, sync theme swap, memory-only `TryGetCachedBitmap`, and `WebView2Control` profile cleanup (TDD Red)**
  ```csharp
  [Fact]
  public void GameThumbnailService_TryGetCachedBitmap_DoesNotTouchDiskOnMiss()
  {
      const long placeId = 987654321L;
      string? diskPath = GameThumbnailService.GetDiskCachePath(placeId);
      Assert.NotNull(diskPath);
      Directory.CreateDirectory(Path.GetDirectoryName(diskPath)!);
      File.WriteAllBytes(diskPath!, new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A });
      Assert.False(GameThumbnailService.TryGetCachedBitmap(placeId, out _));
  }

  [Fact]
  public async Task AsyncRelayCommand_UnhandledException_InvokesDefaultUnhandledErrorHandlerOnce()
  {
      Exception? captured = null;
      var prev = AsyncRelayCommand.DefaultUnhandledErrorHandler;
      AsyncRelayCommand.DefaultUnhandledErrorHandler = ex => captured = ex;
      try
      {
          var cmd = new AsyncRelayCommand(() => Task.FromException(new InvalidOperationException("boom")));
          cmd.Execute(null);
          await Task.Delay(50);
          Assert.IsType<InvalidOperationException>(captured);
      }
      finally { AsyncRelayCommand.DefaultUnhandledErrorHandler = prev; }
  }
  ```
- [ ] **Step 2: Run targeted tests expecting Red**
  `dotnet test Shiro.sln -c Release --nologo --filter "FullyQualifiedName~MvvmFoundationTests|FullyQualifiedName~DialogSmokeTests|FullyQualifiedName~P1WindowsAndUiRegressionTests|FullyQualifiedName~GameThumbnailServiceTests"`
- [ ] **Step 3: Implement Streamer Mode, Policy 2 `RelayCommand`, contrast, theme ordering, async thumbnail decode & WebView cleanup**
  - Register all 9 dialogs cleanly via `WindowBackdropService.ApplyDarkAndBackdrop` + `StreamerModeService.RegisterWindow` without adding net lines to `FastLoginDialog` (`1,148/1,150`).
  - Implement Policy 2 in `RelayCommand.cs` (`AppDispatcher.Post` in `RaiseCanExecuteChanged` + `DefaultUnhandledErrorHandler`) wired in `App.axaml.cs`.
  - Swap `AccentThemeService`/`LocalizationService` merged resource dictionaries on UI thread BEFORE invoking change events.
  - Restrict `GameThumbnailService.TryGetCachedBitmap` to `_bitmapCache` memory hits; offload disk PNG decode to background `Task.Run` inside `GetThumbnailBitmapAsync`.
  - Delete only `Shiro_<guid>` WebView2 user-data subdirectories created by `WebView2Control` upon disposal/reset.
- [ ] **Step 4: Run Release `-warnaserror` + full test suite + `ArchitectureTests` + `Check-Coverage.ps1`**
  ```powershell
  dotnet build Shiro.sln --configuration Release --nologo -warnaserror
  dotnet test Shiro.sln --configuration Release --no-build --nologo
  powershell.exe -NoProfile -ExecutionPolicy Bypass -File .\scripts\Check-Coverage.ps1
  ```
  Expected: `0 Warning(s)`, `0 Error(s)`, `Passed >= 2189, Failed: 0`, Core `>= 92.0%`.
- [ ] **Step 5: Stage & commit Task 4**
  `git add src/Shiro/AvaloniaUI/ src/Shiro/ViewModels/ src/Shiro/Services/Ui/ src/Shiro/Services/Network/GameThumbnailService.cs tests/Shiro.Tests/`
  `git commit -m "fix(ui): extend streamer mode across dialogs, marshal RelayCommand events, and offload theme/thumbnail I/O"`

---

## Execution Handoff

Plan complete. Recommend executing Wave P1 sequentially after committing the WT baseline:
1. **Subagent-driven execution** (`superpowers:subagent-driven-development`) *(recommended)*
2. **Inline execution** (`superpowers:executing-plans`)
