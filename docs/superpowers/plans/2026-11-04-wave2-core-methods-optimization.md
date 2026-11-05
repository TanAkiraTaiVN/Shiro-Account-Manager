# Wave 2 Core Methods Optimization Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Eliminate the remaining 6 deep architectural bottlenecks across Process Job Object lifecycle, Rate Limiter slot pacing, WebSocket fan-out broadcasting, WASAPI Smart Mute COM session caching, Zero-DOM Vault password stripping, and Event-Driven Singleton/Zombie cleanup.

**Architecture:**
1. **Unconditional Job Object Enrollment (`RobloxProcessOptimizer` & `RobloxWatcher`):** Always enroll every launched and discovered `RobloxPlayerBeta` PID into `RobloxJobObjectService` even when `cpuCapPercent == 0`, guaranteeing 100% kernel IOCP exit telemetry (`ProcessExitedFromJob`) without requiring CPU rate-limiting to be enabled.
2. **Atomic Next-Slot Pacing (`RobloxRateLimitCoordinator`):** Replace post-delay `_lastRequestTimes` mutation with an atomic pre-reservation algorithm (`ReserveNextSlotDelay`) so concurrent callers on multi-slot semaphores (e.g., `SessionCheck` with 12 slots) stagger smoothly at `0ms, 1*interval, 2*interval...` instead of firing simultaneous bursts.
3. **Concurrent WebSocket Fan-Out with Timeout & Typed Envelope Serialization (`KestrelWebServer`):** Replace sequential `foreach` awaiting in `BroadcastAsync` and `MemoryStream` allocation in `SerializeBroadcastEnvelope` with source-generated `BroadcastEnvelope<T>` UTF-8 serialization and concurrent `Task.WhenAll` fan-out guarded by a per-client timeout (`2s`) to eliminate Head-of-Line blocking from stalled sockets.
4. **Per-PID WASAPI `ISimpleAudioVolume` COM Pointer Cache (`RobloxAudioService`):** Cache unmanaged `ISimpleAudioVolume` pointers by PID in `RobloxAudioService.Wasapi.cs` and automatically release them on `RobloxJobObjectService.ProcessExitedFromJob` or COM device invalidation, turning `Alt+Tab` Smart Mute transitions between known clients into direct vtable calls without full WASAPI device/session re-enumeration.
5. **Single-Pass Zero-DOM Password Stripping (`AccountDataSerializer`):** Replace the `JsonSerializer.SerializeToNode` DOM clone + `JsonObject.Remove("Password")` loop with a custom `JsonTypeInfoResolver` modifier that omits `Account.Password` during a single-pass `JsonSerializer.SerializeToUtf8Bytes` write.
6. **Event-Driven `RobloxSingletonService` & Single-Scan `CleanZombieProcesses`:** Subscribe `RobloxSingletonService` to `RobloxJobObjectService.ProcessExitedFromJob` for immediate zombie/tray cleanup on client exit, relax the fallback polling timer when IOCP is active, and eliminate the duplicate `GetRunningRobloxPids()` call inside `RobloxProcessService.CleanZombieProcesses()`.

**Tech Stack:** C# 13 / .NET 10 (`net10.0-windows`), Win32 Job Objects + IOCP, WASAPI COM vtable interop, ASP.NET Core Kestrel WebSockets, `System.Text.Json` Source Generation & `JsonTypeInfo` modifiers, xUnit.

---

## Architecture Constraints (Enforced by `ArchitectureTests`)
- **Max File Length:** Every `.cs` file in `src/Shiro` MUST remain `<= 500` lines.
- **Max Partial Class Length:** Every partial class in `src/Shiro` MUST remain `<= 1200` lines across all partials.
- **Ambient Static Ratchet:** Do NOT add any new `Program.Logger` or `AppSettings.` static calls in `src/Shiro`. Always use `AppServices.Logger` and `AppServices.GeneralSettings`.

---

### Task 1: Unconditional Job Object Enrollment & Event-Driven Singleton/Zombie Cleanup

**Files:**
- Modify: `src/Shiro/Services/Windows/RobloxProcessOptimizer.cs`
- Modify: `src/Shiro/Classes/RobloxWatcher.cs`
- Modify: `src/Shiro/Services/Windows/RobloxProcessService.Lifecycle.cs`
- Modify: `src/Shiro/Services/Windows/RobloxSingletonService.cs`
- Create: `src/Shiro/Services/Windows/RobloxSingletonService.Interop.cs` (extract P/Invoke declarations to keep `RobloxSingletonService.cs` well under 500 lines)
- Create: `tests/Shiro.Tests/Wave2ProcessAndSingletonOptimizationTests.cs`

