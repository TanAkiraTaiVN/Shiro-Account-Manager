> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

# P0 Win32 Interop, Multi-Instance & Launch Pipeline Fixes

**Goal:** Eliminate P0 Win32 interop bugs (`JOB_OBJECT_MSG_EXIT_PROCESS`, `ProcessMemoryPriority`, icon fallback, singleton SACL/UAF, `AntiAfk` key-up sign extension), `AutoReconnectService` settings/retry-reset/ordering defects, and `CookieGuard`/`LaunchCoordinator` cancellation and cookie-restore races.

**Architecture:** Keep each Win32 and launch fix isolated cleanly inside its owning service partial while respecting the 485-line source file cap (`AutoReconnectService.Monitor.cs` and `RobloxSingletonService.Holder.cs` extractions). Preserve headless test seams (`DispatchCompletionMessage`, `SetProcessMemoryPriorityNative`, `TestJoinServerOverride`, `TestReconnectDelayMsOverride`) so every P0 fix runs deterministically under `xUnit` without live Roblox clients.

**Tech Stack:** .NET 10 (`net10.0-windows10.0.19041.0`, `win-x64`), C# 13, P/Invoke `[LibraryImport]`, `System.Threading`, `xUnit`, `NSubstitute`.

**Spec:** System Audit Report (`C:/Users/lenovo/.gemini/antigravity/brain/d3d9db7e-c20c-4d8a-b056-391721b18014/system-audit-report.md`) - Section 2 (P0.1, P0.2) & Section 3.1/3.4.

**Global Constraints:**
- `TreatWarningsAsErrors=true`, `<Nullable>enable</Nullable>`, `[LibraryImport]` only (no `[DllImport]`).
- Source files `<= 485` lines (`RobloxSingletonService.cs` is currently 466/485, `AntiAfkService.cs` is 469/485, `AutoReconnectService.cs` is 480/485, `RobloxDirectLauncher.CookieGuard.cs` is 451/485); test files `<= 550` lines.
- All C# comments, docstrings, and log messages MUST be strict ASCII English (`ArchitectureTests.Hygiene.cs`).
- No empty catch blocks, no untyped catch blocks, and no `{` or `}` inline string literals inside `[Fact]`/`[Theory]` test method bodies (hoist constants/regexes to `private static readonly`).
- Preserve branch coverage floor in `tests/coverage-floors.json`: `Services/Launch/AutoReconnectService.cs >= 70%`.
- Stage only explicit files per commit (never `git add -A`, because unrelated workspace changes already exist).

**Review Focus:**
- **Task 1:** Verify Windows SDK constant `JOB_OBJECT_MSG_EXIT_PROCESS = 7` (not `4`), `RobloxAudioService.Wasapi` offloads `InvalidateCachedAudioSession` off the Job Object IOCP callback thread, `ProcessMemoryPriority = 0`, `SoftRelaxWorkingSetNative` behavior when aggressive trim is disabled vs enabled, icon fallback `(IntPtr)32512` -> `(IntPtr)1`, and `uint` shift `(1u << 31)` in `AntiAfkService.cs`.
- **Task 2:** Verify `RobloxSingletonService` SACL includes Low Integrity (`S:(ML;;NW;;;LW)`), `_holderStop`/`_holderReady` are disposed only after the worker thread exits safely, and `HoldHandles` does not hold `hMutex` across `stop.Wait()` when `hEvent == IntPtr.Zero`.
- **Task 3:** Verify `AutoReconnectService` reads/writes `AppSettingsKeys.General.AutoReconnect` (`"AutoReconnect"`) with backward read migration for `"EnableAutoReconnect"`, `RetryCount` never resets while `ProcessId <= 0 || NeedsRetry`, and `ObservedExitCode` is recorded before `ObservedProcessExited = true`.
- **Task 4:** Verify `RobloxDirectLauncher.CookieGuard` / `LaunchCoordinator` do not cancel active backup restore timers spuriously, retain `noOrigSentinelPath` on failed file deletion, zero `plainBytes`, wait to fire `SignalNextLaunchAccount()` until after `executeLaunch` settles, and decouple 20-35s post-launch monitors from batch-scoped `linkedCancellation`.

---

### Task 1: Fix Win32 Constants (`JobObject`, `MemoryPriority`, `WorkingSet`, `Backdrop`, `AntiAfk`)

