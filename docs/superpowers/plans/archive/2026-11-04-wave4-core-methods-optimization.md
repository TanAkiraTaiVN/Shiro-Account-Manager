# Wave 4 Core Methods Optimization Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Upgrade 6 remaining hot-path and core service methods across process detection, account repository indexing, avatar loading, proxy distribution, HTTP JSON request serialization, and backup file enumeration.

**Architecture:**
1. **Single-Pass Win32 Process Snapshot & Zero-Intermediate `string.Create` Masking (`StreamerModeService`):** Replace 8 consecutive `Process.GetProcessesByName` calls per timer tick with a single Win32 `CreateToolhelp32Snapshot` (`PROCESSENTRY32W`) pass with zero managed `Process` allocations, and upgrade `MaskUsername` to `string.Create`.
2. **Indexed $O(1)$ Account Lookup (`AccountRepository`):** Maintain a snapshot-bound lookup index (`ById` and `ByUsername` with `StringComparer.OrdinalIgnoreCase`) inside `AccountRepository` so `GetById` and `GetByUsername` resolve in $O(1)$ without linear scans or closure allocations, with automatic fallback if an `Account`'s `UserID` or `Username` is mutated in-place.
3. **Coalesced In-Flight Avatar Fetch & Decode (`AccountViewModel.Avatar`):** Replace `ConcurrentDictionary<long, byte> _requestedAvatarDownloads` with `ConcurrentDictionary<long, Task<(string Path, Bitmap? Bitmap)>> _inFlightAvatarLoads` so concurrent callers share a single download + disk-decode task and never get stuck in `_avatarLoadState = 2` with a `null` bitmap.
4. **Min-Heap `PriorityQueue` Sticky Proxy Distribution (`ProxyBindingService`):** Replace the $O(N \times M)$ `proxyUsage.MinBy(...)` loop in `DistributeProxiesSticky` with a `.NET` `PriorityQueue<string, (int Usage, int Order)>` min-heap running in $O((M + N) \log M)$ while preserving deterministic tie-breaking order.
5. **Direct UTF-8 `ByteArrayContent` Serialization (`FastJson` & `RestClient`):** Add `FastJson.SerializeToUtf8Bytes` (resolving runtime type metadata from `ShiroAppJson.Default`) and use `ByteArrayContent` in `RestClient.CreateHttpRequestMessage` to eliminate intermediate UTF-16 `string` allocations on JSON requests.
6. **Single-Stat `DirectoryInfo.EnumerateFiles` Backup Inspection (`AccountStore`):** Replace `Directory.GetFiles(...).OrderByDescending(f => new FileInfo(f).CreationTimeUtc)` and double `File.Exists` + `new FileInfo` calls with `DirectoryInfo.EnumerateFiles` (`WIN32_FIND_DATA` pre-populated metadata) and single `FileInfo` inspection.

**Tech Stack:** .NET 10, C# 13, Win32 Toolhelp32 (`CreateToolhelp32Snapshot`, `Process32FirstW`, `Process32NextW`), `PriorityQueue<TElement, TPriority>`, `System.Text.Json` Source Generation (`ShiroAppJson`), xUnit.

---

### Task 1: Single-Pass Win32 Process Snapshot & `string.Create` Masking in `StreamerModeService`

**Files:**
- Modify: `src/Shiro/Services/Windows/StreamerModeService.cs`
- Create: `tests/Shiro.Tests/Wave4CoreOptimizationTests.cs`

- [x] **Step 1: Write failing tests for `StreamerModeService` process name span matching, snapshot scan, and `MaskUsername`**

Create `tests/Shiro.Tests/Wave4CoreOptimizationTests.cs` with tests verifying:
- `StreamerModeService.MatchesStreamingProcessName(ReadOnlySpan<char>)` matches all 8 target streaming process names with or without `.exe` suffix (case-insensitively) and rejects unrelated processes.
- `StreamerModeService.HasAnyStreamingProcessRunning()` executes cleanly via the native Win32 snapshot without throwing.
- `StreamerModeService.MaskUsername` masks usernames of length `0`, `1`, `2`, `3`, and `12` accurately using `string.Create`.

- [x] **Step 2: Implement single-pass Win32 `CreateToolhelp32Snapshot` scan and `string.Create` in `StreamerModeService.cs`**