- [x] **Step 1: Write the failing unit tests in `tests/Shiro.Tests/Wave2ProcessAndSingletonOptimizationTests.cs`**
  - Verify `RobloxProcessOptimizer.ShouldAssignToJobObject(int pid)` returns `true` for any `pid > 0` regardless of `cpuCapPercent`.
  - Verify `RobloxProcessService.PartitionRunningRobloxPids` separates `--launch-to-tray` daemon PIDs from active game client PIDs in a single pass without requiring a second process snapshot.
  - Verify `RobloxSingletonService.OnJobProcessExited(int pid, bool abnormal)` invalidates `_lastObservedPids` for that PID and schedules a non-blocking sweep when enabled.

- [x] **Step 2: Run test to verify it fails**
  - Run: `dotnet test tests/Shiro.Tests --filter "FullyQualifiedName~Wave2ProcessAndSingletonOptimizationTests"`

- [x] **Step 3: Implement Unconditional Job Object Enrollment & Event-Driven Singleton Cleanup**
  - In `RobloxProcessOptimizer.OptimizeLaunchedProcess(int pid)`:
    - Always call `RobloxJobObjectService.AssignProcessByPid(pid)` even when `cpuCapPercent == 0`. Only call `RobloxJobObjectService.SetCpuRateLimit(cpuCapPercent)` when `cpuCapPercent > 0 && RobloxJobObjectService.CurrentCpuRatePercent != cpuCapPercent`.
  - In `RobloxWatcher.ReadLogFiles`:
    - When a new `RobloxProcess` is instantiated with a resolved `ProcessId > 0`, call `RobloxJobObjectService.AssignProcessByPid(process.ProcessId)` so externally launched Roblox instances are also enrolled into the Job Object IOCP stream.
  - In `RobloxProcessService.Lifecycle.cs` (`CleanZombieProcesses`):
    - Add `internal static (List<int> TrayDaemonPids, List<int> ActiveClientPids) PartitionRobloxPidsByCommandLine(IReadOnlyList<int> allPids, Func<int, string?> commandLineResolver)`.
    - Use a single `GetRunningRobloxPids(includeTrayDaemons: true)` snapshot to identify both `--launch-to-tray` daemons to kill and remaining active game clients, eliminating the second `GetRunningRobloxPids()` call.
  - Split `RobloxSingletonService` native P/Invoke declarations into `RobloxSingletonService.Interop.cs`.
  - In `RobloxSingletonService`:
    - Subscribe to `RobloxJobObjectService.ProcessExitedFromJob += OnJobProcessExited` when `Apply(true)` starts and unsubscribe on `Stop()`.
    - When `OnJobProcessExited(int pid, bool abnormal)` fires, remove `pid` from `_lastObservedPids` and trigger `ThreadPool.QueueUserWorkItem(_ => Sweep(force: true))`.

- [x] **Step 4: Run tests to verify they pass**
  - Run: `dotnet test tests/Shiro.Tests --filter "FullyQualifiedName~Wave2ProcessAndSingletonOptimizationTests"`

---

### Task 2: Atomic Next-Slot Pacing in `RobloxRateLimitCoordinator`

**Files:**
- Modify: `src/Shiro/Services/Network/RobloxRateLimitCoordinator.cs`
- Create: `tests/Shiro.Tests/RateLimitAtomicPacingTests.cs`

- [x] **Step 1: Write the failing unit tests in `tests/Shiro.Tests/RateLimitAtomicPacingTests.cs`**
  - Test `ReserveNextSlotDelay(RateLimitCategory category, int minIntervalMs, DateTime utcNow)`:
    - Calling `ReserveNextSlotDelay` 5 times at the exact same `utcNow` with `minIntervalMs = 200` must return staggered delays: `0ms`, `200ms`, `400ms`, `600ms`, `800ms`.
  - Test concurrent `ExecuteWithPacingAsync(RateLimitCategory.SessionCheck, ..., minIntervalMs: 60)` with 4 parallel tasks:
    - Assert that execution start timestamps are staggered rather than all 4 firing at `t = 0ms`.

- [x] **Step 2: Run test to verify it fails**
  - Run: `dotnet test tests/Shiro.Tests --filter "FullyQualifiedName~RateLimitAtomicPacingTests"`

- [x] **Step 3: Implement Atomic Slot Reservation in `RobloxRateLimitCoordinator`**
  - Replace `_lastRequestTimes` lock-free dictionary lookup with a dedicated `_pacingLock` and `_nextAllowedSlotUtc` dictionary.
  - Add `internal TimeSpan ReserveNextSlotDelay(RateLimitCategory category, int minIntervalMs, DateTime utcNow)`:
    - Under `_pacingLock`, read `currentNext = _nextAllowedSlotUtc.GetValueOrDefault(category, DateTime.MinValue)`.
    - Compute `slotUtc = utcNow >= currentNext ? utcNow : currentNext`.
    - Store `_nextAllowedSlotUtc[category] = slotUtc.AddMilliseconds(minIntervalMs)`.
    - Return `slotUtc - utcNow`.
  - Clear `_nextAllowedSlotUtc` inside `ResetBackoff()`.
  - In `ExecuteWithPacingAsync`, after `await WaitForBackoffAsync(cancellationToken)`, reserve the slot via `ReserveNextSlotDelay` and `await Task.Delay(delay, cancellationToken)` if `delay > TimeSpan.Zero`.

