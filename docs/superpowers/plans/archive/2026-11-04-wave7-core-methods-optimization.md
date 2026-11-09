# Wave 7 Core Methods Bugfix & Performance Optimization Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Eliminate the 5 remaining Wave 7 bottlenecks & latent bugs across `Batch`, `RobloxWatcher`/`RobloxProcess`, `AccountLineParser`/`BulkImportViewModel`, `AccountApiDispatcher`/`AccountExportService`, and `ServerListViewModel`.

**Architecture:**
1. **`Batch.GetImage` & `Batch.ProcessPlaceAsync` (`src/Shiro/Classes/Batch.cs`):** Add `RequestId` (`[JsonPropertyName("requestId")]`) to `ThumbnailBatchItem` and build an $O(1)$ `BatchLookupResult` (`ByRequestId` + `ByTargetId` fallback) inside `ProcessBatchAsync` so concurrent callers with the same `targetId` but different `type`/`size` never collide or scan $O(N^2)$ lists. Cap the `AppSettings.LastValidAccount` wait in `ProcessPlaceAsync` to a fast check (skip immediately when `AppServices.AccountRepository.Count == 0`, otherwise at most 6 ticks $\times$ 50ms = 300ms) so unauthenticated startups fall back to public APIs without an 8-second stall.
2. **Deduplicated PEB Command-Line Tracker Lookup & Zero-Allocation Idle Tick (`src/Shiro/Classes/RobloxWatcher.cs`, `src/Shiro/Classes/RobloxProcess.cs`):** Pass `trackerId` already extracted in `RobloxWatcher.CheckProcesses` directly into `new RobloxProcess(process, trackerId)` and cache it in `_expectedTrackerId` so `WaitForLogPathAsync` never re-queries `NtQueryInformationProcess` across its 30 retries. Remove the redundant `RobloxWatcher.Instances.Any(...)` scan inside the candidate loop (already filtered by `claimedPaths`) and only allocate `activePids` when `CloseIfWindowTitle || RememberWindowPositions` is enabled.
3. **Zero-Allocation `SpanLineEnumerator` in `AccountLineParser` & Bounded Parallel `LookupUserIdAsync` in `BulkImportViewModel` (`src/Shiro/ViewModels/Dialogs/AccountLineParser.cs`, `src/Shiro/ViewModels/Dialogs/BulkImportViewModel.cs`):** Replace `raw.Split(new[] { '\r', '\n' })` and `line.Split(new[] { '|', ':' }, 3)` with `raw.AsSpan().EnumerateLines()` and `ReadOnlySpan<char>` slicing (`ParseAccountLineSpan`). In `BulkImportViewModel.StartImportAsync`, pre-resolve missing UserIDs for new `user:pass` entries concurrently using `Parallel.ForEachAsync` bounded by `_cookieConcurrency`.
4. **$O(1)$ Account Lookup & Source-Generated JSON in `AccountApiDispatcher` & `AccountExportService` (`src/Shiro/Services/Network/AccountApiDispatcher.cs`, `src/Shiro/Services/Network/AccountApiDispatcher.Endpoints.cs`, `src/Shiro/Services/Storage/AccountExportService.cs`, `src/Shiro/Services/ShiroAppJson.cs`):** Replace the $O(N)$ `foreach` account scan in `HandleApiEndpointAsync` with $O(1)$ `AccountRepository.GetByUsername` / `GetById`. Replace anonymous object serialization in `AccountApiDispatcher` and `new JsonSerializerOptions { WriteIndented = true }` in `AccountExportService` with source-generated `readonly record struct` DTOs registered in `ShiroAppJsonContext`.
5. **Single-Pass Filter + In-Place Static `Comparison<T>` Sort in `ServerListViewModel` (`src/Shiro/ViewModels/ServerListViewModel.Sorting.cs`, `src/Shiro/ViewModels/ServerListViewModel.ServerOperations.cs`):** Replace chained LINQ `.Where().OrderBy().ThenBy().ToList()` and unconditional `_servers.ReplaceAll(...)` with a single-pass `List<ServerItemViewModel>` filter, static `Comparison<ServerItemViewModel>` delegates (`SortPlayersAsc`, `SortPlayersDesc`, `SortPingAsc`, `SortPingDesc`), and `_servers.Sync(filtered)`. Replace `.Select(async ...)` + `SemaphoreSlim` in `LoadAllRegionsAsync` with `Parallel.ForEachAsync` (`MaxDegreeOfParallelism = 4`).