**Context:**
- `src/Shiro/Services/Windows/RobloxJobObjectService.cs:35` defines `JOB_OBJECT_MSG_EXIT_PROCESS = 4` instead of `7` (`4` is `JOB_OBJECT_MSG_ACTIVE_PROCESS_ZERO`), breaking clean process-exit event delivery from Win32 Job Objects. (`tests/Shiro.Tests/JobObjectCompletionPortTests.cs:26-27` currently tests literal `4` and must be updated alongside the constant fix.) Once fixed, `RobloxAudioService.Wasapi.cs:25` (`ProcessExitedFromJob += static (pid, _) => InvalidateCachedAudioSession(pid)`) will fire on every normal exit and would block the Job Object IOCP thread on `done.Wait()` inside `RunOnWasapiMtaThread` unless dispatched off-thread like `RobloxSingletonService.OnJobProcessExited`.
- `src/Shiro/Services/Windows/MemoryOptimizer.ResourceProfile.cs:11` defines `ProcessMemoryPriority = 39` instead of `0` (`PROCESS_INFORMATION_CLASS.ProcessMemoryPriority`), causing `SetProcessInformation` to fail with `ERROR_INVALID_PARAMETER`, while `SoftRelaxWorkingSetNative` (`lines 86-97`) calls `SetProcessWorkingSetSizeEx(..., -1, -1, ...)` which trims working set even when `IsAggressiveWorkingSetTrimEnabled()` is `false`.
- `src/Shiro/Services/Windows/WindowBackdropService.cs:95-96` only loads icon resource `(IntPtr)1`, whereas MSBuild `<ApplicationIcon>` embeds the main icon under `IDI_APPLICATION` (`32512`).
- `src/Shiro/Services/Windows/AntiAfkService.cs:257` uses signed `(1 << 31)` (`int.MinValue`) when casting to `(IntPtr)` and `lines 284-286` return `false` for foreground pulse eligibility even when foreground.

**Files:**
- Modify: `src/Shiro/Services/Windows/RobloxJobObjectService.cs`
- Modify: `src/Shiro/Services/Windows/RobloxAudioService.Wasapi.cs`
- Modify: `src/Shiro/Services/Windows/MemoryOptimizer.ResourceProfile.cs`
- Modify: `src/Shiro/Services/Windows/WindowBackdropService.cs`
- Modify: `src/Shiro/Services/Windows/AntiAfkService.cs`
- Modify: `tests/Shiro.Tests/JobObjectCompletionPortTests.cs`
- Create: `tests/Shiro.Tests/P0Win32AndLaunchRegressionTests.cs`

**Interfaces:**
- **Modified:** `RobloxJobObjectService.DispatchCompletionMessage(uint messageId, nuint processId)` (`7` = normal exit, `8` = abnormal exit).
- **Modified:** `MemoryOptimizer.SetProcessMemoryPriorityNative(IntPtr hProcess, bool isForeground)` and `MemoryOptimizer.SoftRelaxWorkingSetNative(IntPtr hProcess)` (`src/Shiro/Services/Windows/MemoryOptimizer.ResourceProfile.cs`).
- **Added (internal test seams):** `internal static nuint BuildKeyUpLParam(uint scanCode)` in `AntiAfkService` and `internal static IntPtr ResolvePreferredIconResourceId(Func<IntPtr, IntPtr> loader)` (or overload on icon loader) in `WindowBackdropService`.

**Steps:**
- [ ] **Step 1: Write failing test(s) (TDD Red)**
  - In `tests/Shiro.Tests/JobObjectCompletionPortTests.cs`: update the normal-exit message assertion from `4` to `7` (`JOB_OBJECT_MSG_EXIT_PROCESS = 7`) and add an assertion that message `4` (`JOB_OBJECT_MSG_ACTIVE_PROCESS_ZERO`) returns `false` and does not raise `ProcessExitedFromJob`.
  - Create `tests/Shiro.Tests/P0Win32AndLaunchRegressionTests.cs` (`[Collection("StaticSeams")]`):
    - Assert `MemoryOptimizer.SetProcessMemoryPriorityNative(currentProcHandle, true)` returns `true` on Windows (and `ProcessMemoryPriorityConstant == 0` via internal constant or observable Win32 call succeeding instead of `ERROR_INVALID_PARAMETER`).
    - Assert `SoftRelaxWorkingSetNative` does NOT pass `(-1, -1)` trim when aggressive working-set trim is disabled (add internal seam/helper `ShouldTrimWorkingSetOnSoftRelax(bool aggressiveEnabled)` or verify no trim call).
    - Assert `WindowBackdropService` icon helper prefers `(IntPtr)32512` first and falls back to `(IntPtr)1` if `32512` returns `IntPtr.Zero`.
    - Assert `AntiAfkService.BuildKeyUpLParam(scanCode)` produces `(nuint)(1u | (scanCode << 16) | (1u << 30) | (1u << 31))` (`0xC0...`) cleanly on 64-bit without signed sign-extension of upper 32 bits, and verify `AntiAfkService` pulse evaluation allows foreground keep-alive when configured for foreground pulse.
    - Assert `RobloxJobObjectService.DispatchCompletionMessage(7, pid)` does NOT block synchronously on `RobloxAudioService`'s MTA dispatcher waiter inside the calling IOCP thread.
