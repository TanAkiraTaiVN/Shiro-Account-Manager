# Shiro Game Tab Scheduler Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Implement a full-featured, Liquid Obsidian 2026 Game Tab Scheduler in Shiro Account Manager that schedules opening and gracefully closing Roblox tabs per account or group across daily, weekly, one-time, and overnight shifts with timeline visualization, conflict detection, and run observability.

**Architecture:** Pure domain models and stateless `TimeProvider`-driven evaluation/timeline/conflict logic live under `src/Shiro/Domain/` and `src/Shiro/Services/Launch/`; atomic source-generated JSON persistence lives in `LaunchScheduleStore`; `LaunchSchedulerService` runs a serialized Close-before-Open execution pipeline decoupled from `AccountManager` via `ISchedulerActionExecutor` and a scheduler-aware `AccountLaunchBatchService` partial; Liquid Obsidian UI ships as a dedicated `NavigationPage.Scheduler` page with a 24-hour timeline, non-modal `SkiaGlassSurface` slide-over editor drawer, run history panel, and Dashboard/Command Palette shortcuts.

**Tech Stack:** `.NET 10 (win-x64)`, `C# 14`, `Avalonia 12.1.6`, `Froststrap.LucideAvalonia 2.0.1`, `System.Text.Json` source generation (`ShiroAppJson`), `Microsoft.Extensions.DependencyInjection`, `xUnit v3`, `Avalonia.Headless.XUnit`, `NSubstitute 6.2.0`.

