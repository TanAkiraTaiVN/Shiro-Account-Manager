# Core Methods & Architecture Optimization Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Upgrade the 7 core subsystems of Shiro Account Manager from timer-polling and redundant syscalls/KDF work to event-driven, zero-waste, cryptographically optimal methods while keeping 100% of existing tests and architecture ratchets green.

**Architecture:** Replace redundant 600k-iteration PBKDF2 runs on already-random 256-bit vault keys with RFC 5869 `HKDF-SHA256` (Vault V4 header) while preserving 600k-iteration PBKDF2 for user Master Passwords; cache parsed ECDSA parameters for HBA signing; unify `MemoryOptimizer` and `RobloxBackgroundThrottlerService` into a single-handle resource profile using `SetProcessWorkingSetSizeEx` and `ProcessMemoryPriority`; batch multi-window tiling with Win32 `DeferWindowPos`; attach an I/O Completion Port to `RobloxJobObjectService` for instant process exit notifications; bind Roblox log files deterministically via `-b <browserTrackerId>` header inspection plus `FileSystemWatcher`; pipeline `ServerListViewModel.SearchPlayerAsync` with `System.Threading.Channels` and parallelize `SessionAuditorService` probes; and add structured JSON auth/moderation response classification + `MutationObserver` to `FastLoginCoordinator`.

**Tech Stack:** .NET 10 (win-x64), C# 13, Avalonia UI 11.3.22, Win32 / NTAPI P/Invoke (`LibraryImport`), `System.Security.Cryptography` (`AesGcm`, `HKDF`, `ECDsa`), `System.Threading.Channels`, xUnit 2.9.3, NSubstitute 5.3.0.

**Spec:** Architectural review of core methods conducted on 2026-11-04 across Launch, Auth/Crypto, Windows Integration, and Network subsystems.

## Global Constraints

- `TreatWarningsAsErrors` stays enabled: 0 warnings, 0 errors on `dotnet build -c Release`.
- Every `.cs` file must remain ≤ 500 lines (`NoSourceCodeFiles_ShouldExceed500Lines`) and partial class line budgets must not be exceeded (`NoClass_ShouldExceedLineBudget_AcrossPartials`).
- Zero empty `catch` blocks (`EmptyCatchBlocks_ShouldNotExceedBaseline` = 0).
- `AmbientStaticAccess_ShouldNotExceedPerFolderBaseline`: `Services/Network`, `Services/Auth`, `Services/Storage`, and `ViewModels` must stay at `(0, 0)` references to `Program.Logger` and `AppSettings.`.
- No hardcoded Vietnamese string literals in `.cs` files outside `LocalizationService*` (`CSharpSourceFiles_ShouldNotContain_HardcodedVietnameseStringLiterals`).
- No `.GetAwaiter().GetResult()` sync-over-async calls (`SourceFiles_ShouldNotUse_SyncOverAsyncBlocking`).
- Every test method must assert concrete values (`TestMethods_ShouldAssertOnValues_NotOnlyNullness`).

## Review Focus

1. **Backward compatibility of encrypted vaults (V1, V2, V3, V4):** Existing V3 (600k PBKDF2) and V1/V2 (210k PBKDF2) vaults and Master-Password-locked vaults must still decrypt without data loss when V4 (`HKDF-SHA256` for 32-byte random `vault.key`) is introduced → `Decrypt_SupportsV1_V3_And_V4_Payloads` in Task 1.
2. **Non-32-byte or Master-Password keys passed to `EncryptWithRandomKey`:** Must reject keys that are not 32 bytes and fall back to PBKDF2 for user passwords → `EncryptWithRandomKey_InvalidKeyLength_ThrowsArgumentException` in Task 1.
3. **`DeferWindowPos` failure or invalid HWND during `TileWindows`:** When `BeginDeferWindowPos` or `DeferWindowPos` returns `IntPtr.Zero` (e.g., cross-privilege window), `TileWindows` must gracefully fall back to per-window `SetWindowPos` without losing windows → `TileWindows_FallsBackWhenDeferFails` in Task 3.
4. **Concurrent multi-account launches writing log files within the same second:** A log file whose header contains `-b <otherTrackerId>` must never be claimed by an account with `-b <myTrackerId>` even if timestamps are identical → `DoesLogHeaderMatchProcess_RejectsMismatchedTrackerId` in Task 5.
5. **Cancelling or finding an early match in `SearchPlayerAsync` channel pipeline:** Once a consumer matches `targetAvatarUrl`, the producer must stop fetching subsequent server pages immediately → `SearchPlayerAsync_StopsProducerOnFirstBatchMatch` in Task 6.

---

### Task 1: Vault V4 Fast HKDF Encryption for Random Keys & HBA ECDSA Parameter Caching

**Files:**
- Modify: `src/Shiro/Classes/Cryptography.cs`
- Modify: `src/Shiro/AccountManager.Persistence.cs`
- Modify: `src/Shiro/Services/Auth/RobloxHbaService.cs`
- Test: `tests/Shiro.Tests/CryptographyAndHbaOptimizationTests.cs`

**Interfaces:**
- Produces:
  - `Cryptography.ShiroVaultHeaderV4` (`byte[]`)
  - `Cryptography.EncryptWithRandomKey(ReadOnlySpan<byte> plaintextBytes, ReadOnlySpan<byte> randomKey256) -> byte[]`
  - `RobloxHbaService.ClearKeyCache() -> void`

- [ ] **Step 1: Write the failing tests in `tests/Shiro.Tests/CryptographyAndHbaOptimizationTests.cs`**

