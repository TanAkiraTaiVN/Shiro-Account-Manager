# Wave 6 Core Methods Optimization & Bugfix Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Fix 2 real correctness bugs in the avatar/thumbnail bitmap pipeline (one introduced by Wave 4), and upgrade 3 remaining methods in Discord RPC, Roblox process enumeration, and SVG flag transform parsing.

**Priority order:** Task 1 and Task 2 are bugfixes and must land first. Tasks 3–5 are optimizations.

**Architecture:**
1. **Stale In-Flight Avatar Task Fix (`AccountViewModel.Avatar.cs`):** Add `await Task.Yield()` at the start of `LoadAndDecodeAvatarCoreAsync` so its `finally { TryRemove }` always runs *after* `GetOrAdd` publishes the task, mirroring the Wave 5 `GameThumbnailService` fix. Replace the weak Wave 4 avatar test (`UserID = -55`, never reaches the decode path) with tests that exercise the disk-cached path.
2. **UI-Safe, Lock-Light `LruCache` (`LruBitmapCache.cs`):** Add an opt-in `disposeOnEvict` constructor flag (default `true`, preserving the current generic contract and all 5 existing `LruCache_*` tests). UI bitmap caches (`AccountViewModel._avatarBitmapCache`, `GameThumbnailService._bitmapCache`) pass `disposeOnEvict: false` so bitmaps still referenced by `AccountViewModel._cachedAvatarBitmap` or bound `Image` controls are never disposed under the UI; GC reclaims them once unreferenced. Replace the `LinkedList` move-to-front on every read with a `ConcurrentDictionary` + monotonically increasing access stamp (`Interlocked.Increment`), so `TryGetValue` (hit every scroll frame) is lock-free; eviction (write path only) scans for the minimum stamp under the write lock, preserving exact-LRU semantics.
3. **Discord RPC Handle Leak, Frame Read & Serialization (`DiscordRpcService.cs`, `ShiroAppJson.cs`):** Replace `Process.GetCurrentProcess().Id` (undisposed `Process` per 15 s heartbeat) with `Environment.ProcessId`; replace anonymous-object + `Dictionary<string, object?>` reflection serialization with source-generated DTOs; cache the last encoded `SET_ACTIVITY` frame bytes so the heartbeat re-sends without re-serializing; read the frame header with `ReadExactlyAsync`; write header + payload as one buffer; use `RobloxProcessService.GetRunningRobloxCount()` in `UpdateCurrentPresence`.
4. **Single-Snapshot `GetRunningRobloxProcesses` (`RobloxProcessService.cs`):** Collect PIDs for every name in `RobloxProcessNames` in one Toolhelp32 pass, then `Process.GetProcessById` only the matching PIDs, instead of one `Process.GetProcessesByName` OS-wide scan per name. Keep the existing per-name loop as fallback.
5. **`[GeneratedRegex]` + Span Arguments in `FlagIconService.ParseTransform` (`FlagIconService.cs`):** Source-generated regex for `fn(args)` and `MemoryExtensions.SplitAny` over `ReadOnlySpan<char>` with `stackalloc Range[6]` instead of `Split(new[] {' ', ',', '\t'})`.

**Tech Stack:** .NET 10, C# 13, `ConcurrentDictionary`, `Interlocked`, `System.Text.Json` source generation (`ShiroAppJson`), Win32 Toolhelp32, `[GeneratedRegex]`, xUnit.

---

### Task 1: Fix Stale Completed Task in `_inFlightAvatarLoads` (Wave 4 regression)

**Files:**
- Modify: `src/Shiro/ViewModels/AccountViewModel.Avatar.cs`
- Modify: `tests/Shiro.Tests/Wave4CoreOptimizationTests.cs` (replace weak avatar test)
- Create: `tests/Shiro.Tests/Wave6CoreOptimizationTests.cs`

- [x] **Step 1: Write a failing regression test**

Expose `internal static int InFlightAvatarLoadCountForTesting => _inFlightAvatarLoads.Count;`. In `Wave6CoreOptimizationTests`, write a valid PNG to `StoragePaths.CacheDirectory/Avatars/{userId}.png` for a synthetic positive `userId`, call `FetchAndCacheAvatarAsync()` twice, and assert:
- the returned path equals the disk path,
- `InFlightAvatarLoadCountForTesting == 0` after both calls complete (fails today because the synchronous completion leaves the task in the dictionary).