**Tech Stack:** .NET 10, C# 13, `System.Text.Json` Source Generation, `ReadOnlySpan<char>` / `SpanLineEnumerator`, `Parallel.ForEachAsync`, xUnit.

---

### Task 1: $O(1)$ `RequestId`-Keyed `Batch.GetImage` & Fast Fallback in `Batch.ProcessPlaceAsync`

**Files:**
- Modify: `src/Shiro/Classes/Batch.cs`
- Test: `tests/Shiro.Tests/Wave7CoreOptimizationTests.cs`

- [ ] **Step 1: Update `ThumbnailBatchItem` and `BatchWork` in `src/Shiro/Classes/Batch.cs`**
  - Add `[JsonPropertyName("requestId")] public string? RequestId { get; set; }` to `ThumbnailBatchItem`.
  - Add an internal `BatchLookupResult` class or struct holding `Dictionary<string, string> ByRequestId` and `Dictionary<long, string> ByTargetId` so each `GetImage` caller resolves its exact `requestId` in $O(1)$ (falling back to `ByTargetId` when a test/mock omits `requestId`).
  - In `ProcessPlaceAsync`, only wait for `AppSettings.LastValidAccount` if `AppServices.AccountRepository.Count > 0`, and cap the wait loop to at most `6` attempts of `50ms` (`300ms` max instead of `8,000ms`).

---

### Task 2: Deduplicate Tracker ID Resolution & Optimize `RobloxWatcher` / `RobloxProcess`

**Files:**
- Modify: `src/Shiro/Classes/RobloxWatcher.cs`
- Modify: `src/Shiro/Classes/RobloxProcess.cs`
- Test: `tests/Shiro.Tests/Wave7CoreOptimizationTests.cs`

- [ ] **Step 1: Avoid redundant `HashSet<int>` allocation in `RobloxWatcher.CheckProcesses` and pass `trackerId` to `RobloxProcess`**
  - In `RobloxWatcher.CheckProcesses`, only build `activePids` and `BuildMainWindowMap(activePids)` when `CloseIfWindowTitle || RememberWindowPositions` is `true`.
  - Pass `trackerId` into `new RobloxProcess(process, trackerId)`.
- [ ] **Step 2: Cache `_expectedTrackerId` in `RobloxProcess` and remove duplicate `Instances.Any` lock in `WaitForLogPathAsync`**
  - Store `private string? _expectedTrackerId;` in `RobloxProcess` (initialized from constructor argument, or lazily once via `ResolveExpectedTrackerIdForPid` if `null`).
  - Remove the redundant `lock (RobloxWatcher.InstanceRegistryLock) { if (RobloxWatcher.Instances.Any(...)) continue; }` inside the `candidateLogs` loop since `claimedPaths` already contains all claimed log paths.

---

### Task 3: Span-Based `AccountLineParser` & Bounded Parallel `UserPass` Lookup in `BulkImportViewModel`

**Files:**
- Modify: `src/Shiro/ViewModels/Dialogs/AccountLineParser.cs`
- Modify: `src/Shiro/ViewModels/Dialogs/BulkImportViewModel.cs`
- Test: `tests/Shiro.Tests/Wave7CoreOptimizationTests.cs`

- [ ] **Step 1: Rewrite `AccountLineParser.Classify` & `ParseAccountLine` with `ReadOnlySpan<char>` and `SpanLineEnumerator`**
  - Use `foreach (ReadOnlySpan<char> rawLine in raw.AsSpan().EnumerateLines())` to iterate lines without allocating a `string[]`.
  - Implement `ParseAccountLineSpan(ReadOnlySpan<char> line, out string? cookie, out string? password)` and `CleanCookieSpan(ReadOnlySpan<char> span)` using span slicing instead of `string.Split(new[] { '|' }, 3)` / `string.Split(new[] { ':' }, 3)`.
- [ ] **Step 2: Parallelize `LookupUserIdAsync` for new `UserPass` accounts in `BulkImportViewModel.StartImportAsync`**
  - Pre-resolve `UserID` for unknown usernames using `Parallel.ForEachAsync` with `MaxDegreeOfParallelism = _cookieConcurrency` into a `ConcurrentDictionary<string, long>(StringComparer.OrdinalIgnoreCase)`, then add/update the accounts deterministically in input order.

---

### Task 4: $O(1)$ Account Lookup & Source-Generated JSON in `AccountApiDispatcher` & `AccountExportService`

