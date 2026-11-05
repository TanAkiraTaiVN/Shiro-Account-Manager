# Wave 3 Core Methods Optimization Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Implement the 6 Wave 3 architectural optimizations across `LogRedactor`/`AppLogger`, `RobloxServerQueryService`, `RobloxDatacenterService`, `NexusWebSocketServer`/`ControlledAccount`, `ClientSettingsPatcher`, and `AntiAfkService`.

**Architecture:**
1. **SIMD `SearchValues<char>` Pre-Filtering & Off-Thread Formatting (`LogRedactor` & `AppLogger`):** Short-circuit ordinary log messages in `< 15ns` without running 8 regex replacements, and move `ToString()` + `LogRedactor.Redact()` + timestamp formatting off the caller/UI thread into the single background consumer loop (`ProcessLogQueueAsync`) via a lightweight `LogEntry` struct.
2. **Hedged Parallel ICMP + TCP 443 RTT Probing (`RobloxServerQueryService.MeasurePingMsAsync`):** Race ICMP Echo and TCP 443 handshake concurrently with a shared `CancellationTokenSource` so firewall-filtered Roblox servers return their true TCP RTT immediately (`~20-60ms`) without waiting for a 250ms ICMP timeout first.
3. **Pre-Computed Immutable `DatacenterResolutionResult` & Source-Gen Deserialization (`RobloxDatacenterService`):** Deserialize `datacenters.json` via `ShiroAppJson.Default.ListDatacenterEntryItem` and pre-compute the `DatacenterResolutionResult` once per entry at load time so `TryResolve` becomes a zero-allocation $O(1)$ lookup.
4. **Zero-Stream Single-Frame WebSocket Decoding & Bounded Send Timeout (`NexusWebSocketServer` & `ControlledAccount`):** Decode single-frame WebSocket messages (`result.EndOfMessage == true` on first read) directly from `ReadOnlySpan<byte>` without allocating or copying into a `MemoryStream`, and guard `ControlledAccount.SendMessageAsync` with a 3-second timeout to prevent hung sockets from deadlocking `_sendLock`.
5. **Source-Generated JSON & Compiled Regex in `ClientSettingsPatcher`:** Replace reflection `JsonSerializer.Deserialize` and per-call `new JsonSerializerOptions { WriteIndented = true }` in `ApplyFpsLimitToDirectory` with `ShiroAppJson.Default.DictionaryStringJsonElement`, and replace dynamic double-pass `Regex.IsMatch` + `Regex.Replace` in `ConfigureGlobalBasicSettings` with `[GeneratedRegex]`.
6. **IOCP Event-Driven Client Pruning & Hoisted ScanCode in `AntiAfkService`:** Subscribe `AntiAfkService` to `RobloxJobObjectService.ProcessExitedFromJob` for immediate `_clients` cleanup and hoist `MapVirtualKey((uint)VK_F15, 0)` out of the per-process loop in `ExecutePassAsync`.

**Tech Stack:** C# 13 / .NET 10 (`net10.0-windows`), `System.Buffers.SearchValues<char>`, `System.Threading.Channels`, `System.Text.Json` Source Generation, `[GeneratedRegex]`, xUnit.

---

## Architecture Constraints (Enforced by `ArchitectureTests`)
- **Max File Length:** Every `.cs` file in `src/Shiro` MUST remain `<= 500` lines.
- **Max Partial Class Length:** Every partial class in `src/Shiro` MUST remain `<= 1200` lines across all partials.
- **Ambient Static Ratchet:** Do NOT add any new `Program.Logger` or `AppSettings.` static calls in `src/Shiro`. Always use `AppServices.Logger` and `AppServices.GeneralSettings`.

---

### Task 1: SIMD `SearchValues<char>` Pre-Filtering in `LogRedactor` & Off-Thread Formatting in `AppLogger`

**Files:**
- Modify: `src/Shiro/Services/Storage/LogRedactor.cs`
- Modify: `src/Shiro/Services/Storage/AppLogger.cs`
- Create: `tests/Shiro.Tests/Wave3LoggerAndRedactorTests.cs`

- [x] **Step 1: Write the failing unit tests in `tests/Shiro.Tests/Wave3LoggerAndRedactorTests.cs`**
  - Verify `LogRedactor.MayContainSensitiveData(ReadOnlySpan<char>)` returns `false` for ordinary log strings (e.g., `"Multi-Instance Ready"`, `"[AntiAfk] Kept alive 4 Roblox client(s)"`) and `true` for strings containing secret markers (`WARNING:-DO-NOT-SHARE-THIS`, `.ROBLOSECURITY=`, `"password":`, `Bearer `, `BLOX-`, `socks5://user:pass@`).
  - Verify `LogRedactor.Redact` returns the exact same string reference (`ReferenceEquals`) when no sensitive markers are present, and accurately redacts all 8 secret patterns when present.
  - Verify `AppLogger.FormatLogLine` formats `LogEntry` accurately on the consumer thread.