**Spec:** [2026-11-24-game-tab-scheduler-design.md](file:///d:/Dự Án Visual/Shiro-Account-Manager/docs/superpowers/specs/2026-11-24-game-tab-scheduler-design.md)

## Global Constraints

- **Zero New NuGet Packages:** Do not edit `Directory.Packages.props` or `packages.lock.json` (`RestorePackagesWithLockFile=true`).
- **Strict File & Partial Line Budgets (`ArchitectureTests.Budgets`):**
  - Every `.cs` in `src/Shiro/` `<= 485` lines; every partial class total `<= 1150` lines (NOTE: `AccountManager*.cs` is currently `1139` lines — add at most `<= 8` lines across all `AccountManager*.cs` files; `StoragePaths.cs` is `480` lines — add at most `<= 2` lines).
  - Every `.axaml` in `src/Shiro/` `<= 400` lines and every line `<= 200` chars (NOTE: `DashboardView.axaml` is currently `391` lines — add at most `<= 3` lines).
  - Every `.cs` test file under `tests/Shiro.Tests/<Subfolder>/` `<= 550` lines (0 `.cs` files in `tests/Shiro.Tests/` root).
- **Layering & Naming (`ArchitectureTests.Layering` & `StaticState` & `P3DomainPurityTests`):**
  - `src/Shiro/Domain/` has zero dependencies on `Avalonia`, `Shiro.ViewModels`, `Shiro.AvaloniaUI`, `System.Net.Http`, `Shiro.Http`, or `Microsoft.Win32`.
  - `Shiro.Services` has zero dependency on `Shiro.AccountManager` or `Shiro.AvaloniaUI.Views`.
  - Every non-nested class in `Shiro.Services` must end with an approved suffix (`Service`, `Store`, `Helper`, `Info`, `Item`, `Result`, `Config`, `Coordinator`, etc.). Put pure evaluation value types in `src/Shiro/Domain/` (namespace `Shiro`).
  - `Shiro.ViewModels` has zero dependency on `Shiro.AvaloniaUI.Views`.
- **Hygiene & Avalonia Safety (`ArchitectureTests.Hygiene`, `Shiro2026UiGuardrailTests`):**
  - `TreatWarningsAsErrors=true`, `AvaloniaUseCompiledBindingsByDefault=true`, zero Avalonia binding errors (`BindingErrorSink`), zero empty/untyped `catch` blocks, zero `async void`, zero `IsEnglish` branching, zero hardcoded Vietnamese string literals in `.cs`, zero Vietnamese diacritics in C# comments.
  - All new UI copy added symmetrically to BOTH `src/Shiro/Resources/Strings/en.json` and `src/Shiro/Resources/Strings/vi.json`.
  - `{DynamicResource}` for non-localization keys must end with `Brush`/`Color` or contain `Theme`/`Font`; use `{StaticResource}` for `CornerRadius`/`BoxShadows`.
  - Use only verified Lucide icon names (`Clock`, `Play`, `Square`, `Plus`, `ArrowRight`, `Pencil`, `Copy`, `Trash`, `LayoutGrid`, `Bell`, `ShieldAlert`, `Activity`, `Folder`, `User`, `House`, `Gamepad2`, `Check`, `X`, `Search`, `Info`).
- **Coverage Gates:** Overall line `>= 75.0%`, overall branch `>= 60.0%`, core `>= 92.0%`, plus all existing floors in `tests/coverage-floors.json`.
- **Working Tree Safety:** Unrelated user edits exist in the working tree — never run `git add -A` or touch unrelated files; stage only explicit scheduler paths when committing.

## Review Focus

1. **Master-Password Vault Locked at Startup (`IsVaultLockedPendingMasterPassword == true`):** Scheduler must NOT evaluate triggers or consume `LastTriggeredStartKey` / `LastTriggeredEndKey` against 0 accounts while locked, and must run deferred `CatchUpIfActiveOnStartup` once immediately after the vault unlocks (`Task 3`).
2. **Overnight Weekly Shift Closing on Unchecked Next Weekday (`22:00 → 06:00`, e.g., Friday only):** Must fire `Close` on Saturday `06:00` using Friday's occurrence key AND project the `00:00–06:00` carryover segment onto Saturday's 24-hour timeline (`Task 2`).
3. **Manual `Run Now` / `Close Tabs` Inside vs. Outside 90s Grace Window:** Clicking `Run Now` 10 minutes early must NOT suppress the upcoming scheduled `Open` or disable `OneTime`; clicking `Run Now` inside `[Start, Start+90s)` must mark `LastTriggeredStartKey` so the `1s` tick does not immediately kill and relaunch the newly opened tabs (`Task 3`).
4. **Public-Server `JobId == ""` Schedule With Per-Account `SavedJobId`:** Scheduled `JoinServer` with blank `JobId` must strictly join a Public Server (`LaunchRequest.JobId == ""`) without falling back to `account.GetField("SavedJobId")` and without mutating Dashboard `PlaceId` / `JobId` / `SelectedAccounts` (`Task 3`).
5. **Streamer Mode Live Toggle on Open Scheduler UI:** Toggling `StreamerModeService.IsActive` while `SchedulerView` and `SchedulerEditorDrawer` are open must immediately mask account usernames and Job IDs across schedule cards, editor account checklist, tooltips, and run history with zero binding errors (`Task 5` & `Task 6`).

---

## File Structure Map

- **Domain (`src/Shiro/Domain/`):**
  - `LaunchSchedule.cs` — `LaunchSchedule`, `ScheduleAccountRef`, and schedule enums (`ScheduleRecurrence`, `ScheduleDaysOfWeek`, `ScheduleTargetMode`, `ScheduleLaunchMode`, `ScheduleArrangeMode`).
  - `ScheduleRunEntry.cs` — `ScheduleRunEntry`, `LaunchScheduleDocument`, `ScheduleRunPhase`, `ScheduleRunOutcome`.
  - `ScheduleEvaluationModels.cs` — `ScheduleDueEvent`, `ScheduleTransitionInfo`, `ScheduleConflictInfo`, `ScheduleConflictKind`, `ScheduleTimelineSegment`, `ScheduleValidationResult`.
- **Storage (`src/Shiro/Services/Storage/` & `src/Shiro/Services/`):**
  - `LaunchScheduleStore.cs` — Atomic JSON load/save (`.tmp` → `File.Replace`/`File.Move`), `.backup` fallback on corruption, `50`-entry history cap, thread-safe lock.
  - Modify: `StoragePaths.cs` (+1 line `LaunchSchedulesFile`), `AppSettingsKeys.cs` (+1 line `SchedulerEnabled`), `ShiroAppJson.cs` (+`[JsonSerializable]` registrations).
- **Launch Engine (`src/Shiro/Services/Launch/`):**
  - `LaunchScheduleEvaluatorHelper.cs` — Stateless validation, due-event evaluation, startup catch-up detection, next-transition calculation, 24h timeline projection + greedy lane packing, and conflict detection.
  - `AccountLaunchBatchService.Scheduler.cs` — Scheduler-aware partial returning `Task<LaunchBatchResult>`, suppressing generic summary toast, bypassing `SavedJobId` fallback.
  - `ISchedulerActionExecutor.cs`, `ILaunchSchedulerService.cs`, `SchedulerHostActionExecutorService.cs`.
  - `LaunchSchedulerService.cs` + `LaunchSchedulerService.Execution.cs` — `TimeProvider`-driven `1s` timer, vault-lock gate, CRUD, serialized `SemaphoreSlim(1, 1)` Close-before-Open pipeline, `30s` queue-contention drain, Error 273 pre-launch close, handoff cooldown, post-launch window arrange, Toast/Webhook/History emission.
- **Host Wiring:**
  - Modify: `AppBootstrap.cs`, `AppServices.cs`, `AccountManager.Lifecycle.cs` (≤ 6 lines total).
- **ViewModels (`src/Shiro/ViewModels/Scheduler/`):**
  - `SchedulerTimelineBlockViewModel.cs`, `ScheduleItemViewModel.cs`, `ScheduleEditorViewModel.cs`, `SchedulerViewModel.cs`.
  - Modify: `MainViewModel.cs`, `CommandPaletteCatalog.cs`, `en.json`, `vi.json`.
- **Views (`src/Shiro/AvaloniaUI/Views/` & `Panels/`):**
  - `SchedulerView.axaml`/`.cs`, `Panels/SchedulerTimelinePanel.axaml`/`.cs`, `Panels/SchedulerEditorDrawer.axaml`/`.cs`, `Panels/SchedulerHistoryPanel.axaml`/`.cs`.
  - Modify: `MainWindow.axaml`, `MainWindow.Navigation.cs`, `MainWindow.Chrome2026.cs`, `DashboardView.axaml`, `DashboardView.ContextMenu.cs`, `Panels/DashboardBulkBarPanel.axaml`.
- **Tests (`tests/Shiro.Tests/`):**
  - `TestInfrastructure/TestTimeProvider.cs`
  - `Launch/LaunchScheduleStoreTests.cs`, `Launch/LaunchScheduleEvaluatorTests.cs`, `Launch/LaunchSchedulerServiceTests.cs`, `Launch/LaunchSchedulerBootstrapTests.cs`
  - `Ui/SchedulerViewModelTests.cs`, `Ui/SchedulerViewUiTests.cs`
  - Modify: `Architecture/ArchitectureTests.cs:128`, `Ui/CommandPalette2Tests.cs:179` (`29` → `31`).

---

### Task 1: Domain Models, Source-Gen JSON & Atomic `LaunchScheduleStore`

**Files:**
- Create: `src/Shiro/Domain/LaunchSchedule.cs`
- Create: `src/Shiro/Domain/ScheduleRunEntry.cs`
- Create: `src/Shiro/Domain/ScheduleEvaluationModels.cs`
- Create: `src/Shiro/Services/Storage/LaunchScheduleStore.cs`
- Modify: `src/Shiro/Services/Storage/StoragePaths.cs:65-70`
- Modify: `src/Shiro/Services/Storage/AppSettingsKeys.cs:80-84`
- Modify: `src/Shiro/Services/ShiroAppJson.cs:120-128`
- Test: `tests/Shiro.Tests/Launch/LaunchScheduleStoreTests.cs`

**Interfaces:**
- Consumes: `StoragePaths.GetUserFile(string)`, `ShiroAppJson.Default`, `IAppLogger`.
- Produces:
  - `namespace Shiro`: `LaunchSchedule`, `ScheduleAccountRef`, `ScheduleRecurrence`, `ScheduleDaysOfWeek`, `ScheduleTargetMode`, `ScheduleLaunchMode`, `ScheduleArrangeMode`, `ScheduleRunPhase`, `ScheduleRunOutcome`, `ScheduleRunEntry`, `LaunchScheduleDocument`, `ScheduleDueEvent`, `ScheduleTransitionInfo`, `ScheduleConflictKind`, `ScheduleConflictInfo`, `ScheduleTimelineSegment`, `ScheduleValidationResult`.
  - `StoragePaths.LaunchSchedulesFile` (`string`).
  - `AppSettingsKeys.General.SchedulerEnabled` (`"SchedulerEnabled"`).
  - `LaunchScheduleStore(Func<string>? filePathProvider = null, IAppLogger? logger = null)` with `Load()`, `Save(LaunchScheduleDocument doc)`, `Flush()`, `MaxHistoryEntries = 50`.

- [ ] **Step 1: Write failing tests in `tests/Shiro.Tests/Launch/LaunchScheduleStoreTests.cs`**

Cover:
1. `Load_ReturnsEmptyDocument_WhenFileMissing`
2. `SaveAndLoad_RoundTripsAllScheduleFieldsAndHistory_ViaSourceGenJson` (verifies `Id`, `Name`, `Recurrence`, `DaysOfWeek`, `OneTimeDate`, `StartTime`, `EndTime`, `TargetAccounts` with `UserId`+`Username`, `PlaceId`, `JobId`, `HandoffDelaySeconds`, `PostLaunchArrange`, `CatchUpIfActiveOnStartup`, `SendWebhookNotification`, `LastTriggeredStartKey`, `LastTriggeredEndKey`, and `History`)
3. `Save_TrimsHistoryToMax50Entries_NewestFirst`
4. `Load_FallsBackToBackup_WhenPrimaryFileCorrupted`
5. `Save_WritesAtomically_LeavingNoTempFile`

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test tests\Shiro.Tests --filter "FullyQualifiedName~LaunchScheduleStoreTests"`
Expected: FAIL (types not defined).

- [ ] **Step 3: Create domain models in `src/Shiro/Domain/`**

- `LaunchSchedule.cs`: enums + `ScheduleAccountRef` + `LaunchSchedule` with exact properties & defaults from Spec §5.1 + `Clone()` helper for safe draft editing/snapshotting.
- `ScheduleRunEntry.cs`: `ScheduleRunPhase`, `ScheduleRunOutcome`, `ScheduleRunEntry`, `LaunchScheduleDocument` (`SchemaVersion = 1`).
- `ScheduleEvaluationModels.cs`: `readonly record struct`s (`ScheduleDueEvent`, `ScheduleTransitionInfo`, `ScheduleConflictInfo`, `ScheduleTimelineSegment`, `ScheduleValidationResult`) and `ScheduleConflictKind` enum (`AccountOverlap`, `ExclusiveCloseCollision`).

- [ ] **Step 4: Register paths, settings key, source-gen JSON & implement `LaunchScheduleStore`**

- Add `public static string LaunchSchedulesFile => GetUserFile("LaunchSchedules.json");` in `StoragePaths.cs` (keep file `<= 485` lines).
- Add `public const string SchedulerEnabled = "SchedulerEnabled";` in `AppSettingsKeys.General`.
- Add `[JsonSerializable(typeof(LaunchScheduleDocument))]`, `LaunchSchedule`, `List<LaunchSchedule>`, `ScheduleAccountRef`, `List<ScheduleAccountRef>`, `ScheduleRunEntry`, `List<ScheduleRunEntry>` to `ShiroAppJson.cs`.
- Implement `LaunchScheduleStore` in `src/Shiro/Services/Storage/LaunchScheduleStore.cs`: lock-protected `Load()`, `Save(LaunchScheduleDocument)` (clamping `History` to `MaxHistoryEntries = 50`, writing `.tmp`, rotating existing file to `.backup`, atomic `File.Move(tmp, path, overwrite: true)`), and `Flush()`.

- [ ] **Step 5: Run tests to verify they pass**

Run: `dotnet test tests\Shiro.Tests --filter "FullyQualifiedName~LaunchScheduleStoreTests|FullyQualifiedName~DomainFolder_ShouldNotDependOn|FullyQualifiedName~NoSourceCodeFiles_ShouldExceed500Lines"`
Expected: PASS (all store + domain purity + line budget checks pass).

---

### Task 2: Pure Deterministic Evaluator, 24h Timeline Projector & Conflict Detector

**Files:**
- Create: `src/Shiro/Services/Launch/LaunchScheduleEvaluatorHelper.cs`
- Test: `tests/Shiro.Tests/Launch/LaunchScheduleEvaluatorTests.cs`

**Interfaces:**
- Consumes: Task 1 Domain types (`LaunchSchedule`, `ScheduleEvaluationModels`, `Account`).
- Produces: `public static class LaunchScheduleEvaluatorHelper`:
  - `const int DefaultGraceSeconds = 90;`
  - `const int OpenOnlyConflictFootprintMinutes = 15;`
  - `bool TryParseTime(string? text, out TimeOnly time)`
  - `bool TryParseDate(string? text, out DateOnly date)`
  - `bool IsOvernight(LaunchSchedule schedule)`
  - `ScheduleValidationResult Validate(LaunchSchedule schedule)`
  - `IReadOnlyList<ScheduleDueEvent> GetDueEvents(IReadOnlyList<LaunchSchedule> schedules, DateTimeOffset localNow, TimeZoneInfo? tz = null)`
  - `IReadOnlyList<LaunchSchedule> GetStartupCatchUpSchedules(IReadOnlyList<LaunchSchedule> schedules, DateTimeOffset localNow, TimeZoneInfo? tz = null)`
  - `bool IsWithinTriggerGraceWindow(LaunchSchedule schedule, ScheduleRunPhase phase, DateTimeOffset localNow, out string occurrenceKey, TimeZoneInfo? tz = null)`
  - `bool IsCurrentlyActive(LaunchSchedule schedule, DateTimeOffset localNow, TimeZoneInfo? tz = null)`
  - `ScheduleTransitionInfo? GetNextTransition(LaunchSchedule schedule, DateTimeOffset localNow, TimeZoneInfo? tz = null)`
  - `ScheduleTransitionInfo? GetSoonestTransition(IReadOnlyList<LaunchSchedule> schedules, DateTimeOffset localNow, TimeZoneInfo? tz = null)`
  - `IReadOnlyList<ScheduleTimelineSegment> ProjectDayTimeline(IReadOnlyList<LaunchSchedule> schedules, DateOnly date)`
  - `IReadOnlyList<ScheduleConflictInfo> DetectConflicts(IReadOnlyList<LaunchSchedule> schedules, DateOnly startDate, Func<LaunchSchedule, IReadOnlyCollection<string>> resolveAccountKeys)`

- [ ] **Step 1: Write failing tests in `tests/Shiro.Tests/Launch/LaunchScheduleEvaluatorTests.cs`**

Cover:
1. `Validate_RejectsEqualStartAndEndTime_InvalidTimeFormat_EmptyTarget_OrZeroPlaceIdWhenJoinServer`
2. `GetDueEvents_FiresStartAndEndWithin90sGraceWindow_AndRespectsOccurrenceDedupKeys`
3. `GetDueEvents_OvernightWeeklyShift_FiresCloseOnNextMorningEvenWhenNextWeekdayUnchecked` *(Review Focus #2)*
4. `GetDueEvents_ReturnsClosesOrderedBeforeOpens_AtExactHandoffBoundary`
5. `GetStartupCatchUpSchedules_SelectsOnlyOptInActiveSchedulesNotAlreadyTriggered`
6. `IsWithinTriggerGraceWindow_DistinguishesEarlyManualRunFromInWindowRun`
7. `GetNextTransition_FindsNextStartOrEndAcrossWeekdaysAndOneTimeDates`
8. `ProjectDayTimeline_SplitsOvernightCarryoverOntoNextDay_AndPacksConcurrentLanes` *(Review Focus #2)*
9. `DetectConflicts_DetectsAccountOverlap_OpenOnlyFootprint_AndExclusiveCloseCollision`
10. `DstConversion_HandlesInvalidSpringForwardAndAmbiguousFallBackTimesWithoutThrowing`

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test tests\Shiro.Tests --filter "FullyQualifiedName~LaunchScheduleEvaluatorTests"`
Expected: FAIL (`LaunchScheduleEvaluatorHelper` not found).

- [ ] **Step 3: Implement `LaunchScheduleEvaluatorHelper` in `src/Shiro/Services/Launch/LaunchScheduleEvaluatorHelper.cs`**

Implement exact Spec §5.2 rules (keep file `<= 485` lines; if close to 420 lines, split timeline/conflict projection into `LaunchScheduleEvaluatorHelper.Timeline.cs` partial):
- Inspect both `today` and `yesterday` start dates for overnight ownership.
- Resolve `(DateOnly, TimeOnly)` through `tz ?? TimeZoneInfo.Local` (`IsInvalidTime` → `+1h`, `IsAmbiguousTime` → first offset).
- Sort due events with all `Close` events first, then `Open` events.
- Pack timeline lanes greedily by sorting segments by `(StartMinute, EndMinute)` and assigning the lowest lane whose end `<= segment.StartMinute`.

- [ ] **Step 4: Run tests to verify they pass**

Run: `dotnet test tests\Shiro.Tests --filter "FullyQualifiedName~LaunchScheduleEvaluatorTests"`
Expected: PASS.

---

### Task 3: Scheduler-Aware Batch Launch, Action Executor & `LaunchSchedulerService`

**Files:**
- Create: `src/Shiro/Services/Launch/AccountLaunchBatchService.Scheduler.cs`
- Modify: `src/Shiro/Services/Launch/AccountLaunchBatchService.cs:11` (`internal static class` → `internal static partial class`)
- Create: `src/Shiro/Services/Launch/ISchedulerActionExecutor.cs`
- Create: `src/Shiro/Services/Launch/ILaunchSchedulerService.cs`
- Create: `src/Shiro/Services/Launch/SchedulerHostActionExecutorService.cs`
- Create: `src/Shiro/Services/Launch/LaunchSchedulerService.cs`
- Create: `src/Shiro/Services/Launch/LaunchSchedulerService.Execution.cs`
- Create: `tests/Shiro.Tests/TestInfrastructure/TestTimeProvider.cs`
- Test: `tests/Shiro.Tests/Launch/LaunchSchedulerServiceTests.cs`

**Interfaces:**
- Consumes: Task 1 (`LaunchScheduleStore`, domain types), Task 2 (`LaunchScheduleEvaluatorHelper`), `IAccountRepository`, `AccountPersistenceService.IsVaultLockedPendingMasterPassword`, `ISettingsService`, `IToastNotifier`, `IHostUiBridgeService`, `ILaunchCoordinator`, `RobloxProcessService.CloseAccountRobloxProcesses`, `RobloxWindowArrangerService.ResolveRunningAccountPids` / `ArrangeWindows`, `WebhookNotificationService.EnqueueAlert`.
- Produces:
  - `AccountLaunchBatchService.LaunchAccountsForSchedulerAsync(ILaunchCoordinator, IReadOnlyList<Account>, long placeId, string? jobId, bool vipServer, int delaySeconds, bool asyncJoin, Action<string>? statusCallback, CancellationToken)` → `Task<LaunchBatchResult>` (builds `LaunchRequest` directly without `SavedJobId` fallback, suppresses generic summary toast).
  - `AccountLaunchBatchService.LaunchHomeAccountsForSchedulerAsync(...)` → `Task<LaunchBatchResult>`.
  - `ISchedulerActionExecutor`:
    - `IReadOnlySet<Account> GetLiveRunningAccounts(IReadOnlyList<Account> candidates)`
    - `Task<int> CloseAccountsAsync(IReadOnlyList<Account> accounts, CancellationToken ct)`
    - `Task<bool> WaitForLaunchQueueIdleAsync(TimeSpan timeout, CancellationToken ct)`
    - `bool TryEnsureMultiRoblox(int targetCount)`
    - `Task<(int Succeeded, int Attempted, bool Cancelled, string Detail)> LaunchServerAsync(IReadOnlyList<Account> accounts, long placeId, string jobId, bool isVip, CancellationToken ct)`
    - `Task<(int Succeeded, int Attempted, bool Cancelled, string Detail)> LaunchHomeAsync(IReadOnlyList<Account> accounts, CancellationToken ct)`
    - `void ArrangeWindows(ScheduleArrangeMode mode)`
    - `void NotifyWebhook(LaunchSchedule schedule, ScheduleRunEntry entry)`
    - `void NotifyLiveProcessStatusChanged()`
  - `ILaunchSchedulerService : IDisposable`:
    - `bool IsGlobalEnabled { get; set; }`
    - `bool IsRunning { get; }`
    - `IReadOnlyList<LaunchSchedule> GetSchedules()`
    - `IReadOnlyList<ScheduleRunEntry> GetHistory()`
    - `event Action? SchedulesChanged`, `event Action? HistoryChanged`, `event Action? StateChanged`
    - `void AttachExecutor(ISchedulerActionExecutor executor)`
    - `void Start()`, `void StopForShutdown()`
    - `ScheduleValidationResult UpsertSchedule(LaunchSchedule schedule)`
    - `bool DeleteSchedule(string scheduleId, out LaunchSchedule? removed)`
    - `bool RestoreSchedule(LaunchSchedule schedule)`
    - `bool SetScheduleEnabled(string scheduleId, bool enabled)`
    - `void ClearHistory()`
    - `Task<ScheduleRunEntry?> RunScheduleNowAsync(string scheduleId, CancellationToken ct = default)`
    - `Task<ScheduleRunEntry?> CloseScheduleTabsNowAsync(string scheduleId, CancellationToken ct = default)`
    - `internal Task EvaluateTickOnceAsync(CancellationToken ct = default)` (deterministic test hook)

- [ ] **Step 1: Create `tests/Shiro.Tests/TestInfrastructure/TestTimeProvider.cs` & write failing tests in `tests/Shiro.Tests/Launch/LaunchSchedulerServiceTests.cs`**

Cover:
1. `EvaluateTick_ExecutesClosesBeforeOpens_WaitsHandoffCooldown_ThenLaunches`
2. `EvaluateTick_Error273Prevention_ClosesLiveTargetAccountBeforeScheduledOpen`
3. `StartupCatchUp_SkipsAlreadyRunningAccounts_AndLaunchesOnlyMissingTargets`
4. `VaultLockedOnStartup_PausesTicksAndDefersCatchUpUntilUnlocked` *(Review Focus #1)*
5. `ManualRunNow_OutsideGraceWindow_DoesNotSuppressScheduledOpenOrDisableOneTime` *(Review Focus #3)*
6. `ManualRunNow_Inside90sGraceWindow_MarksOccurrenceKey_PreventingImmediateDuplicateRelaunch` *(Review Focus #3)*
7. `PublicServerSchedule_BypassesAccountSavedJobId_AndSuppressesGenericBatchToast` *(Review Focus #4)*
8. `QueueContention_WaitsUpTo30s_RecordsSkippedIfStillBusy_WithoutCancellingManualBatch`
9. `CloseOtherTabsBeforeLaunch_ClosesOnlyRunningNonTargetAccounts`
10. `InvalidAndBannedAccounts_AreSkippedAndReportedInHistory`
11. `OneTimeSchedule_AutoDisablesAfterTerminalPhase_AndPersists`
12. `PostLaunchArrange_AndWebhookNotification_InvokeExecutorWhenConfigured`

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test tests\Shiro.Tests --filter "FullyQualifiedName~LaunchSchedulerServiceTests"`
Expected: FAIL (types not defined).

- [ ] **Step 3: Implement scheduler batch helpers, executor, and `LaunchSchedulerService`**

- Make `AccountLaunchBatchService` `partial` and add `AccountLaunchBatchService.Scheduler.cs`: constructs `LaunchRequest(account, placeId, jobId ?? string.Empty, followUser: false, vipServer: isVip, openHome: openHome)` directly (bypassing `SavedJobId` fallback) and awaits `launchCoordinator.EnqueueAsync(...)` returning `LaunchBatchResult` with zero `ToastService` calls.
- Implement `ISchedulerActionExecutor`, `ILaunchSchedulerService`, `SchedulerHostActionExecutorService` (calling `WebhookNotificationService.EnqueueAlert` with Streamer Mode safe summary when `SendWebhookNotification` is true).
- Implement `LaunchSchedulerService.cs` (CRUD, `IsGlobalEnabled` persisted to `AppSettingsKeys.General.SchedulerEnabled`, `1s` `PeriodicTimer` loop using `TimeProvider`, `AccountRepository.AccountsChanged` subscription for vault-unlock catch-up, `StopForShutdown`) and `LaunchSchedulerService.Execution.cs` (serialized `SemaphoreSlim(1, 1)` gate, Close-before-Open ordering, Error 273 live target pre-close, `HandoffDelaySeconds` via `Task.Delay(delay, _timeProvider, ct)`, `30s` `WaitForLaunchQueueIdleAsync`, `3s` arrange settle delay, Toast emission, history append & store save).
- Keep each `.cs` file `<= 450` lines.

- [ ] **Step 4: Run tests to verify they pass**

Run: `dotnet test tests\Shiro.Tests --filter "FullyQualifiedName~LaunchSchedulerServiceTests"`
Expected: PASS.

---

### Task 4: DI Composition Root, `AccountManager` Wiring & Shutdown Registration

**Files:**
- Modify: `src/Shiro/Services/Composition/AppBootstrap.cs:385-400`
- Modify: `src/Shiro/Services/AppServices.cs:220-230`
- Modify: `src/Shiro/AccountManager.Lifecycle.cs` (add `<= 6` lines total across `InitializeBackendServices`, `InitializePostStartup`, `BuildShutdownCoordinator`)
- Test: `tests/Shiro.Tests/Launch/LaunchSchedulerBootstrapTests.cs`

**Interfaces:**
- Consumes: Task 1 (`LaunchScheduleStore`), Task 3 (`ILaunchSchedulerService`, `LaunchSchedulerService`, `SchedulerHostActionExecutorService`).
- Produces:
  - `AppServices.LaunchScheduleStore` (`LaunchScheduleStore`)
  - `AppServices.LaunchScheduler` (`ILaunchSchedulerService`)

- [ ] **Step 1: Write failing tests in `tests/Shiro.Tests/Launch/LaunchSchedulerBootstrapTests.cs`**

Cover:
1. `AppBootstrap_RegistersLaunchScheduleStoreAndScheduler_AsSingletons`
2. `AccountManager_ShutdownCoordinator_StopsSchedulerInStopMonitors_AndFlushesStoreInFlushPersistence`
3. `SchedulerServices_HaveZeroDependencyOnAccountManagerOrViews`

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test tests\Shiro.Tests --filter "FullyQualifiedName~LaunchSchedulerBootstrapTests"`
Expected: FAIL (`AppServices.LaunchScheduler` not defined).

- [ ] **Step 3: Wire DI & `AccountManager.Lifecycle.cs`**

- Register `LaunchScheduleStore` and `ILaunchSchedulerService` (`LaunchSchedulerService`) in `AppBootstrap.BuildContainerLocked`.
- Expose `AppServices.LaunchScheduleStore` and `AppServices.LaunchScheduler`.
- In `AccountManager.Lifecycle.cs` (staying strictly `<= 1150` lines total across all `AccountManager*.cs`):
  - Wire `AppServices.LaunchScheduler.AttachExecutor(new SchedulerHostActionExecutorService(() => launchCoordinator, GetConfiguredAccountJoinDelaySeconds, () => AppServices.GeneralSettings.GetBool("AsyncJoin"), () => TryEnableMultiRoblox(promptIfRobloxRunning: false), () => SafeLifetimeToken));`
  - Call `AppServices.LaunchScheduler.Start();` in `InitializePostStartup()`.
  - Register `LaunchScheduler.StopForShutdown` (`ShutdownPhase.StopMonitors`) and `LaunchScheduleStore.Flush` (`ShutdownPhase.FlushPersistence`) in `BuildShutdownCoordinator()`.

- [ ] **Step 4: Run bootstrap + architecture tests**

Run: `dotnet test tests\Shiro.Tests --filter "FullyQualifiedName~LaunchSchedulerBootstrapTests|FullyQualifiedName~ArchitectureTests"`
Expected: PASS (note: `AllCommandPaletteBuiltinCommands` still passes at 29 until Task 5).

---

### Task 5: Symmetric EN/VI Localization, Scheduler ViewModels & Command Palette

**Files:**
- Modify: `src/Shiro/Resources/Strings/en.json`
- Modify: `src/Shiro/Resources/Strings/vi.json`
- Create: `src/Shiro/ViewModels/Scheduler/SchedulerTimelineBlockViewModel.cs`
- Create: `src/Shiro/ViewModels/Scheduler/ScheduleItemViewModel.cs`
- Create: `src/Shiro/ViewModels/Scheduler/ScheduleEditorViewModel.cs`
- Create: `src/Shiro/ViewModels/Scheduler/SchedulerViewModel.cs`
- Modify: `src/Shiro/ViewModels/MainViewModel.cs`
- Modify: `src/Shiro/ViewModels/CommandPaletteCatalog.cs`
- Modify: `tests/Shiro.Tests/Architecture/ArchitectureTests.cs:128` (`29` → `31`)
- Modify: `tests/Shiro.Tests/Ui/CommandPalette2Tests.cs:179` (`29` → `31`)
- Test: `tests/Shiro.Tests/Ui/SchedulerViewModelTests.cs`

**Interfaces:**
- Consumes: Task 1–4 (`ILaunchSchedulerService`, `LaunchScheduleEvaluatorHelper`, `IAccountRepository`, `StreamerModeService`, `LocalizationService`, `ToastService.ShowWithUndo`).
- Produces:
  - `NavigationPage.Scheduler` on `MainViewModel` (`IsSchedulerActive`, `SchedulerViewModel Scheduler`, `NavigateToSchedulerCommand`, `OpenNewScheduleForAccounts(IReadOnlyList<Account> accounts, string? placeId, string? jobId)`).
  - `SchedulerViewModel` (`IsEngineEnabled`, `EnabledCount`, `HasActiveBadge`, `NextEventSummaryText`, `NextEventCountdownText`, `SelectedDayOffset`, `TimelineSegments`, `Conflicts`, `HasConflicts`, `FilteredSchedules`, `HistoryEntries`, `IsHistoryOpen`, `FilterMode`, `SearchText`, `Editor`, `NewScheduleCommand`, `ToggleHistoryCommand`, `ClearHistoryCommand`, `SelectDayCommand`, `SetFilterCommand`, `SetPageVisible(bool)`).
  - `ScheduleItemViewModel` (`Id`, `Name`, `WindowBadgeText`, `RecurrenceText`, `StatusBadgeText`, `TargetSummaryText`, `DestinationSummaryText`, `IsEnabled`, `IsActiveNow`, `HasConflict`, `AccentBrush`, `RunNowCommand`, `CloseTabsCommand`, `AddNextShiftCommand`, `EditCommand`, `DuplicateCommand`, `DeleteCommand`).
  - `ScheduleEditorViewModel` (`IsOpen`, `IsEditingExisting`, `Name`, `ColorHex`, `Recurrence`, `OneTimeDateText`, `DayMon..DaySun`, `StartTimeText`, `HasEndTime`, `EndTimeText`, `IsOvernightPreview`, `TargetMode`, `SelectedGroup`, `AvailableGroups`, `AccountChecklist`, `AccountSearchText`, `LaunchMode`, `PlaceIdText`, `JobIdText`, `JobIdMaskChar`, `CloseOtherTabsBeforeLaunch`, `HandoffDelaySeconds`, `PostLaunchArrange`, `CatchUpIfActiveOnStartup`, `SendWebhookNotification`, `ValidationError`, `LiveConflictWarning`, `ApplyDurationHoursCommand`, `SaveCommand`, `CancelCommand`, `SelectReadyAccountsCommand`, `UseDashboardSelectionCommand`, `ClearAccountsCommand`, `SelectRecentOrFavoriteGameCommand`).

- [ ] **Step 1: Write failing tests in `tests/Shiro.Tests/Ui/SchedulerViewModelTests.cs`**

Cover:
1. `CreateEditDuplicateAndDeleteWithUndo_UpdatesSchedulesAndRestoresOnUndo`
2. `DurationQuickChips_ComputeEndTimeAndFlagOvernightWraparound`
3. `AddNextShift_ChainsStartTimeFromPreviousEndTimeAndClonesGameTarget`
4. `StreamerModeToggle_MasksAccountUsernamesAndJobIdsInCardsEditorAndHistory` *(Review Focus #5)*
5. `Editor_ShowsLiveValidationAndNonBlockingConflictPreview`
6. `OpenNewScheduleForAccounts_NavigatesToSchedulerAndPrefillsAccountsPlaceAndJobId`
7. `FilterAndSearch_FilterByAllEnabledActiveNowPausedAndName`
8. `CommandPalette_IncludesNavSchedulerAndToolSchedulerCommands`

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test tests\Shiro.Tests --filter "FullyQualifiedName~SchedulerViewModelTests"`
Expected: FAIL (`SchedulerViewModel` not defined).

- [ ] **Step 3: Add symmetric EN/VI strings & implement Scheduler ViewModels + `MainViewModel` / `CommandPaletteCatalog`**

- Add all `Nav_Scheduler`, `Nav_Tip_Scheduler`, `Ctx_ScheduleLaunch`, `Bar_ScheduleSelected`, `Sched_*`, and `Scheduler.*` keys symmetrically to `en.json` and `vi.json` (sorted cleanly, zero missing keys).
- Implement the 4 ViewModel files under `src/Shiro/ViewModels/Scheduler/` (each `<= 450` lines, pure `ObservableObject` / `RelayCommand`, zero references to `AvaloniaUI.Views`; use `StreamerModeService.GetDisplayUsername` & `MaskJobId` for all rendered account/job labels; `1s` countdown timer runs ONLY when `IsPageVisible == true` and disposes cleanly in `MainViewModel.Dispose()`).
- Wire `NavigationPage.Scheduler`, `Scheduler`, `NavigateToSchedulerCommand`, `OpenNewScheduleForAccounts`, language & streamer refresh forwarding in `MainViewModel.cs`.
- Add `nav.scheduler` (`Icon="Clock"`) and `tool.scheduler` (`Icon="Play"`) to `CommandPaletteCatalog.cs`; update `29` → `31` in `ArchitectureTests.cs:128` and `CommandPalette2Tests.cs:179`.

- [ ] **Step 4: Run ViewModel, Localization & Command Palette tests**

Run: `dotnet test tests\Shiro.Tests --filter "FullyQualifiedName~SchedulerViewModelTests|FullyQualifiedName~LocalizationIntegrityTests|FullyQualifiedName~CommandPalette2Tests|FullyQualifiedName~AllCommandPaletteBuiltinCommands"`
Expected: PASS.

---

### Task 6: Liquid Obsidian 2026 Views (`SchedulerView`, Timeline, Editor Drawer, History) & Shell/Dashboard Wiring

**Files:**
- Create: `src/Shiro/AvaloniaUI/Views/Panels/SchedulerTimelinePanel.axaml` & `.axaml.cs`
- Create: `src/Shiro/AvaloniaUI/Views/Panels/SchedulerEditorDrawer.axaml` & `.axaml.cs`
- Create: `src/Shiro/AvaloniaUI/Views/Panels/SchedulerHistoryPanel.axaml` & `.axaml.cs`
- Create: `src/Shiro/AvaloniaUI/Views/SchedulerView.axaml` & `.axaml.cs`
- Modify: `src/Shiro/AvaloniaUI/Views/MainWindow.axaml`
- Modify: `src/Shiro/AvaloniaUI/Views/MainWindow.Navigation.cs`
- Modify: `src/Shiro/AvaloniaUI/Views/MainWindow.Chrome2026.cs`
- Modify: `src/Shiro/AvaloniaUI/Views/DashboardView.axaml` (add `<= 2` lines to keep `<= 400`)
- Modify: `src/Shiro/AvaloniaUI/Views/DashboardView.ContextMenu.cs`
- Modify: `src/Shiro/AvaloniaUI/Views/Panels/DashboardBulkBarPanel.axaml`
- Test: `tests/Shiro.Tests/Ui/SchedulerViewUiTests.cs`

**Interfaces:**
- Consumes: Task 5 ViewModels (`MainViewModel.Scheduler`, `SchedulerViewModel`, `ScheduleEditorViewModel`, `ScheduleItemViewModel`, `SchedulerTimelineBlockViewModel`), existing Liquid Obsidian controls (`SkiaGlassSurface`, `StatusPulseDot`, `SpringCompositionHelper`, `ThemeTokens.axaml`, `ViewStyles.axaml`).
- Produces: Named controls `BtnNavScheduler`, `ViewScheduler`, `ViewSchedulerTimeline`, `ViewSchedulerEditor`, `ViewSchedulerHistory`, `BtnNewSchedule`.

- [ ] **Step 1: Write failing headless Avalonia UI tests in `tests/Shiro.Tests/Ui/SchedulerViewUiTests.cs`**

Cover:
1. `MainWindow_NavigatesToScheduler_UpdatesActiveIndicator_AndRespectsCollapsedRail`
2. `SchedulerView_RendersTimelineCardsEditorAndHistory_WithZeroBindingErrors`
3. `EscapeKey_DismissesSchedulerEditorDrawer_BeforeDashboardSelection`
4. `TimelineBlockClick_OpensScheduleInEditorDrawer`
5. `DashboardContextMenuAndBulkBar_OpenSchedulerWithPrefilledTargets`
6. `StreamerModeToggle_UpdatesVisibleSchedulerControlsWithZeroBindingErrors` *(Review Focus #5)*

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test tests\Shiro.Tests --filter "FullyQualifiedName~SchedulerViewUiTests"`
Expected: FAIL (`SchedulerView` / `BtnNavScheduler` not found).

- [ ] **Step 3: Build Liquid Obsidian views & wire `MainWindow` + `DashboardView`**

- Keep EVERY `.axaml` file `<= 380` lines and every line `<= 200` chars; `x:CompileBindings="True"` on every view/panel; `{DynamicResource}` only for brushes/colors/loc keys and `{StaticResource}` for radii/shadows.
- `SchedulerTimelinePanel`: 24h axis (`00:00`..`24:00`), day selector pills, dynamic canvas/grid positioning timeline blocks proportionally (`Bounds.Width`) with clickable buttons + tooltips + vertical `Now` indicator line on `Today`, plus inline conflict warning banner.
- `SchedulerEditorDrawer`: non-modal `Width="390"` `SkiaGlassSurface` right slide-over with `SpringCompositionHelper` entrance, 4 compact sections (Identity & Timing with duration chips, Accounts Target with Group/Checklist toggle, Game Destination with Recent/Favorites picker, Rotation & Handoff options), inline validation & live conflict banner, `Cancel` / `Save`.
- `SchedulerHistoryPanel`: collapsible card showing the last 50 run entries with phase/outcome semantic badges and `Clear`.
- `SchedulerView`: header hero + live countdown + master switch + timeline + filter/search bar + schedule cards + empty state + overlaid `SchedulerEditorDrawer`.
- `MainWindow.axaml` / `Navigation.cs` / `Chrome2026.cs`: add `BtnNavScheduler` (with pulsing green badge matching `BtnNavTools`, `.collapsed` rail rule compatible), `ViewScheduler`, `ViewModel.Scheduler.SetPageVisible(activeView == ViewScheduler)`, and `if (vm.Scheduler.Editor.IsOpen) { vm.Scheduler.Editor.CloseCommand.Execute(null); return true; }` in `TryDismissTopmostLayer`.
- `DashboardView.axaml` / `ContextMenu.cs` / `DashboardBulkBarPanel.axaml`: add `"Schedule Launch..."` and `"Schedule Selected..."` shortcuts calling `ViewModel.OpenNewScheduleForAccounts(...)`.

- [ ] **Step 4: Run UI + Guardrail + Sidebar tests**

Run: `dotnet test tests\Shiro.Tests --filter "FullyQualifiedName~SchedulerViewUiTests|FullyQualifiedName~Shiro2026UiGuardrailTests|FullyQualifiedName~SidebarRailAndNavigationTests"`
Expected: PASS (0 binding errors, all Lucide icons resolved, collapsed rail intact).

---

### Task 7: Full Verification, Architecture Compliance & Coverage Gates

**Files:**
- Verify only (fix any regressions uncovered across modified/created files).

- [ ] **Step 1: Run complete unit + headless UI + architecture test suite**

Run: `dotnet test tests\Shiro.Tests --configuration Release`
Expected: PASS (0 build warnings with `TreatWarningsAsErrors=true`, all 2,619+ existing tests + all new Scheduler tests passing).

- [ ] **Step 2: Run Cobertura coverage check against CI gates**

Run: `dotnet test tests\Shiro.Tests --collect:"XPlat Code Coverage" --settings tests\coverage.runsettings --results-directory coverage-scheduler && powershell -ExecutionPolicy Bypass -File .\scripts\Check-Coverage.ps1 -CoverageDir "coverage-scheduler"`
Expected: PASS (`Overall >= 75.0%`, `Branch >= 60.0%`, `Core >= 92.0%`, all `coverage-floors.json` entries satisfied). Clean up temporary `coverage-scheduler` directory afterward.

- [ ] **Step 3: Review `git status` and diff hygiene**

Run: `git status --short`
Expected: Only intended Scheduler files created/modified (unrelated pre-existing user edits untouched), no temporary coverage artifacts left behind.
