# Phase 2 — P1 System Stability, Shutdown Lifecycle, Launch Hardening & Privacy Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use `superpowers:subagent-driven-development` (recommended) or `superpowers:executing-plans` to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Eliminate all Phase 2 P1 stability, shutdown, concurrency, launch, privacy, proxy-isolation, and test-isolation defects in Shiro Account Manager v5.0.0 so the application shuts down deterministically without races or leaked background handles, avoids UI cross-thread crashes and accidental context-menu actions, hardens Roblox launch/reconnect/proxy routing, closes Streamer Mode leaks, and achieves 100% deterministic test passes even while a live `Shiro.exe` process is running.

**Architecture:** Introduce a dedicated 4-phase `ShutdownCoordinator` (`Services/Composition/ShutdownCoordinator.cs`) that serializes and orders shutdown across all background services without permanently bricking test-shared singletons. Keep every fix within existing domain/service boundaries, preserving all existing `ArchitectureTests` ratchets, partial-class line budgets, and localization symmetry (`en.json` / `vi.json`), while adding targeted pure helpers for deterministic unit testing of Win32, WebView2, networking, and UI logic.

**Tech Stack:** .NET 10 (`net10.0-windows10.0.19041.0`), C# 14, Avalonia UI 11.3.22, Microsoft.Web.WebView2 1.0.4314.35, Win32 `[LibraryImport]` P/Invoke, xUnit 2.9.3, Avalonia.Headless.XUnit, NSubstitute 6.2.0.

**Spec:** Phase 2 P1 stability roadmap from the 2026-11-21 comprehensive codebase audit (Sections 3.5, 3.7, 3.9, 3.10, 3.11, 4.5, and 7.2), reconciled against existing regression contracts (`P0Batch2RegressionTests` for high-memory threshold semantics, `Task4UiStabilityTests` / `Plan3FinalFixRegressionTests` for `HideUsernames` Settings toggle contract, `RobloxBackgroundThrottlerTests` for opt-in background throttling, and all `ArchitectureTests*.cs` invariants).

## Global Constraints

- **Architecture Guardrails (verified by `ArchitectureTests*.cs`):**
  - Every `.cs` file in `src/Shiro/` MUST stay `<= 485` lines (`NoSourceCodeFiles_ShouldExceed500Lines`).
  - Every `.cs` file in `tests/Shiro.Tests/` MUST stay `<= 550` lines (`NoTestFiles_ShouldExceed550Lines`).
  - Every `partial class` total across files MUST stay `<= 1150` lines (`NoClass_ShouldExceedLineBudget_AcrossPartials`).
  - Zero compiler warnings (`-warnaserror`), zero empty `catch` blocks, zero untyped `catch` blocks (except `AppLogger.cs`), zero Vietnamese diacritics in C# comments/strings outside `LocalizationService*`, zero `.GetAwaiter().GetResult()`, zero `[DllImport]`.
  - `.Wait(TimeSpan...)` is ONLY permitted in `AccountManager.Lifecycle.cs` and `Services/Storage/AppLogger.cs` (`WaitWithTimeSpan_ShouldNotExceedAllowList`) — in `AppBootstrap.cs` / `ShutdownCoordinator.cs`, use `.Wait(int millisecondsTimeout)` or `async`/`await`.
  - New files under `src/Shiro/Services/` MUST live in a domain subfolder (`Composition`, `Auth`, `Launch`, `Storage`, `Windows`, `Network`, `Ui`), never at `Services/` root (`Services_Directory_ShouldRemainModularized`), and non-nested classes in `Shiro.Services` MUST end with an approved suffix (e.g., `Coordinator`, `Service`, `Helper`) (`Services_ShouldFollowNamingConvention`).
  - Zero `Program.Logger` and zero `AppSettings.` references in `Services/*`, `ViewModels/*`, `Domain/*`, and `AvaloniaUI/Views/*` — always use `AppServices.Logger` and `AppServices.GeneralSettings` (`AmbientStaticAccess_ShouldNotExceedPerFolderBaseline`).
  - `ProxyBindingService.ClearClients` MUST remain `internal static` (not `public`) (`LegacyDpiNexusAndEconomyRemnants_ShouldStayRemoved`).
  - `Batch.cs` MUST NOT contain `Task.Run` (`Batch_ShouldNotUseTaskRun`).
  - `en.json` and `vi.json` MUST stay 100% key-symmetric.
  - Every `[Fact]`/`[Theory]` MUST assert concrete values (not only `Assert.NotNull`/`Assert.NotEmpty`/`Assert.True(x >= 0)`).
- **Build & Test Command Notes (.NET 10 SDK):**
  - `--no-incremental` is valid ONLY on `dotnet build`, NEVER on `dotnet test`.
  - Standard filtered TDD run: `dotnet build "Shiro.sln" --no-incremental -warnaserror` followed by `dotnet test "tests/Shiro.Tests/Shiro.Tests.csproj" --no-build --filter "<Filter>"`.
  - Do **NOT** terminate any live user `Shiro.exe` process during verification.
- **Tight File Line Budgets to Watch:**
  - `AccountManager*.cs` partial total: **1128 / 1150 lines** -> Task 1 shrinks `AccountManager.Lifecycle.cs` by moving ordered shutdown stages to `ShutdownCoordinator.cs`.
  - `FastLoginDialog*.cs` partial total: **1138 / 1150 lines** -> Task 8 adds `<= 3` lines to `FastLoginDialog.axaml.cs` and puts all proxy parsing/environment logic in `WebView2Control.cs` (362 lines).
  - `ToolsStatusViewModel.cs`: **481 / 485 lines** -> Task 7 edits are **0 net lines**.
  - `RobloxWatcher.cs`: **480 / 485 lines** -> Task 5 edits are **+1 net line** (`<= 481`).
  - `RobloxAudioService.cs` (**480 / 485**) & `RobloxAudioService.Wasapi.cs` (**471 / 485**) -> Task 1 adds `Shutdown()` concisely in `Wasapi.cs` (`<= 483`) and keeps `RobloxAudioService.cs` unchanged (`480`).
  - `WebhookNotificationService.cs`: **473 / 485 lines** -> Task 1 converts to `partial` and adds `Services/Network/WebhookNotificationService.Lifecycle.cs` (total ~525 / 1150).
  - `DiscordRpcService.cs`: **474 / 485 lines** -> Task 7 edits are `<= +4` lines (`<= 478`).
  - `GameInfoService.cs`: **474 / 485 lines** -> Task 3 edits are `<= +6` lines (`<= 480`).
  - `P0StorageAndRedactionRegressionTests.cs`: **538 / 550 lines** -> Task 3 edit is **0 net lines**.

## Review Focus

1. **Serialized idempotent shutdown without bricking test-shared singletons:** Concurrent `AccountManager.ShutdownBackend()` calls cannot race `ClearCachedVaultKey()` against `SaveAccounts()`; `AppBootstrap.DisposeSynchronously()` runs async shutdown callbacks with bounded wait; and shutting down static/singleton services (`RobloxAudioService`, `AutoReconnectService`, `AccountPersistenceService`, `AccountRuntimeStateStore`, `WebhookNotificationService`, `SharedHttpClient`, `ProxyBindingService`) releases timers/threads/handles cleanly while remaining transparently re-usable if subsequent tests invoke them — pinned by Task 1 (`tests/Shiro.Tests/Phase2ShutdownCoordinatorTests.cs`).
2. **Off-UI-thread events & unattached context menu actions:** Background `StreamerModeService.StateChanged` notifications marshal via `AppDispatcher.Post` and guard `_isClosed` in `AccountFieldsDialog` (and `FastLoginDialog`); unattached context menu items return `null` instead of silently targeting `FilteredAccounts.FirstOrDefault()` — pinned by Task 2 (`tests/Shiro.Tests/Ui/Phase2UiSafetyTests.cs`).
3. **NTFS mtime preservation, unified `RecentGames` locking, off-lock dialogs & immutable vault key copies:** `CleanupStaleRuntimeArtifacts` exempts `EmbeddedBrowser` and checks recursive descendant write times for all other browser folders; `GameInfoService` and `LaunchBarViewModel` share `_recentGamesLock` for reads and disk writes; `AccountPersistenceService` never invokes `AppServices.Dialogs.ShowError` inside `lock (saveLock)`; and zeroing a `byte[]` from `StoragePaths.GetOrCreateVaultKey()` cannot corrupt the internal cache — pinned by Task 3 (`tests/Shiro.Tests/Phase2StorageAndLockSafetyTests.cs`).
4. **Launch cookie-sync, de-elevation environment, VC++ consent, PPPoE VPN, Watcher ceiling, throttler harmony & reconnect guards:** `rbxid=` and `rbxuid=` both extract `trackerUserId`; de-elevation creates/destroys a token environment block with `CREATE_UNICODE_ENVIRONMENT`; VC++ install prompts with localized confirmation instead of running silently in constructor; VN residential PPPoE is not flagged as VPN; Watcher default RAM limit is 2048 MB; `MemoryOptimizer` does not override `RobloxBackgroundThrottlerService` on solo clients; and `AutoReconnectService` skips invalid/banned accounts while propagating `CancellationToken` to `JoinServer` and `WaitForExitAsync` — pinned by Tasks 4, 5, 6 (`tests/Shiro.Tests/Phase2LaunchAndPrerequisiteTests.cs`, `tests/Shiro.Tests/Phase2NetworkWatcherAndOptimizerTests.cs`, `tests/Shiro.Tests/Phase2AutoReconnectHardeningTests.cs`).
5. **Streamer Mode privacy, WebView2/Batch proxy compliance & host-independent mutex tests:** Streamer Mode hides Discord RPC game/counts, status/toast usernames/Robux, and displayed JobIds; WebView2 binds `--proxy-server` without CLI credentials, isolates proxy user-data folders, supplies HTTP auth via `BasicAuthenticationRequested`, and surfaces `CreateWindowEx` failures; `Batch` never attaches `.ROBLOSECURITY` to public asset thumbnails and routes authenticated place lookups through account proxy/HBA; and singleton mutex tests isolate kernel names via `HolderNativeOps` so they pass 100% alongside a live `Shiro.exe` — pinned by Tasks 7, 8, 9 (`tests/Shiro.Tests/Phase2StreamerModePrivacyTests.cs`, `tests/Shiro.Tests/Phase2WebView2AndBatchProxyTests.cs`, `tests/Shiro.Tests/P0Win32AndLaunchRegressionTests.cs`, `tests/Shiro.Tests/P1WindowsAndUiRegressionTests.cs`).

---

### Task 1: Atomic Shutdown Serialization, `ShutdownCoordinator` & Background Resource Disposal

**Files:**
- Create: `src/Shiro/Services/Composition/ShutdownCoordinator.cs`
- Create: `src/Shiro/Services/Network/WebhookNotificationService.Lifecycle.cs`
- Modify: `src/Shiro/AccountManager.cs:28-42`
- Modify: `src/Shiro/AccountManager.Lifecycle.cs:203-276`
- Modify: `src/Shiro/Services/Composition/AppBootstrap.cs:301-332`
- Modify: `src/Shiro/Services/Storage/AccountPersistenceService.cs:25-66, 352-370`
- Modify: `src/Shiro/Services/Storage/AccountPersistenceService.Recovery.cs:15-21`
- Modify: `src/Shiro/Services/Storage/AccountRuntimeStateStore.cs:174-217`
- Modify: `src/Shiro/Services/Windows/RobloxAudioService.Wasapi.cs:18-23, 68-139`
- Modify: `src/Shiro/Services/Windows/RobloxJobObjectService.cs:366-377`
- Modify: `src/Shiro/Services/Windows/Win32TrayHelper.cs:184-219`
- Modify: `src/Shiro/Services/Network/WebhookNotificationService.cs:30-55, 262-301`
- Modify: `src/Shiro/Services/Launch/LaunchCoordinator.cs:17-28, 107-125, 250-267, 317-333`
- Modify: `src/Shiro/Services/Launch/AutoReconnectService.cs:21-26, 114-124, 191-248, 374-389`
- Test: `tests/Shiro.Tests/Phase2ShutdownCoordinatorTests.cs`