- [x] **Step 2: Run test to verify it fails**
  - Run: `dotnet test tests/Shiro.Tests --filter "FullyQualifiedName~Wave3LoggerAndRedactorTests"`

- [x] **Step 3: Implement `SearchValues<char>` Fast Path in `LogRedactor` & `LogEntry` Struct in `AppLogger`**
  - In `LogRedactor.cs`:
    - Add `SearchValues<char>` for trigger characters and fast per-rule checks before invoking regexes.
  - In `AppLogger.cs`:
    - Define `internal readonly record struct LogEntry(DateTime Timestamp, int ThreadId, string Level, string MessageText, Exception? Exception);`
    - Change `_channel` from `Channel<string>` to `Channel<LogEntry>`.
    - Perform `LogRedactor.Redact` and line formatting inside `FormatLogLine(in LogEntry entry, string loggerName)` on the single background reader task in `ProcessLogQueueAsync`.

- [x] **Step 4: Run tests to verify they pass**
  - Run: `dotnet test tests/Shiro.Tests --filter "FullyQualifiedName~Wave3LoggerAndRedactorTests"`

---

### Task 2: Hedged Parallel ICMP + TCP 443 Probe in `RobloxServerQueryService` & Pre-Computed `RobloxDatacenterService`

**Files:**
- Modify: `src/Shiro/Services/ShiroAppJson.cs`
- Modify: `src/Shiro/Services/Network/RobloxDatacenterService.cs`
- Modify: `src/Shiro/Services/Network/RobloxServerQueryService.cs`
- Create: `tests/Shiro.Tests/Wave3ServerPingAndDatacenterTests.cs`

- [x] **Step 1: Write the failing unit tests in `tests/Shiro.Tests/Wave3ServerPingAndDatacenterTests.cs`**
  - Test `RobloxServerQueryService.RacePingProbesAsync(Func<CancellationToken, Task<int?>> icmpProbe, Func<CancellationToken, Task<int?>> tcpProbe)`:
    - When `icmpProbe` times out after `250ms` (returning `null`) and `tcpProbe` succeeds in `35ms`, `RacePingProbesAsync` returns `35` in `< 120ms` without waiting for `icmpProbe` to finish its 250ms timeout!
    - When `icmpProbe` succeeds in `20ms` and `tcpProbe` takes `200ms`, `RacePingProbesAsync` returns `20` immediately and cancels `tcpProbe`.
  - Test `RobloxDatacenterService.Default.TryResolve`:
    - Verify repeated calls to `TryResolve` for the same datacenter return the exact same pre-computed `DatacenterResolutionResult` instance (`ReferenceEquals(r1, r2)`).

- [x] **Step 2: Run test to verify it fails**
  - Run: `dotnet test tests/Shiro.Tests --filter "FullyQualifiedName~Wave3ServerPingAndDatacenterTests"`

- [x] **Step 3: Implement Hedged Ping Probe & Pre-Computed Datacenter Resolution**
  - In `ShiroAppJson.cs`:
    - Add `[JsonSerializable(typeof(DatacenterLocationItem))]`, `[JsonSerializable(typeof(DatacenterEntryItem))]`, `[JsonSerializable(typeof(List<DatacenterEntryItem>))]`.
  - In `RobloxDatacenterService.cs`:
    - Deserialize `datacenters.json` using `ShiroAppJson.Default.ListDatacenterEntryItem`.
    - Pre-compute `DatacenterResolutionResult` once per `DatacenterEntryItem` via `BuildResolutionResult(DatacenterEntryItem entry)` during constructor initialization and store `DatacenterResolutionResult` directly in `_byDataCenterId`, `_byExactIp`, and `_bySubnet24`.
  - In `RobloxServerQueryService.cs`:
    - Add `internal static async Task<int?> RacePingProbesAsync(Func<CancellationToken, Task<int?>> icmpProbe, Func<CancellationToken, Task<int?>> tcpProbe, CancellationToken cancellationToken = default)` and call it from `MeasurePingMsAsync`.

- [x] **Step 4: Run tests to verify they pass**
  - Run: `dotnet test tests/Shiro.Tests --filter "FullyQualifiedName~Wave3ServerPingAndDatacenterTests"`

---

### Task 3: Single-Frame Span WebSocket Decoding in `NexusWebSocketServer` & Send Timeout in `ControlledAccount`

**Files:**
- Modify: `src/Shiro/Nexus/NexusWebSocketServer.cs`
- Modify: `src/Shiro/Nexus/ControlledAccount.cs`
- Create: `tests/Shiro.Tests/Wave3NexusWebSocketTests.cs`