- [ ] **Step 2: Run targeted tests to verify expected Red failure**
  Run:
  ```powershell
  dotnet test Shiro.sln --no-restore --nologo --filter "FullyQualifiedName~JobObjectCompletionPortTests|FullyQualifiedName~P0Win32AndLaunchRegressionTests|FullyQualifiedName~PerformanceAndStaleJobIdRegressionTests|FullyQualifiedName~MemoryOptimizerBehaviorTests"
  ```
  Confirm compile/assertion failures because `JOB_OBJECT_MSG_EXIT_PROCESS` is still `4` and `BuildKeyUpLParam` / `ResolvePreferredIconResourceId` / `ProcessMemoryPriority = 0` are not yet fixed.
- [ ] **Step 3: Implement minimal fixes**
  - Edit `src/Shiro/Services/Windows/RobloxJobObjectService.cs:35`: change `JOB_OBJECT_MSG_EXIT_PROCESS = 4` to `JOB_OBJECT_MSG_EXIT_PROCESS = 7`.
  - Edit `src/Shiro/Services/Windows/RobloxAudioService.Wasapi.cs:25`: offload `InvalidateCachedAudioSession(pid)` onto `ThreadPool.QueueUserWorkItem` (same non-blocking dispatch pattern as `RobloxSingletonService.OnJobProcessExited`) so the IOCP completion thread never stalls on `done.Wait()`.
  - Edit `src/Shiro/Services/Windows/MemoryOptimizer.ResourceProfile.cs:11`: change `ProcessMemoryPriority = 39` to `ProcessMemoryPriority = 0`. In `SoftRelaxWorkingSetNative` (`lines 86-97`), gate the `(-1, -1)` trim on `IsAggressiveWorkingSetTrimEnabled()` (or apply `QUOTA_LIMITS_HARDWS_MIN_DISABLE` without empty-working-set trim using current min/max or explicit trim guard).
  - Edit `src/Shiro/Services/Windows/WindowBackdropService.cs:95-96`: try `(IntPtr)32512` first via `LoadImage`, falling back to `(IntPtr)1` when `IntPtr.Zero`.
  - Edit `src/Shiro/Services/Windows/AntiAfkService.cs`: use unsigned `BuildKeyUpLParam(uint scanCode)` (`unchecked((IntPtr)(nint)(nuint)(1u | (scanCode << 16) | (1u << 30) | (1u << 31)))`), fix foreground predicate (`lines 284-286`), and keep `AntiAfkService.cs` `<= 485` lines (extract keystroke helper into a small `AntiAfkService.Helpers.cs` partial if adding the seam pushes line count above 475).
- [ ] **Step 4: Run targeted tests & ArchitectureTests to verify Green pass**
  Run:
  ```powershell
  dotnet test Shiro.sln --no-restore --nologo --filter "FullyQualifiedName~JobObjectCompletionPortTests|FullyQualifiedName~P0Win32AndLaunchRegressionTests|FullyQualifiedName~PerformanceAndStaleJobIdRegressionTests|FullyQualifiedName~MemoryOptimizerBehaviorTests|FullyQualifiedName~AntiAfk|FullyQualifiedName~ArchitectureTests"
  ```
  Verify `AntiAfkService.cs` is `<= 485` lines, `0` warnings, `0` failed tests.
- [ ] **Step 5: Commit**
  ```powershell
  git add src/Shiro/Services/Windows/RobloxJobObjectService.cs src/Shiro/Services/Windows/RobloxAudioService.Wasapi.cs src/Shiro/Services/Windows/MemoryOptimizer.ResourceProfile.cs src/Shiro/Services/Windows/WindowBackdropService.cs src/Shiro/Services/Windows/AntiAfkService.cs tests/Shiro.Tests/JobObjectCompletionPortTests.cs tests/Shiro.Tests/P0Win32AndLaunchRegressionTests.cs
  git commit -m "fix(win32): correct job exit const, memory priority, icon ID, and key-up lParam"
  ```
  *(Also stage `AntiAfkService.Helpers.cs` if created in Step 3.)*