```csharp
using System;
using System.Security.Cryptography;
using System.Text;
using Shiro.Classes;
using Shiro.Services;
using Xunit;

namespace Shiro.Tests
{
    public class CryptographyAndHbaOptimizationTests
    {
        [Fact]
        public void EncryptWithRandomKey_ProducesV4Header_AndRoundTripsViaDecrypt()
        {
            byte[] key = RandomNumberGenerator.GetBytes(32);
            byte[] plain = Encoding.UTF8.GetBytes("{\"accounts\":[{\"u\":\"shiro\"}]}");

            byte[] encrypted = Cryptography.EncryptWithRandomKey(plain, key);

            Assert.True(Cryptography.IsEncrypted(encrypted));
            Assert.False(Cryptography.IsLegacyHeader(encrypted));
            Assert.True(encrypted.AsSpan(0, Cryptography.ShiroVaultHeaderV4.Length)
                .SequenceEqual(Cryptography.ShiroVaultHeaderV4));

            byte[] decrypted = Cryptography.Decrypt(encrypted, key);
            Assert.Equal(plain, decrypted);
        }

        [Fact]
        public void EncryptWithRandomKey_InvalidKeyLength_ThrowsArgumentException()
        {
            byte[] shortKey = new byte[16];
            byte[] plain = Encoding.UTF8.GetBytes("payload");

            var ex = Assert.Throws<ArgumentException>(() => Cryptography.EncryptWithRandomKey(plain, shortKey));
            Assert.Contains("32", ex.Message, StringComparison.Ordinal);
        }

        [Fact]
        public void Decrypt_SupportsV3Pbkdf2_And_V4Hkdf_Payloads()
        {
            byte[] key = RandomNumberGenerator.GetBytes(32);
            byte[] plain = Encoding.UTF8.GetBytes("secret-account-vault");

            byte[] v3 = Cryptography.Encrypt(plain, key);
            byte[] v4 = Cryptography.EncryptWithRandomKey(plain, key);

            Assert.Equal(plain, Cryptography.Decrypt(v3, key));
            Assert.Equal(plain, Cryptography.Decrypt(v4, key));
        }

        [Fact]
        public void RobloxHbaService_CachesEcParameters_AndClearKeyCacheResetsCleanly()
        {
            using var ecdsa = ECDsa.Create(ECCurve.NamedCurves.nistP256);
            string pkcs8Base64 = Convert.ToBase64String(ecdsa.ExportPkcs8PrivateKey());

            bool ok1 = RobloxHbaService.TryBuildBoundAuthToken(
                pkcs8Base64, "https://auth.roblox.com/v1/authentication-ticket/", "POST", "{}", null, 1700000000L, out string token1);
            bool ok2 = RobloxHbaService.TryBuildBoundAuthToken(
                pkcs8Base64, "https://auth.roblox.com/v1/authentication-ticket/", "POST", "{}", null, 1700000000L, out string token2);

            Assert.True(ok1);
            Assert.True(ok2);
            Assert.StartsWith("v1|", token1, StringComparison.Ordinal);
            Assert.StartsWith("v1|", token2, StringComparison.Ordinal);

            RobloxHbaService.ClearKeyCache();

            bool okAfterClear = RobloxHbaService.TryBuildBoundAuthToken(
                pkcs8Base64, "https://auth.roblox.com/v1/authentication-ticket/", "POST", "{}", null, 1700000000L, out string token3);
            Assert.True(okAfterClear);
            Assert.StartsWith("v1|", token3, StringComparison.Ordinal);
        }
    }
}
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test tests/Shiro.Tests -c Release --filter "FullyQualifiedName~CryptographyAndHbaOptimizationTests"`
Expected: FAIL with compilation error (`ShiroVaultHeaderV4`, `EncryptWithRandomKey`, `ClearKeyCache` not defined).

- [ ] **Step 3: Implement `ShiroVaultHeaderV4` & `EncryptWithRandomKey` in `src/Shiro/Classes/Cryptography.cs`, wire vault-key save in `src/Shiro/AccountManager.Persistence.cs`, and cache `ECParameters` in `src/Shiro/Services/Auth/RobloxHbaService.cs`**

In `src/Shiro/Classes/Cryptography.cs`:
- Define `public static readonly byte[] ShiroVaultHeaderV4 = Encoding.UTF8.GetBytes("SHIRO_SECURE_VAULT_V4_AES_GCM_HKDF_SHA256_AUTHENTICATED_STORE");` and `private static readonly byte[] HkdfInfo = Encoding.UTF8.GetBytes("Shiro.Vault.AES256GCM.v4");`.
- Update `IsEncrypted(ReadOnlySpan<byte> data)` to check `ShiroVaultHeaderV4` first.
- Add `public static byte[] EncryptWithRandomKey(ReadOnlySpan<byte> plaintextBytes, ReadOnlySpan<byte> randomKey256)` which validates `randomKey256.Length == KeySize` (32), writes `ShiroVaultHeaderV4`, fills 16-byte `salt` and 12-byte `nonce`, derives `derivedKey` via `HKDF.DeriveKey(HashAlgorithmName.SHA256, randomKey256, derivedKey, salt, HkdfInfo)`, encrypts with `AesGcm`, and zeroes `derivedKey`.
- Update `Decrypt(byte[] encrypted, byte[] password)`: when header matches `ShiroVaultHeaderV4`, derive the 32-byte key via `HKDF.DeriveKey(HashAlgorithmName.SHA256, password, derivedKey, salt, HkdfInfo)` instead of `Rfc2898DeriveBytes.Pbkdf2`.
- In `src/Shiro/AccountManager.Persistence.cs` (`SaveAccounts`): when `PasswordHash.IsEmpty` (using 32-byte `vaultKey`), call `Cryptography.EncryptWithRandomKey(SaveData, vaultKey)` instead of `Cryptography.Encrypt(SaveData, vaultKey)`.
- In `src/Shiro/Services/Auth/RobloxHbaService.cs`: add `private static readonly ConcurrentDictionary<string, ECParameters> CachedEcParameters = new(StringComparer.Ordinal);` and `public static void ClearKeyCache()` that wipes cached `keyBytes` and `ECParameters.D` arrays with `CryptographicOperations.ZeroMemory` before clearing both dictionaries. In `TryBuildBoundAuthToken`, lookup or populate `CachedEcParameters[trimmedKey]` and create `using ECDsa ecdsa = ECDsa.Create(ecParams);`.

- [ ] **Step 4: Run tests to verify they pass**

