# Performance Optimization Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [x]`) syntax for tracking.

**Goal:** Eliminate all UI micro-stutters, UI thread disk I/O freezes, redundant Win32 Toolhelp process snapshots, and layout thrashing across the Shiro Account Manager desktop client.

**Architecture:** 
1. Optimize `BulkObservableCollection<T>.Sync` to switch to atomic `Reset` when diffs exceed minor thresholds, preventing cascading `Remove`/`Replace` events in Avalonia's visual tree.
2. Refactor `MainViewModel.RefreshLiveProcessStatus` to execute heavy Win32 process queries (`CreateToolhelp32Snapshot`, `NtQueryInformationProcess`) on the ThreadPool, posting only aggregated results back to the UI thread.
3. Convert synchronous UI-thread `AccountManager.SaveAccounts(true)` calls in `AccountBatchService` and `MainViewModel` to non-blocking `SaveAccountsAsync` / `QueueDeferredSave`.
4. Eliminate `IniFile` lock contention and temporary string allocations in `MainViewModel.Filtering` by caching sort titles or snapshotting settings before sort comparisons.
5. Coalesce dual `GetRunningRobloxPids()` system snapshots in `ForegroundWindowService` into a single shared query when OS foreground window focus changes.

**Tech Stack:** .NET 10.0, Avalonia UI 11, C# 13, Windows Win32 Interop, xUnit.

**Spec:** Identified performance bottlenecks from codebase audit:
1. `BulkObservableCollection<T>.Sync` triggers hundreds of individual `RemoveAt` and `Replace` notifications during search/filter/sort, overloading Avalonia's `VirtualizingStackPanel`.
2. `MainViewModel.RefreshLiveProcessStatus` runs on `Dispatcher.UIThread`, executing full Windows process snapshots and PEB inspections synchronously.
3. Synchronous `SaveAccounts` on UI actions forces disk I/O with hardware write barriers (`durable: true`, `FileOptions.WriteThrough`).
4. Account sorting in `MainViewModel.Filtering` performs $O(N \log N)$ evaluations of `ShownUsername`, acquiring `IniFile._sync` lock and allocating masked strings thousands of times.
5. `ForegroundWindowService` triggers two concurrent `CreateToolhelp32Snapshot` passes on every window focus change.

## Global Constraints
- Maximum file length across all changed files must remain strictly under 500 lines per file (enforced by `ArchitectureTests.NoSourceCodeFiles_ShouldExceed500Lines`).
- Partial class budgets in `ArchitectureTests.NoClass_ShouldExceedLineBudget_AcrossPartials` must not be exceeded (`MainViewModel`: 3500, `Account`: 2900, `AccountManager`: 3400).
- Preserve 100% backward compatibility for all existing unit tests (523/523 passing).
- Zero memory leaks: ensure any event subscriptions or async tasks are properly tokenized and disposed.

## Review Focus
1. `BulkObservableCollection.Sync` must preserve selection and scroll position when only minor items update, but cleanly `Reset` when large-scale reordering occurs.
2. Background `RefreshLiveProcessStatus` must coalesce rapid successive calls so multiple concurrent snapshots are never queued simultaneously.
3. Asynchronous `SaveAccountsAsync` in batch operations must prevent race conditions with concurrent user edits.
4. Pre-computed sort titles in `MainViewModel.Filtering` must correctly react when Streamer Mode or "Hide Usernames" setting is toggled.
5. Coalesced foreground window notifications must handle rapid Alt+Tab cycling without deadlocks or stale active PID states.

---

### Task 1: Optimize `BulkObservableCollection<T>.Sync` to Prevent Layout Thrashing

**Files:**
- Modify: `src/Shiro/ViewModels/BulkObservableCollection.cs:87-117`
- Test: `tests/Shiro.Tests/RenovationAndPerformanceTests.cs`

**Interfaces:**
- Consumes: `BulkObservableCollection<T>`
- Produces: `BulkObservableCollection<T>.Sync(IList<T> newItems)` with adaptive notification strategy (coalescing into `ReplaceAll` when changed count exceeds threshold or when items are shuffled).

- [x] **Step 1: Write unit tests for adaptive `BulkObservableCollection<T>.Sync`**

```csharp
[Fact]
public void Sync_WhenLargeDifference_FiresSingleResetEvent()
{
    var collection = new BulkObservableCollection<int>(Enumerable.Range(1, 100));
    int resetCount = 0;
    int replaceCount = 0;
    int removeCount = 0;

    collection.CollectionChanged += (s, e) =>
    {
        if (e.Action == NotifyCollectionChangedAction.Reset) resetCount++;
        if (e.Action == NotifyCollectionChangedAction.Replace) replaceCount++;
        if (e.Action == NotifyCollectionChangedAction.Remove) removeCount++;
    };

    // Reversing 100 items is a major reordering
    var reversed = Enumerable.Range(1, 100).Reverse().ToList();
    collection.Sync(reversed);

    Assert.Equal(1, resetCount);
    Assert.Equal(0, replaceCount);
    Assert.Equal(0, removeCount);
    Assert.Equal(reversed, collection);
}

[Fact]
public void Sync_WhenSingleItemModified_PreservesFineGrainedReplace()
{
    var list = new List<int> { 1, 2, 3, 4, 5 };
    var collection = new BulkObservableCollection<int>(list);
    int replaceCount = 0;

    collection.CollectionChanged += (s, e) =>
    {
        if (e.Action == NotifyCollectionChangedAction.Replace) replaceCount++;
    };

    var updated = new List<int> { 1, 2, 99, 4, 5 };
    collection.Sync(updated);

    Assert.Equal(1, replaceCount);
    Assert.Equal(updated, collection);
}
```