---

### Task 2: Fix `RobloxSingletonService` SACL, Wait-Handle Disposal Race & Zero-EventMutex Hold

**Context:**
- `src/Shiro/Services/Windows/RobloxSingletonService.cs:156` uses `const string RestrictedInteractiveSddl = "D:P(A;;GA;;;SY)(A;;GA;;;BA)(A;;GA;;;IU)";` without a Mandatory Integrity Label (`S:(ML;;NW;;;LW)`). Without the SACL, the kernel assigns Medium integrity to objects created by Shiro, blocking Low-integrity Roblox processes from opening `ROBLOX_singletonMutex` / `ROBLOX_singletonEvent`.
- `lines 105-111` join `oldThread` for 2s outside `_gate`, then dispose `oldStop`/`oldReady` unconditionally even if `HoldHandles` (`lines 251, 254, 271`) is still touching those `ManualResetEventSlim` instances.
- `lines 225-271` hold `hMutex` and still enter `stop.Wait()` even when `CreateEvent` returned `IntPtr.Zero` (`hEvent == IntPtr.Zero`).
- `RobloxSingletonService.cs` is currently 466/485 lines; extract `HoldHandles` / security descriptor helpers into `RobloxSingletonService.Holder.cs` so both partial files remain well under 300 lines.

**Files:**
- Modify: `src/Shiro/Services/Windows/RobloxSingletonService.cs`
- Create: `src/Shiro/Services/Windows/RobloxSingletonService.Holder.cs`
- Modify: `tests/Shiro.Tests/P0Win32AndLaunchRegressionTests.cs`

**Interfaces:**
- **Internal / Unchanged public API:** `RobloxSingletonService.Apply(bool)`, `Stop()`, `MutexHeld`, `StatusText`.
- **Internal test seams:** `internal const string RestrictedInteractiveSddl` (with `S:(ML;;NW;;;LW)`) and `internal static bool ShouldAbortHoldOnZeroEvent(IntPtr hEvent)` / extracted `Holder` helpers in `RobloxSingletonService.Holder.cs`.

**Steps:**
- [ ] **Step 1: Write failing test(s) (TDD Red)**
  - In `tests/Shiro.Tests/P0Win32AndLaunchRegressionTests.cs`:
    - Assert `RobloxSingletonService.RestrictedInteractiveSddl` contains `"S:(ML;;NW;;;LW)"` alongside `"D:P(A;;GA;;;SY)(A;;GA;;;BA)(A;;GA;;;IU)"`.
    - Add a concurrency stress test calling `EnsureMutex(true)` / `EnsureMutex(false)` repeatedly in rapid alternation (or exercising the extracted holder-lifecycle coordinator) to assert zero `ObjectDisposedException` when `oldThread` teardown races with `HoldHandles`.
    - Assert that when event creation fails (`hEvent == IntPtr.Zero`), `HoldHandles` releases/closes `hMutex` immediately and exits without entering `stop.Wait()` with `_handlesHeld == false`.
- [ ] **Step 2: Run targeted tests to verify expected Red failure**
  Run:
  ```powershell
  dotnet test Shiro.sln --no-restore --nologo --filter "FullyQualifiedName~P0Win32AndLaunchRegressionTests|FullyQualifiedName~MultiInstanceAndReconnectTests"
  ```
  Confirm Red failure on `RestrictedInteractiveSddl` missing `S:(ML;;NW;;;LW)` and missing holder-abort/lifecycle test seam.
- [ ] **Step 3: Implement minimal fix & partial split**
  - Create `src/Shiro/Services/Windows/RobloxSingletonService.Holder.cs` (`#nullable enable`, `public static partial class RobloxSingletonService`):
    - Move `RestrictedInteractiveSddl` (updated to `"D:P(A;;GA;;;SY)(A;;GA;;;BA)(A;;GA;;;IU)S:(ML;;NW;;;LW)"`), SDDL security descriptor builder, and `HoldHandles` / cleanup logic from `RobloxSingletonService.cs`.
    - Ensure `HoldHandles` tracks active reference / disposes `stop` and `ready` cleanly ONLY from a deterministic single owner after `HoldHandles` `finally` completes (or ref-counted wrapper / `Interlocked` ownership swap where `oldThread` does not suffer UAF if the 2s `Join` times out!).
    - In `HoldHandles`: when `hEvent == IntPtr.Zero`, log error, do NOT call `stop.Wait()`, release `mutexOwned`/`hMutex` cleanly in `finally`, and set `ready?.Set()`.
  - Trim `RobloxSingletonService.cs` cleanly after moving holder code to `.Holder.cs`.