Run: `dotnet test tests/Shiro.Tests -c Release --filter "FullyQualifiedName~CryptographyAndHbaOptimizationTests"`
Expected: PASS (4/4). Run full suite `dotnet test tests/Shiro.Tests -c Release` to confirm all existing vault/recovery tests pass.

- [ ] **Step 5: Commit**

```bash
git add src/Shiro/Classes/Cryptography.cs src/Shiro/AccountManager.Persistence.cs src/Shiro/Services/Auth/RobloxHbaService.cs tests/Shiro.Tests/CryptographyAndHbaOptimizationTests.cs
git commit -m "perf(crypto): use HKDF-SHA256 V4 for 256-bit vault keys and cache HBA ECParameters"
```

---

### Task 2: Unified Background Resource Profile & Gentle Working-Set Trimming

**Files:**
- Modify: `src/Shiro/Services/Windows/MemoryOptimizer.cs`
- Modify: `src/Shiro/Services/Windows/RobloxBackgroundThrottlerService.cs`
- Modify: `src/Shiro/Services/Windows/ForegroundWindowService.cs`
- Test: `tests/Shiro.Tests/UnifiedResourceOptimizerTests.cs`

**Interfaces:**
- Produces:
  - `MemoryOptimizer.ComputeTargetPriorityClass(bool isForeground, bool throttlerEnabled, int targetBackgroundFps) -> uint`
  - `MemoryOptimizer.ApplyProcessFocusProfile(int pid, bool isForeground) -> void` (now unified with `RobloxBackgroundThrottlerService` state)

- [ ] **Step 1: Write the failing tests in `tests/Shiro.Tests/UnifiedResourceOptimizerTests.cs`**

```csharp
using System.Diagnostics;
using Shiro.Services;
using Xunit;

namespace Shiro.Tests
{
    [Collection("StaticSeams")]
    public class UnifiedResourceOptimizerTests
    {
        [Theory]
        [InlineData(true, false, 10, RobloxProcessOptimizer.NORMAL_PRIORITY_CLASS)]
        [InlineData(true, true, 5, RobloxProcessOptimizer.NORMAL_PRIORITY_CLASS)]
        [InlineData(false, false, 10, RobloxProcessOptimizer.BELOW_NORMAL_PRIORITY_CLASS)]
        [InlineData(false, true, 30, RobloxProcessOptimizer.BELOW_NORMAL_PRIORITY_CLASS)]
        [InlineData(false, true, 15, RobloxBackgroundThrottlerService.IDLE_PRIORITY_CLASS)]
        [InlineData(false, true, 5, RobloxBackgroundThrottlerService.IDLE_PRIORITY_CLASS)]
        public void ComputeTargetPriorityClass_UnifiesMemoryOptimizerAndBackgroundThrottler(
            bool isForeground,
            bool throttlerEnabled,
            int targetBackgroundFps,
            uint expectedPriorityClass)
        {
            uint actual = MemoryOptimizer.ComputeTargetPriorityClass(isForeground, throttlerEnabled, targetBackgroundFps);
            Assert.Equal(expectedPriorityClass, actual);
        }

        [Fact]
        public void BackgroundThrottler_OnForegroundWindowChanged_TracksThrottledStateAndSynchronizesFocus()
        {
            var throttler = RobloxBackgroundThrottlerService.Instance;
            bool prev = throttler.IsEnabled;
            try
            {
                throttler.IsEnabled = true;
                throttler.TargetBackgroundFps = 10;

                throttler.OnForegroundWindowChanged(activeProcessId: 1001, runningRobloxPids: new[] { 1001, 1002 });

                Assert.False(throttler.IsProcessThrottled(1001));
                Assert.True(throttler.IsProcessThrottled(1002));

                throttler.OnForegroundWindowChanged(activeProcessId: 1002, runningRobloxPids: new[] { 1001, 1002 });

                Assert.True(throttler.IsProcessThrottled(1001));
                Assert.False(throttler.IsProcessThrottled(1002));
            }
            finally
            {
                throttler.IsEnabled = prev;
                throttler.RestoreAllThrottledProcesses();
            }
        }
    }
}
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test tests/Shiro.Tests -c Release --filter "FullyQualifiedName~UnifiedResourceOptimizerTests"`
Expected: FAIL (`MemoryOptimizer.ComputeTargetPriorityClass` not defined).

- [ ] **Step 3: Implement unified focus profile in `src/Shiro/Services/Windows/MemoryOptimizer.cs`, `RobloxBackgroundThrottlerService.cs`, and `ForegroundWindowService.cs`**

- In `src/Shiro/Services/Windows/MemoryOptimizer.cs`:
  - Add `internal static uint ComputeTargetPriorityClass(bool isForeground, bool throttlerEnabled, int targetBackgroundFps)` returning `NORMAL_PRIORITY_CLASS` when `isForeground`, `IDLE_PRIORITY_CLASS` (`0x00000040`) when `throttlerEnabled && targetBackgroundFps <= 15`, and `BELOW_NORMAL_PRIORITY_CLASS` otherwise.
  - Add `SetProcessWorkingSetSizeEx` (`kernel32.dll`, `QUOTA_LIMITS_HARDWS_MIN_DISABLE = 0x00000002`) and `MEMORY_PRIORITY_INFORMATION` (`ProcessMemoryPriority = 39`, `MEMORY_PRIORITY_LOW = 2`, `MEMORY_PRIORITY_NORMAL = 5`).
  - Update `ApplyProcessFocusProfile(int pid, bool isForeground)` to open `PROCESS_QUERY_LIMITED_INFORMATION | PROCESS_SET_INFORMATION | PROCESS_SET_QUOTA` once, set priority via `ComputeTargetPriorityClass(isForeground, RobloxBackgroundThrottlerService.Instance.IsEnabled, RobloxBackgroundThrottlerService.Instance.TargetBackgroundFps)`, toggle `EcoQoS`, set `ProcessMemoryPriority`, and invoke soft working-set quota relaxation on background transition.
- In `src/Shiro/Services/Windows/RobloxBackgroundThrottlerService.cs`:
  - In `OnForegroundWindowChanged`, only call `TrimWorkingSet(pid)` when `AppSettings.General.Get<bool>("AllowEmptyWorkingSetTrimming", false)` is `true`, and delegate priority/EcoQoS application to `ApplyThrottleState` which also sets `EcoQoS` so the two services never conflict.
