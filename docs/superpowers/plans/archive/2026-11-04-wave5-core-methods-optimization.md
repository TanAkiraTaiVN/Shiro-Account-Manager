# Wave 5 Core Methods Optimization Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Upgrade 5 remaining hot-path methods across Presence API querying, `IniFile` JSON configuration serialization, off-UI-thread `GameThumbnailService` disk decoding, zero-intermediate-allocation `ProxyManagerService.ParseProxy` span parsing, and `Parallel.ForEachAsync` batch concurrency.

**Architecture:**
1. **Streamed Source-Generated `ExecuteJsonAsync` in `Presence.GetPresence` (`Presence.cs`):** Replace the anonymous `new { userIds = batch }` request body and `ExecuteAsync` UTF-16 string response parsing in `Presence.GetPresence` with `RobloxPresenceUsersRequest` and `PresenceClient.ExecuteJsonAsync(..., ShiroAppJson.Default.RobloxPresenceEnvelope, cancellationToken)`.
2. **Direct Single-Pass `Utf8JsonWriter` Serialization in `IniFile` (`IniFile.cs`):** Replace the multi-dictionary allocation chain (`Dictionary<string, Dictionary<string, string>>` + `Sections` array + `section.ToDictionary()` + reflection `JsonSerializer.Serialize`) in `IniFile.ToJsonString` and `IniFile.Save` with direct `Utf8JsonWriter` serialization over `_sections` and `_properties`.
3. **Async Coalesced Disk Decode in `GameThumbnailService.GetThumbnailBitmapAsync` (`GameThumbnailService.cs`):** Keep the synchronous fast path in `GetThumbnailBitmapAsync` strictly to $O(1)$ in-memory `_bitmapCache.TryGetValue`, moving synchronous disk I/O (`File.OpenRead` + `Bitmap.DecodeToWidth`) into the async coalesced `_inFlightDownloads` pipeline guarded by a decode semaphore so UI callers never block on disk PNG decoding.
4. **Span-Based Proxy Parsing in `ProxyManagerService.ParseProxy` (`ProxyManagerService.cs`):** Replace `working.Split(':')`, `string.Join(":", colons.Skip(3))`, and intermediate `.Substring(...)` calls with `ReadOnlySpan<char>` slicing and `MemoryExtensions.Split` (`Span<Range>`) on the stack.
5. **`Parallel.ForEachAsync` Bounded Concurrency (`AvatarService.cs`, `GameThumbnailService.cs`, `ProxyManagerService.cs`):** Replace manual `SemaphoreSlim` + `.Select(async ...).ToArray()` + `Task.WhenAll` fan-out loops in `AvatarService.BatchEnsureAvatarsAsync`, `GameThumbnailService.BatchEnsureThumbnailsAsync`, and `ProxyManagerService.TestAllProxiesAsync` with `Parallel.ForEachAsync`.

**Tech Stack:** .NET 10, C# 13, `System.Text.Json` (`Utf8JsonWriter`, `ShiroAppJson`), `ReadOnlySpan<char>`, `MemoryExtensions.Split`, `Parallel.ForEachAsync`, xUnit.

---

### Task 1: Streamed Source-Generated `ExecuteJsonAsync` in `Presence.GetPresence`

**Files:**
- Modify: `src/Shiro/Classes/Presence.cs`
- Create: `tests/Shiro.Tests/Wave5CoreOptimizationTests.cs`

- [x] **Step 1: Write unit tests for `Presence.GetPresence` and `Wave5CoreOptimizationTests`**

Create `tests/Shiro.Tests/Wave5CoreOptimizationTests.cs` to verify `Presence`, `IniFile`, `GameThumbnailService`, and `ProxyManagerService` behavior.

- [x] **Step 2: Upgrade `Presence.GetPresence` in `src/Shiro/Classes/Presence.cs`**

In `src/Shiro/Classes/Presence.cs`:
- Replace `PresenceClient.ExecuteAsync(new RestRequest("v1/presence/users", Method.Post).AddJsonBody(new { userIds = batch }), cancellationToken)` and `FastJson.TryDeserialize<RobloxPresenceEnvelope>(response.Content, ...)` with `PresenceClient.ExecuteJsonAsync(new RestRequest("v1/presence/users", Method.Post).AddJsonBody(new RobloxPresenceUsersRequest(batch)), ShiroAppJson.Default.RobloxPresenceEnvelope, cancellationToken)`.
- Replace legacy `Program.Logger` calls in `GetPresence` with `AppServices.Logger`.

---

### Task 2: Direct Single-Pass `Utf8JsonWriter` Serialization in `IniFile`

**Files:**
- Modify: `src/Shiro/Classes/IniFile.cs`
- Modify: `tests/Shiro.Tests/Wave5CoreOptimizationTests.cs`

- [x] **Step 1: Add unit tests for `IniFile.ToJsonString()`, `IniFile.ToUtf8JsonBytes()`, and `IniFile.Save()`**

In `tests/Shiro.Tests/Wave5CoreOptimizationTests.cs`:
- Verify round-trip `IniFile` save and reload preserves all sections, properties, and typed values (`bool`, `int`, `decimal`, `string`) with indented JSON formatting.

- [x] **Step 2: Implement direct `Utf8JsonWriter` writing in `IniFile.cs`**

