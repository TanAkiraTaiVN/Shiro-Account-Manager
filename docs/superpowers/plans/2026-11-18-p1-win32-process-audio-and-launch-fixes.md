# P1 Win32 Interop, Audio, Watcher & Launch Pipeline Fixes Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.
> **Pre-execution baseline note:** Live working tree currently holds 96 modified files + 8 untracked helpers/tests passing 2,189/2,189 tests cleanly (`dotnet build -c Release -warnaserror`, `Check-Coverage.ps1` Core 93.88%). Commit or snapshot that baseline onto `main` before executing Wave P1.

**Goal:** Eliminate all remaining P1 Win32 solo-client priority leaks in `OptimizeRobloxClients`, WASAPI MTA hangs & multi-session audio gaps, PID reuse races across Anti-AFK/arranger/watcher/AutoReconnect, tray/window enumeration false positives, protocol/fallback token de-elevation bypasses, false Captcha loops, and `ClientSettingsPatcher` XML defects.

**Architecture:** Reuse `src/Shiro/Services/Windows/Win32NativeHelper.cs` (141L) for Win32 `[LibraryImport]` calls and extract focused helpers (`RobloxProcessIdentityHelper.cs`, `RobloxLaunchExecutorHelper.cs`) so tight files (`RobloxWatcher.cs` 433/485, `MemoryOptimizer.cs` 435/485, `RobloxAudioService.cs` 474/485, `AccountLaunchService` 1,105/1,150 across 4 partials) remain strictly below line budgets and `tests/coverage-floors.json` branch floors.

**Tech Stack:** .NET 10 (`net10.0-windows10.0.19041.0`, `win-x64`), C# 14, `[LibraryImport]` P/Invoke, WASAPI COM interop, xUnit, NSubstitute.