- [x] **Step 1: Write the failing unit tests in `tests/Shiro.Tests/Wave3NexusWebSocketTests.cs`**
  - Test `NexusWebSocketServer.ReceiveFullTextMessageAsync(WebSocket socket, byte[] buffer, int maxPayloadBytes, CancellationToken ct)` with:
    - Single-frame message (`EndOfMessage == true` on first read): decodes directly from `buffer` without allocating a `MemoryStream`.
    - Multi-frame message (`EndOfMessage == false` then `true`): reassembles across frames accurately.
  - Test `ControlledAccount.SendMessageAsync(string message, int timeoutMs)` with a stalled `WebSocket`:
    - Completes cleanly within the timeout instead of hanging indefinitely.

- [x] **Step 2: Run test to verify it fails**
  - Run: `dotnet test tests/Shiro.Tests --filter "FullyQualifiedName~Wave3NexusWebSocketTests"`

- [x] **Step 3: Implement Single-Frame Fast Path & Bounded Send Timeout**
  - In `NexusWebSocketServer.cs`:
    - Extract `internal static async Task<(WebSocketMessageType MessageType, string? Text, bool PayloadExceeded)> ReceiveFullTextMessageAsync(WebSocket webSocket, byte[] buffer, int maxPayloadBytes, CancellationToken ct)` that decodes `Encoding.UTF8.GetString(buffer.AsSpan(0, result.Count))` directly when `result.EndOfMessage` is `true` on the first frame, only lazily allocating a `MemoryStream` if a message spans multiple frames.
  - In `ControlledAccount.cs`:
    - Update `SendMessageAsync(string Message, int sendTimeoutMs = 3000)` to use a `CancellationTokenSource(sendTimeoutMs)` for both `_sendLock.WaitAsync(cts.Token)` and `WebSocket.SendAsync(..., cts.Token)`.

- [x] **Step 4: Run tests to verify they pass**
  - Run: `dotnet test tests/Shiro.Tests --filter "FullyQualifiedName~Wave3NexusWebSocketTests"`

---

### Task 4: Source-Generated JSON & Compiled Regex in `ClientSettingsPatcher` + IOCP Pruning in `AntiAfkService`

**Files:**
- Modify: `src/Shiro/Classes/ClientSettingsPatcher.cs`
- Modify: `src/Shiro/Services/Windows/AntiAfkService.cs`
- Create: `tests/Shiro.Tests/Wave3ClientSettingsAndAntiAfkTests.cs`

- [x] **Step 1: Write the failing unit tests in `tests/Shiro.Tests/Wave3ClientSettingsAndAntiAfkTests.cs`**
  - Test `ClientSettingsPatcher.UpdateBasicSettingsXmlContent(string xml, int? graphicsQualityLevel, bool? fullscreen, out bool modified)` updates `<int name="GraphicsQualityLevel">` and `<bool name="Fullscreen">` in a single `[GeneratedRegex]` pass.
  - Test `ClientSettingsPatcher.ApplyFpsLimitToDirectory` in a temporary directory: writes and removes `DFIntTaskSchedulerTargetFps` using source-generated JSON metadata.
  - Test `AntiAfkService.RemoveClientState(int pid)` removes an exited client from `_clients` immediately when notified by `RobloxJobObjectService.ProcessExitedFromJob`.

- [x] **Step 2: Run test to verify it fails**
  - Run: `dotnet test tests/Shiro.Tests --filter "FullyQualifiedName~Wave3ClientSettingsAndAntiAfkTests"`

- [x] **Step 3: Implement Source-Gen `ClientSettingsPatcher` & Event-Driven `AntiAfkService`**
  - In `ClientSettingsPatcher.cs`:
    - Mark class `public static partial class ClientSettingsPatcher`.
    - Add `[GeneratedRegex(@"<int name=""GraphicsQualityLevel"">\d+</int>")]` and `[GeneratedRegex(@"<bool name=""Fullscreen"">(true|false)</bool>")]`.
    - Use `ShiroAppJson.Default.DictionaryStringJsonElement` and `Utf8JsonWriter(..., new JsonWriterOptions { Indented = true })` in `ApplyFpsLimitToDirectory` so zero reflection or per-call `JsonSerializerOptions` instances are created.
  - In `AntiAfkService.cs`:
    - Subscribe `RobloxJobObjectService.ProcessExitedFromJob += static (pid, _) => RemoveClientState(pid);` in static constructor.
    - Hoist `uint scanCode = MapVirtualKey((uint)VK_F15, 0);` and `lParamDown`/`lParamUp` computation outside the `foreach (int pid in pids)` loop in `ExecutePassAsync`.

- [x] **Step 4: Run tests to verify they pass**
  - Run: `dotnet test tests/Shiro.Tests --filter "FullyQualifiedName~Wave3ClientSettingsAndAntiAfkTests"`

---

### Task 5: Full Regression & `ArchitectureTests` Verification

**Files:**
- Verify all modified files in `src/Shiro` and `tests/Shiro.Tests`

- [x] **Step 1: Run full test suite including `ArchitectureTests`**
  - Run: `dotnet test tests/Shiro.Tests -c Release`
  - Expected: 100% pass, 0 warnings, 0 errors, all 26 `ArchitectureTests` green.