In `src/Shiro/Services/Windows/StreamerModeService.cs`:
- Add `CreateToolhelp32Snapshot`, `Process32FirstW`, `Process32NextW`, `CloseHandle`, and `PROCESSENTRY32W` interop definitions.
- Add `internal static bool MatchesStreamingProcessName(ReadOnlySpan<char> exeFile)` that strips `.exe` if present and checks against `StreamingProcessNames` using `StringComparison.OrdinalIgnoreCase` without heap allocations.
- Add `internal static bool HasAnyStreamingProcessRunning()` that scans `CreateToolhelp32Snapshot(TH32CS_SNAPPROCESS, 0)` in a single pass.
- Update `CheckStreamingProcesses` to call `HasAnyStreamingProcessRunning()`.
- Update `MaskUsername` to use `string.Create`.
- Replace legacy `Program.Logger` and `AppSettings.General` calls in `StreamerModeService.cs` with `AppServices.Logger` and `AppServices.GeneralSettings`.

---

### Task 2: Snapshot-Synchronized $O(1)$ Lookup Index in `AccountRepository`

**Files:**
- Modify: `src/Shiro/Services/Storage/AccountRepository.cs`
- Modify: `src/Shiro/AccountManager.Accounts.cs`
- Modify: `tests/Shiro.Tests/Wave4CoreOptimizationTests.cs`

- [x] **Step 1: Add unit tests for `AccountRepository.GetById` and `AccountRepository.GetByUsername` $O(1)$ index and mutation resilience**

In `tests/Shiro.Tests/Wave4CoreOptimizationTests.cs`:
- Verify `GetById` and `GetByUsername` (case-insensitive) return the expected `Account` across multiple lookups on an unchanged snapshot.
- Verify that adding, removing, or mutating an `Account`'s `UserID` or `Username` in-place is immediately reflected by `GetById` and `GetByUsername`.

- [x] **Step 2: Implement snapshot-bound lookup index in `AccountRepository.cs`**

In `src/Shiro/Services/Storage/AccountRepository.cs`:
- Introduce a private immutable `SnapshotIndex` record/class holding `(IReadOnlyList<Account> Snapshot, Dictionary<long, Account> ById, Dictionary<string, Account> ByUsername)`.
- In `GetById(long userId)` and `GetByUsername(string username)`, compare `ReferenceEquals(currentSnapshot, index.Snapshot)`:
  - Fast path: O(1) dictionary lookup + verify the returned `Account` still has the matching `UserID` / `Username`.
  - Fallback path (if snapshot changed or in-place property mutation occurred): rebuild `SnapshotIndex` from `currentSnapshot` and resolve.
- In `AccountManager.Accounts.cs` (`GetUserIDAsync`), use `AccountRepository.Default.GetByUsername(trimmedName)` instead of a linear `AccountsList.FirstOrDefault` scan.

---

### Task 3: Coalesced In-Flight Avatar Fetch & Decode in `AccountViewModel.Avatar`

**Files:**
- Modify: `src/Shiro/ViewModels/AccountViewModel.Avatar.cs`
- Modify: `tests/Shiro.Tests/Wave4CoreOptimizationTests.cs`

- [x] **Step 1: Implement coalesced `_inFlightAvatarLoads` in `AccountViewModel.Avatar.cs`**

In `src/Shiro/ViewModels/AccountViewModel.Avatar.cs`:
- Replace `ConcurrentDictionary<long, byte> _requestedAvatarDownloads` with `ConcurrentDictionary<long, Task<(string Path, Bitmap? Bitmap)>> _inFlightAvatarLoads`.
- Extract `GetOrStartAvatarLoadAsync(long userId)` which coalesces concurrent downloads and disk decodes per `userId`, double-checking `_avatarBitmapCache` inside `_diskDecodeGate` before decoding PNG bytes.
- Ensure all concurrent callers (`LoadOrFetchAvatarInBackgroundAsync` and `FetchAndCacheAvatarAsync`) await `GetOrStartAvatarLoadAsync(userId)` and apply the resulting `(Path, Bitmap)` to the instance so a second caller is never left with `_cachedAvatarBitmap = null`.

---

### Task 4: Min-Heap `PriorityQueue` in `ProxyBindingService.DistributeProxiesSticky`

**Files:**
- Modify: `src/Shiro/Services/Network/ProxyBindingService.cs`
- Modify: `tests/Shiro.Tests/Wave4CoreOptimizationTests.cs`

- [x] **Step 1: Add unit tests for `ProxyBindingService.DistributeProxiesSticky` tie-breaking and load balancing**

In `tests/Shiro.Tests/Wave4CoreOptimizationTests.cs`:
- Test `DistributeProxiesSticky` with existing sticky assignments, unassigned accounts, duplicate proxy entries, and ties to verify $O((M + N) \log M)$ heap assignment matches deterministic order.