**Spec:** System Audit Report (`C:/Users/lenovo/.gemini/antigravity/brain/d3d9db7e-c20c-4d8a-b056-391721b18014/system-audit-report.md`) — Sections 2.2, 3.1 (#3/#6/#7), 3.2 (#10/#11), & P1 Roadmap items 2–3.

---

## Global Constraints

- `TreatWarningsAsErrors=true`, `<Nullable>enable</Nullable>`, `[LibraryImport]` only (zero `[DllImport]`, zero `.GetAwaiter().GetResult()`).
- Budgets (`ArchitectureTests.Budgets.cs`): `src` files `<= 485` (`RobloxAudioService.cs` 474/485, `.Wasapi.cs` 412/485, `RobloxWatcher.cs` 433/485, `MemoryOptimizer.cs` 435/485, `AccountLaunchService` total `1,105/1,150`); `tests` `<= 550`.
- Strict ASCII English comments/logs; zero empty/untyped `catch`; zero inline `{`/`}` format literals inside `[Fact]`/`[Theory]` methods; zero `AccountManager.`, `Program.Logger`, or `AppSettings.` in `Services/Windows` or `Services/Launch`.
- Coverage floors (`tests/coverage-floors.json`): `RobloxProcessService.Lifecycle.cs >= 67%`, `LaunchCoordinator.Helpers.cs >= 74%`, `AccountLaunchService.Diagnostics.cs >= 77%`, `AccountLaunchService.cs >= 73%`, `AutoReconnectService.cs >= 70%`, `RobloxPlaceHelper.cs >= 86%`.

## Review Focus

- **Solo-client parity (`OptimizeRobloxClients` vs `EvaluateWithPids`):** `EvaluateWithPids` (`MemoryOptimizer.cs:56-66`) is `[x]` in WT, whereas `OptimizeRobloxClients` (`lines 288-324`) still applies low memory priority + soft trim (`line 308`) and skips `NORMAL_PRIORITY_CLASS` + `DisableEcoQoS` when `!effectiveLowerPriority`. Unify via `ShouldApplySoloRestore` / `ResourceProfile.cs`.
- **Anti-AFK 20m ceiling:** Keep `ComputeNextDueUtc(now, 50, 0) == 1080s` (`AntiAfkServiceTests.cs:55-56`) while clamping `(clampedMinutes * 60) + jitterSeconds` to `[30, 1140]` so `1140 + 15s TickPeriod <= 1155s < 1200s`.
- **Captcha conflict #1:** Update `LaunchCoordinatorHelpersBehaviorTests.cs:186-190` (and keep `:295-302` testing a real challenged marker + invalidation) so bare `returnToLuaApp: (stage:UGCGame)` returns `ClientGameJoinState.Pending` instead of triggering a 75s Captcha loop.

---

### Task 1: Fix `OptimizeRobloxClients` Solo Recovery, Anti-AFK Ceiling, Tray `PostMessage`, Alignment & Foreground Sequencing

**Files:**
- Done in WT: `src/Shiro/Services/Windows/MemoryOptimizer.cs:56-66` (`EvaluateWithPids` solo restore) & `src/Shiro/Services/Windows/ProcessExtensions.cs:82-89` (CLI `CreationTime` check via `Win32NativeHelper.GetProcessTimes`)
- Modify: `src/Shiro/Services/Windows/MemoryOptimizer.cs:275-325` & `.ResourceProfile.cs:46-140`
- Modify: `src/Shiro/Services/Windows/AntiAfkService.cs:26-34,172-215`
- Modify: `src/Shiro/Services/Windows/Win32TrayHelper.cs:13-15,55-78,256-285`
- Modify: `src/Shiro/Services/Windows/ProcessExtensions.cs:104-147`
- Modify: `src/Shiro/Services/Windows/ForegroundWindowService.cs:29-33,129-185`
- Test: `tests/Shiro.Tests/P1WindowsAndUiRegressionTests.cs` (`[Collection("SerialGlobalState")]`)
- Verify compatibility: `tests/Shiro.Tests/ForegroundWindowServiceBehaviorTests.cs`, `Win32TrayHelperBehaviorTests.cs`, `AntiAfkServiceTests.cs`

**Interfaces:**
- **Modified:** `Win32TrayHelper.PostMessage(IntPtr hWnd, uint Msg, nuint wParam, nint lParam)`.
- **Added internal seams:**
  - `MemoryOptimizer.ShouldRestoreFullPerformanceOnSolo(int runningClientCount)` (`MemoryOptimizer.ResourceProfile.cs`)
  - `Win32TrayHelper.IsAllowedTrayClass(ReadOnlySpan<char> className)`
  - `ProcessExtensions.TryValidateUnicodeStringSlice(nint baseAddress, int bufferBytes, ushort lengthBytes, nint stringBufferPtr)`
  - `ForegroundWindowService.ShouldCommitForegroundSequence(long capturedSequence, long currentSequence)`

- [x] `MemoryOptimizer.EvaluateWithPids` solo-client restore & `ProcessExtensions` CLI creation-time cache — *Verified Done in WT*
- [ ] **Step 1: Write failing tests in `tests/Shiro.Tests/P1WindowsAndUiRegressionTests.cs` (TDD Red)**
  ```csharp
  [Fact]
  public void MemoryOptimizer_SoloClient_RestoresFullPerformanceEvenWhenUnfocused()
  {
      Assert.True(MemoryOptimizer.ShouldRestoreFullPerformanceOnSolo(1));
      Assert.False(MemoryOptimizer.ShouldRestoreFullPerformanceOnSolo(2));
  }

  [Fact]
  public void AntiAfkService_Interval18WithMaxJitter_CapsDueAt1140Seconds()
  {
      var now = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
      Assert.Equal(now.AddSeconds(1140), AntiAfkService.ComputeNextDueUtc(now, 18, 120));
      Assert.Equal(now.AddSeconds(1080), AntiAfkService.ComputeNextDueUtc(now, 50, 0));
  }

  [Fact]
  public void Win32TrayHelper_IsAllowedTrayClass_RejectsArbitrarySubstringClasses()
  {
      Assert.True(Win32TrayHelper.IsAllowedTrayClass("Shell_TrayWnd"));
      Assert.True(Win32TrayHelper.IsAllowedTrayClass("NotifyIconOverflowWindow"));
      Assert.False(Win32TrayHelper.IsAllowedTrayClass("RobloxAppTrayPreview"));
  }

  [Fact]
  public void ProcessExtensions_TryValidateUnicodeStringSlice_RejectsOddOrOutOfBoundsPointers()
  {
      Assert.False(ProcessExtensions.TryValidateUnicodeStringSlice(0x1000, 64, 5, 0x1010));
      Assert.False(ProcessExtensions.TryValidateUnicodeStringSlice(0x1000, 64, 32, 0x1030));
      Assert.True(ProcessExtensions.TryValidateUnicodeStringSlice(0x1000, 64, 16, 0x1010));
  }

  [Fact]
  public void ForegroundWindowService_StaleSequence_IsRejected()
  {
      Assert.False(ForegroundWindowService.ShouldCommitForegroundSequence(4, 5));
      Assert.True(ForegroundWindowService.ShouldCommitForegroundSequence(5, 5));
  }
  ```
- [ ] **Step 2: Run targeted tests expecting Red**
  `dotnet test Shiro.sln -c Release --nologo --filter "FullyQualifiedName~P1WindowsAndUiRegressionTests|FullyQualifiedName~ForegroundWindowServiceBehaviorTests|FullyQualifiedName~Win32TrayHelperBehaviorTests|FullyQualifiedName~AntiAfkServiceTests"`
  Expected: New tests fail compilation or assertions because internal seams and tightened bounds do not exist yet.
- [ ] **Step 3: Implement fixes cleanly respecting line caps**
  - `MemoryOptimizer.cs:288-324` & `.ResourceProfile.cs`: When `ShouldRestoreFullPerformanceOnSolo(pids.Count)` is true (i.e. `!effectiveLowerPriority`), restore `NORMAL_PRIORITY_CLASS` (`ComputeTargetPriorityClass(true, ...)`), restore `SetProcessMemoryPriorityNative(hProcess, isForeground: true)`, call `DisableEcoQoS(hProcess)`, and skip `SoftRelaxWorkingSetNative` / low-priority throttling.
  - `AntiAfkService.cs`: In `ComputeNextDueUtc`, clamp `(clampedMinutes * 60) + jitterSeconds` to `[30, 1140]`; add `long ProcessCreationTicks` to `ClientState` and evict/re-seed entry when live `GetProcessTimes` ticks differ.
  - `Win32TrayHelper.cs`: Change `PostMessage` params to `(IntPtr hWnd, uint Msg, nuint wParam, nint lParam)`; replace `.Contains("Tray")`/`.Contains("Overflow")`/`.Contains("XamlIsland")` with exact allowlist (`Shell_TrayWnd`, `Shell_SecondaryTrayWnd`, `NotifyIconOverflowWindow`, `TopLevelWindowForOverflowXamlIsland`, `ToolbarWindow32`, `Windows.UI.Composition.DesktopWindowContentBridge`) + Explorer PID filter (`explorer.exe`) + `IsWindowVisible` unless `includeHiddenOverflow` is true.
  - `ProcessExtensions.cs`: Allocate 8-byte aligned heap/fixed buffer in `QueryCommandLineCore`, check `returnLength >= Marshal.SizeOf<UNICODE_STRING>()`, validate even `Length > 0` and `[Buffer, Buffer + Length)` inside `[bufPtr, bufPtr + allocatedBytes)`.
  - `ForegroundWindowService.cs`: Increment `long seq = Interlocked.Increment(ref _transitionSequence)` in `OnWinEvent`, capture `seq`, and check `ShouldCommitForegroundSequence(seq, Volatile.Read(ref _transitionSequence))` inside the `ThreadPool` worker (`Start()` continues returning `true`).
- [ ] **Step 4: Run targeted tests + `ArchitectureTests` expecting 100% PASS, 0 warnings**
  `dotnet test Shiro.sln -c Release --nologo --filter "FullyQualifiedName~P1WindowsAndUiRegressionTests|FullyQualifiedName~ForegroundWindowServiceBehaviorTests|FullyQualifiedName~Win32TrayHelperBehaviorTests|FullyQualifiedName~AntiAfkServiceTests|FullyQualifiedName~ArchitectureTests"`
- [ ] **Step 5: Stage & commit Task 1**
  `git add src/Shiro/Services/Windows/MemoryOptimizer*.cs src/Shiro/Services/Windows/AntiAfkService.cs src/Shiro/Services/Windows/Win32TrayHelper.cs src/Shiro/Services/Windows/ProcessExtensions.cs src/Shiro/Services/Windows/ForegroundWindowService.cs tests/Shiro.Tests/P1WindowsAndUiRegressionTests.cs`
  `git commit -m "fix(win32): harden solo memory restore, anti-afk ceiling, tray filtering, and foreground sequencing"`

---

### Task 2: Fix `RobloxAudioService` Multi-Session Audio Pointers, Bounded MTA Wait & `SmartMute` Race

**Files:**
- Modify: `src/Shiro/Services/Windows/RobloxAudioService.cs:18-75,142-205` (keep `<= 485`; currently `474/485` — place helper logic in `.Wasapi.cs`)
- Modify: `src/Shiro/Services/Windows/RobloxAudioService.Wasapi.cs:17,105-261` (`412/485`)
- Test: `tests/Shiro.Tests/AudioSessionCacheTests.cs` (`[Collection("SerialGlobalState")]`), `tests/Shiro.Tests/RobloxAudioServiceConcurrencyTests.cs`, `tests/Shiro.Tests/RobloxAudioServiceTests.cs`

**Interfaces:**
- **Modified:** Store `ConcurrentDictionary<int, ImmutableArray<IntPtr>> _cachedVolumePointers` so multiple COM sessions per PID (e.g. FMOD + embedded WebView2) are cached and `ComRelease`d together without leaking or overwriting sibling pointers. Retain overload `AreAllPidsCached(IReadOnlyList<int>, IReadOnlyDictionary<int, IntPtr>)` alongside the multi-pointer check so existing `AudioSessionCacheTests.cs` compiles unchanged.
- **Modified:** `internal static T RunOnWasapiMtaThread<T>(Func<T> work, int timeoutMs = 1500)` using heap-owned completion state (`MtaWorkItem`) with ref-counted `ManualResetEventSlim` disposal (`Interlocked.Decrement(ref pendingOwners)`) so caller timeout never throws `ObjectDisposedException` when a stalled COM call finishes later.
- **Added field:** `private static long _smartMuteConfigVersion;` incremented inside `lock (_gate)` in `ConfigureSmartMute` and checked post-COM in `EvaluateSmartMuteWithHook`.

- [ ] **Step 1: Write failing tests in `AudioSessionCacheTests.cs` & `RobloxAudioServiceConcurrencyTests.cs` (TDD Red)**
  ```csharp
  [Fact]
  public void RunOnWasapiMtaThread_WhenWorkExceedsTimeout_ThrowsTimeoutWithoutLateDisposeCrash()
  {
      using var gate = new ManualResetEventSlim(false);
      Assert.Throws<TimeoutException>(() => RobloxAudioService.RunOnWasapiMtaThread(() =>
      {
          gate.Wait(300);
          return 1;
      }, timeoutMs: 25));
      gate.Set();
  }

  [Fact]
  public void MultiSessionCache_StoresMultiplePointersPerPidAndReleasesAll()
  {
      var cache = new Dictionary<int, IReadOnlyList<IntPtr>>
      {
          [1001] = new[] { new IntPtr(0x1111), new IntPtr(0x2222) }
      };
      Assert.True(RobloxAudioService.AreAllPidsCached(new[] { 1001 }, cache));
  }
  ```
  Plus a concurrency test in `RobloxAudioServiceConcurrencyTests.cs` asserting `ConfigureSmartMute(false)` during an in-flight `applyFocus` delegate prevents `_lastActiveRobloxPid` / `_smartMuteEnabled` overwrite when `applyFocus` finishes.
- [ ] **Step 2: Run targeted audio tests expecting Red**
  `dotnet test Shiro.sln -c Release --nologo --filter "FullyQualifiedName~AudioSessionCacheTests|FullyQualifiedName~RobloxAudioService"`
- [ ] **Step 3: Implement multi-session cache, bounded MTA wait & `_smartMuteConfigVersion` guard**
  - Add `MtaWorkItem` wrapper in `RobloxAudioService.Wasapi.cs:105-143` throwing `TimeoutException` when `!done.Wait(timeoutMs)`.
  - Upgrade `_cachedVolumePointers` to `ImmutableArray<IntPtr>` per PID, iterating all pointers in `TryForEachCachedAudioSessionOnMta` and calling `ComRelease` on every pointer in `InvalidateCachedAudioSessionOnMta` / `ClearAudioSessionCacheOnMta`.
  - Switch `_smartMuteEnabled` to `volatile` in `RobloxAudioService.cs:20`; increment `_smartMuteConfigVersion` in `ConfigureSmartMute`; capture `long version = _smartMuteConfigVersion;` before releasing `_gate` in `EvaluateSmartMuteWithHook` (`line 154`) and abort state commit if `!_smartMuteEnabled || version != _smartMuteConfigVersion` (`line 187`).
- [ ] **Step 4: Run 3x stress audio tests + `ArchitectureTests` expecting 3/3 PASS**
  `1..3 | % { dotnet test Shiro.sln -c Release --nologo --filter "FullyQualifiedName~AudioSessionCacheTests|FullyQualifiedName~RobloxAudioService|FullyQualifiedName~ArchitectureTests" }`
- [ ] **Step 5: Stage & commit Task 2**
  `git add src/Shiro/Services/Windows/RobloxAudioService.cs src/Shiro/Services/Windows/RobloxAudioService.Wasapi.cs tests/Shiro.Tests/AudioSessionCacheTests.cs tests/Shiro.Tests/RobloxAudioServiceConcurrencyTests.cs`
  `git commit -m "fix(audio): support multi-session WASAPI cache, bounded MTA wait, and SmartMute config versioning"`

---

### Task 3: Add `(pid, CreationTimeUtc)` PID-Reuse Guards & `RobloxWatcher` Main-Window / Kill-Suppress Safety

**Files:**
- Already present in WT: `AutoReconnectService.Models.cs:222-235` (`SuppressReconnectByPid(int processId)`) & `Win32NativeHelper.GetProcessTimes`.
- Create: `src/Shiro/Services/Windows/RobloxProcessIdentityHelper.cs` (holds shared creation-time helper + `IsExcludedUtilityWindowClass`, keeping `RobloxWatcher.cs` `<= 485`)
- Modify: `src/Shiro/Services/Windows/RobloxWindowArrangerService.cs:36,192-227,229-249`
- Modify: `src/Shiro/Services/Windows/RobloxWatcher.cs:40-42,175-204,245-273,339-360,402-430` & `.FileWatcher.cs:18-36`
- Modify: `src/Shiro/Services/Launch/AutoReconnectService.Monitor.cs:150-165`
- Test: `tests/Shiro.Tests/P1WindowsAndUiRegressionTests.cs`, `tests/Shiro.Tests/P1LaunchRegressionTests.cs`

**Interfaces:**
- **New:** `RobloxProcessIdentityHelper.TryGetProcessCreationTicks(int pid, out long creationTicks)` & `IsSameProcessInstance(int pid, long expectedCreationTicks, out long currentTicks)`.
- **New:** `RobloxProcessIdentityHelper.ShouldSelectAsMainWindow(uint style, uint exStyle, ReadOnlySpan<char> className)` rejecting `WS_CHILD`, `WS_EX_TOOLWINDOW`, `"IME"`, `"MSCTFIME UI"`, `"Chrome_WidgetWin_"`, and `"Default IME"`.

- [ ] **Step 1: Write failing tests in `P1WindowsAndUiRegressionTests.cs` & `P1LaunchRegressionTests.cs` (TDD Red)**
  ```csharp
  [Fact]
  public void ProcessIdentity_WhenCreationTicksDiffer_DetectsPidReuseAcrossCaches()
  {
      bool same = RobloxProcessIdentityHelper.IsCreationMatch(expectedTicks: 1000L, actualTicks: 2000L);
      Assert.False(same);
  }

  [Fact]
  public void BuildMainWindowFilter_RejectsToolWindowAndImeUtilityClasses()
  {
      Assert.False(RobloxProcessIdentityHelper.ShouldSelectAsMainWindow(0, 0x00000080u, "WINDOWSCLIENT".AsSpan()));
      Assert.False(RobloxProcessIdentityHelper.ShouldSelectAsMainWindow(0, 0, "IME".AsSpan()));
      Assert.True(RobloxProcessIdentityHelper.ShouldSelectAsMainWindow(0, 0, "WINDOWSCLIENT".AsSpan()));
  }
  ```
  Plus tests verifying `RobloxWatcher` evicts recycled `Seen` / `_pidMetadataCache` entries when creation ticks change, `KillNative` triggers `AutoReconnectService.Instance.SuppressReconnectByPid(pid)`, and `AutoReconnectService.Monitor` marks recycled PID (`actualTicks != session.ProcessCreationTicks`) as exited.
- [ ] **Step 2: Run targeted tests expecting Red**
  `dotnet test Shiro.sln -c Release --nologo --filter "FullyQualifiedName~P1WindowsAndUiRegressionTests|FullyQualifiedName~P1LaunchRegressionTests"`
- [ ] **Step 3: Implement `RobloxProcessIdentityHelper` + update Arranger, Watcher, and `AutoReconnectService.Monitor`**
  - Key/validate `_pidTrackerCache` in `RobloxWindowArrangerService.cs:36` and `_pidMetadataCache` + `Seen` in `RobloxWatcher.cs:42` by `long CreationTicks`.
  - Filter `BuildMainWindowMap` (`RobloxWatcher.cs:339-360`) with `ShouldSelectAsMainWindow` (`GetWindowLongPtr` styles + `Win32NativeHelper.GetClassNameW`) so IME/WebView2 utility windows cannot shadow `"Roblox"`.
  - Call `AutoReconnectService.Instance.SuppressReconnectByPid(pid)` inside `KillNative` (`RobloxWatcher.cs:402-419`).
  - Store `ProcessCreationTicks` on `ReconnectSession` at launch/attach and treat mismatch in `AutoReconnectService.Monitor.cs:150-165` as `processExited = true`.
- [ ] **Step 4: Run targeted tests + `ArchitectureTests` + `AutoReconnectService.cs >= 70%` branch check**
  `dotnet test Shiro.sln -c Release --nologo --filter "FullyQualifiedName~P1WindowsAndUiRegressionTests|FullyQualifiedName~P1LaunchRegressionTests|FullyQualifiedName~AutoReconnect|FullyQualifiedName~ArchitectureTests"`
- [ ] **Step 5: Stage & commit Task 3**
  `git add src/Shiro/Services/Windows/RobloxProcessIdentityHelper.cs src/Shiro/Services/Windows/RobloxWindowArrangerService.cs src/Shiro/Services/Windows/RobloxWatcher*.cs src/Shiro/Services/Launch/AutoReconnectService*.cs tests/Shiro.Tests/P1WindowsAndUiRegressionTests.cs tests/Shiro.Tests/P1LaunchRegressionTests.cs`
  `git commit -m "fix(process): guard watcher, arranger, and AutoReconnect caches against PID reuse"`

---

### Task 4: Unify Direct De-Elevated Launch Paths, Refine Captcha Log Classifier & Fix `ClientSettingsPatcher`

**Files:**
- Already aligned in WT: `SettingsViewModel.Advanced.cs:104` already defaults `EnableTokenDeElevation` to `false`.
- Create: `src/Shiro/Services/Launch/RobloxLaunchExecutorHelper.cs` (unifies Game `AccountLaunchService.cs:166-246` + Home `.Home.cs:28-91` execution paths and routes all spawns through `RobloxDirectLauncher.SpawnRobloxProcess`, reducing `AccountLaunchService` below `1,105/1,150`)
- Modify: `src/Shiro/Services/Launch/AccountLaunchService.cs:152-251,372-413` (`>= 73%`) & `.Home.cs:15-96`
- Modify: `src/Shiro/Services/Launch/RobloxDirectLauncher.DeElevation.cs:108,129-141` (`EnableTokenDeElevation` default `false`; remove cross-session `?? explorers.FirstOrDefault()` fallback!)
- Modify: `src/Shiro/Services/Launch/AutoReconnectService.cs:283` (wrap reconnect `JoinServer` in `using var _ = RobloxDirectLauncher.BeginCookieGuardScope();`)
- Modify: `src/Shiro/Services/Windows/RobloxProcess.LogPatterns.cs:151-170` & `src/Shiro/Services/Launch/LaunchCoordinator.Helpers.cs:158-167` (`>= 74%`)
- Modify: `src/Shiro/Services/Launch/ClientSettingsPatcher.cs:24,33,45-91,200-241`
- Update tests (Conflict #1): `tests/Shiro.Tests/LaunchCoordinatorHelpersBehaviorTests.cs:186-190,295-302`
- Add tests: `tests/Shiro.Tests/P1LaunchRegressionTests.cs`, `RobloxDirectLauncherDeElevationBehaviorTests.cs`, `ClientSettingsPatcherTests.cs`

**Interfaces:**
- **New:** `RobloxLaunchExecutorHelper.ExecuteNativeLaunchAsync(string protocolUri, string playerExeArgs, string ticket, string username, string? trackerId, CancellationToken ct)`
- **Modified:** `RobloxDirectLauncher.SelectExplorerPidForCurrentSession(Process[] explorers, int currentSessionId)` returns `0` (never an unrelated TS session's `explorer.exe`) when no `p.SessionId == currentSessionId` match exists.

- [ ] **Step 1: Update Conflict #1 expectations in `LaunchCoordinatorHelpersBehaviorTests.cs:186-190,295-302` & write failing tests (TDD Red)**
  - At `LaunchCoordinatorHelpersBehaviorTests.cs:186-190`: update bare `"[FLog::SingleSurfaceApp] returnToLuaApp: (stage:UGCGame)"` expectation from `ChallengeFailedToLuaApp` to `ClientGameJoinState.Pending` (bare return without challenge marker must NOT trigger a 75s Captcha stall), and at `lines 295-302` ensure `mixedLines` uses an explicit challenge marker (`challengedByGcs`) alongside `"feature":"challengeInvalidated"` so the real invalidation branch stays tested while non-challenge `WebView2 browser is successfully launched` returns `Pending`.
  - Add failing tests:
    - `SelectExplorerPidForCurrentSession_WhenNoMatchingSession_ReturnsZeroInsteadOfCrossSessionExplorer`
    - `RobloxDirectLauncher_EnableTokenDeElevationDefault_IsFalseMatchingSettingsViewModel`
    - `ClientSettingsPatcher_SelfClosingPropertiesAndNegativeGraphics_InsertsAndReplacesWithoutScanningHost`
    - `AutoReconnectService_ReconnectJoin_EntersCookieGuardScope`
- [ ] **Step 2: Run targeted tests expecting Red**
  `dotnet test Shiro.sln -c Release --nologo --filter "FullyQualifiedName~LaunchCoordinatorHelpersBehaviorTests|FullyQualifiedName~P1LaunchRegressionTests|FullyQualifiedName~RobloxDirectLauncherDeElevationBehaviorTests|FullyQualifiedName~ClientSettingsPatcherTests"`
- [ ] **Step 3: Implement `RobloxLaunchExecutorHelper`, de-elevation alignment, cookie guard, Captcha classifier, and `ClientSettingsPatcher` fixes**
  - Align `RobloxDirectLauncher.DeElevation.cs:108` default to `false` and delete `?? explorers.FirstOrDefault()` (`line 139`).
  - Route protocol fallback and direct `.exe` launches through `RobloxLaunchExecutorHelper` calling `RobloxDirectLauncher.SpawnRobloxProcess` cleanly across Game & Home paths.
  - Wrap `AutoReconnectService.cs:283` inside `using var cookieGuard = RobloxDirectLauncher.BeginCookieGuardScope();`.
  - Remove generic `WebView2 browser is successfully launched` from `IsGameJoinChallengedLogLine` (`LogPatterns.cs:158`) and require `hasChallenge` in `LaunchCoordinator.Helpers.cs:161` before flagging `challengeFailed = true` on `ReturnToLuaApp` or invalidation.
  - In `ClientSettingsPatcher.cs`: update `GraphicsQualityXmlRegex` to `-?\d+`, update `UserGameSettingsPropertiesRegex` + `UpdateBasicSettingsXmlContent` to expand self-closing `<Properties />` cleanly, batch PotatoMode + UnlockFPS into one `ConfigureGlobalBasicSettings(...)` write inside `PatchSettings`, and skip `FindRobloxVersionFolders()` host disk scans when `settingsPathOverride`/`xmlPathOverride` is supplied.
- [ ] **Step 4: Run Release `-warnaserror` build + targeted suites + `ArchitectureTests` + `Check-Coverage.ps1`**
  ```powershell
  dotnet build Shiro.sln --configuration Release --nologo -warnaserror
  dotnet test Shiro.sln --configuration Release --no-build --nologo --filter "FullyQualifiedName~P1|FullyQualifiedName~Launch|FullyQualifiedName~ClientSettingsPatcher|FullyQualifiedName~ArchitectureTests"
  powershell.exe -NoProfile -ExecutionPolicy Bypass -File .\scripts\Check-Coverage.ps1
  ```
  Expected: `0 Warning(s)`, `0 Error(s)`, all coverage floors (`LaunchCoordinator.Helpers.cs >= 74%`, `AccountLaunchService.cs >= 73%`, `AutoReconnectService.cs >= 70%`) and `92.0%` Core gate `SUCCESS`.
- [ ] **Step 5: Stage & commit Task 4**
  `git add src/Shiro/Services/Launch/RobloxLaunchExecutorHelper.cs src/Shiro/Services/Launch/AccountLaunchService*.cs src/Shiro/Services/Launch/RobloxDirectLauncher.DeElevation.cs src/Shiro/Services/Launch/AutoReconnectService.cs src/Shiro/Services/Windows/RobloxProcess.LogPatterns.cs src/Shiro/Services/Launch/LaunchCoordinator.Helpers.cs src/Shiro/Services/Launch/ClientSettingsPatcher.cs tests/Shiro.Tests/LaunchCoordinatorHelpersBehaviorTests.cs tests/Shiro.Tests/P1LaunchRegressionTests.cs tests/Shiro.Tests/RobloxDirectLauncherDeElevationBehaviorTests.cs tests/Shiro.Tests/ClientSettingsPatcherTests.cs`
  `git commit -m "fix(launch): unify de-elevated execution, harden Captcha classifier, and fix ClientSettingsPatcher XML"`

---

## Execution Handoff

Plan complete. Recommend committing or snapshotting the verified 96-file WT baseline onto `main` first, then executing Wave P1 sequentially with either:
1. **Subagent-driven execution** (`superpowers:subagent-driven-development`) *(recommended)* — isolates each task's edits behind fresh review & coverage checks.
2. **Inline execution** (`superpowers:executing-plans`) — runs Task 1–4 directly in the active session with checkpoint stops.