- [x] **Step 4: Run tests to verify they pass**
  - Run: `dotnet test tests/Shiro.Tests --filter "FullyQualifiedName~RateLimitAtomicPacingTests"`

---

### Task 3: Concurrent WebSocket Fan-Out with Timeout & Zero-Stream Broadcast Serialization

**Files:**
- Modify: `src/Shiro/Services/ShiroAppJson.cs`
- Modify: `src/Shiro/Services/Network/KestrelWebServer.cs`
- Create: `tests/Shiro.Tests/WebSocketBroadcastOptimizationTests.cs`

- [x] **Step 1: Write the failing unit tests in `tests/Shiro.Tests/WebSocketBroadcastOptimizationTests.cs`**
  - Test `KestrelWebServer.SerializeBroadcastEnvelope(string eventName, object? payload)` produces identical UTF-8 JSON structure (`{"event":...,"timestamp":...,"data":...}`) for `StatusBroadcastPayload`, `LaunchProgressBroadcastPayload`, `PulseBroadcastPayload`, `string`, and `null` without `MemoryStream`.
  - Test `KestrelWebServer.BroadcastToSocketsAsync` with a fast mock `WebSocket` and a stalled mock `WebSocket` (whose `SendAsync` delays 5 seconds):
    - Assert the fast client receives the broadcast immediately (`< 200ms`).
    - Assert the stalled client times out and is pruned from the active dictionary without throwing or blocking indefinitely.

- [x] **Step 2: Run test to verify it fails**
  - Run: `dotnet test tests/Shiro.Tests --filter "FullyQualifiedName~WebSocketBroadcastOptimizationTests"`

- [x] **Step 3: Implement Typed Broadcast Envelopes & Concurrent Fan-Out**
  - In `src/Shiro/Services/ShiroAppJson.cs`:
    - Add `internal readonly record struct BroadcastEnvelope<T>([property: JsonPropertyName("event")] string EventName, long timestamp, T data);`
    - Register `[JsonSerializable(typeof(BroadcastEnvelope<StatusBroadcastPayload>))]`, `[JsonSerializable(typeof(BroadcastEnvelope<LaunchProgressBroadcastPayload>))]`, `[JsonSerializable(typeof(BroadcastEnvelope<PulseBroadcastPayload>))]`, `[JsonSerializable(typeof(BroadcastEnvelope<string?>))]` on `ShiroAppJson`.
  - In `src/Shiro/Services/Network/KestrelWebServer.cs`:
    - Update `SerializeBroadcastEnvelope(string eventName, object? payload)` to use `JsonSerializer.SerializeToUtf8Bytes` directly with `ShiroAppJson.Default.BroadcastEnvelope...` (zero `MemoryStream` / `Utf8JsonWriter` manual allocation).
    - Add `internal static Task BroadcastToSocketsAsync(ConcurrentDictionary<WebSocket, SemaphoreSlim> sockets, ArraySegment<byte> segment, int sendTimeoutMs = 2000)` that fans out to all open sockets concurrently using `Task.WhenAll`, applies a per-socket timeout (`CancellationTokenSource(sendTimeoutMs)`), and removes/disposes any faulted or timed-out socket.

- [x] **Step 4: Run tests to verify they pass**
  - Run: `dotnet test tests/Shiro.Tests --filter "FullyQualifiedName~WebSocketBroadcastOptimizationTests"`

---

### Task 4: Per-PID WASAPI `ISimpleAudioVolume` COM Session Cache in `RobloxAudioService`

**Files:**
- Modify: `src/Shiro/Services/Windows/RobloxAudioService.Wasapi.cs`
- Modify: `src/Shiro/Services/Windows/RobloxAudioService.cs`
- Create: `tests/Shiro.Tests/AudioSessionCacheTests.cs`

- [x] **Step 1: Write the failing unit tests in `tests/Shiro.Tests/AudioSessionCacheTests.cs`**
  - Test `RobloxAudioService.AreAllPidsCached(IReadOnlyList<int> runningPids, IReadOnlyDictionary<int, IntPtr> cache)` returns `true` only when `runningPids.Count > 0` and every PID in `runningPids` has a non-zero cached pointer.
  - Test `RobloxAudioService.InvalidateCachedAudioSession(int pid)` safely removes and releases a cached entry.