**Files:**
- Modify: `src/Shiro/Services/ShiroAppJson.cs`
- Modify: `src/Shiro/Services/Storage/AccountExportService.cs`
- Modify: `src/Shiro/Services/Network/AccountApiDispatcher.cs`
- Modify: `src/Shiro/Services/Network/AccountApiDispatcher.Endpoints.cs`
- Test: `tests/Shiro.Tests/Wave7CoreOptimizationTests.cs`

- [ ] **Step 1: Register strongly-typed DTOs in `src/Shiro/Services/ShiroAppJson.cs`**
  - Add `readonly record struct` types:
    - `AccountExportJsonItem(string? Username, long UserID, string? Alias, string? Group, string? Password, long RobuxBalance, string? Proxy, DateTime LastUse, string? SecurityToken)` (and `List<AccountExportJsonItem>`)
    - `WebServerV2Response(bool Success, string Message)`
    - `WebSocketStatsPayload(int ActiveClients, string Server, string[] Endpoints)`
    - `AntiAfkStatusPayload(bool Enabled, int TapsSent, string Status)`
    - `AntiAfkTogglePayload(bool Enabled, string Status)`
    - `MemoryTrimPayload(long BytesSaved, int ClientsCount)`
    - `MultiRobloxStatusPayload(bool Enabled, string Status, int TotalClosed, bool MutexHeld)`
    - `MultiRobloxTogglePayload(bool Enabled, string Status)`
    - `HideWindowsPayload(int HiddenCount, int TotalClients, bool AreHidden)`
    - `ShowWindowsPayload(int ShownCount, int TotalClients, bool AreHidden)`
    - `ToggleWindowsPayload(bool AreHidden, int AffectedCount)`
    - `PotatoModeTogglePayload(bool PotatoMode)`
    - `AutoReconnectStatusPayload(bool Enabled, int ActiveSessions, int TotalReconnections)`
    - `AutoReconnectTogglePayload(bool Enabled, int ActiveSessions)`
- [ ] **Step 2: Update `AccountExportService.ExportAccounts` to use `List<AccountExportJsonItem>` and `FastJson.Serialize(..., indented: true)`**
  - Eliminate `new JsonSerializerOptions { WriteIndented = true }` and anonymous `.Select(...)` projection.
- [ ] **Step 3: Update `AccountApiDispatcher` to use $O(1)$ `AccountRepository` lookup and typed DTOs**
  - Replace the `foreach (Account candidate in AppServices.AccountRepository.GetAll())` loop in `HandleApiEndpointAsync` with `ResolveTargetAccount(accountParam)` using `AppServices.AccountRepository.GetByUsername(accountParam)` and `AppServices.AccountRepository.GetById(targetAccountId)`.
  - Use `JsonSerializer.Serialize(..., ShiroAppJson.Default.*)` for `WebServerResponse` and the status/toggle endpoints.

---

### Task 5: Single-Pass Filter, Static `Comparison<T>` Sort & `Parallel.ForEachAsync` in `ServerListViewModel`

**Files:**
- Modify: `src/Shiro/ViewModels/ServerListViewModel.Sorting.cs`
- Modify: `src/Shiro/ViewModels/ServerListViewModel.ServerOperations.cs`
- Test: `tests/Shiro.Tests/Wave7CoreOptimizationTests.cs`

- [ ] **Step 1: Optimize `ApplyServerSortAndFilter` in `src/Shiro/ViewModels/ServerListViewModel.Sorting.cs`**
  - Compute `minPlayersThreshold` and filter `_allServers` into a pre-sized `List<ServerItemViewModel>(_allServers.Count)` without LINQ iterators.
  - Sort `filtered` in-place using static `Comparison<ServerItemViewModel>` methods (`ComparePlayersAsc`, `ComparePlayersDesc`, `ComparePingAsc`, `ComparePingDesc`).
  - Update `_servers.Sync(filtered)` so unchanged or minimally changed server lists do not trigger full UI layout resets.
- [ ] **Step 2: Optimize `LoadAllRegionsAsync` in `src/Shiro/ViewModels/ServerListViewModel.ServerOperations.cs`**
  - Hoist `string notLoadedText = LocalizationService.Get("ServerList.NotLoaded");` outside the filter loop.
  - Use `Parallel.ForEachAsync(pendingServers, new ParallelOptions { MaxDegreeOfParallelism = 4, CancellationToken = ct }, async (server, token) => await LoadRegionCoreAsync(server, token))` instead of `SemaphoreSlim` + `.Select(...).ToArray()`.