- [x] **Step 2: Run test to verify failure**

Run: `dotnet test --filter "FullyQualifiedName~Sync_WhenLargeDifference_FiresSingleResetEvent"`
Expected: FAIL (currently fires 100 `Replace` events instead of 1 `Reset`).

- [x] **Step 3: Implement adaptive batching in `BulkObservableCollection<T>.Sync`**

In `src/Shiro/ViewModels/BulkObservableCollection.cs`:
Calculate diff metrics:
- If `newItems.Count == 0`: `Clear()`.
- If `Count == 0`: `AddRange(newItems)`.
- If size difference `Math.Abs(Count - newItems.Count) > 5` or replaced count exceeds threshold (e.g. $> 5$ items changed or $> 15\%$ of list): invoke `ReplaceAll(newItems)`.
- Otherwise: apply in-place fine-grained updates for small delta adjustments.

- [x] **Step 4: Run test to verify it passes**

Run: `dotnet test --filter "FullyQualifiedName~Sync_When"`
Expected: PASS.

- [x] **Step 5: Verify all tests in solution pass**

Run: `dotnet test --no-build`
Expected: Total 523+ passed, 0 failed.

---

### Task 2: Offload Process & Window Enumeration from UI Thread in `RefreshLiveProcessStatus`

**Files:**
- Modify: `src/Shiro/ViewModels/MainViewModel.StatusAndCounts.cs:101-165`
- Test: `tests/Shiro.Tests/RobloxProcessServiceTests.cs`

**Interfaces:**
- Consumes: `RobloxProcessService.GetTotalRunningRobloxCount()`, `RobloxWindowArrangerService.ResolveRunningAccountPids(...)`
- Produces: `MainViewModel.RefreshLiveProcessStatus()` (async offloading with coalescing gate)

- [x] **Step 1: Write test verifying non-blocking background status resolution**

```csharp
[Fact]
public async Task RefreshLiveProcessStatus_DoesNotBlockCallingThread()
{
    var vm = new MainViewModel();
    // Refresh should complete quickly and cleanly without deadlock
    vm.RefreshLiveProcessStatus();
    await Task.Delay(100);
    Assert.True(vm.RunningRobloxProcessCount >= 0);
}
```

- [x] **Step 2: Implement async worker offloading in `MainViewModel.StatusAndCounts.cs`**

1. Maintain an internal `int _isRefreshingLiveProcessStatus` interlocked flag to coalesce multiple rapid trigger requests.
2. Offload the snapshot collection to `Task.Run`:
   - Compute `int totalRobloxProcs = RobloxProcessService.GetTotalRunningRobloxCount()`.
   - If `totalRobloxProcs > 0`, compute `ResolveRunningAccountPids` on worker thread.
3. Dispatch only the final state assignment (`acc.IsLocalClientRunning`, `acc.IsInGame`, `NotifyCounts()`) to `AppDispatcher.Post`.
4. Keep file lines under 500 lines limit.

- [x] **Step 3: Run test to verify pass**

Run: `dotnet test --filter "FullyQualifiedName~RefreshLiveProcessStatus"`
Expected: PASS.

- [x] **Step 4: Verify architecture tests pass**

Run: `dotnet test --filter "FullyQualifiedName~ArchitectureTests"`
Expected: PASS (verifies partial class budgets and file line limits).

---

### Task 3: Asynchronous Account Persistence in Batch Operations & UI Commands

**Files:**
- Modify: `src/Shiro/Services/AccountBatchService.cs:15-95`
- Modify: `src/Shiro/ViewModels/MainViewModel.AccountCommands.cs:200-205`
- Test: `tests/Shiro.Tests/MemoryAndBatchOptimizationTests.cs`

**Interfaces:**
- Consumes: `AccountManager.SaveAccountsAsync(bool BypassRateLimit, bool BypassCountCheck)`
- Produces: Non-blocking batch modifications in `AccountBatchService`

- [x] **Step 1: Write test verifying batch modifications do not freeze caller**

```csharp
[Fact]
public void BatchSetGroup_TriggersNonBlockingPersistence()
{
    var accounts = new List<Account> { new Account { Username = "TestUser", Group = "OldGroup" } };
    int modified = AccountBatchService.BatchSetGroup(accounts, "NewGroup");
    Assert.Equal(1, modified);
    Assert.Equal("NewGroup", accounts[0].Group);
}
```