- In `src/Shiro/Services/Windows/ForegroundWindowService.cs`:
  - In `OnWinEvent`, if `needThrottler` is active alongside `needMemory`, update `RobloxBackgroundThrottlerService.Instance` tracking without double-opening the same process handles.

- [ ] **Step 4: Run tests to verify they pass**

Run: `dotnet test tests/Shiro.Tests -c Release --filter "FullyQualifiedName~UnifiedResourceOptimizerTests"`
Expected: PASS.

- [ ] **Step 5: Commit**

```bash
git add src/Shiro/Services/Windows/MemoryOptimizer.cs src/Shiro/Services/Windows/RobloxBackgroundThrottlerService.cs src/Shiro/Services/Windows/ForegroundWindowService.cs tests/Shiro.Tests/UnifiedResourceOptimizerTests.cs
git commit -m "perf(windows): unify background priority/EcoQoS profile and avoid aggressive EmptyWorkingSet"
```

---

### Task 3: Atomic Multi-Window Tiling (`DeferWindowPos`) & Fast-Path `--launch-to-tray` Filtering

**Files:**
- Modify: `src/Shiro/Services/Windows/RobloxWindowService.Tiling.cs`
- Modify: `src/Shiro/Services/Windows/ProcessExtensions.cs`
- Modify: `src/Shiro/Services/Windows/RobloxProcessService.cs`
- Test: `tests/Shiro.Tests/WindowTilingAndTrayFilterTests.cs`

**Interfaces:**
- Produces:
  - `RobloxWindowService.ComputeTilePlacements(IReadOnlyList<IntPtr> windows, DesktopRect workArea, int customCols = 0, int customRows = 0) -> (int Cols, int Rows, List<(IntPtr Hwnd, int X, int Y, int Width, int Height)> Placements)`
  - `ProcessExtensions.IsLaunchToTrayCommandLine(string? commandLine) -> bool`
  - `ProcessExtensions.IsLaunchToTrayProcess(int pid) -> bool`

- [ ] **Step 1: Write the failing tests in `tests/Shiro.Tests/WindowTilingAndTrayFilterTests.cs`**

```csharp
using System;
using System.Collections.Generic;
using Shiro.Services;
using Xunit;

namespace Shiro.Tests
{
    [Collection("StaticSeams")]
    public class WindowTilingAndTrayFilterTests
    {
        [Fact]
        public void ComputeTilePlacements_CalculatesExactCoordinatesForFourWindows()
        {
            var windows = new List<IntPtr> { (IntPtr)101, (IntPtr)102, (IntPtr)103, (IntPtr)104 };
            var workArea = new RobloxWindowService.DesktopRect(0, 0, 1920, 1080);

            var (cols, rows, placements) = RobloxWindowService.ComputeTilePlacements(windows, workArea);

            Assert.Equal(2, cols);
            Assert.Equal(2, rows);
            Assert.Equal(4, placements.Count);
            Assert.Equal(((IntPtr)101, 0, 0, 960, 540), placements[0]);
            Assert.Equal(((IntPtr)102, 960, 0, 960, 540), placements[1]);
            Assert.Equal(((IntPtr)103, 0, 540, 960, 540), placements[2]);
            Assert.Equal(((IntPtr)104, 960, 540, 960, 540), placements[3]);
        }

        [Theory]
        [InlineData("RobloxPlayerBeta.exe --app -t ticket -j url", false)]
        [InlineData("RobloxPlayerBeta.exe --launch-to-tray", true)]
        [InlineData("RobloxPlayerBeta.exe --LAUNCH-TO-TRAY --watcher", true)]
        [InlineData(null, false)]
        [InlineData("", false)]
        public void IsLaunchToTrayCommandLine_DetectsTrayDaemonFlag(string? cmd, bool expected)
        {
            Assert.Equal(expected, ProcessExtensions.IsLaunchToTrayCommandLine(cmd));
        }

        [Fact]
        public void IsLaunchToTrayProcess_UsesCommandLineOverride()
        {
            var prev = ProcessExtensions.TestCommandLineOverride;
            try
            {
                ProcessExtensions.TestCommandLineOverride = pid =>
                    pid == 4242 ? "C:\\RobloxPlayerBeta.exe --launch-to-tray" : "C:\\RobloxPlayerBeta.exe --app";

                Assert.True(ProcessExtensions.IsLaunchToTrayProcess(4242));
                Assert.False(ProcessExtensions.IsLaunchToTrayProcess(4243));
            }
            finally
            {
                ProcessExtensions.TestCommandLineOverride = prev;
            }
        }
    }
}
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test tests/Shiro.Tests -c Release --filter "FullyQualifiedName~WindowTilingAndTrayFilterTests"`
Expected: FAIL (`ComputeTilePlacements`, `IsLaunchToTrayCommandLine`, `IsLaunchToTrayProcess` not defined).

- [ ] **Step 3: Implement `DeferWindowPos` batching in `RobloxWindowService.Tiling.cs` and cached tray-daemon filtering in `ProcessExtensions.cs` / `RobloxProcessService.cs`**

- In `src/Shiro/Services/Windows/RobloxWindowService.Tiling.cs`:
  - Add `[LibraryImport("user32.dll", SetLastError = true)]` declarations for `BeginDeferWindowPos(int nNumWindows)`, `DeferWindowPos(IntPtr hWinPosInfo, IntPtr hWnd, IntPtr hWndInsertAfter, int x, int y, int cx, int cy, uint uFlags)`, and `EndDeferWindowPos(IntPtr hWinPosInfo)`.
  - Extract `public static (int Cols, int Rows, List<(IntPtr Hwnd, int X, int Y, int Width, int Height)> Placements) ComputeTilePlacements(IReadOnlyList<IntPtr> windows, DesktopRect workArea, int customCols = 0, int customRows = 0)`.
  - In `TileWindows`, first restore minimized windows via `ShowWindow(hWnd, SW_RESTORE)`, then attempt atomic layout via `BeginDeferWindowPos` / `DeferWindowPos` / `EndDeferWindowPos`. If `BeginDeferWindowPos` or any `DeferWindowPos` returns `IntPtr.Zero`, fall back to `SetWindowPos` for each placement so cross-privilege windows still tile reliably.