**Interfaces:**
- Produces:
  - `public enum ShutdownPhase { StopMonitors = 1, StopServersAndIntegrations = 2, FlushPersistence = 3, ClearSecretsAndPools = 4 }`
  - `public interface IShutdownParticipant { ShutdownPhase Phase { get; } string Name { get; } void ExecuteShutdown(); }`
  - `public sealed class ShutdownCoordinator` (`void Register(IShutdownParticipant participant)`, `void Register(ShutdownPhase phase, string name, Action action)`, `IReadOnlyList<string> ExecuteAll()`)
  - `AccountPersistenceService.StopDeferredSave()` (`internal void`)
  - `AccountRuntimeStateStore.Shutdown()` (`public static bool`)
  - `RobloxAudioService.Shutdown()` (`public static void`)
  - `Win32TrayHelper.CancelPendingRefresh()` (`public static void`)
  - `WebhookNotificationService.Shutdown(int drainTimeoutMs = 1000)` (`public static void`)
  - `AutoReconnectService.StopForShutdown()` (`public void`) and `internal CancellationToken LifecycleToken` (consumed by Task 6)

- [ ] **Step 1: Write failing tests in `tests/Shiro.Tests/Phase2ShutdownCoordinatorTests.cs`**

Create `tests/Shiro.Tests/Phase2ShutdownCoordinatorTests.cs` (`[Collection("SerialGlobalState")]`) with tests covering:
1. `ShutdownCoordinator_ExecutesPhasesInOrder_IsolatesExceptions_AndIsIdempotent`: registers participants out of order across all 4 `ShutdownPhase` values, including one that throws `InvalidOperationException`; asserts `ExecuteAll()` executes in exact order `StopMonitors -> StopServersAndIntegrations -> FlushPersistence -> ClearSecretsAndPools`, continues past the throwing participant, and returns an empty list on a second `ExecuteAll()` call.
2. `AppBootstrap_DisposeSynchronously_ExecutesAsyncShutdownCallbacks`: initializes an `AppBootstrap` via `InitializeAsync(..., shutdownHostAsync: async () => { await Task.Yield(); callbackRan = true; })`, calls `bootstrap.DisposeSynchronously()`, and asserts `callbackRan == true`.
3. `AccountManager_ShutdownBackend_ConcurrentCalls_SerializeAndRunOnce`: sets `AccountManager.SuppressExitOnShutdown = true`, creates a temporary `AccountManager`, invokes `ShutdownBackend()` concurrently from 6 tasks via `Barrier`, asserts `IsDisposed == true` after all complete and zero exceptions occur.
4. `BackgroundServices_ShutdownAndReenterCleanly`: verifies:
   - `AccountPersistenceService.StopDeferredSave()` disposes deferred timer without throwing and blocks subsequent `QueueDeferredSave()` while `IsShutdownRequested == true`; resetting `IsShutdownRequested = false` allows lazy re-creation.
   - `AccountRuntimeStateStore.Shutdown()` flushes dirty state, disposes `_debounceTimer`, and allows subsequent `RecordLastUse` to recreate the timer.
   - `Win32TrayHelper.CancelPendingRefresh()` cancels a scheduled refresh safely without throwing.
   - `RobloxAudioService.Shutdown()` clears cached audio sessions, shuts down the WASAPI MTA worker cleanly, and a subsequent `RobloxAudioService.ProbeWasapiExecutionContext()` still succeeds with `ApartmentState.MTA`.
   - `WebhookNotificationService.Shutdown(200)` completes cleanly and a subsequent `EnqueueAlert` still functions.
   - `AutoReconnectService.StopForShutdown()` cancels active sessions and stops monitoring without throwing, and `LaunchCoordinator.Dispose()` disposes `queueGate` safely both when idle and when an enqueue is active.

- [ ] **Step 2: Run tests to verify they fail**

Run:
```powershell
dotnet build "Shiro.sln" --no-incremental -warnaserror
```
Expected: FAIL to compile because `ShutdownCoordinator`, `IShutdownParticipant`, `ShutdownPhase`, `StopDeferredSave`, `AccountRuntimeStateStore.Shutdown`, `RobloxAudioService.Shutdown`, `Win32TrayHelper.CancelPendingRefresh`, `WebhookNotificationService.Shutdown`, and `AutoReconnectService.StopForShutdown` do not exist yet.

- [ ] **Step 3: Implement `ShutdownCoordinator`, atomic `ShutdownBackend`, `AppBootstrap.DisposeSynchronously`, and service shutdown hooks**

1. Create `src/Shiro/Services/Composition/ShutdownCoordinator.cs` (`namespace Shiro.Services`):
   - `ShutdownPhase` (`StopMonitors = 1`, `StopServersAndIntegrations = 2`, `FlushPersistence = 3`, `ClearSecretsAndPools = 4`), `IShutdownParticipant`, and `ShutdownCoordinator`.
   - `ExecuteAll()` uses `Interlocked.Exchange(ref _executed, 1) != 0` guard, snapshots registered participants sorted stably by `(Phase, registrationIndex)`, runs each inside `try/catch (Exception ex)` logging via `AppServices.Logger?.Debug(...)`, and returns the ordered names executed.
2. Update `src/Shiro/Services/Composition/AppBootstrap.cs:301-332`:
   - Snapshot `asyncCallbacks = new List<Func<ValueTask>>(_asyncShutdownCallbacks)` before clearing `_asyncShutdownCallbacks`.
   - After running `syncCallbacks` in reverse order, iterate `asyncCallbacks` in reverse order and wait with bounded timeout using `Task.Run(() => cb().AsTask()).Wait(2000)` (integer ms — satisfies `WaitWithTimeSpan_ShouldNotExceedAllowList` and `SourceFiles_ShouldNotUse_SyncOverAsyncBlocking`), catching `Exception` and logging via `AppServices.Logger?.Debug`.
3. Update `src/Shiro/Services/Storage/AccountPersistenceService.cs` & `AccountPersistenceService.Recovery.cs`:
   - Replace `private readonly Timer _deferredSaveTimer;` with `private readonly System.Threading.Lock _deferredTimerLock = new(); private Timer? _deferredSaveTimer;`.
   - In `QueueDeferredSave()`: lock `_deferredTimerLock`; if `IsShutdownRequested`, return immediately; lazily create `_deferredSaveTimer` and `.Change(1500, Timeout.Infinite)`.
   - Add `internal void StopDeferredSave()` that locks `_deferredTimerLock`, sets `IsShutdownRequested = true`, changes timer to `(Timeout.Infinite, Timeout.Infinite)`, disposes it, and sets `_deferredSaveTimer = null`.
   - Guard the timer callback with `if (IsShutdownRequested) return;`.
4. Update `src/Shiro/Services/Storage/AccountRuntimeStateStore.cs`:
   - Add `public static bool Shutdown()`: calls `Flush()`, then under `lock (_sync)` disposes `_debounceTimer` and sets `_debounceTimer = null`.
5. Update `src/Shiro/Services/Windows/RobloxAudioService.Wasapi.cs` (keep `<= 485` lines):
   - Change line 78 to `using var started = new ManualResetEventSlim(false);`.
   - Change `_wasapiQueue` from `readonly` to swappable under `_wasapiWorkerInitLock`, passing the local queue instance into the worker thread closure (`var queue = _wasapiQueue;`).
   - Add `public static void Shutdown()`: calls `ConfigureSmartMute(false)`, `ClearAudioSessionCache()`, then under `lock (_wasapiWorkerInitLock)` completes adding on the active queue, captures and clears `_wasapiWorkerThread` / `_wasapiWorkerThreadId = -1`, swaps in a fresh `new BlockingCollection<Action>()`, and outside the lock joins the old worker thread with `oldThread.Join(500)` and disposes the old queue if joined.
6. Update `src/Shiro/Services/Windows/RobloxJobObjectService.cs:366-377`:
   - Capture `bool joined = threadToJoin == null || !threadToJoin.IsAlive || threadToJoin == Thread.CurrentThread || threadToJoin.Join(TimeSpan.FromSeconds(2));`.
   - Only call `Win32NativeHelper.CloseHandle(hIocp)` when `joined` is `true`; otherwise log a debug warning so the blocked `GetQueuedCompletionStatus` thread never suffers a handle UAF.
7. Update `src/Shiro/Services/Windows/Win32TrayHelper.cs`:
   - Add `public static void CancelPendingRefresh()` that locks `_refreshTrayLock`, cancels and disposes `_refreshTrayCts`, and sets `_refreshTrayCts = null`.
8. Convert `WebhookNotificationService` to `public static partial class WebhookNotificationService` and create `src/Shiro/Services/Network/WebhookNotificationService.Lifecycle.cs`:
   - Manage swappable `AlertChannel`, `_loopCts`, and `_loopTask` under `_lifecycleLock` (lazily started on first `EnqueueAlert` or static init).
   - Provide `public static void Shutdown(int drainTimeoutMs = 1000)`: marks current writer complete, waits up to `Math.Clamp(drainTimeoutMs, 50, 5000)` ms for `_loopTask` via `task.Wait(timeout)` (int ms), cancels `_loopCts` if still pending, disposes old CTS, and resets state so a future `EnqueueAlert` starts a fresh loop cleanly.
9. Update `src/Shiro/Services/Launch/LaunchCoordinator.cs`:
   - Add `private int _activeQueueUsers; private int _gateDisposed;` with `TryEnterQueueGate()` / `ExitQueueGate(bool acquired)` / `TryDisposeQueueGate()` (same ref-counted pattern as `AutoReconnectService.Models.cs`), calling `TryDisposeQueueGate()` in `Dispose()` when `_activeQueueUsers == 0`.
10. Update `src/Shiro/Services/Launch/AutoReconnectService.cs`:
    - Add `private readonly TimeSpan _monitorInterval; private CancellationTokenSource _lifecycleCts = new();` and `internal CancellationToken LifecycleToken { get { lock (_lock) return _lifecycleCts.Token; } }`.
    - Add `public void StopForShutdown()`: cancels & replaces `_lifecycleCts` under `_lock`, sets `_monitorTimer.Change(Timeout.Infinite, Timeout.Infinite)`, and calls `SuppressAll()`.
    - In `RegisterSession(...)`: under `_lock`, if `!_disposed`, ensure `_monitorTimer` is re-armed (`_monitorTimer.Change(_monitorInterval, _monitorInterval)`) when a session is registered after a previous `StopForShutdown()`.
    - In `Dispose()`: also cancel & dispose `_lifecycleCts`.