- [ ] **Step 4: Run targeted tests & ArchitectureTests to verify Green pass**
  Run:
  ```powershell
  dotnet test Shiro.sln --no-restore --nologo --filter "FullyQualifiedName~P0Win32AndLaunchRegressionTests|FullyQualifiedName~MultiInstanceAndReconnectTests|FullyQualifiedName~ArchitectureTests"
  ```
  Verify `RobloxSingletonService.cs` and `RobloxSingletonService.Holder.cs` are both `<= 485` lines and all tests pass.
- [ ] **Step 5: Commit**
  ```powershell
  git add src/Shiro/Services/Windows/RobloxSingletonService.cs src/Shiro/Services/Windows/RobloxSingletonService.Holder.cs tests/Shiro.Tests/P0Win32AndLaunchRegressionTests.cs
  git commit -m "fix(multi-instance): add Low-Integrity SACL, prevent wait-handle UAF, and bail on null event"
  ```

---

### Task 3: Fix `AutoReconnectService` Settings Key, `RetryCount` Reset Guard & Exit Code Ordering

**Context:**
- `AutoReconnectService.cs:39,43` reads/writes `"EnableAutoReconnect"` while `SettingsViewModel.Advanced.cs:87,131` reads/writes `AppSettingsKeys.General.AutoReconnect` (`"AutoReconnect"`), so toggling Auto-Reconnect in Settings does not persist across restarts! (Note: existing tests `AccountHbaAndAutoReconnectExtendedTests.cs:22,28,166` and `AuditRuntimeRegressionTests.cs:70,107` write `"EnableAutoReconnect"` directly; unify cleanly on `AppSettingsKeys.General.AutoReconnect` with backward-compatible fallback read when `Exists("EnableAutoReconnect") && !Exists(AppSettingsKeys.General.AutoReconnect)` AND update those tests to verify canonical `AppSettingsKeys.General.AutoReconnect` plus legacy key fallback!)
- `AutoReconnectService.cs:269-278` resets `session.RetryCount = 0` when `(now - session.LaunchTimeUtc) >= StableResetWindow && !session.LastDisconnectUtc.HasValue`, even when `session.ProcessId <= 0 && session.NeedsRetry == true` (a session still waiting to retry or failed launch!).
- `AutoReconnectService.cs:218-220` sets `session.ObservedProcessExited = true` BEFORE populating `session.ObservedExitCode`, creating a torn read in `CheckSessionsCallback` where a clean exit (`0`) is misread as abnormal (`null`).
- `AutoReconnectService.cs` is 480/485 lines and has a `>= 70%` branch coverage floor in `tests/coverage-floors.json`! Extract helper/monitor polling (`CheckSessionsCallback`, `ObserveProcessExitImmediateAsync`, `ScheduleBackoffReconnectAsync`) carefully OR split `AutoReconnectService.Monitor.cs` while keeping `AutoReconnectService.cs >= 70%` branch coverage! (Wait: `ArchitectureTests.Ratchets.cs:71` enforces `"Services/Launch/AutoReconnectService.cs"` branch floor `>= 70%`, so both `AutoReconnectService.cs` and any new `.Monitor.cs` must be well covered!)

**Files:**
- Modify: `src/Shiro/Services/Launch/AutoReconnectService.cs`
- Modify: `src/Shiro/Services/Launch/AutoReconnectService.Models.cs`
- Create (if needed for 485-line budget): `src/Shiro/Services/Launch/AutoReconnectService.Monitor.cs`
- Modify: `tests/Shiro.Tests/AccountHbaAndAutoReconnectExtendedTests.cs`
- Modify: `tests/Shiro.Tests/AuditRuntimeRegressionTests.cs`
- Modify: `tests/Shiro.Tests/P0Win32AndLaunchRegressionTests.cs`