- In `src/Shiro/Services/Windows/ProcessExtensions.cs`:
  - Add `public static bool IsLaunchToTrayCommandLine(string? commandLine)` and `public static bool IsLaunchToTrayProcess(int pid)`.
- In `src/Shiro/Services/Windows/RobloxProcessService.cs`:
  - Add `public static List<int> GetRunningRobloxPids(bool includeTrayDaemons)` (with `GetRunningRobloxPids()` defaulting to `includeTrayDaemons = false`), filtering out `ProcessExtensions.IsLaunchToTrayProcess(pid)` on the native snapshot path while `CleanZombieProcesses()` calls `GetRunningRobloxPids(includeTrayDaemons: true)` so tray daemons are still purged by `CleanZombieProcesses`!

- [ ] **Step 4: Run tests to verify they pass**

Run: `dotnet test tests/Shiro.Tests -c Release --filter "FullyQualifiedName~WindowTilingAndTrayFilterTests"`
Expected: PASS. Run full test suite to verify `RobloxProcessService` tests pass.

- [ ] **Step 5: Commit**

```bash
git add src/Shiro/Services/Windows/RobloxWindowService.Tiling.cs src/Shiro/Services/Windows/ProcessExtensions.cs src/Shiro/Services/Windows/RobloxProcessService.cs src/Shiro/Services/Windows/RobloxProcessService.Lifecycle.cs tests/Shiro.Tests/WindowTilingAndTrayFilterTests.cs
git commit -m "perf(windows): batch TileWindows with DeferWindowPos and filter tray daemons in native snapshot"
```

---

### Task 4: Job Object I/O Completion Port Process Exit Notifications

**Files:**
- Modify: `src/Shiro/Services/Windows/RobloxJobObjectService.cs`
- Modify: `src/Shiro/Services/Launch/AutoReconnectService.cs`
- Test: `tests/Shiro.Tests/JobObjectCompletionPortTests.cs`

**Interfaces:**
- Produces:
  - `RobloxJobObjectService.ProcessExitedFromJob` (`event Action<int, bool>?` — `(int pid, bool abnormalExit)`)
  - `RobloxJobObjectService.DispatchCompletionMessage(uint messageId, nuint processId) -> bool`

- [ ] **Step 1: Write the failing tests in `tests/Shiro.Tests/JobObjectCompletionPortTests.cs`**

```csharp
using Shiro.Services;
using Xunit;

namespace Shiro.Tests
{
    [Collection("StaticSeams")]
    public class JobObjectCompletionPortTests
    {
        [Fact]
        public void DispatchCompletionMessage_RaisesProcessExitedFromJob_OnNormalAndAbnormalExit()
        {
            int observedPid = 0;
            bool observedAbnormal = false;
            int callCount = 0;

            void Handler(int pid, bool abnormal)
            {
                observedPid = pid;
                observedAbnormal = abnormal;
                callCount++;
            }

            RobloxJobObjectService.ProcessExitedFromJob += Handler;
            try
            {
                // JOB_OBJECT_MSG_EXIT_PROCESS = 4
                bool handledExit = RobloxJobObjectService.DispatchCompletionMessage(4, 12345);
                Assert.True(handledExit);
                Assert.Equal(1, callCount);
                Assert.Equal(12345, observedPid);
                Assert.False(observedAbnormal);

                // JOB_OBJECT_MSG_ABNORMAL_EXIT_PROCESS = 8
                bool handledAbnormal = RobloxJobObjectService.DispatchCompletionMessage(8, 67890);
                Assert.True(handledAbnormal);
                Assert.Equal(2, callCount);
                Assert.Equal(67890, observedPid);
                Assert.True(observedAbnormal);

                // Unrelated message = 99
                Assert.False(RobloxJobObjectService.DispatchCompletionMessage(99, 111));
                Assert.Equal(2, callCount);
            }
            finally
            {
                RobloxJobObjectService.ProcessExitedFromJob -= Handler;
            }
        }
    }
}
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test tests/Shiro.Tests -c Release --filter "FullyQualifiedName~JobObjectCompletionPortTests"`
Expected: FAIL (`ProcessExitedFromJob` and `DispatchCompletionMessage` not defined).

- [ ] **Step 3: Implement IOCP listener in `src/Shiro/Services/Windows/RobloxJobObjectService.cs` and subscribe in `src/Shiro/Services/Launch/AutoReconnectService.cs`**

- In `src/Shiro/Services/Windows/RobloxJobObjectService.cs`:
  - Define constants `JobObjectAssociateCompletionPortInformation = 7`, `JOB_OBJECT_MSG_NEW_PROCESS = 6`, `JOB_OBJECT_MSG_EXIT_PROCESS = 4`, `JOB_OBJECT_MSG_ABNORMAL_EXIT_PROCESS = 8`.
  - Add `[StructLayout(LayoutKind.Sequential)] private struct JOBOBJECT_ASSOCIATE_COMPLETION_PORT { public IntPtr CompletionKey; public IntPtr CompletionPort; }`.
  - P/Invoke `CreateIoCompletionPort`, `GetQueuedCompletionStatus`, and `PostQueuedCompletionStatus` from `kernel32.dll`.
  - Expose `public static event Action<int, bool>? ProcessExitedFromJob;` and `internal static bool DispatchCompletionMessage(uint messageId, nuint processId)` which invalidates `RobloxProcessService.InvalidateSnapshotCache()` on `NEW_PROCESS` / `EXIT_PROCESS` / `ABNORMAL_EXIT_PROCESS` and invokes `ProcessExitedFromJob((int)processId, messageId == JOB_OBJECT_MSG_ABNORMAL_EXIT_PROCESS)`.
  - In `EnsureJobHandle()`, attach a completion port and start a background `Thread` (`IsBackground = true`, `Name = "shiro-job-iocp"`) pumping `GetQueuedCompletionStatus`. Cleanly post a `0` quit packet and close `_hIocp` inside `Close()`.