11. Update `src/Shiro/AccountManager.cs` & `src/Shiro/AccountManager.Lifecycle.cs`:
    - Add `private readonly System.Threading.Lock _shutdownLock = new(); private int _shutdownStarted;` and make `shutdownFinalized` `volatile`.
    - In `ShutdownBackend()`: acquire `lock (_shutdownLock)`; if `Interlocked.Exchange(ref _shutdownStarted, 1) != 0` return immediately (concurrent callers block until shutdown finishes, then return cleanly; same-thread re-entry returns immediately!).
    - Build a `ShutdownCoordinator` registering all 4 ordered stages:
      - `StopMonitors`: `CancelLaunching`, `launchCoordinator?.Dispose()`, `lifetimeCancellation.Cancel()`, `AutoCookieRefresh` stop/dispose, `PresenceTimer` stop/dispose, `AutoReconnectService.Instance.StopForShutdown()`, `StreamerModeService.Shutdown()`, `RobloxSingletonService.Stop()`, `AntiAfkService.Stop()`, `RobloxAudioService.Shutdown()`, `RobloxWatcher.Stop()`, `RobloxBackgroundThrottlerService.Instance.IsEnabled = false`, `MemoryOptimizer.ConfigureAutoTrim(false)`, `Win32TrayHelper.CancelPendingRefresh()`, `RobloxJobObjectService.Close()`, `RobloxWindowService.ShowAllWindows()`.
      - `StopServersAndIntegrations`: `AltManagerWS` dispose (`Task.Run(...).Wait(TimeSpan.FromSeconds(2))`), `WebhookNotificationService.Shutdown(1000)`, `presenceService?.Dispose()`, `DiscordRpcService.Default.Dispose()`.
      - `FlushPersistence`: `RobloxDirectLauncher.RestoreOriginalCookies()`, `AccountRuntimeStateStore.Shutdown()`, `Persistence.SaveAccounts(bypassRateLimit: true)`, `Persistence.StopDeferredSave()`, `CancelDeferredSaveSettings()` + save `SavedPlaceId` + `settingsService?.Save()`.
      - `ClearSecretsAndPools`: `StoragePaths.ClearCachedVaultKey()`, `RobloxHbaService.ClearKeyCache()`, `ProxyBindingService.ClearClients()`, `SharedHttpClient.DisposeAll()`, dispose `lifetimeCancellation` & `cookieRefreshGate`.
    - Set `shutdownFinalized = true` in `finally`, then preserve existing `!SuppressExitOnShutdown && !Program.Closed && Avalonia.Application.Current == null` exit check.

- [ ] **Step 4: Run tests and architecture guardrails to verify pass**

Run:
```powershell
dotnet build "Shiro.sln" --no-incremental -warnaserror
dotnet test "tests/Shiro.Tests/Shiro.Tests.csproj" --no-build --filter "FullyQualifiedName~Phase2ShutdownCoordinatorTests|FullyQualifiedName~AccountManagerLifecycleBranchTests|FullyQualifiedName~ArchitectureTests"
```
Expected: 0 build warnings, 100% PASS across all filtered tests including every `ArchitectureTests` line/pattern/layering check.

- [ ] **Step 5: Commit**

```powershell
git add src/Shiro/Services/Composition/ShutdownCoordinator.cs src/Shiro/Services/Network/WebhookNotificationService.Lifecycle.cs src/Shiro/AccountManager.cs src/Shiro/AccountManager.Lifecycle.cs src/Shiro/Services/Composition/AppBootstrap.cs src/Shiro/Services/Storage/AccountPersistenceService.cs src/Shiro/Services/Storage/AccountPersistenceService.Recovery.cs src/Shiro/Services/Storage/AccountRuntimeStateStore.cs src/Shiro/Services/Windows/RobloxAudioService.Wasapi.cs src/Shiro/Services/Windows/RobloxJobObjectService.cs src/Shiro/Services/Windows/Win32TrayHelper.cs src/Shiro/Services/Network/WebhookNotificationService.cs src/Shiro/Services/Launch/LaunchCoordinator.cs src/Shiro/Services/Launch/AutoReconnectService.cs tests/Shiro.Tests/Phase2ShutdownCoordinatorTests.cs
git commit -m "fix(lifecycle): serialize ShutdownBackend and coordinate background service disposal"
```

---

### Task 2: UI Thread-Safe `AccountFieldsDialog` & Fail-Closed `DashboardView.ContextMenu`

**Files:**
- Modify: `src/Shiro/AvaloniaUI/Views/AccountFieldsDialog.axaml.cs:38-76`
- Modify: `src/Shiro/AvaloniaUI/Views/FastLoginDialog.axaml.cs:361-365`
- Modify: `src/Shiro/AvaloniaUI/Views/DashboardView.ContextMenu.cs:16-45`
- Test: `tests/Shiro.Tests/Ui/Phase2UiSafetyTests.cs`

**Interfaces:**
- Produces:
  - `DashboardView.ResolveItemFromMenuForTest(object? sender)` (`internal AccountViewModel?`)
  - `AccountFieldsDialog.InvokeStreamerModeChangedForTest(bool active)` (`internal void`)

- [ ] **Step 1: Write failing headless UI tests in `tests/Shiro.Tests/Ui/Phase2UiSafetyTests.cs`**

Create `tests/Shiro.Tests/Ui/Phase2UiSafetyTests.cs` (`[Collection("SerialGlobalState")]`) with `[AvaloniaFact]` tests:
1. `AccountFieldsDialog_StreamerModeChangedFromBackgroundThread_UpdatesHeaderWithoutCrossThreadCrash`: opens `AccountFieldsDialog(account)` in headless Avalonia, toggles `StreamerModeService.IsEnabled = true`, invokes `InvokeStreamerModeChangedForTest(true)` from `await Task.Run(...)`, pumps `Dispatcher.UIThread.RunJobs()`, asserts zero exceptions and `TxtHeaderTitle.Text` does not contain raw username; then closes dialog and invokes `InvokeStreamerModeChangedForTest(false)` from background thread asserting zero exceptions.
2. `DashboardView_GetItemFromMenu_UnattachedMenuItem_ReturnsNullInsteadOfFirstAccount`: creates `DashboardView` with a `MainViewModel` whose `AccountsVm.FilteredAccounts` contains 2 accounts (one selected, one unselected); asserts `view.ResolveItemFromMenuForTest(new MenuItem())` returns `null`, `view.ResolveItemFromMenuForTest(null)` returns `null`, and `view.ResolveItemFromMenuForTest(new MenuItem { DataContext = secondAccountVm })` returns `secondAccountVm`.

- [ ] **Step 2: Run tests to verify they fail**

Run:
```powershell
dotnet build "Shiro.sln" --no-incremental -warnaserror
dotnet test "tests/Shiro.Tests/Shiro.Tests.csproj" --no-build --filter "FullyQualifiedName~Phase2UiSafetyTests"
```
Expected: FAIL (compile error for test helpers / `ResolveItemFromMenuForTest` returns first account instead of `null`, and background call mutates `TxtHeaderTitle` off UI thread).

- [ ] **Step 3: Marshal `AccountFieldsDialog` & `FastLoginDialog` Streamer Mode callbacks via `AppDispatcher.Post` and remove dangerous `DashboardView.ContextMenu` fallback**

1. In `src/Shiro/AvaloniaUI/Views/AccountFieldsDialog.axaml.cs`:
   - Add `private volatile bool _isClosed;`.
   - Change `_onStreamerModeChanged` to `_ => AppDispatcher.Post(() => { if (!_isClosed) UpdateHeaderTitle(); });`.
   - Set `_isClosed = true;` at the top of `OnClosing` before unsubscribing `StreamerModeService.StateChanged`.
   - Add `internal void InvokeStreamerModeChangedForTest(bool active) => _onStreamerModeChanged(active);`.
2. In `src/Shiro/AvaloniaUI/Views/FastLoginDialog.axaml.cs:361-365`:
   - Wrap the body of `OnStreamerModeStateChanged(bool _)` in `AppDispatcher.Post(() => { if (!_isClosed && !string.IsNullOrEmpty(_username) && TxtDialogTitle != null) TxtDialogTitle.Text = _queue.Title; });` (net `<= +1` line, preserving 1150 partial budget).
3. In `src/Shiro/AvaloniaUI/Views/DashboardView.ContextMenu.cs:42-44`:
   - Replace `return ViewModel?.AccountsVm.FilteredAccounts?.FirstOrDefault(a => a.IsSelected) ?? ViewModel?.AccountsVm.FilteredAccounts?.FirstOrDefault();` with `return null;`.
   - Add `internal AccountViewModel? ResolveItemFromMenuForTest(object? sender) => GetItemFromMenu(sender);`.

- [ ] **Step 4: Run tests to verify pass**

Run:
```powershell
dotnet build "Shiro.sln" --no-incremental -warnaserror
dotnet test "tests/Shiro.Tests/Shiro.Tests.csproj" --no-build --filter "FullyQualifiedName~Phase2UiSafetyTests|FullyQualifiedName~Task4UiStabilityTests|FullyQualifiedName~DialogSmokeTests"
```
Expected: 100% PASS.

- [ ] **Step 5: Commit**

```powershell
git add src/Shiro/AvaloniaUI/Views/AccountFieldsDialog.axaml.cs src/Shiro/AvaloniaUI/Views/FastLoginDialog.axaml.cs src/Shiro/AvaloniaUI/Views/DashboardView.ContextMenu.cs tests/Shiro.Tests/Ui/Phase2UiSafetyTests.cs
git commit -m "fix(ui): marshal AccountFieldsDialog streamer updates to UI thread and fail-close context menu target resolution"
```

---

### Task 3: Storage & Lock Safety (`EmbeddedBrowser` Preservation, Unified `RecentGames` Lock, Off-Lock Dialogs, Defensive Vault Key Copy)

**Files:**
- Modify: `src/Shiro/Services/Storage/StoragePaths.cs:201-243`
- Modify: `src/Shiro/Services/Network/GameInfoService.cs:105-143, 357-429`
- Modify: `src/Shiro/ViewModels/Dashboard/LaunchBarViewModel.cs:292-313`
- Modify: `src/Shiro/Services/Storage/AccountPersistenceService.cs:106-208, 223-293`
- Modify: `src/Shiro/Services/Storage/StoragePaths.VaultKey.cs:385-402`
- Modify: `tests/Shiro.Tests/StoragePathsTests.cs:288-315`
- Modify: `tests/Shiro.Tests/P0StorageAndRedactionRegressionTests.cs:162-174`
- Test: `tests/Shiro.Tests/Phase2StorageAndLockSafetyTests.cs`