**Interfaces:**
- **Modified:** `AutoReconnectService.IsEnabled` (`get`/`set` backed by `AppSettingsKeys.General.AutoReconnect`, falling back on read to legacy `"EnableAutoReconnect"` only when canonical key is absent).
- **Added / Extracted (internal):** `internal static bool ShouldResetRetryCount(ReconnectSession session, DateTime nowUtc, TimeSpan stableResetWindow)` enforcing `session.IsActive && !session.IsSuppressed && session.ProcessId > 0 && !session.NeedsRetry && !session.ObservedProcessExited && !session.LastDisconnectUtc.HasValue && (nowUtc - session.LaunchTimeUtc) >= stableResetWindow`.

**Steps:**
- [ ] **Step 1: Write failing test(s) (TDD Red)**
  - In `tests/Shiro.Tests/P0Win32AndLaunchRegressionTests.cs`:
    - Test `IsEnabled` setter writes `AppSettingsKeys.General.AutoReconnect` (`"AutoReconnect"`) and getter reads it consistently with `SettingsViewModel.Advanced`, plus falls back cleanly if only legacy `"EnableAutoReconnect"` exists in INI.
    - Test `ShouldResetRetryCount` returns `false` when `ProcessId == 0` or `NeedsRetry == true` or `ObservedProcessExited == true` even when `(now - LaunchTimeUtc) >= StableResetWindow`, and returns `true` only for healthy running sessions (`ProcessId > 0 && !NeedsRetry && !ObservedProcessExited && !LastDisconnectUtc.HasValue`).
    - Test `ObserveProcessExit` helper/ordering ensures `ObservedExitCode` is written and published via `Interlocked` / lock or pre-assignment BEFORE `ObservedProcessExited = true`.
  - Update `tests/Shiro.Tests/AccountHbaAndAutoReconnectExtendedTests.cs:22,28,166` and `tests/Shiro.Tests/AuditRuntimeRegressionTests.cs:70,107` to set/assert `AppSettingsKeys.General.AutoReconnect` (`"AutoReconnect"`).
- [ ] **Step 2: Run targeted tests to verify expected Red failure**
  Run:
  ```powershell
  dotnet test Shiro.sln --no-restore --nologo --filter "FullyQualifiedName~P0Win32AndLaunchRegressionTests|FullyQualifiedName~AccountHbaAndAutoReconnectExtendedTests|FullyQualifiedName~AuditRuntimeRegressionTests|FullyQualifiedName~AutoReconnect"
  ```
  Confirm assertions fail on `"AutoReconnect"` key sync and `RetryCount` reset on failed-retry session.
- [ ] **Step 3: Implement minimal fixes**
  - Update `AutoReconnectService.cs:39,43`: use `AppSettingsKeys.General.AutoReconnect` (on `get`, check `AppServices.GeneralSettings.Exists(AppSettingsKeys.General.AutoReconnect) ? ... : AppServices.GeneralSettings.Get<bool>("EnableAutoReconnect", false)`; on `set`, write `AppSettingsKeys.General.AutoReconnect`).
  - Update `ObserveProcessExitImmediateAsync` (`lines 218-220`) and `OnJobProcessExited` (`AutoReconnectService.Models.cs:247-267`): resolve `exitCode` BEFORE assigning `session.ObservedExitCode = exitCode; Interlocked.MemoryBarrier(); session.ObservedProcessExited = true;` (under `lock (_lock)` or barriered assignment).
  - Add `ShouldResetRetryCount` and use it in `CheckSessionsCallback` (`lines 269-278`).
  - If `AutoReconnectService.cs` exceeds 475 lines, move `OnMaxRetriesExceeded`, `NotifyDisconnected`, `NotifyConnected`, or `CheckSessionsCallback` helpers into `AutoReconnectService.Monitor.cs` so `AutoReconnectService.cs <= 485` lines while keeping all branches in `AutoReconnectService.cs` exercised above `70%`.
- [ ] **Step 4: Run AutoReconnect suite & ArchitectureTests + check branch coverage**
  Run:
  ```powershell
  dotnet test Shiro.sln --no-restore --nologo --filter "FullyQualifiedName~AutoReconnect|FullyQualifiedName~P0Win32AndLaunchRegressionTests|FullyQualifiedName~AccountHbaAndAutoReconnectExtendedTests|FullyQualifiedName~AuditRuntimeRegressionTests|FullyQualifiedName~ArchitectureTests"
  ```
  Also run coverage script for `AutoReconnectService` or verify full line counts (`<= 485`).