- In `src/Shiro/Services/Launch/AutoReconnectService.cs`:
  - Subscribe `RobloxJobObjectService.ProcessExitedFromJob += OnJobProcessExited;` in constructor (and unsubscribe in `Dispose`), where `OnJobProcessExited(int pid, bool abnormal)` marks matching active session's `ObservedProcessExited = true` (and `ObservedExitCode = abnormal ? -1 : null`) and triggers `CheckSessionsCallback(null)`.

- [ ] **Step 4: Run tests to verify they pass**

Run: `dotnet test tests/Shiro.Tests -c Release --filter "FullyQualifiedName~JobObjectCompletionPortTests"`
Expected: PASS.

- [ ] **Step 5: Commit**

```bash
git add src/Shiro/Services/Windows/RobloxJobObjectService.cs src/Shiro/Services/Launch/AutoReconnectService.cs tests/Shiro.Tests/JobObjectCompletionPortTests.cs
git commit -m "feat(windows): attach IO completion port to RobloxJobObjectService for instant exit events"
```

---

### Task 5: Deterministic Log-File Binding by Tracker ID & Event-Driven Log Watcher

**Files:**
- Modify: `src/Shiro/Classes/RobloxProcess.LogPatterns.cs`
- Modify: `src/Shiro/Classes/RobloxProcess.cs`
- Modify: `src/Shiro/Classes/RobloxWatcher.cs`
- Test: `tests/Shiro.Tests/LogBindingAndWatcherTests.cs`

**Interfaces:**
- Produces:
  - `enum LogHeaderMatchResult { Inconclusive = 0, Matched = 1, Mismatched = 2 }`
  - `RobloxProcess.EvaluateLogHeaderForTracker(string? logHeaderSample, string? expectedTrackerId) -> LogHeaderMatchResult`

- [ ] **Step 1: Write the failing tests in `tests/Shiro.Tests/LogBindingAndWatcherTests.cs`**

```csharp
using Shiro.Classes;
using Xunit;

namespace Shiro.Tests
{
    public class LogBindingAndWatcherTests
    {
        [Theory]
        [InlineData("2026-11-04T10:00:00.000Z,0.1,1234,6 [FLog::Output] Command line: RobloxPlayerBeta.exe --app -t abc -b 112233445566", "112233445566", LogHeaderMatchResult.Matched)]
        [InlineData("2026-11-04T10:00:00.000Z,0.1,1234,6 [FLog::Output] browserTrackerId=112233445566", "112233445566", LogHeaderMatchResult.Matched)]
        [InlineData("2026-11-04T10:00:00.000Z,0.1,1234,6 [FLog::Output] Command line: RobloxPlayerBeta.exe --app -t abc -b 998877665544", "112233445566", LogHeaderMatchResult.Mismatched)]
        [InlineData("2026-11-04T10:00:00.000Z,0.1,1234,6 [FLog::Output] Starting Roblox client...", "112233445566", LogHeaderMatchResult.Inconclusive)]
        [InlineData("", "112233445566", LogHeaderMatchResult.Inconclusive)]
        [InlineData("any header", "", LogHeaderMatchResult.Inconclusive)]
        public void EvaluateLogHeaderForTracker_ClassifiesTrackerOwnershipDeterministically(
            string headerSample,
            string expectedTrackerId,
            LogHeaderMatchResult expected)
        {
            var actual = RobloxProcess.EvaluateLogHeaderForTracker(headerSample, expectedTrackerId);
            Assert.Equal(expected, actual);
        }
    }
}
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test tests/Shiro.Tests -c Release --filter "FullyQualifiedName~LogBindingAndWatcherTests"`
Expected: FAIL (`LogHeaderMatchResult` and `EvaluateLogHeaderForTracker` not defined).

- [ ] **Step 3: Implement deterministic log header matching in `RobloxProcess.LogPatterns.cs` / `RobloxProcess.cs` and `FileSystemWatcher` in `RobloxWatcher.cs`**

- In `src/Shiro/Classes/RobloxProcess.LogPatterns.cs`:
  - Define `internal enum LogHeaderMatchResult { Inconclusive = 0, Matched = 1, Mismatched = 2 }`.
  - Add `[GeneratedRegex(@"(?:\-b\s+|browserTrackerId[=:]\s*)(\d{6,20})", RegexOptions.IgnoreCase)] private static partial Regex LogTrackerIdRegex();`.
  - Implement `internal static LogHeaderMatchResult EvaluateLogHeaderForTracker(string? logHeaderSample, string? expectedTrackerId)`: if either is empty, return `Inconclusive`; match `LogTrackerIdRegex()` against `logHeaderSample`; if no match, return `Inconclusive`; if matched group equals `expectedTrackerId`, return `Matched`, else `Mismatched`.