Clean up the temp PNG in `finally`.

- [x] **Step 2: Fix `LoadAndDecodeAvatarCoreAsync`**

Add `await Task.Yield();` as the first statement inside `LoadAndDecodeAvatarCoreAsync`. The yield guarantees the method never completes synchronously, so `GetOrAdd` always publishes the task before the `finally { _inFlightAvatarLoads.TryRemove(userId, out _); }` runs.

- [x] **Step 3: Replace the weak Wave 4 test**

Delete `AccountViewModel_Avatar_CoalescesConcurrentLoads` (uses `UserID = -55`, never enters the decode path) from `Wave4CoreOptimizationTests.cs`; add a concurrent test in `Wave6CoreOptimizationTests` that starts 8 `FetchAndCacheAvatarAsync()` calls for the same disk-cached `userId` on separate `AccountViewModel` instances and asserts all 8 return the same path and the same `Bitmap` reference via `AvatarImage`.

---

### Task 2: UI-Safe, Lock-Light `LruCache`

**Files:**
- Modify: `src/Shiro/Services/Ui/LruBitmapCache.cs`
- Modify: `src/Shiro/ViewModels/AccountViewModel.Avatar.cs`
- Modify: `src/Shiro/Services/Network/GameThumbnailService.cs`
- Modify: `tests/Shiro.Tests/Wave6CoreOptimizationTests.cs`

- [x] **Step 1: Write failing tests**

- `LruCache_DisposeOnEvictFalse_NeverDisposesEvictedOverwrittenOrClearedValues` (capacity 2, insert 3, overwrite 1, `Clear()`; assert no `TestDisposable.Disposed`).
- `LruCache_ConcurrentReadsAndWrites_RemainConsistent` (`Parallel.For` 10 000 mixed `TryGetValue`/`Set` over 64 keys with capacity 32; assert `Count <= 32` and no exceptions).
- Existing 5 `LruCache_*` tests in `AutoReconnectAndProxyTests.cs` must continue to pass unchanged (default `disposeOnEvict: true`).

- [x] **Step 2: Rewrite `LruCache` internals**

- Constructor: `LruCache(int capacity, bool disposeOnEvict = true)`.
- Storage: `ConcurrentDictionary<TKey, Entry>` where `Entry` holds `TValue Value` and `long Stamp`.
- `TryGetValue`: lock-free `TryGetValue` + `Volatile.Write(ref entry.Stamp, Interlocked.Increment(ref _clock))`.
- `Set`: under `_writeLock`, insert/replace; when `Count > capacity`, scan for the entry with minimum `Stamp` and remove exactly one (exact LRU, satisfies `LruCache_AccessMovesToFront_EvictsTrueLru`). Dispose evicted/replaced values via `AppDispatcher.Post` only when `_disposeOnEvict`.
- `TryRemove`/`Clear`: same, `Clear` disposes only when `_disposeOnEvict`.

- [x] **Step 3: Opt UI caches out of disposal**

- `AccountViewModel.Avatar.cs`: `new LruCache<long, Bitmap>(MaxAvatarBitmapCacheEntries, disposeOnEvict: false)`.
- `GameThumbnailService.cs`: `new LruCache<long, Bitmap>(256, disposeOnEvict: false)`.

---

### Task 3: Discord RPC Handle Leak, Frame Read & Source-Generated Payloads

**Files:**
- Modify: `src/Shiro/Services/Network/DiscordRpcService.cs`
- Modify: `src/Shiro/Services/ShiroAppJson.cs`
- Modify: `tests/Shiro.Tests/Wave6CoreOptimizationTests.cs`

- [x] **Step 1: Write tests**