- [ ] **Step 5: Commit**
  ```powershell
  git add src/Shiro/Services/Launch/AutoReconnectService.cs src/Shiro/Services/Launch/AutoReconnectService.Models.cs tests/Shiro.Tests/AccountHbaAndAutoReconnectExtendedTests.cs tests/Shiro.Tests/AuditRuntimeRegressionTests.cs tests/Shiro.Tests/P0Win32AndLaunchRegressionTests.cs
  git commit -m "fix(launch): unify AutoReconnect setting key, guard retry reset, and order exit code"
  ```
  *(Also stage `AutoReconnectService.Monitor.cs` if created.)*

---

### Task 4: Fix `CookieGuard` Restore Races, DPAPI Plaintext Zeroing & Launch Monitor Cancellation

**Context:**
- `RobloxDirectLauncher.CookieGuard.cs:238,287,293`: `ScheduleOriginalCookieRestore` and `SchedulePostSpawnCookieRestore` call `ScheduleOriginalCookieRestore(5_000)` when `IsAnyRobloxRunning()` returns `true`, which cancels/disposes the active `_restoreCts` timer! Meanwhile `LaunchCoordinator.cs:239-240` calls `SchedulePostSpawnCookieRestore(lastSpawnedPid)` unconditionally after Pass 1 even when no cookie was injected (`_hasPendingBackupRestore == false`, e.g. ticket launches), and `RestoreOriginalCookies` (`line 413`) deletes `noOrigSentinelPath` even if `File.Delete(cookiesDatPath)` throws! Also `plainBytes` (`line 171`) is not zeroed in a `finally` block.
- `AccountLaunchService.cs:387,399` & `.Home.cs:177` invoke `AppServices.HostUiBridge.SignalNextLaunchAccount()` synchronously BEFORE `executeLaunch` returns, waking the next queued item prematurely (`LaunchCoordinator` waits on `nextSignal`), and `LaunchCoordinator.cs:254` disposes `linkedCancellation` as soon as the batch finishes, aborting 20-35s post-launch monitors (`MonitorNativeAuthRateLimitAsync`, `AdjustWindowPositionAsync`) that received `cancellationToken`.

**Files:**
- Modify: `src/Shiro/Services/Launch/RobloxDirectLauncher.CookieGuard.cs`
- Modify: `src/Shiro/Services/Launch/LaunchCoordinator.cs`
- Modify: `src/Shiro/Services/Launch/AccountLaunchService.cs`
- Modify: `src/Shiro/Services/Launch/AccountLaunchService.Home.cs`
- Modify: `tests/Shiro.Tests/RobloxDirectLauncherCookieGuardTests.cs`
- Modify: `tests/Shiro.Tests/P0Win32AndLaunchRegressionTests.cs`

**Interfaces:**
- **Modified:** `RobloxDirectLauncher` internal `HasPendingCookieBackup` / `TryConsumePostSpawnRestorePending` guard, and `RestoreOriginalCookiesCore` (keeps sentinel if cookie file deletion throws).
- **Modified:** `AccountLaunchService` passes an app-shutdown-linked / non-batch-disposed `CancellationToken` (`CancellationToken.None` or independent monitor token cancelled only on app shutdown) to `MonitorNativeAuthRateLimitAsync` and `AdjustWindowPositionAsync`, and signals `SignalNextLaunchAccount()` via `LaunchCoordinator` after `executeLaunch` settles (check existing callers of `SignalNextLaunchAccount` so sequential flow remains backward-compatible!).

**Steps:**
- [ ] **Step 1: Write failing test(s) (TDD Red)**
  - In `tests/Shiro.Tests/RobloxDirectLauncherCookieGuardTests.cs` (or `P0Win32AndLaunchRegressionTests.cs`):
    - Assert `SchedulePostSpawnCookieRestore` when no backup/injection is pending is a no-op and does NOT disturb an active `_restoreCts`.
    - Assert re-scheduling restore while Roblox is running defers cleanly without immediately cancelling/losing an active scheduled timer unnecessarily OR extends existing timer safely, and `RestoreOriginalCookies` keeps `noOrigSentinelPath` intact when `cookiesDatPath` cannot be deleted (e.g. read-only / locked file / mock IO failure).
  - In `tests/Shiro.Tests/P0Win32AndLaunchRegressionTests.cs`:
    - Test `LaunchCoordinator.LaunchBatchAsync` completing quickly while `MonitorNativeAuthRateLimitAsync` / `AdjustWindowPositionAsync` (or a test monitor hook capturing the token passed to background launch tasks) receives a `CancellationToken` that is NOT canceled when `LaunchCoordinator` disposes its batch `linkedCancellation`!
    - Test launch batch sequencing asserts `SignalNextLaunchAccount()` is only invoked after `executeLaunch` settles (when launched via `AccountLaunchService` + `LaunchCoordinator`).