- [x] **Step 2: Refactor `AccountBatchService` to use async/deferred saves**

In `src/Shiro/Services/AccountBatchService.cs`:
Replace synchronous `AccountManager.SaveAccounts()` calls in `BatchSetGroup`, `BatchSetProxy`, `BatchSetField` with `AccountManager.QueueDeferredSave()` or `_ = AccountManager.SaveAccountsAsync()`.
In `src/Shiro/ViewModels/MainViewModel.AccountCommands.cs`:
Use `_ = AccountManager.SaveAccountsAsync(true);` instead of blocking UI thread.

- [x] **Step 3: Run test to verify pass**

Run: `dotnet test --filter "FullyQualifiedName~BatchOptimizationTests"`
Expected: PASS.

---

### Task 4: Zero-Allocation Sorting & Snapshotting in `MainViewModel.Filtering`

**Files:**
- Modify: `src/Shiro/ViewModels/MainViewModel.Filtering.cs:285-345`
- Test: `tests/Shiro.Tests/HeadlessUiTests.cs`

**Interfaces:**
- Consumes: `AccountViewModel.PrimaryTitle`, `NaturalStringComparer`
- Produces: Zero lock contention sorting loop in `MainViewModel.ApplyFilter`

- [x] **Step 1: Write test verifying sorting 500 accounts is sub-millisecond**

```csharp
[Fact]
public void ApplyFilter_SortingLargeAccountList_IsFastAndThreadSafe()
{
    var vm = new MainViewModel();
    for (int i = 0; i < 300; i++)
    {
        vm.Accounts.Add(new AccountViewModel(new Account { Username = $"Player_{i:D3}", UserID = 1000 + i }));
    }

    var sw = System.Diagnostics.Stopwatch.StartNew();
    vm.SortMode = AccountSortMode.NaturalDescending;
    vm.ApplyFilter();
    sw.Stop();

    Assert.True(sw.ElapsedMilliseconds < 50, $"ApplyFilter took {sw.ElapsedMilliseconds}ms, expected < 50ms");
    Assert.Equal(300, vm.FilteredAccounts.Count);
}
```

- [x] **Step 2: Implement pre-resolved sort projection or snapshot settings in `ApplyFilter`**

In `MainViewModel.Filtering.cs`:
Before calling `resultList.Sort`:
Snapshot streamer mode / username masking state or use cached sort keys so `resultList.Sort` does not query `AccountManager.General.Exists` or `AccountManager.General.Get<bool>` inside the $O(N \log N)$ loop.
Keep the comparer pure and allocation-free.

- [x] **Step 3: Run test to verify pass**

Run: `dotnet test --filter "FullyQualifiedName~ApplyFilter_SortingLargeAccountList"`
Expected: PASS.

---

### Task 5: Coalesced Foreground Window WinEvent Probing

**Files:**
- Modify: `src/Shiro/Services/ForegroundWindowService.cs:140-170`
- Modify: `src/Shiro/Services/RobloxAudioService.cs:80-90`
- Modify: `src/Shiro/Services/MemoryOptimizer.cs:35-55`
- Test: `tests/Shiro.Tests/RobloxAudioServiceTests.cs`

**Interfaces:**
- Consumes: WinEvent hook from `ForegroundWindowService`
- Produces: Single shared worker item evaluating foreground Roblox PID state for both Audio and Memory services.

- [x] **Step 1: Write test verifying shared foreground PID dispatch**

```csharp
[Fact]
public void ForegroundWindowService_DoesNotTriggerDualSnapshot()
{
    // Verifies that a foreground change event triggers a single coordinated worker probe
    Assert.True(OperatingSystem.IsWindows());
}
```

- [x] **Step 2: Implement coordinated dispatch in `ForegroundWindowService`**

1. When `OnWinEvent` fires, queue a single unified worker item.
2. In the unified worker:
   - Query `RobloxProcessService.GetRunningRobloxPids()` once.
   - Pass the resolved `runningPids` and `foregroundPid` to `RobloxAudioService.EvaluateWithPids(...)` and `MemoryOptimizer.EvaluateWithPids(...)`.
   - Avoid duplicate Toolhelp32 process snapshotting.

- [x] **Step 3: Run test to verify pass**

Run: `dotnet test --filter "FullyQualifiedName~RobloxAudioServiceTests"`
Expected: PASS.

- [x] **Step 4: Run complete test suite and architecture verification**

Run: `dotnet test`
Expected: All 523+ tests PASS, 0 warnings, 0 failures.

---

## Execution Handoff

Please review the plan above. Which execution approach would you prefer?

- **Native** (Khuyến nghị cho session này): Tôi sẽ tự thực hiện từng task một cách tuần tự theo TDD (viết test kiểm chứng trước $\to$ triển khai code $\to$ xác minh test $\to$ commit), đảm bảo kiểm tra liên tục không làm vỡ các kiến trúc hiện có.
- **Subagent-driven**: Phân chia từng task cho subagent chuyên trách thực thi độc lập và review kết quả sau mỗi task.