- In `src/Shiro/Classes/RobloxProcess.cs` (`WaitForLogPathAsync`):
  - Resolve the process's expected `browserTrackerId` once via `ProcessExtensions.GetCommandLineByPid(RbxProcess.Id)`.
  - When iterating `candidateLogs`, read up to the first 8192 bytes of the candidate file stream and evaluate `EvaluateLogHeaderForTracker(sample, expectedTrackerId)`:
    - If `Mismatched`, close the stream and continue to the next candidate (never steal another account's log file).
    - Prefer `Matched` candidates first, falling back to `Inconclusive` candidates if no explicit tracker is present in the header yet.
- In `src/Shiro/Classes/RobloxWatcher.cs`:
  - In `Start()`, create a `FileSystemWatcher` watching `%LOCALAPPDATA%\Roblox\logs` for `*_Player_*_last.log` (`NotifyFilters.LastWrite | NotifyFilters.FileName | NotifyFilters.Size`) that coalesces `LogFileRead` invocations via a 150ms debounce gate, and dispose it in `Stop()`.

- [ ] **Step 4: Run tests to verify they pass**

Run: `dotnet test tests/Shiro.Tests -c Release --filter "FullyQualifiedName~LogBindingAndWatcherTests"`
Expected: PASS.

- [ ] **Step 5: Commit**

```bash
git add src/Shiro/Classes/RobloxProcess.LogPatterns.cs src/Shiro/Classes/RobloxProcess.cs src/Shiro/Classes/RobloxWatcher.cs tests/Shiro.Tests/LogBindingAndWatcherTests.cs
git commit -m "feat(watcher): bind Roblox logs deterministically by tracker ID and watch log directory events"
```

---

### Task 6: Producer–Consumer Channel Pipeline for Player Search & Parallel Session Auditing

**Files:**
- Modify: `src/Shiro/ViewModels/ServerListViewModel.PlayerSearch.cs`
- Modify: `src/Shiro/Services/Auth/SessionAuditorService.cs`
- Test: `tests/Shiro.Tests/PlayerSearchPipelineAndAuditorTests.cs`

**Interfaces:**
- Consumes: `System.Threading.Channels.Channel`, `SessionAuditorService.AuditAccountAsync`
- Produces: Pipelined `ServerListViewModel.SearchPlayerAsync` and concurrent economy queries in `SessionAuditorService.AuditAccountAsync`

- [ ] **Step 1: Write the failing tests in `tests/Shiro.Tests/PlayerSearchPipelineAndAuditorTests.cs`**

```csharp
using System;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Shiro.Classes;
using Shiro.Services;
using Xunit;

namespace Shiro.Tests
{
    [Collection("StaticSeams")]
    public class PlayerSearchPipelineAndAuditorTests
    {
        private sealed class ConcurrentProbeHandler : HttpMessageHandler
        {
            private int _inFlightEconomyCalls;
            public int MaxObservedConcurrentEconomyCalls { get; private set; }

            protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            {
                string url = request.RequestUri?.AbsoluteUri ?? string.Empty;
                if (url.Contains("/v1/users/authenticated", StringComparison.Ordinal))
                {
                    return new HttpResponseMessage(HttpStatusCode.OK)
                    {
                        Content = new StringContent("{\"id\":777,\"name\":\"ShiroUser\",\"displayName\":\"Shiro\"}")
                    };
                }

                if (url.Contains("economy.roblox.com", StringComparison.Ordinal))
                {
                    int current = Interlocked.Increment(ref _inFlightEconomyCalls);
                    int snapshotMax;
                    do
                    {
                        snapshotMax = MaxObservedConcurrentEconomyCalls;
                        if (current <= snapshotMax) break;
                    }
                    while (Interlocked.CompareExchange(ref snapshotMax, current, MaxObservedConcurrentEconomyCalls) != MaxObservedConcurrentEconomyCalls);
                    if (current > MaxObservedConcurrentEconomyCalls)
                        MaxObservedConcurrentEconomyCalls = current;

                    await Task.Delay(60, cancellationToken).ConfigureAwait(false);
                    Interlocked.Decrement(ref _inFlightEconomyCalls);

                    string body = url.Contains("/currency", StringComparison.Ordinal)
                        ? "{\"robux\":1500}"
                        : "{\"pendingRobuxTotal\":350}";
                    return new HttpResponseMessage(HttpStatusCode.OK)
                    {
                        Content = new StringContent(body)
                    };
                }

                return new HttpResponseMessage(HttpStatusCode.NotFound);
            }
        }

        [Fact]
        public async Task AuditAccountAsync_FetchesCurrencyAndPendingRobuxConcurrently()
        {
            using var handler = new ConcurrentProbeHandler();
            using var client = new HttpClient(handler);
            var prevFactory = SessionAuditorService.TestHttpClientFactory;
            try
            {
                SessionAuditorService.TestHttpClientFactory = _ => client;
                var account = new Account { Username = "ShiroUser", SecurityToken = "_|WARNING:-DO-NOT-SHARE-THIS._test" };

                var result = await SessionAuditorService.AuditAccountAsync(account);

                Assert.Equal(AccountSessionStatus.Valid, result.Status);
                Assert.Equal(1500, result.RobuxBalance);
                Assert.Equal(350, result.PendingRobux);
                Assert.Equal(2, handler.MaxObservedConcurrentEconomyCalls);
            }
            finally
            {
                SessionAuditorService.TestHttpClientFactory = prevFactory;
            }
        }
    }
}
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test tests/Shiro.Tests -c Release --filter "FullyQualifiedName~PlayerSearchPipelineAndAuditorTests"`
Expected: FAIL (`Assert.Equal(2, handler.MaxObservedConcurrentEconomyCalls)` fails with Actual `1` because currency and pending Robux are currently awaited sequentially).

- [ ] **Step 3: Parallelize `SessionAuditorService.AuditAccountAsync` economy probes and pipeline `ServerListViewModel.SearchPlayerAsync` with `Channel<T>`**

- In `src/Shiro/Services/Auth/SessionAuditorService.cs` (lines 204–258):
  - Extract local async functions `FetchCurrencyAsync()` and `FetchPendingRobuxAsync()` and execute them concurrently via `await Task.WhenAll(FetchCurrencyAsync(), FetchPendingRobuxAsync()).ConfigureAwait(false);`.
- In `src/Shiro/ViewModels/ServerListViewModel.PlayerSearch.cs` (lines 94–223):
  - Create `using var searchLinkedCts = CancellationTokenSource.CreateLinkedTokenSource(ct);` and `var channel = System.Threading.Channels.Channel.CreateBounded<(List<TokenRequest> Requests, Dictionary<string, ServerData> RequestMap, ServerData? FallbackServer)>(new System.Threading.Channels.BoundedChannelOptions(6) { SingleWriter = true, FullMode = System.Threading.Channels.BoundedChannelFullMode.Wait });`.
  - Run 1 Producer task that fetches `v1/games/{placeId}/servers/public` pages and writes packed 100-token batches into `channel.Writer` until `cursor` is empty or `Volatile.Read(ref matchedServer) != null`, then calls `channel.Writer.TryComplete()`.
  - Run 3 Consumer tasks reading from `channel.Reader.ReadAllAsync(searchLinkedCts.Token)` that POST `v1/batch` to `thumbClient`, set `matchedServer` via `Interlocked.CompareExchange`, and call `searchLinkedCts.Cancel()` as soon as a match is found.

- [ ] **Step 4: Run tests to verify they pass**

Run: `dotnet test tests/Shiro.Tests -c Release --filter "FullyQualifiedName~PlayerSearchPipelineAndAuditorTests|FullyQualifiedName~ServerListPlayerSearch"`
Expected: PASS.

- [ ] **Step 5: Commit**

```bash
git add src/Shiro/Services/Auth/SessionAuditorService.cs src/Shiro/ViewModels/ServerListViewModel.PlayerSearch.cs tests/Shiro.Tests/PlayerSearchPipelineAndAuditorTests.cs
git commit -m "perf(network): pipeline player server search with Channels and parallelize session auditor probes"
```

---

### Task 7: Structured Auth/Moderation JSON Classification & `MutationObserver` Login Script

**Files:**
- Modify: `src/Shiro/Services/Auth/FastLoginCoordinator.cs`
- Test: `tests/Shiro.Tests/FastLoginStructuredClassificationTests.cs`

**Interfaces:**
- Produces:
  - `FastLoginCoordinator.ClassifyAuthOrModerationApiPayload(string? jsonPayload, int httpStatusCode = 0) -> string`

- [ ] **Step 1: Write the failing tests in `tests/Shiro.Tests/FastLoginStructuredClassificationTests.cs`**

```csharp
using Shiro.Services;
using Xunit;

namespace Shiro.Tests
{
    public class FastLoginStructuredClassificationTests
    {
        [Theory]
        [InlineData("{\"errors\":[{\"code\":1,\"message\":\"Incorrect username or password.\"}]}", 403, "WRONG_PASSWORD")]
        [InlineData("{\"errors\":[{\"code\":4,\"message\":\"Account has been locked.\"}]}", 403, "ACCOUNT_LOCKED")]
        [InlineData("{\"errors\":[{\"code\":9002,\"message\":\"User is not authenticated.\"}]}", 401, "UNAUTHENTICATED_MODERATION")]
        [InlineData("{\"punishmentTypeData\":{\"punishmentType\":\"Ban 7 Days\"}}", 200, "BAN_7_DAYS")]
        [InlineData("{\"punishmentTypeData\":{\"punishmentType\":\"Ban 3 Days\"}}", 200, "BAN_3_DAYS")]
        [InlineData("{\"punishmentTypeData\":{\"punishmentType\":\"Ban 1 Day\"}}", 200, "BAN_1_DAY")]
        [InlineData("{\"punishmentTypeData\":{\"punishmentType\":\"Ban 14 Days\"}}", 200, "BAN_14_DAYS")]
        [InlineData("{\"punishmentTypeData\":{\"punishmentType\":\"Delete\"}}", 200, "TERMINATED")]
        [InlineData("{\"punishmentTypeData\":{\"punishmentType\":\"Warn\"}}", 200, "WARNING")]
        [InlineData("{}", 200, "")]
        public void ClassifyAuthOrModerationApiPayload_ParsesStructuredRobloxApiResponses(
            string json,
            int statusCode,
            string expectedCode)
        {
            string actual = FastLoginCoordinator.ClassifyAuthOrModerationApiPayload(json, statusCode);
            Assert.Equal(expectedCode, actual);
        }

        [Fact]
        public void BuildCredentialFillScript_IncludesMutationObserver()
        {
            string script = FastLoginCoordinator.BuildCredentialFillScript("ShiroUser", "Pass123!", forceClick: true);
            Assert.Contains("MutationObserver", script);
        }
    }
}
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test tests/Shiro.Tests -c Release --filter "FullyQualifiedName~FastLoginStructuredClassificationTests"`
Expected: FAIL (`ClassifyAuthOrModerationApiPayload` not defined).

- [ ] **Step 3: Implement `ClassifyAuthOrModerationApiPayload` and `MutationObserver` in `src/Shiro/Services/Auth/FastLoginCoordinator.cs`**

- In `src/Shiro/Services/Auth/FastLoginCoordinator.cs`:
  - Add `public static string ClassifyAuthOrModerationApiPayload(string? jsonPayload, int httpStatusCode = 0)` using `JsonDocument.Parse`:
    - Inspect `errors` array: `code == 1` → `"WRONG_PASSWORD"`, `code == 4 || code == 10` → `"ACCOUNT_LOCKED"`, `code == 9002` → `"UNAUTHENTICATED_MODERATION"`.
    - Inspect `punishmentTypeData.punishmentType` (or root `punishmentType`): `"Delete"` / `"Terminate"` → `"TERMINATED"`, `"Warn"` / `"Warning"` → `"WARNING"`, `"Ban 14 Days"` → `"BAN_14_DAYS"`, `"Ban 7 Days"` → `"BAN_7_DAYS"`, `"Ban 3 Days"` → `"BAN_3_DAYS"`, `"Ban 1 Day"` → `"BAN_1_DAY"`, any other non-empty punishment → `"ACCOUNT_BANNED"`.
  - Update `ClassifyDomIssueCode` to check `ClassifyAuthOrModerationApiPayload(combinedText)` first if `combinedText` contains `{`.
  - Update `GetLoginPageEnhancementScript` and `BuildCredentialFillScript` to attach a `MutationObserver` on `document.documentElement` (`childList: true, subtree: true`) that triggers `hookPass()` / `tick()` immediately when React mounts the login inputs and disconnects once submitted.

- [ ] **Step 4: Run tests to verify they pass**

Run: `dotnet test tests/Shiro.Tests -c Release --filter "FullyQualifiedName~FastLoginStructuredClassificationTests"`
Expected: PASS. Run full test suite `dotnet test tests/Shiro.Tests -c Release` to confirm all architecture & unit tests pass.

- [ ] **Step 5: Commit**

```bash
git add src/Shiro/Services/Auth/FastLoginCoordinator.cs tests/Shiro.Tests/FastLoginStructuredClassificationTests.cs
git commit -m "feat(auth): add structured JSON auth/moderation classification and MutationObserver autofill"
```