- [ ] **Step 2: Run targeted tests to verify expected Red failure**
  Run:
  ```powershell
  dotnet test Shiro.sln --no-restore --nologo --filter "FullyQualifiedName~RobloxDirectLauncherCookieGuardTests|FullyQualifiedName~P0Win32AndLaunchRegressionTests|FullyQualifiedName~LaunchCoordinatorTests|FullyQualifiedName~P1LaunchRegressionTests|FullyQualifiedName~AccountLaunchService"
  ```
- [ ] **Step 3: Implement minimal fixes**
  - Edit `src/Shiro/Services/Launch/RobloxDirectLauncher.CookieGuard.cs`:
    - Wrap `plainBytes` (`line 171`) in `try { ... } finally { CryptographicOperations.ZeroMemory(plainBytes); }`.
    - Track `_hasPendingInjectionRestore` (or check existence of backup/sentinel files cleanly) so `SchedulePostSpawnCookieRestore(pid)` does nothing unless a backup/sentinel injection actually occurred. Avoid cancelling/replacing the timer when another retry is already pending inside the same active wait, or reschedule safely outside lock without self-cancelling the running callback's CTS prematurely.
    - In `RestoreOriginalCookies`: perform `File.Delete(cookiesDatPath)` cleanly first and only delete `noOrigSentinelPath` if `cookiesDatPath` was actually removed or absent!
  - Edit `src/Shiro/Services/Launch/LaunchCoordinator.cs`:
    - Only call `RobloxDirectLauncher.SchedulePostSpawnCookieRestore(lastSpawnedPid)` (`line 240`) when cookie injection actually occurred.
    - Keep `AccountLaunchService` -> `LaunchCoordinator` signaling cleanly ordered: signal `SignalNextLaunchAccount()` AFTER `executeLaunch` outcome returns (preserving existing `AccountLaunchServiceBehaviorTests` / `LaunchCoordinatorTests` behavior if direct callers call `AccountLaunchService` standalone — verify with a parameter or `LaunchCoordinator` completion hook!).
    - Edit `AccountLaunchService.cs` & `AccountLaunchService.Home.cs`: pass `CancellationToken.None` (or `AppServices.AppShutdownToken` if exposed, defaulting to `CancellationToken.None`) to `MonitorNativeAuthRateLimitAsync` and `AdjustWindowPositionAsync` so disposing batch `linkedCancellation` at `LaunchCoordinator.cs:254` never kills 20-35s post-launch monitors.
- [ ] **Step 4: Run full Launch test suite & ArchitectureTests to verify Green pass**
  Run:
  ```powershell
  dotnet test Shiro.sln --no-restore --nologo --filter "FullyQualifiedName~RobloxDirectLauncher|FullyQualifiedName~LaunchCoordinator|FullyQualifiedName~AccountLaunchService|FullyQualifiedName~P1LaunchRegressionTests|FullyQualifiedName~P0Win32AndLaunchRegressionTests|FullyQualifiedName~ArchitectureTests"
  ```
  Verify `RobloxDirectLauncher.CookieGuard.cs`, `AccountLaunchService.cs`, and `LaunchCoordinator.cs` are `<= 485` lines.
- [ ] **Step 5: Commit**
  ```powershell
  git add src/Shiro/Services/Launch/RobloxDirectLauncher.CookieGuard.cs src/Shiro/Services/Launch/LaunchCoordinator.cs src/Shiro/Services/Launch/AccountLaunchService.cs src/Shiro/Services/Launch/AccountLaunchService.Home.cs tests/Shiro.Tests/RobloxDirectLauncherCookieGuardTests.cs tests/Shiro.Tests/P0Win32AndLaunchRegressionTests.cs
  git commit -m "fix(launch): fix CookieGuard restore races, zero plaintext bytes, and decouple post-launch monitors"
  ```

---

## Self-Review & Execution Gate
- All 4 tasks target separate files/responsibilities and each ends with a runnable test verification + single commit.
- Check line counts against `ArchitectureTests.Budgets.cs` (`<= 485` src, `<= 550` tests) and `tests/coverage-floors.json` (`AutoReconnectService.cs >= 70%`) before committing each task.