- [x] **Step 2: Run test to verify it fails**
  - Run: `dotnet test tests/Shiro.Tests --filter "FullyQualifiedName~AudioSessionCacheTests"`

- [x] **Step 3: Implement Per-PID COM Volume Cache in `RobloxAudioService`**
  - In `RobloxAudioService.Wasapi.cs`:
    - Maintain `private static readonly ConcurrentDictionary<int, IntPtr> _cachedVolumePointers = new();`
    - Subscribe `RobloxJobObjectService.ProcessExitedFromJob += (pid, _) => InvalidateCachedAudioSession(pid);` in static constructor.
    - Add `internal static bool AreAllPidsCached(IReadOnlyList<int> runningPids, IReadOnlyDictionary<int, IntPtr> cache)`.
    - Add `internal static void InvalidateCachedAudioSession(int pid)` and `internal static void ClearAudioSessionCache()`.
    - Add fast path in `ForEachRobloxAudioSession`: if `AreAllPidsCached(robloxPids, _cachedVolumePointers)` is `true`, iterate directly over the cached `NativeSimpleAudioVolume` pointers; if any `TryGetMute` / `TrySetMute` call fails (e.g., `AUDCLNT_E_DEVICE_INVALIDATED`), call `ClearAudioSessionCache()` and fall through to the full WASAPI enumeration which repopulates `_cachedVolumePointers` (using `ComAddRef` or retaining `pVolume` in the cache).
    - When `ConfigureSmartMute(false)` is called, release all cached pointers after unmuting.

- [x] **Step 4: Run tests to verify they pass**
  - Run: `dotnet test tests/Shiro.Tests --filter "FullyQualifiedName~AudioSessionCacheTests"`

---

### Task 5: Single-Pass Zero-DOM Password Stripping in `AccountDataSerializer`

**Files:**
- Modify: `src/Shiro/Services/Storage/AccountDataSerializer.cs`
- Create: `tests/Shiro.Tests/AccountDataSerializerZeroDomTests.cs`

- [x] **Step 1: Write the failing unit tests in `tests/Shiro.Tests/AccountDataSerializerZeroDomTests.cs`**
  - Verify `AccountDataSerializer.NoPasswordJsonOptions` uses a custom `JsonTypeInfoResolver` modifier that strips the `Password` property from `Account` without mutating the `Account.Password` property on the in-memory `Account` instance.
  - Verify `AccountDataSerializer.Serialize(accounts, includePasswords: false)` produces valid JSON where `"Password"` is completely absent from the serialized UTF-8 bytes, while preserving all other fields (`Username`, `SecurityToken`, `Alias`, `Fields`, etc.) and leaving `account.Password` intact in memory.

- [x] **Step 2: Run test to verify it fails**
  - Run: `dotnet test tests/Shiro.Tests --filter "FullyQualifiedName~AccountDataSerializerZeroDomTests"`

- [x] **Step 3: Implement Single-Pass Zero-DOM Serialization in `AccountDataSerializer`**
  - In `AccountDataSerializer.cs`:
    - Create `internal static readonly JsonSerializerOptions NoPasswordJsonOptions = CreateNoPasswordJsonOptions();`
    - Configure `CreateNoPasswordJsonOptions()` using `ShiroAppJson.Default.WithAddedModifier(static typeInfo => ...)` so that when `typeInfo.Type == typeof(Account)`, the `"Password"` property's `ShouldSerialize` delegate is set to `static (_, _) => false`.
    - In `Serialize(IReadOnlyCollection<Account> accounts, bool includePasswords = true)`:
      - Construct `var envelope = new AccountEnvelope { schemaVersion = CurrentSchemaVersion, accounts = accounts ?? Array.Empty<Account>() };`
      - When `includePasswords == true`, call `JsonSerializer.SerializeToUtf8Bytes(envelope, ShiroAppJson.Default.AccountEnvelope)`.
      - When `includePasswords == false`, call `JsonSerializer.SerializeToUtf8Bytes(envelope, (JsonTypeInfo<AccountEnvelope>)NoPasswordJsonOptions.GetTypeInfo(typeof(AccountEnvelope)))`.
      - Remove `using System.Text.Json.Nodes;` and the `JsonArray` / `JsonObject` DOM tree clone loop completely.

- [x] **Step 4: Run tests to verify they pass**
  - Run: `dotnet test tests/Shiro.Tests --filter "FullyQualifiedName~AccountDataSerializerZeroDomTests"`

---

### Task 6: Full Regression & Architecture Verification

**Files:**
- Verify all modified files in `src/Shiro` and `tests/Shiro.Tests`

- [x] **Step 1: Run full test suite including `ArchitectureTests`**
  - Run: `dotnet test tests/Shiro.Tests -c Release`
  - Expected: 100% pass, 0 warnings, 0 errors, all 26 `ArchitectureTests` green.