- [x] **Step 2: Upgrade `ProxyBindingService.DistributeProxiesSticky` to use `PriorityQueue<string, (int Usage, int Order)>`**

In `src/Shiro/Services/Network/ProxyBindingService.cs`:
- After tallying initial usage counts in `proxyUsage`, populate a `PriorityQueue<string, (int Usage, int Order)>(proxyList.Count)` where `Order` is the proxy's index in `proxyList`.
- For each account in `needsAssignment`, `TryDequeue(out string? proxy, out var priority)`, assign `acc.Proxy = proxy`, and `Enqueue(proxy, (priority.Usage + 1, priority.Order))`.

---

### Task 5: Direct UTF-8 `ByteArrayContent` JSON Serialization in `FastJson` & `RestClient`

**Files:**
- Modify: `src/Shiro/Services/Storage/JsonExtensions.cs`
- Modify: `src/Shiro/Classes/Utilities.cs`
- Modify: `src/Shiro/Http/RestClient.cs`
- Modify: `tests/Shiro.Tests/Wave4CoreOptimizationTests.cs`

- [x] **Step 1: Add unit tests for `FastJson.SerializeToUtf8Bytes` and `RestClient` JSON body serialization**

In `tests/Shiro.Tests/Wave4CoreOptimizationTests.cs`:
- Test `FastJson.SerializeToUtf8Bytes` with both `object`-typed and strongly-typed DTOs registered in `ShiroAppJson`.
- Test `RestClient.ExecuteAsync` with `request.AddJsonBody(...)` using a mock `HttpMessageHandler` to verify `Content-Type: application/json; charset=utf-8` and exact UTF-8 payload bytes.

- [x] **Step 2: Implement `FastJson.SerializeToUtf8Bytes` and update `RestClient.CreateHttpRequestMessage`**

- In `src/Shiro/Services/Storage/JsonExtensions.cs`:
  - Add `FastJson.SerializeToUtf8Bytes<T>(T value, bool indented = false)` using `ShiroAppJson.Default.GetTypeInfo(value.GetType())` when available.
  - Enhance `FastJson.Serialize<T>(T value, bool indented = false)` to also check `ShiroAppJson.Default.GetTypeInfo(value.GetType())` when `T` is `object`.
- In `src/Shiro/Classes/Utilities.cs`:
  - Expose `Utilities.FastJson.SerializeToUtf8Bytes<T>(T value, bool indented = false)`.
- In `src/Shiro/Http/RestClient.cs`:
  - When `request.JsonBody` is not a raw `string` or `JsonElement`, serialize directly to UTF-8 bytes via `Utilities.FastJson.SerializeToUtf8Bytes(request.JsonBody)` and wrap in `ByteArrayContent` with `MediaTypeHeaderValue("application/json") { CharSet = "utf-8" }`.

---

### Task 6: Single-Pass `DirectoryInfo.EnumerateFiles` in `AccountStore`

**Files:**
- Modify: `src/Shiro/Services/Storage/AccountStore.cs`
- Modify: `tests/Shiro.Tests/Wave4CoreOptimizationTests.cs`

- [x] **Step 1: Add unit tests for `AccountStore.GetHistoricalBackups`, `PruneHistoricalBackups`, and `WriteWithBackup`**

In `tests/Shiro.Tests/Wave4CoreOptimizationTests.cs`:
- Verify `GetHistoricalBackups` returns backup paths ordered by `CreationTimeUtc` descending.
- Verify `PruneHistoricalBackups` retains at most `MaxHistoricalBackups` while preserving the largest backup file.

- [x] **Step 2: Upgrade `AccountStore` to use `DirectoryInfo.EnumerateFiles` and single `FileInfo` inspection**

In `src/Shiro/Services/Storage/AccountStore.cs`:
- In `PruneHistoricalBackups(string dir)`, use `new DirectoryInfo(dir).EnumerateFiles("AccountData_*.json.bak")`.
- In `GetHistoricalBackups()`, use `new DirectoryInfo(BackupsDirectory).EnumerateFiles("AccountData_*.json.bak").OrderByDescending(f => f.CreationTimeUtc).Select(f => f.FullName).ToList()`.
- In `WriteWithBackup`, replace `if (File.Exists(primaryPath))` + `new FileInfo(primaryPath)` with a single `var info = new FileInfo(primaryPath); if (info.Exists && info.Length > 0)`.

- [x] **Step 3: Run full test suite & architecture verification**

Run: `dotnet test tests/Shiro.Tests -c Release`
Expected: All tests pass with 0 errors and 0 warnings.