In `src/Shiro/Classes/IniFile.cs`:
- Add `internal void WritePropertiesTo(Utf8JsonWriter writer)` on `IniSection` that locks `_sync` and writes each key/value pair directly without allocating a `Dictionary<string, string>`.
- Add `public byte[] ToUtf8JsonBytes()` on `IniFile` that writes `{ "Section": { "Key": "Value" } }` directly via `Utf8JsonWriter` (`Indented = true`) over `ArrayBufferWriter<byte>` without allocating `IniSection[]` or `Dictionary<string, Dictionary<string, string>>`.
- Update `ToJsonString()` to decode `ToUtf8JsonBytes()` via `Encoding.UTF8.GetString(...)` and update `Save(string path)` to write the UTF-8 bytes directly via `StoragePaths.AtomicWriteBytes(resolvedPath, ToUtf8JsonBytes(), durable: true)`.

---

### Task 3: Off-UI-Thread Coalesced Disk Decode in `GameThumbnailService`

**Files:**
- Modify: `src/Shiro/Services/Network/GameThumbnailService.cs`
- Modify: `tests/Shiro.Tests/Wave5CoreOptimizationTests.cs`

- [x] **Step 1: Update `GameThumbnailService.GetThumbnailBitmapAsync` and `DownloadAndCacheThumbnailCoreAsync`**

In `src/Shiro/Services/Network/GameThumbnailService.cs`:
- Add `private static readonly SemaphoreSlim _diskDecodeGate = new(6, 6);`.
- In `GetThumbnailBitmapAsync(long placeId, string? cdnUrl = null, CancellationToken cancellationToken = default)`:
  - Check `_bitmapCache.TryGetValue(placeId, out var cached)` synchronously ($O(1)$ memory-only check).
  - If not in memory, coalesce via `_inFlightDownloads.GetOrAdd(placeId, id => DownloadAndCacheThumbnailCoreAsync(id, cdnUrl))`.
- Inside `DownloadAndCacheThumbnailCoreAsync(long placeId, string? cdnUrl)`:
  - First yield (`await Task.Yield()`) and check if the file already exists on disk (`diskPath`). If so, acquire `_diskDecodeGate`, double-check `_bitmapCache`, decode `Bitmap.DecodeToWidth(stream, 96)`, store in `_bitmapCache`, and return without making an HTTP request.

---

### Task 4: Span-Based Zero-Intermediate-Allocation `ProxyManagerService.ParseProxy`

**Files:**
- Modify: `src/Shiro/Services/Network/ProxyManagerService.cs`
- Modify: `tests/Shiro.Tests/Wave5CoreOptimizationTests.cs`

- [x] **Step 1: Add comprehensive unit tests for `ProxyManagerService.ParseProxy` across all proxy formats**

In `tests/Shiro.Tests/Wave5CoreOptimizationTests.cs`:
- Test `host:port`, `http://host:port`, `socks5://user:pass@host:port`, `host:port@user:pass`, `host:port:user:pass`, `host:port:user:p@ss:with:colons`, comment lines (`#`), and invalid ports.

- [x] **Step 2: Refactor `ProxyManagerService.ParseProxy` to use `ReadOnlySpan<char>` and `MemoryExtensions.Split`**

In `src/Shiro/Services/Network/ProxyManagerService.cs`:
- Use `ReadOnlySpan<char>` and `Span<Range> colonRanges = stackalloc Range[5]` (`working.Split(colonRanges, ':')`) to parse `ip:port:user:pass`, `user:pass@host:port`, and `host:port@user:pass` without allocating intermediate `string[]` or `Substring` objects.

---

### Task 5: `Parallel.ForEachAsync` Bounded Concurrency in Batch Network Operations

**Files:**
- Modify: `src/Shiro/Services/Network/AvatarService.cs`
- Modify: `src/Shiro/Services/Network/GameThumbnailService.cs`
- Modify: `src/Shiro/Services/Network/ProxyManagerService.cs`

- [x] **Step 1: Upgrade `AvatarService.BatchEnsureAvatarsAsync`, `GameThumbnailService.BatchEnsureThumbnailsAsync`, and `ProxyManagerService.TestAllProxiesAsync` to `Parallel.ForEachAsync`**

- Replace `using var downloadGate = new SemaphoreSlim(8, 8); var tasks = entries.Select(...).ToArray(); await Task.WhenAll(tasks);` in `AvatarService.cs` and `GameThumbnailService.cs` with `await Parallel.ForEachAsync(entries, new ParallelOptions { MaxDegreeOfParallelism = 8, CancellationToken = cancellationToken }, async (entry, ct) => ...)`!
- Replace `using var semaphore = new SemaphoreSlim(maxConcurrency, maxConcurrency); var tasks = snapshot.Select(...); await Task.WhenAll(tasks);` in `ProxyManagerService.TestAllProxiesAsync` with `await Parallel.ForEachAsync(snapshot, new ParallelOptions { MaxDegreeOfParallelism = Math.Max(1, maxConcurrency), CancellationToken = ct }, async (proxy, token) => ...)`.

- [x] **Step 2: Run full test suite & architecture verification**

Run: `dotnet test tests/Shiro.Tests -c Release`
Expected: All tests pass with 0 errors and 0 warnings.