- `DiscordRpcService_BuildActivityFrame_ProducesValidHeaderAndJson`: call an `internal static byte[] BuildFrame(int opcode, ReadOnlySpan<byte> json)` and an `internal byte[] BuildSetActivityPayload(DiscordActivityItem?)`; assert little-endian opcode/length header, `"cmd":"SET_ACTIVITY"`, `"pid":<Environment.ProcessId>`, buttons and `"activity":null` for clear.
- `DiscordRpcService_ReadFrame_HandlesFragmentedHeader`: feed a stream that returns the 8-byte header in 3 + 5 byte chunks; assert opcode/payload parsed correctly (fails today with "Incomplete frame header").

- [x] **Step 2: Add source-generated DTOs**

Add to `ShiroAppJson.cs` (with `[JsonSourceGenerationOptions(DefaultIgnoreCondition = WhenWritingNull)]` on the DTO context if needed): `DiscordHandshakePayload(int v, string client_id)`, `DiscordSetActivityCommand(string cmd, DiscordSetActivityArgs args, string nonce)`, `DiscordSetActivityArgs(int pid, DiscordActivityPayload? activity)`, `DiscordActivityPayload`, `DiscordTimestampsPayload`, `DiscordAssetsPayload`, `DiscordButtonPayload`. Register each with `[JsonSerializable]`.

- [x] **Step 3: Refactor `DiscordRpcService`**

- `Environment.ProcessId` everywhere.
- `SendActivityAsync` serializes once via `JsonSerializer.SerializeToUtf8Bytes(command, ShiroAppJson.Default.DiscordSetActivityCommand)`, builds one combined frame buffer, stores it in `_lastActivityFrame`; `WorkerLoopAsync` heartbeat re-sends `_lastActivityFrame` directly.
- `WriteFrameAsync(byte[] frame, ...)` performs a single `WriteAsync`.
- `ReadFrameAsync` uses `ReadExactlyAsync(header)` and `ReadExactlyAsync(buffer)`.
- `UpdateCurrentPresence` uses `RobloxProcessService.GetRunningRobloxCount()`.

---

### Task 4: Single-Snapshot `RobloxProcessService.GetRunningRobloxProcesses`

**Files:**
- Modify: `src/Shiro/Services/Windows/RobloxProcessService.cs`

- [x] **Step 1: Implement single-pass PID collection**

Add `private static unsafe bool TryCollectRobloxPidsNative(List<int> pids)` that walks one Toolhelp32 snapshot and adds `th32ProcessID` when `szExeFile` (minus `.exe`) matches any entry of `RobloxProcessNames` (`OrdinalIgnoreCase`). In `GetRunningRobloxProcesses`, call it first and map PIDs through `Process.GetProcessById` (catch `ArgumentException` for exited PIDs); fall back to the existing per-name loop only if the native call fails. Existing `RobloxProcessService*Tests` cover behavior.

---

### Task 5: `[GeneratedRegex]` + Span Arguments in `FlagIconService.ParseTransform`

**Files:**
- Modify: `src/Shiro/Services/Ui/FlagIconService.cs`
- Modify: `tests/Shiro.Tests/Wave6CoreOptimizationTests.cs`

- [x] **Step 1: Write tests**

Expose `internal static Matrix ParseTransformForTesting(string)`; assert `translate(10,20)`, `scale(2)`, `rotate(90 5 5)`, `matrix(1 0 0 1 3 4)`, and a chained `translate(1 2) scale(3)` produce the expected matrices (compare `M11..M32` with tolerance).

- [x] **Step 2: Refactor**

Make `FlagIconService` `partial`, add `[GeneratedRegex(@"([a-zA-Z]+)\s*\(([^)]+)\)")] private static partial Regex TransformFunctionRegex();`. Parse args with `ReadOnlySpan<char>` + `stackalloc Range[6]` + `SplitAny(" ,\t", StringSplitOptions.RemoveEmptyEntries)` and `double.TryParse(span, NumberStyles.Float, CultureInfo.InvariantCulture, ...)`; compare function names with `Equals(..., OrdinalIgnoreCase)` instead of `ToLowerInvariant()`.

---

### Task 6: Verification

- [x] **Step 1:** `dotnet test tests/Shiro.Tests -c Release` — all tests pass, 0 warnings, all `ArchitectureTests` green (files ≤ 500 lines, no new `Program.Logger`/`AppSettings.` usages in touched code).