**Interfaces:**
- Produces:
  - `StoragePaths.IsDirectoryStale(string directory, DateTime cutoffUtc)` (`internal static bool`, relied on by Task 8's `EmbeddedBrowser_Proxy_*` folders)
  - `GameInfoService.GetRecentGamesSnapshot()` (`internal static List<Game>`)
  - `StoragePaths.PeekCachedVaultKeyBufferForTesting()` (`internal static byte[]?`)

- [ ] **Step 1: Write failing tests in `tests/Shiro.Tests/Phase2StorageAndLockSafetyTests.cs`**

Create `tests/Shiro.Tests/Phase2StorageAndLockSafetyTests.cs` (`[Collection("SerialGlobalState")]`) with 4 targeted tests:
1. `CleanupStaleRuntimeArtifacts_PreservesEmbeddedBrowser_AndPreservesDirWithFreshNestedFile`: under `OverrideDataDirectoryForTesting(tempDir)`:
   - Creates `BrowserSessions\EmbeddedBrowser` with `LastWriteTimeUtc = UtcNow.AddDays(-30)`.
   - Creates `BrowserSessions\EmbeddedBrowser_Proxy_abc\nested\fresh.dat` (fresh file) while setting the parent `EmbeddedBrowser_Proxy_abc` `LastWriteTimeUtc = UtcNow.AddDays(-30)`.
   - Creates `BrowserSessions\abandoned-old` with `LastWriteTimeUtc = UtcNow.AddDays(-30)`.
   - Calls `StoragePaths.CleanupStaleRuntimeArtifacts(TimeSpan.FromDays(7))`; asserts `EmbeddedBrowser` still exists, `EmbeddedBrowser_Proxy_abc\nested\fresh.dat` still exists, and `abandoned-old` was deleted (`removed == 1`).
2. `GameInfoService_RecentGamesSnapshot_ConcurrentWithWrites_NeverThrowsOrCorruptsFile`: concurrently runs `GameInfoService.AddRecentGameAsync`, `GameInfoService.CachePlaceName`, and `GameInfoService.GetRecentGamesSnapshot()` across 4 threads for 40 iterations under `OverrideDataDirectoryForTesting`; asserts 0 exceptions and `StoragePaths.RecentGamesFile` parses as valid JSON.
3. `AccountPersistenceService_LoadCorruptAccounts_InvokesShowErrorOutsideSaveLock`: sets up a corrupt `AccountData.json` and a mock `IDialogService` whose `ShowError(string)` callback spawns a `Task.Run(() => persistence.SaveAccounts(bypassRateLimit: false))` and waits up to 2 seconds for it to acquire `saveLock`; asserts the nested `SaveAccounts` call completes without deadlock (proving `saveLock` was released BEFORE `ShowError` was invoked!).
4. `StoragePaths_GetOrCreateVaultKey_ReturnsDefensiveCopy_ZeroingDoesNotCorruptCache`: calls `byte[] key1 = StoragePaths.GetOrCreateVaultKey()`, zeroes `key1` with `CryptographicOperations.ZeroMemory(key1)`, calls `byte[] key2 = StoragePaths.GetOrCreateVaultKey()`, and asserts `key2.Length == 32`, `Assert.Contains(key2, b => b != 0)`, and `Assert.NotSame(key1, key2)`.

- [ ] **Step 2: Run tests to verify they fail**

Run:
```powershell
dotnet build "Shiro.sln" --no-incremental -warnaserror
dotnet test "tests/Shiro.Tests/Shiro.Tests.csproj" --no-build --filter "FullyQualifiedName~Phase2StorageAndLockSafetyTests"
```
Expected: FAIL (`EmbeddedBrowser` deleted, `ShowError` deadlocks/times out holding `saveLock`, zeroing `key1` zeroes `key2`, `GetRecentGamesSnapshot` missing).

- [ ] **Step 3: Implement all 4 storage & lock safety fixes**

1. `src/Shiro/Services/Storage/StoragePaths.cs`:
   - In `CleanupStaleRuntimeArtifacts`: skip any directory whose `Path.GetFileName(directory)` equals `"EmbeddedBrowser"` (`StringComparison.OrdinalIgnoreCase`).
   - Add `internal static bool IsDirectoryStale(string directory, DateTime cutoffUtc)`: checks `Directory.GetLastWriteTimeUtc(directory) >= cutoffUtc` (return `false`), then enumerates `Directory.EnumerateFileSystemEntries(directory, "*", new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true })` and returns `false` immediately upon finding ANY entry with `File.GetLastWriteTimeUtc(entry) >= cutoffUtc`; if an `IOException`/`UnauthorizedAccessException` prevents checking, fail safe by returning `false`.
   - Call `if (!IsDirectoryStale(directory, cutoffUtc)) continue;` instead of checking only the top-level directory `LastWriteTimeUtc`.
2. `src/Shiro/Services/Network/GameInfoService.cs` & `src/Shiro/ViewModels/Dashboard/LaunchBarViewModel.cs`:
   - In `GameInfoService.UpdateRecentGamesCachedTitle`: perform `StoragePaths.AtomicWriteText(StoragePaths.RecentGamesFile, serialized)` **inside** `lock (_recentGamesLock)` right after serializing (matching `AddRecentGameAsync` and saving 2 lines!).
   - Add `internal static List<Game> GetRecentGamesSnapshot()` in `GameInfoService.cs` returning a shallow copy of `AppServices.ServerBrowserState.RecentGames` inside `lock (_recentGamesLock)` (stays `<= 480` lines).
   - In `LaunchBarViewModel.ReloadRecentGames()`: replace `var recent = AppServices.ServerBrowserState.RecentGames; if (recent != null) { lock (recent) { ... } }` with `foreach (var g in GameInfoService.GetRecentGamesSnapshot()) { ... }`.
3. `src/Shiro/Services/Storage/AccountPersistenceService.cs`:
   - In `LoadAccounts(byte[]? Hash, out bool unlocked)`: declare `string? deferredCorruptError = null;` before `lock (saveLock)`, assign it in `AccountDecodeStatus.Corrupt` instead of calling `AppServices.Dialogs.ShowError(...)`, and invoke `if (deferredCorruptError != null) AppServices.Dialogs.ShowError(deferredCorruptError);` immediately after `lock (saveLock)` exits.
   - In `TryLoadFromBackupAsync` / `CompleteConfirmedBackupRestoreLocked`: change `CompleteConfirmedBackupRestoreLocked` to return `(BackupLoadResult Result, string? ErrorMessage)` so `TryLoadFromBackupAsync` invokes `AppServices.Dialogs.ShowError(errorMsg)` AFTER releasing `saveLock`!
4. `src/Shiro/Services/Storage/StoragePaths.VaultKey.cs`:
   - In `GetOrCreateVaultKey()`: return `cached.AsSpan().ToArray()` / `resolvedKey.AsSpan().ToArray()` (defensive copy, matching `GetOrCreateVaultKeyCopy()`).
   - Add `internal static byte[]? PeekCachedVaultKeyBufferForTesting()` returning `GetCachedVaultKeyLocked(NormalizeVaultCacheKey())` under `_vaultKeyLock`.
   - Update `tests/Shiro.Tests/StoragePathsTests.cs:296, 312` from `Assert.Same(keyB, ...)` / `Assert.Same(globalKeyRef, ...)` to `Assert.Equal` + `Assert.NotSame`.
   - Update `tests/Shiro.Tests/P0StorageAndRedactionRegressionTests.cs:162` to obtain `byte[] previousRef = StoragePaths.PeekCachedVaultKeyBufferForTesting()!;` after priming via `StoragePaths.GetOrCreateVaultKey()` (0 net lines -> stays 538/550).

- [ ] **Step 4: Run tests to verify pass**

Run:
```powershell
dotnet build "Shiro.sln" --no-incremental -warnaserror
dotnet test "tests/Shiro.Tests/Shiro.Tests.csproj" --no-build --filter "FullyQualifiedName~Phase2StorageAndLockSafetyTests|FullyQualifiedName~StoragePathsTests|FullyQualifiedName~P0StorageAndRedactionRegressionTests|FullyQualifiedName~SecurityHardeningTests|FullyQualifiedName~AccountPersistence"
```
Expected: 100% PASS.

- [ ] **Step 5: Commit**

```powershell
git add src/Shiro/Services/Storage/StoragePaths.cs src/Shiro/Services/Network/GameInfoService.cs src/Shiro/ViewModels/Dashboard/LaunchBarViewModel.cs src/Shiro/Services/Storage/AccountPersistenceService.cs src/Shiro/Services/Storage/StoragePaths.VaultKey.cs tests/Shiro.Tests/StoragePathsTests.cs tests/Shiro.Tests/P0StorageAndRedactionRegressionTests.cs tests/Shiro.Tests/Phase2StorageAndLockSafetyTests.cs
git commit -m "fix(storage): preserve EmbeddedBrowser, unify RecentGames lock, move dialogs off saveLock, and copy vault key"
```

---

### Task 4: Cookie Rotation Tracker (`rbxid`/`rbxuid`), De-Elevation User Environment Block & VC++ Redistributable Confirmation Prompt

**Files:**
- Modify: `src/Shiro/Services/Launch/RobloxCookieFileHelper.cs:120-136`
- Modify: `src/Shiro/Services/Launch/RobloxDirectLauncher.DeElevation.cs:59-84, 207-265`
- Modify: `src/Shiro/Services/Launch/PrerequisiteService.cs:94-110`
- Modify: `src/Shiro/AccountManager.cs:201-206`
- Modify: `src/Shiro/AccountManager.Lifecycle.cs:140-155`
- Modify: `src/Shiro/Resources/Strings/en.json`
- Modify: `src/Shiro/Resources/Strings/vi.json`
- Test: `tests/Shiro.Tests/Phase2LaunchAndPrerequisiteTests.cs`

**Interfaces:**
- Produces:
  - `RobloxDirectLauncher.ResolveDeElevationEnvironmentArgs(IntPtr envBlock)` (`internal static (uint CreationFlags, IntPtr Environment)`)
  - `PrerequisiteService.PromptAndInstallVCRedistAsync(ISettingsService promptsSettings, Func<string, string, Task<bool>> confirmAsync, Func<bool>? needsVcRedistOverride = null, Func<CancellationToken, Task<bool>>? installOverride = null, CancellationToken cancellationToken = default)` (`public static Task<bool>`)

- [ ] **Step 1: Write failing tests in `tests/Shiro.Tests/Phase2LaunchAndPrerequisiteTests.cs`**

Create `tests/Shiro.Tests/Phase2LaunchAndPrerequisiteTests.cs` (`[Collection("SerialGlobalState")]`) with 4 tests:
1. `RobloxCookieFileHelper_ExtractsUserIdFromBothRbxIdAndRbxUid`: creates DPAPI-protected temporary `RobloxCookies.dat` files containing `RBXEventTrackerV2` with `rbxid=44556677&browserid=trk-1` and `rbxuid=88990011&browserid=trk-2`; asserts `TryExtractTrackerMetadataFromCookiesDatFile` returns `true` with `userId == 44556677` / `browserTrackerId == "trk-1"` and `userId == 88990011` / `browserTrackerId == "trk-2"`, and that `CanSyncRotatedCookieForTarget` blocks mismatched account `UserID = 11111` when `rbxid=44556677`.
2. `RobloxDirectLauncher_DeElevation_UsesUnicodeEnvironmentBlockAndDestroysIt`: asserts `ResolveDeElevationEnvironmentArgs(new IntPtr(0x1234)) == (0x00000400u, new IntPtr(0x1234))` and `ResolveDeElevationEnvironmentArgs(IntPtr.Zero) == (0u, IntPtr.Zero)`; asserts `RobloxDirectLauncher.DeElevation.cs` invokes `CreateEnvironmentBlock` and cleans up via `DestroyEnvironmentBlock` in `finally`.
3. `PrerequisiteService_PromptAndInstallVCRedistAsync_RequiresExplicitConfirmation`:
   - User declines (`confirmAsync => false`): `VCPrompted` is set to `"true"`, `installOverride` called `0` times, returns `false`.
   - User accepts (`confirmAsync => true`): `VCPrompted` is set to `"true"`, `installOverride` called `1` time, returns `true`.
   - Already prompted (`VCPrompted = "true"`): `confirmAsync` called `0` times, `installOverride` called `0` times, returns `false`.
   - `AccountManager.cs` constructor does NOT call `InstallVCRedistAsync` directly.
4. `Localization_VcRedistPromptKeys_ExistInBothEnglishAndVietnamese`: asserts `Prerequisite.VcRedistPromptTitle` and `Prerequisite.VcRedistPromptMessage` resolve to non-empty distinct strings in both `en.json` and `vi.json`.

- [ ] **Step 2: Run tests to verify they fail**

Run:
```powershell
dotnet build "Shiro.sln" --no-incremental -warnaserror
```
Expected: FAIL to compile / fail tests because `rbxid` is ignored, `ResolveDeElevationEnvironmentArgs` and `PromptAndInstallVCRedistAsync` do not exist, and prompt localization keys are missing.

- [ ] **Step 3: Implement cookie tracker fix, de-elevation environment block, and VC++ user confirmation flow**

1. `src/Shiro/Services/Launch/RobloxCookieFileHelper.cs:126`:
   - Change `if (string.Equals(key, "rbxuid", StringComparison.OrdinalIgnoreCase) && !string.IsNullOrEmpty(v))` to match BOTH `"rbxuid"` and `"rbxid"` (`string.Equals(key, "rbxuid", StringComparison.OrdinalIgnoreCase) || string.Equals(key, "rbxid", StringComparison.OrdinalIgnoreCase)`).
2. `src/Shiro/Services/Launch/RobloxDirectLauncher.DeElevation.cs`:
   - Add `[LibraryImport("userenv.dll", SetLastError = true)]` declarations for `CreateEnvironmentBlock(out IntPtr lpEnvironment, IntPtr hToken, [MarshalAs(UnmanagedType.Bool)] bool bInherit)` and `DestroyEnvironmentBlock(IntPtr lpEnvironment)`.
   - Add `private const uint CREATE_UNICODE_ENVIRONMENT = 0x00000400;` and `internal static (uint CreationFlags, IntPtr Environment) ResolveDeElevationEnvironmentArgs(IntPtr envBlock) => envBlock != IntPtr.Zero ? (CREATE_UNICODE_ENVIRONMENT, envBlock) : (0u, IntPtr.Zero);`.
   - Inside `TrySpawnDeElevated` after `DuplicateTokenEx`: call `IntPtr lpEnv = IntPtr.Zero; bool hasEnv = CreateEnvironmentBlock(out lpEnv, hNewToken, bInherit: false);` (if `!hasEnv`, log debug error and set `lpEnv = IntPtr.Zero`); pass `ResolveDeElevationEnvironmentArgs(lpEnv)`'s `CreationFlags` and `Environment` to `CreateProcessWithToken`; in `finally`, if `lpEnv != IntPtr.Zero`, call `DestroyEnvironmentBlock(lpEnv)`.
3. `src/Shiro/Services/Launch/PrerequisiteService.cs`:
   - Add `PromptAndInstallVCRedistAsync(...)`: checks `promptsSettings.Exists("VCPrompted")` and `needsVcRedist()`, shows confirmation via `confirmAsync(LocalizationService.Get("Prerequisite.VcRedistPromptMessage"), LocalizationService.Get("Prerequisite.VcRedistPromptTitle"))`, sets `promptsSettings.Set("VCPrompted", "true")`, and invokes `installOverride ?? InstallVCRedistAsync` ONLY when user clicked confirm!
4. `src/Shiro/Resources/Strings/en.json` & `src/Shiro/Resources/Strings/vi.json`:
   - Add `Prerequisite.VcRedistPromptTitle` and `Prerequisite.VcRedistPromptMessage` in matching alphabetical order in both files.
5. `src/Shiro/AccountManager.cs:201-205` & `src/Shiro/AccountManager.Lifecycle.cs:147`:
   - Remove the silent `VCPrompted` / `InstallVCRedistAsync` block from the `AccountManager()` constructor (and remove now-unused `vcredistInstallTask` or bind it in `InitializePostStartup()`).
   - In `AccountManager.Lifecycle.cs` (`InitializePostStartup()`): if `Avalonia.Application.Current != null && !AppSettings.Prompts.Exists("VCPrompted")`, dispatch `vcredistInstallTask = ObserveBackgroundTaskAsync(PrerequisiteService.PromptAndInstallVCRedistAsync(AppSettings.Prompts, (msg, title) => AppDispatcher.InvokeTaskAsync(() => AppServices.Dialogs.ShowConfirmationAsync(msg, title)), cancellationToken: lifetimeCancellation.Token), "VCRedist prerequisite prompt");`.

- [ ] **Step 4: Run tests to verify pass**

Run:
```powershell
dotnet build "Shiro.sln" --no-incremental -warnaserror
dotnet test "tests/Shiro.Tests/Shiro.Tests.csproj" --no-build --filter "FullyQualifiedName~Phase2LaunchAndPrerequisiteTests|FullyQualifiedName~RobloxDirectLauncher|FullyQualifiedName~PrerequisiteService|FullyQualifiedName~ArchitectureTests"
```
Expected: 100% PASS (including localization symmetry & partial class line budgets).

- [ ] **Step 5: Commit**

```powershell
git add src/Shiro/Services/Launch/RobloxCookieFileHelper.cs src/Shiro/Services/Launch/RobloxDirectLauncher.DeElevation.cs src/Shiro/Services/Launch/PrerequisiteService.cs src/Shiro/AccountManager.cs src/Shiro/AccountManager.Lifecycle.cs src/Shiro/Resources/Strings/en.json src/Shiro/Resources/Strings/vi.json tests/Shiro.Tests/Phase2LaunchAndPrerequisiteTests.cs
git commit -m "fix(launch): parse rbxid tracker metadata, isolate de-elevation environment block, and confirm VCRedist install"
```

---

### Task 5: Residential PPPoE VPN Detection, Watcher RAM Default & Throttler/MemoryOptimizer Coordination

**Files:**
- Modify: `src/Shiro/Services/Network/NetworkSignatureHelper.cs:21-65`
- Modify: `src/Shiro/Services/Windows/RobloxWatcher.cs:47-51`
- Modify: `src/Shiro/ViewModels/ServerListViewModel.Watcher.cs:15-17`
- Modify: `src/Shiro/Services/Storage/ServerWatcherConfigService.cs:55-90`
- Modify: `src/Shiro/Services/Windows/MemoryOptimizer.ResourceProfile.cs:46-52`
- Modify: `src/Shiro/Services/Windows/MemoryOptimizer.cs:183-185, 275-282`
- Test: `tests/Shiro.Tests/Phase2NetworkWatcherAndOptimizerTests.cs`

**Interfaces:**
- Produces:
  - `NetworkSignatureHelper.IsVpnOrTunnelAdapter(string? name, string? description, NetworkInterfaceType interfaceType)` (`internal static bool`)
  - `RobloxWatcher.DefaultMemoryThresholdMb` (`internal const int = 2048`)
  - `MemoryOptimizer.ShouldRestoreFullPerformanceOnSolo(int runningClientCount, bool backgroundThrottlerEnabled)` (`internal static bool`)

- [ ] **Step 1: Write failing tests in `tests/Shiro.Tests/Phase2NetworkWatcherAndOptimizerTests.cs`**

Create `tests/Shiro.Tests/Phase2NetworkWatcherAndOptimizerTests.cs` (`[Collection("SerialGlobalState")]`) with 3 tests:
1. `NetworkSignatureHelper_IsVpnOrTunnelAdapter_IgnoresResidentialPppoe_DetectsVpns`:
   - `false` for `("Broadband Connection", "WAN Miniport (PPPOE)", NetworkInterfaceType.Ppp)`, `("Viettel Fiber", "PPPoE Connection", NetworkInterfaceType.Ppp)`, `("FPT Telecom", "Dial-up PPP", NetworkInterfaceType.Ppp)`, `("Teredo Tunneling Pseudo-Interface", "Microsoft Teredo", NetworkInterfaceType.Tunnel)`, `("Ethernet", "Intel(R) Ethernet", NetworkInterfaceType.Ethernet)`.
   - `true` for `("Work VPN", "WAN Miniport (L2TP)", NetworkInterfaceType.Ppp)`, `("Corp", "WAN Miniport (SSTP)", NetworkInterfaceType.Ppp)`, `("Office", "WAN Miniport (IKEv2)", NetworkInterfaceType.Ppp)`, `("ProtonVPN", "ProtonVPN Tunnel", NetworkInterfaceType.Tunnel)`, `("WARP", "Cloudflare WARP", NetworkInterfaceType.GenericModem)`, `("wg0", "WireGuard Tunnel", NetworkInterfaceType.Unknown)`.
2. `Watcher_DefaultMemoryThresholdMb_IsSafe2048Mb_AndPreservesSavedValues`:
   - Asserts `RobloxWatcher.DefaultMemoryThresholdMb == 2048`.
   - `ServerWatcherConfigService.Load(new IniSection("Watcher"), syncRuntime: false).MemoryThresholdMb == 2048m`.
   - When `MemoryLowValue` is explicitly saved as `"1536"`, `Load(...)` returns `1536m`.
   - `RobloxWatcher.ShouldCloseForMemoryThreshold(true, workingSetMb: 1500, thresholdMb: RobloxWatcher.DefaultMemoryThresholdMb)` returns `false`, and `workingSetMb: 2500` returns `true`.
3. `MemoryOptimizer_ShouldRestoreFullPerformanceOnSolo_DefersToActiveBackgroundThrottler`:
   - Asserts `MemoryOptimizer.ShouldRestoreFullPerformanceOnSolo(1, backgroundThrottlerEnabled: false) == true`.
   - Asserts `MemoryOptimizer.ShouldRestoreFullPerformanceOnSolo(1, backgroundThrottlerEnabled: true) == false`.
   - Asserts `MemoryOptimizer.ShouldRestoreFullPerformanceOnSolo(2, backgroundThrottlerEnabled: false) == false`.
   - Asserts existing 1-arg `MemoryOptimizer.ShouldRestoreFullPerformanceOnSolo(1) == true` remains unchanged.

- [ ] **Step 2: Run tests to verify they fail**

Run:
```powershell
dotnet build "Shiro.sln" --no-incremental -warnaserror
```
Expected: FAIL to compile because `IsVpnOrTunnelAdapter`, `DefaultMemoryThresholdMb`, and 2-arg `ShouldRestoreFullPerformanceOnSolo` do not exist yet.

- [ ] **Step 3: Implement PPPoE VPN classifier, safe 2048 MB watcher default, and optimizer/throttler coordination**

1. `src/Shiro/Services/Network/NetworkSignatureHelper.cs`:
   - Extract `internal static bool IsVpnOrTunnelAdapter(string? name, string? description, NetworkInterfaceType interfaceType)`:
     - Return `false` if `interfaceType == NetworkInterfaceType.Loopback || IsTransitionPseudoInterface(name, description, interfaceType)`.
     - Return `true` if `interfaceType == NetworkInterfaceType.Tunnel`.
     - Check existing known VPN keywords (`Proton`, `WireGuard`, `Wintun`, `OpenVPN`, `NordLynx`, `TAP-Windows`, `Tailscale`, `Mullvad`, `ZeroTier`, `Cisco`, `AnyConnect`, `GlobalProtect`, `Fortinet`, `SoftEther`, `Radmin`, `Hamachi`, `WARP`, `Cloudflare WARP`) PLUS standard VPN protocol markers (`VPN`, `L2TP`, `PPTP`, `SSTP`, `IKEv2`) against `name` and `description` — returning `true` when matched.
     - Plain `NetworkInterfaceType.Ppp` without any VPN marker (residential PPPoE) returns `false`.
   - Replace inline adapter classification in `IsVpnOrTunnelInterfaceActive` with `if (!IsVpnOrTunnelAdapter(nic.Name, nic.Description, nic.NetworkInterfaceType)) continue;`.
2. `src/Shiro/Services/Windows/RobloxWatcher.cs`, `src/Shiro/ViewModels/ServerListViewModel.Watcher.cs`, `src/Shiro/Services/Storage/ServerWatcherConfigService.cs`:
   - Add `internal const int DefaultMemoryThresholdMb = 2048;` in `RobloxWatcher.cs` and set `public static int MemoryLowValue = DefaultMemoryThresholdMb;` (+1 net line -> `481 / 485`).
   - Update `ServerListViewModel.Watcher.cs:16` default to `RobloxWatcher.DefaultMemoryThresholdMb`.
   - Update `ServerWatcherConfigService.cs:64, 87` defaults from `200m` to `(decimal)RobloxWatcher.DefaultMemoryThresholdMb`.
3. `src/Shiro/Services/Windows/MemoryOptimizer.ResourceProfile.cs` & `src/Shiro/Services/Windows/MemoryOptimizer.cs`:
   - Add `internal static bool ShouldRestoreFullPerformanceOnSolo(int runningClientCount, bool backgroundThrottlerEnabled) => !backgroundThrottlerEnabled && ShouldRestoreFullPerformanceOnSolo(runningClientCount);`.
   - In `MemoryOptimizer.OptimizeRobloxClients` (L276): pass `RobloxBackgroundThrottlerService.Instance.IsEnabled` into `ShouldRestoreFullPerformanceOnSolo(pids.Count, throttlerEnabled)`.
   - In `MemoryOptimizer.OnPeriodicCheck` (L184): if `RobloxBackgroundThrottlerService.Instance.IsEnabled` is `true`, let `effectiveLowerPriority = false` when `pids.Count == 1` so `MemoryOptimizer` trims only on extreme memory and never overrides the throttler's priority class on unfocused solo clients.

- [ ] **Step 4: Run tests to verify pass**

Run:
```powershell
dotnet build "Shiro.sln" --no-incremental -warnaserror
dotnet test "tests/Shiro.Tests/Shiro.Tests.csproj" --no-build --filter "FullyQualifiedName~Phase2NetworkWatcherAndOptimizerTests|FullyQualifiedName~P0Batch2RegressionTests|FullyQualifiedName~RobloxBackgroundThrottlerTests|FullyQualifiedName~P1WindowsAndUiRegressionTests|FullyQualifiedName~PerformanceAndStaleJobIdRegressionTests|FullyQualifiedName~BugHuntRound3Tests"
```
Expected: 100% PASS.

- [ ] **Step 5: Commit**

```powershell
git add src/Shiro/Services/Network/NetworkSignatureHelper.cs src/Shiro/Services/Windows/RobloxWatcher.cs src/Shiro/ViewModels/ServerListViewModel.Watcher.cs src/Shiro/Services/Storage/ServerWatcherConfigService.cs src/Shiro/Services/Windows/MemoryOptimizer.ResourceProfile.cs src/Shiro/Services/Windows/MemoryOptimizer.cs tests/Shiro.Tests/Phase2NetworkWatcherAndOptimizerTests.cs
git commit -m "fix(windows): exclude residential PPPoE from VPN detection, raise Watcher default RAM limit to 2048MB, and stop throttler/optimizer conflict"
```

---

### Task 6: `AutoReconnectService` Account Eligibility & Full `CancellationToken` Propagation

**Files:**
- Modify: `src/Shiro/Services/Launch/AutoReconnectService.cs:60-90, 249-330`
- Modify: `src/Shiro/Services/Launch/AutoReconnectService.Models.cs:70-95`
- Modify: `src/Shiro/Services/Launch/AutoReconnectService.Monitor.cs:84-107`
- Test: `tests/Shiro.Tests/Phase2AutoReconnectHardeningTests.cs`

**Interfaces:**
- Consumes: `AutoReconnectService.LifecycleToken` and `StopForShutdown()` from Task 1.
- Produces:
  - `AutoReconnectService.IsAccountEligibleForReconnect(Account? account)` (`internal static bool`)
  - `AutoReconnectService.TestJoinServerWithTokenOverride` (`internal Func<ReconnectSession, CancellationToken, Task<string>>?`)

- [ ] **Step 1: Write failing tests in `tests/Shiro.Tests/Phase2AutoReconnectHardeningTests.cs`**

Create `tests/Shiro.Tests/Phase2AutoReconnectHardeningTests.cs` (`[Collection("SerialGlobalState")]`) with 4 tests:
1. `IsAccountEligibleForReconnect_RejectsNullExpiredAndBannedAccounts`: asserts `false` for `null`, `new Account { Valid = false }`, `new Account { Valid = true, AccountStatusIssue = "Banned" }`, and `true` for `new Account { Valid = true }`.
2. `HandleProcessExit_WhenAccountExpiredOrBanned_SuppressesWithoutAttemptingReconnect`: creates an isolated `AutoReconnectService` with `IsEnabled = true`, registers an account with `Valid = false` (and another with `Valid = true, AccountStatusIssue = "Warned/Locked"`), hooks `ReconnectAttempted` and `TestJoinServerWithTokenOverride`, triggers `HandleProcessExit(session)`, waits 100 ms, asserts `reconnectAttempts == 0`, `joinCalls == 0`, and `service.TryGetAccountSession(acc, out _) == false`.
3. `HandleProcessExit_WhenAccountInvalidatedDuringBackoff_AbortsBeforeJoinServer`: uses `TestReconnectDelayMsOverride = 60`, starts `HandleProcessExit` for a valid account, sets `acc.Valid = false` immediately during backoff, awaits 200 ms, asserts `joinCalls == 0`.
4. `HandleProcessExit_PropagatesCancelableTokenToJoinServer_CanceledOnSuppressOrShutdown`: uses `TestReconnectDelayMsOverride = 1` and `TestJoinServerWithTokenOverride` that records the passed `CancellationToken` and awaits `Task.Delay(5000, ct)`; calls `service.SuppressReconnect(acc.Username)` (and in a second case `service.StopForShutdown()`); asserts `capturedToken.CanBeCanceled == true` and `capturedToken.IsCancellationRequested == true` within 500 ms.

- [ ] **Step 2: Run tests to verify they fail**

Run:
```powershell
dotnet build "Shiro.sln" --no-incremental -warnaserror
```
Expected: FAIL to compile because `IsAccountEligibleForReconnect` and `TestJoinServerWithTokenOverride` do not exist yet.

- [ ] **Step 3: Implement eligibility guards and full `CancellationToken` propagation**

1. `src/Shiro/Services/Launch/AutoReconnectService.cs`:
   - Add `internal static bool IsAccountEligibleForReconnect(Account? account) => account != null && account.Valid && !account.IsBannedOrLocked;`.
   - Add `TestJoinServerWithTokenOverride` (same singleton `AsyncLocal` / instance field pattern as `TestJoinServerOverride`).
   - At the start of `HandleProcessExit(ReconnectSession session)`: if `!IsAccountEligibleForReconnect(session.Account)`, mark `session.IsSuppressed = true; session.IsActive = false;`, remove `sessionKey` from `_sessions`, log warning, and return before touching `RetryCount` or invoking `ReconnectAttempted`.
   - Pass `LifecycleToken` into `session.ResetReconnectToken(LifecycleToken)`.
   - Inside the backoff `Task.Run`, both after `Task.Delay` and after acquiring `_reconnectGate`, re-check `IsAccountEligibleForReconnect(session.Account)`; if false, suppress/remove session and return without calling `JoinServer`.
   - Invoke `TestJoinServerWithTokenOverride != null ? await TestJoinServerWithTokenOverride(session, reconnectToken) : (TestJoinServerOverride != null ? await TestJoinServerOverride(session) : await session.Account.JoinServer(session.PlaceId, session.JobId, session.FollowUser, session.JoinVip, cancellationToken: reconnectToken))`.
2. `src/Shiro/Services/Launch/AutoReconnectService.Models.cs`:
   - Give `ReconnectSession` a per-session observation cancellation source (`_observeCts`) canceled & disposed inside `CancelPendingReconnect()`, and expose `internal CancellationToken GetOrCreateObserveToken(CancellationToken serviceToken)` returning a linked token.
3. `src/Shiro/Services/Launch/AutoReconnectService.Monitor.cs:84-107`:
   - In `ObserveProcessExitImmediateAsync(ReconnectSession session)`: obtain `CancellationToken observeToken = session.GetOrCreateObserveToken(LifecycleToken);` and call `await proc.WaitForExitAsync(observeToken).ConfigureAwait(false);`, catching `OperationCanceledException` silently so exiting/suppressing/shutting down immediately disposes `using var proc` without leaking OS process handles!

- [ ] **Step 4: Run tests to verify pass**

Run:
```powershell
dotnet build "Shiro.sln" --no-incremental -warnaserror
dotnet test "tests/Shiro.Tests/Shiro.Tests.csproj" --no-build --filter "FullyQualifiedName~Phase2AutoReconnectHardeningTests|FullyQualifiedName~AutoReconnect|FullyQualifiedName~MultiInstanceAndReconnectTests"
```
Expected: 100% PASS.

- [ ] **Step 5: Commit**

```powershell
git add src/Shiro/Services/Launch/AutoReconnectService.cs src/Shiro/Services/Launch/AutoReconnectService.Models.cs src/Shiro/Services/Launch/AutoReconnectService.Monitor.cs tests/Shiro.Tests/Phase2AutoReconnectHardeningTests.cs
git commit -m "fix(reconnect): skip invalid or banned accounts and propagate CancellationToken to JoinServer and WaitForExitAsync"
```

---

### Task 7: Close Streamer Mode Privacy Leaks (Discord RPC & UI Status/Toasts)

**Files:**
- Modify: `src/Shiro/Services/Network/DiscordRpcService.cs:296-333`
- Modify: `src/Shiro/ViewModels/Dashboard/AccountCollectionCommandActions.cs:24-36, 100-135`
- Modify: `src/Shiro/ViewModels/Dashboard/ToolsStatusViewModel.cs:420-430`
- Modify: `src/Shiro/ViewModels/ServerList/ServerBrowserTabViewModel.Operations.cs:19-79`
- Test: `tests/Shiro.Tests/Phase2StreamerModePrivacyTests.cs`

**Interfaces:**
- Consumes: `StreamerModeService.IsActive`, `StreamerModeService.GetDisplayUsername(...)`, `StreamerModeService.ShouldMaskRobux()`, `StreamerModeService.MaskJobId(...)`.

- [ ] **Step 1: Write failing privacy leak tests in `tests/Shiro.Tests/Phase2StreamerModePrivacyTests.cs`**

Create `tests/Shiro.Tests/Phase2StreamerModePrivacyTests.cs` (`[Collection("SerialGlobalState")]`) with 4 tests (all restoring `StreamerModeService.IsEnabled` in `finally`):
1. `DiscordRpcService_UpdatePresence_InStreamerMode_HidesGameNameCountsAndJoinUrl`: sets `StreamerModeService.IsEnabled = true`, calls `service.UpdatePresence("SecretBloxGame", 99887766, "secret-job-uuid", totalAccounts: 17, activeInGameAccounts: 4)` with `DiscordRpcService.TestAvailablePipesOverride = () => Array.Empty<int>()`, inspects private `_lastActivity` via reflection and serialized payload via `BuildSetActivityPayload(activity)`: asserts payload does NOT contain `"SecretBloxGame"`, `"99887766"`, `"secret-job-uuid"`, `"17"`, or `"buttons"`, and asserts `activity.Details == "Shiro Account Manager"` and `activity.State == LocalizationService.Get("DiscordRpc.StreamerMode")`.
2. `AccountCollectionCommandActions_InStreamerMode_MasksUsernameAndRobuxBalance`: sets `StreamerModeService.IsEnabled = true` and `StreamerModeHideUsernames = "true"`, `StreamerModeHideRobux = "true"`, runs `CheckRobuxSingleAsync` (with stubbed balance or account pre-populated + `TestHttpClientFactory` returning `{"robux": 98765}`) and captures all `setStatus` strings: asserts none contain `"SecretStreamerUser"` or `"98,765"` or `"98765"`, and final status contains `StreamerModeService.MaskUsername("SecretStreamerUser")` and `"***"`.
3. `ToolsStatusViewModel_FocusAccountWindow_InStreamerMode_MasksUsernameInToastAndStatus`: sets `StreamerModeService.IsEnabled = true`, executes `FocusAccountWindowCommand` for an `AccountViewModel` with `Username = "SecretFocusUser"` (no live Roblox window -> toast path), asserts `ToastService` items and `StatusMessage` do NOT contain `"SecretFocusUser"`.
4. `ServerBrowserTabViewModel_InStreamerMode_MasksJobIdInStatusMessages`: sets `StreamerModeService.IsEnabled = true`, invokes `SelectServerJobIdCommand` and `CopyJobIdCommand` on a `ServerItemViewModel` with `Id = "deadbeef-1234-5678-90ab-cdef12345678"`, asserts `MainViewModel.StatusMessage` and `LoadingStatus` contain `"***-***"` and do NOT contain `"deadbeef-1234-5678-90ab-cdef12345678"`, while `MainViewModel.JobId` still receives the real JobId for launching.

- [ ] **Step 2: Run tests to verify they fail**

Run:
```powershell
dotnet build "Shiro.sln" --no-incremental -warnaserror
dotnet test "tests/Shiro.Tests/Shiro.Tests.csproj" --no-build --filter "FullyQualifiedName~Phase2StreamerModePrivacyTests"
```
Expected: FAIL on all 4 tests because Discord RPC leaks `SecretBloxGame`, `AccountCollectionCommandActions` leaks raw username & Robux, `ToolsStatusViewModel` leaks raw username, and `ServerBrowserTabViewModel.Operations` leaks raw JobId.

- [ ] **Step 3: Mask all 4 Streamer Mode leaks in place**

1. `src/Shiro/Services/Network/DiscordRpcService.cs:304-322` (`<= +4` lines -> `<= 478 / 485`):
   - When `isStreamerMode` is `true`: set `details = "Shiro Account Manager";` and `state = LocalizationService.Get("DiscordRpc.StreamerMode");` (bypassing both `DiscordRpc.PlayingGame` and `DiscordRpc.ManagingAccounts` / `DiscordRpc.ClientsOnline`!).
2. `src/Shiro/ViewModels/Dashboard/AccountCollectionCommandActions.cs`:
   - In `ValidateSingleSession` (L30-34): compute `string shownName = StreamerModeService.GetDisplayUsername(accountVm.Username);` and use `shownName` in all 3 branches.
   - In `RefreshAllRobuxAsync` (L107): pass `StreamerModeService.GetDisplayUsername(acc.Username)`.
   - In `CheckRobuxSingleAsync` (L126-134): use `string shownName = StreamerModeService.GetDisplayUsername(accountVm.Username);` and pass `StreamerModeService.ShouldMaskRobux() ? (object)"***" : bal` as argument 1 of `Main.RobuxBalanceResult`.
3. `src/Shiro/ViewModels/Dashboard/ToolsStatusViewModel.cs:424-428` (**0 net lines** -> stays `481 / 485`):
   - Replace `avm.Username` with `StreamerModeService.GetDisplayUsername(avm.Username)` on lines 424 and 428.
4. `src/Shiro/ViewModels/ServerList/ServerBrowserTabViewModel.Operations.cs:29, 32, 40, 75-76`:
   - Add small helper `private static string FormatDisplayServerId(string serverId) => StreamerModeService.IsActive ? StreamerModeService.MaskJobId(serverId) : serverId;` and use it for `StatusMessage` and `_setLoadingStatus` in `SelectServerJobId`, `CopyJobId`, and `JoinServerAsync` (keep raw `targetId` / `targetServer` for `_mainViewModel.JobId`, `ClipboardHelper.SetText`, and `JoinServerFromHostAsync`).

- [ ] **Step 4: Run tests to verify pass**

Run:
```powershell
dotnet build "Shiro.sln" --no-incremental -warnaserror
dotnet test "tests/Shiro.Tests/Shiro.Tests.csproj" --no-build --filter "FullyQualifiedName~Phase2StreamerModePrivacyTests|FullyQualifiedName~DiscordRpc|FullyQualifiedName~Task4UiStabilityTests|FullyQualifiedName~Plan3FinalFixRegressionTests|FullyQualifiedName~ArchitectureTests"
```
Expected: 100% PASS.

- [ ] **Step 5: Commit**

```powershell
git add src/Shiro/Services/Network/DiscordRpcService.cs src/Shiro/ViewModels/Dashboard/AccountCollectionCommandActions.cs src/Shiro/ViewModels/Dashboard/ToolsStatusViewModel.cs src/Shiro/ViewModels/ServerList/ServerBrowserTabViewModel.Operations.cs tests/Shiro.Tests/Phase2StreamerModePrivacyTests.cs
git commit -m "fix(privacy): eliminate Streamer Mode leaks in Discord RPC, status bar, toasts, and server JobId display"
```

---

### Task 8: Per-Account Proxy Routing for WebView2 (`FastLoginDialog`) & `Batch.cs` Proxy/HBA Hardening

**Files:**
- Modify: `src/Shiro/AvaloniaUI/Controls/WebView2Control.cs`
- Modify: `src/Shiro/AvaloniaUI/Views/FastLoginDialog.axaml.cs:86-113`
- Modify: `src/Shiro/Services/Launch/Batch.cs:320-377`
- Test: `tests/Shiro.Tests/Phase2WebView2AndBatchProxyTests.cs`

**Interfaces:**
- Consumes: `ProxyBindingService.GetEffectiveProxyForAccount(Account?)`, `ProxyManagerService.ParseProxy(string?)`, `InMemorySecretProtector`, `StoragePaths.IsDirectoryStale` (from Task 3 for `EmbeddedBrowser_Proxy_*` folders).
- Produces:
  - `WebView2Control.ConfigureProxy(string? rawProxy)` (`public void`)
  - `WebView2Control.TryParseProxyForWebView(string? rawProxy, out string? proxyServerArg, out string? username, out string? password, out bool isSocksWithAuth)` (`internal static bool`)
  - `WebView2Control.BuildAdditionalBrowserArguments(string? proxyServerArg)` (`internal static string`)
  - `WebView2Control.ResolveEnvironmentDataFolder(string? proxyServerArg)` (`internal static string`)

- [ ] **Step 1: Write failing tests in `tests/Shiro.Tests/Phase2WebView2AndBatchProxyTests.cs`**

Create `tests/Shiro.Tests/Phase2WebView2AndBatchProxyTests.cs` (`[Collection("SerialGlobalState")]`) with 5 tests:
1. `WebView2Control_TryParseProxyForWebView_StripsCredentialsAndFlagsSocksAuth`:
   - `http://alice:s3cr3t@10.20.30.40:8080` -> `true`, `proxyServerArg == "http://10.20.30.40:8080"`, `username == "alice"`, `password == "s3cr3t"`, `isSocksWithAuth == false`.
   - `10.20.30.40:8080:bob:p@ss` -> `true`, `proxyServerArg == "http://10.20.30.40:8080"`, `username == "bob"`, `password == "p@ss"`, `isSocksWithAuth == false`.
   - `socks5://user:pw@10.20.30.40:1080` -> `true`, `proxyServerArg == "socks5://10.20.30.40:1080"`, `username == "user"`, `password == "pw"`, `isSocksWithAuth == true`.
   - `null` / `""` -> `false`, `proxyServerArg == null`.
2. `WebView2Control_BuildAdditionalBrowserArguments_AndDataFolder_IsolatePerProxyWithoutCredentialLeak`:
   - `BuildAdditionalBrowserArguments(null)` does NOT contain `--proxy-server` and contains `--disable-features=`.
   - `BuildAdditionalBrowserArguments("http://10.20.30.40:8080")` contains `--proxy-server=http://10.20.30.40:8080` and contains no `@`.
   - `ResolveEnvironmentDataFolder(null)` ends with `EmbeddedBrowser`.
   - `ResolveEnvironmentDataFolder("http://10.20.30.40:8080")` is inside `StoragePaths.BrowserSessionsDirectory`, starts with `EmbeddedBrowser_Proxy_`, is deterministic, differs from a second proxy, and does NOT contain `"10.20.30.40"`.
3. `WebView2Control_ConfigureProxy_ProtectsCredentialsInMemory`:
   - Calls `control.ConfigureProxy("http://proxyuser:VerySecretPass99@10.20.30.40:8080")`; reflects all instance string fields on `WebView2Control` and asserts none contain `"VerySecretPass99"` or `"proxyuser"`.
4. `Batch_GetAssetImage_DoesNotAttachRoblosecurityCookie`:
   - Sets `AppServices.SelectionState.LastValidAccount = new Account { SecurityToken = "_|WARNING:-DO-NOT-SHARE-THIS.--secret-cookie" }`, mocks `IRobloxApiClient.Thumbnails` capturing the outgoing `HttpRequestMessage`, invokes `Batch.GetAssetImage` (via `Batch.GetGameIcon` or `internal` test entry / reflection), asserts outgoing request has NO `Cookie` header containing `.ROBLOSECURITY`.
5. `Batch_ProcessPlaceAsync_AttachesHbaAndUsesAccountProxyHttpClient`:
   - Creates an `Account` with valid HBA key + cookie + proxy, hooks `RobloxAccountSessionClient.TestHttpClientFactory` capturing the outgoing `HttpRequestMessage` and returning mock `multiget-place-details` JSON, invokes `Batch.GetGameIcon(uniquePlaceId)` with `IRobloxApiClient.Thumbnails` stubbed; asserts the `multiget-place-details` request went through `TestHttpClientFactory` (not unproxied `SharedHttpClient`) and carried the `x-bound-auth-token` header.

- [ ] **Step 2: Run tests to verify they fail**

Run:
```powershell
dotnet build "Shiro.sln" --no-incremental -warnaserror
```
Expected: FAIL to compile because `ConfigureProxy`, `TryParseProxyForWebView`, `BuildAdditionalBrowserArguments`, and `ResolveEnvironmentDataFolder` do not exist yet, and `Batch` leaks cookie / skips proxy & HBA.

- [ ] **Step 3: Implement WebView2 per-account proxy support and `Batch.cs` proxy/HBA hardening**

1. `src/Shiro/AvaloniaUI/Controls/WebView2Control.cs` (keep `<= 485` lines):
   - Replace single `SharedEnvironmentTask` with `ConcurrentDictionary<string, Task<CoreWebView2Environment>> EnvironmentTasks` keyed by `"direct"` or SHA-256 hex digest of `proxyServerArg`.
   - Store `_proxyServerArg` (host/port only, no credentials), `_protectedProxyUser` (`byte[]?`), `_protectedProxyPass` (`byte[]?`), and `_proxyAuthAttemptCount` (reset on `NavigationStarting`, capped at 2 to prevent infinite 407 auth loops).
   - Implement `TryParseProxyForWebView`, `BuildAdditionalBrowserArguments`, `ResolveEnvironmentDataFolder`, and `ConfigureProxy(string? rawProxy)` (using `ProxyManagerService.ParseProxy`, `ProxyManagerService.FormatHostForUri`, and `InMemorySecretProtector.Protect`; logging a warning when `isSocksWithAuth == true` because Chromium does not support SOCKS5 auth natively).
   - In `GetOrCreateEnvironmentAsync(string? proxyServerArg)`: use `ResolveEnvironmentDataFolder(proxyServerArg)`, call `CleanupOrphanedProfiles(dataFolder)`, build `CoreWebView2EnvironmentOptions { AdditionalBrowserArguments = BuildAdditionalBrowserArguments(proxyServerArg) }`, and cache non-faulted tasks per environment key.
   - In `CreateIsolatedControllerCoreAsync`: ALWAYS resolve the environment for current `_proxyServerArg` (so `ResetToFreshProfileAsync` switches environments when batch FastLogin moves to an account with a different proxy!), attach `BasicAuthenticationRequested` on `controller.CoreWebView2` (unprotecting credentials on demand and setting `e.Response.UserName`/`e.Response.Password`, or `e.Cancel = true` if attempt count > 2), and detach handlers when closing/resetting old controllers.
   - In `CreateNativeControlCore`: if `_childHwnd == IntPtr.Zero`, invoke `CoreWebView2InitializationCompleted?.Invoke(this, new WebView2InitializedEventArgs(new InvalidOperationException($"CreateWindowEx failed (Win32 error {Marshal.GetLastWin32Error()}).")))` instead of silently hanging.
2. `src/Shiro/AvaloniaUI/Views/FastLoginDialog.axaml.cs:86-112` (+2 lines -> total `FastLoginDialog*.cs` `<= 1141 / 1150`):
   - Inside `LoadAccountAtIndex`, call `WvBrowser?.ConfigureProxy(ProxyBindingService.GetEffectiveProxyForAccount(account));`.
3. `src/Shiro/Services/Launch/Batch.cs:320-377` (NO `Task.Run`!):
   - In `GetAssetImage(long assetId)`: remove the 3 lines that attach `LastValidAccount?.SecurityToken` as `.ROBLOSECURITY` cookie (`thumbnails.roblox.com/v1/assets` is public).
   - In `ProcessPlaceAsync`: capture `Account? authAccount = AppServices.SelectionState.LastValidAccount;`, build `RestRequest` via `AppServices.AccountApiService.MakeRequest(authAccount, ...)` (when non-null), and execute:
     - If `authAccount != null && (RobloxAccountSessionClient.TestHttpClientFactory != null || !string.IsNullOrWhiteSpace(ProxyBindingService.GetEffectiveProxyForAccount(authAccount)))`: call `await AppServices.AccountApiService.ExecuteAccountRestAsync(authAccount, GamesAPI, "https://games.roblox.com/", request).ConfigureAwait(false)`.
     - Else: if `authAccount != null`, call `AppServices.AccountApiService.ApplySessionFingerprintAndHbaToRestRequest(authAccount, request, GamesAPI.BaseUrl ?? "https://games.roblox.com/");`, call `await GamesAPI.ExecuteAsync(request).ConfigureAwait(false)`, and call `AppServices.AccountApiService.TryUpdateRotatedCookie(authAccount, response);`.

- [ ] **Step 4: Run tests to verify pass**

Run:
```powershell
dotnet build "Shiro.sln" --no-incremental -warnaserror
dotnet test "tests/Shiro.Tests/Shiro.Tests.csproj" --no-build --filter "FullyQualifiedName~Phase2WebView2AndBatchProxyTests|FullyQualifiedName~BatchBehaviorTests|FullyQualifiedName~BatchAndDispatcherOptimizationTests|FullyQualifiedName~P0AuditRemediationTests|FullyQualifiedName~ArchitectureTests"
```
Expected: 100% PASS (including `Batch_ShouldNotUseTaskRun` and `NoClass_ShouldExceedLineBudget_AcrossPartials`).

- [ ] **Step 5: Commit**

```powershell
git add src/Shiro/AvaloniaUI/Controls/WebView2Control.cs src/Shiro/AvaloniaUI/Views/FastLoginDialog.axaml.cs src/Shiro/Services/Launch/Batch.cs tests/Shiro.Tests/Phase2WebView2AndBatchProxyTests.cs
git commit -m "fix(proxy): route WebView2 FastLogin through per-account proxy and enforce proxy/HBA in Batch lookups"
```

---

### Task 9: Isolate `ROBLOX_singletonMutex` Tests from Live Host Processes

**Files:**
- Modify: `src/Shiro/Services/Windows/RobloxSingletonService.Holder.cs:57-70, 240-245`
- Modify: `src/Shiro/Services/Windows/RobloxSingletonService.cs:74-78`
- Modify: `tests/Shiro.Tests/P0Win32AndLaunchRegressionTests.cs:261-298`
- Modify: `tests/Shiro.Tests/P1WindowsAndUiRegressionTests.cs:185-220`
- Test: `tests/Shiro.Tests/Phase2SingletonTestIsolationTests.cs`

**Interfaces:**
- Produces:
  - `RobloxSingletonService.NativeOpsOverrideForTest` (`internal static HolderNativeOps?`)
  - `RobloxSingletonService.CreateSuffixedNativeOpsForTest(string suffix)` (`internal static HolderNativeOps`)

- [ ] **Step 1: Write failing isolation test in `tests/Shiro.Tests/Phase2SingletonTestIsolationTests.cs`**

Create `tests/Shiro.Tests/Phase2SingletonTestIsolationTests.cs` (`[Collection("SerialGlobalState")]`):
- `CreateSuffixedNativeOpsForTest_IsolatesFromExternalHeldMutex_WhileCompilingRealSddl`: on Windows, holds a real named OS mutex `$"ROBLOX_singletonMutex_occupied_{Guid.NewGuid():N}"` on the test thread, configures `RobloxSingletonService.NativeOpsOverrideForTest = RobloxSingletonService.CreateSuffixedNativeOpsForTest($"_isolated_{Guid.NewGuid():N}")`, calls `RobloxSingletonService.EnsureMutex(true)`, and asserts `RobloxSingletonService.MutexHeld == true`, `RobloxSingletonService.AccessDenied == false`, and `RobloxSingletonService.StatusText == "Mutex Active • Multi-Instance Running"`; restores `NativeOpsOverrideForTest = null` and calls `RobloxSingletonService.Stop()` in `finally`.

- [ ] **Step 2: Run test to verify it fails**

Run:
```powershell
dotnet build "Shiro.sln" --no-incremental -warnaserror
```
Expected: FAIL to compile because `NativeOpsOverrideForTest` and `CreateSuffixedNativeOpsForTest` do not exist yet.

- [ ] **Step 3: Add `NativeOpsOverrideForTest` / `CreateSuffixedNativeOpsForTest` and isolate the 2 regression tests**

1. `src/Shiro/Services/Windows/RobloxSingletonService.Holder.cs`:
   - Add `internal static HolderNativeOps? NativeOpsOverrideForTest { get; set; }`.
   - Add `internal static HolderNativeOps CreateSuffixedNativeOpsForTest(string suffix)`: returns a `HolderNativeOps` that delegates `CreateMutex` to `CreateMutexNative(pSd, init, name + suffix)` and `CreateEvent` to `CreateEventNative(pSd, manual, initState, name + suffix)`, keeping real `WaitForSingleObject`, `ReleaseMutex`, and `CloseHandle`.
   - In `HoldHandles(HolderHandle holder, long generation)`: pass `NativeOpsOverrideForTest ?? DefaultNativeOps` to `HoldHandlesCore`.
2. `src/Shiro/Services/Windows/RobloxSingletonService.cs:74-78`:
   - In `ResetForTest(bool isEnabled = true)`: reset `NativeOpsOverrideForTest = null;` before `StopCore(...)`.
3. `tests/Shiro.Tests/P0Win32AndLaunchRegressionTests.cs:261-298`:
   - Set `RobloxSingletonService.NativeOpsOverrideForTest = RobloxSingletonService.CreateSuffixedNativeOpsForTest("_" + Guid.NewGuid().ToString("N"));` before `RobloxSingletonService.EnsureMutex(true)`, assert `Assert.True(RobloxSingletonService.MutexHeld);` unconditionally on Windows, and reset `NativeOpsOverrideForTest = null;` in `finally` before `RobloxSingletonService.Stop()`.
4. `tests/Shiro.Tests/P1WindowsAndUiRegressionTests.cs:185-220`:
   - Set `RobloxSingletonService.NativeOpsOverrideForTest = RobloxSingletonService.CreateSuffixedNativeOpsForTest("_" + Guid.NewGuid().ToString("N"));` before `RobloxSingletonService.Apply(true)`, assert `Assert.True(heldAfterReenable);` (no longer masked by `accessDeniedAfterReenable` contention!), and clear in `finally` (before `ResetForTest`).

- [ ] **Step 4: Run tests to verify pass**

Run:
```powershell
dotnet build "Shiro.sln" --no-incremental -warnaserror
dotnet test "tests/Shiro.Tests/Shiro.Tests.csproj" --no-build --filter "FullyQualifiedName~Phase2SingletonTestIsolationTests|FullyQualifiedName~P0Win32AndLaunchRegressionTests|FullyQualifiedName~P1WindowsAndUiRegressionTests"
```
Expected: 100% PASS regardless of whether a live `Shiro.exe` or `RobloxPlayerBeta.exe` is running on the machine.

- [ ] **Step 5: Commit**

```powershell
git add src/Shiro/Services/Windows/RobloxSingletonService.Holder.cs src/Shiro/Services/Windows/RobloxSingletonService.cs tests/Shiro.Tests/P0Win32AndLaunchRegressionTests.cs tests/Shiro.Tests/P1WindowsAndUiRegressionTests.cs tests/Shiro.Tests/Phase2SingletonTestIsolationTests.cs
git commit -m "test(windows): isolate ROBLOX_singletonMutex regression tests via suffixed HolderNativeOps"
```

---

### Final Release Verification Gate (After All 9 Tasks Complete)

- [ ] **Step 1: Full clean Release restore, `-warnaserror` build, and full test suite**

Run:
```powershell
dotnet restore "Shiro.sln" --runtime win-x64 /p:SelfContained=true --locked-mode
dotnet build "Shiro.sln" --configuration Release --no-restore --no-incremental -warnaserror
dotnet test "tests/Shiro.Tests/Shiro.Tests.csproj" --configuration Release --no-build --logger "console;verbosity=normal"
```
Expected:
- `Build succeeded. 0 Warning(s), 0 Error(s)`
- **100% of unit, integration, UI, and architecture tests PASS (`Failed: 0, Skipped: 0`)** — including the 2 newly isolated mutex tests even if a live `Shiro.exe` is running!

- [ ] **Step 2: Run Coverage Gate (matching CI release workflow)**

Run:
```powershell
dotnet test "tests/Shiro.Tests/Shiro.Tests.csproj" --configuration Release --no-build --collect:"XPlat Code Coverage" --settings "tests/coverage.runsettings" --results-directory "coverage" --logger "console;verbosity=minimal"
./scripts/Check-Coverage.ps1 -CoverageDir "coverage" -MinOverall 75.0 -MinBranch 60.0 -MinCore 92.0
```
Expected: Exit code `0` (`Overall >= 75%`, `Branch >= 60%`, `Core >= 92%`). Clean up temporary `coverage/` folder after verifying.

- [ ] **Step 3: Review branch diff & commit history**

Run:
```powershell
git status --short
git log --oneline main..HEAD
```
Expected: Clean working tree, 9 focused TDD commits ready for `superpowers:finishing-a-development-branch`.