# Critical & Severe Bugs Remediation Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Remediate all 17 verified critical and severe production bugs across storage/encryption, WASAPI COM interop, Avalonia UI dialog/thread lifecycle, cookie/HBA launch pipelines, log/process watchers, Nexus/Kestrel security, and Native AOT JSON serialization.

**Architecture:**
1. **Vault & Backup Persistence Hardening (`StoragePaths.VaultKey.cs`, `AccountManager.Persistence.cs`, `AccountManager.BackupRecovery.cs`, `VaultKeyRecoveryService.AccountMerge.cs`, `Account.cs`, `AccountDataSerializer.cs`):** Recover from `vault.key.bak` before generating a new random key and never overwrite an existing `.bak` when account data exists; move corrupt primary `AccountData.json` files out of the way before saving recovered backups; block destructive saves while a Master-Password vault is locked (`IsVaultLockedPendingMasterPassword`); unify `Account.Fields` locking on `_fieldsLock`; and suppress `QueueDeferredSave()` during JSON deserialization.
2. **WASAPI COM Thread-Safety & Apartment Integrity (`RobloxAudioService.cs`, `RobloxAudioService.Wasapi.cs`):** Guard all `_cachedVolumePointers` lookups, vtable invocations, and `ComRelease` calls under `_gate` with active MTA initialization (`CoInitializeEx`), and evict stale pointers on re-enumeration.
3. **Avalonia Dialog Owner Visibility & WebView2 Lifecycle (`MessageBoxDialog.axaml.cs`, `InputDialog.axaml.cs`, `AvaloniaDialogService.cs`, `WebView2Control.cs`, `FastLoginDialog*.cs`):** Only use `owner` windows with `IsVisible == true` so hidden-to-tray `MainWindow` instances never fault `ShowDialog(owner)` or deadlock `Dispatcher.UIThread.PushFrame(frame)`; guard async dialog openers and post-close `WebView2Control`/`FastLoginDialog` continuations.
4. **Launch, CookieGuard, Auto-Reconnect & HBA Signing (`RobloxDirectLauncher.CookieGuard.cs`, `AccountLaunchService.Home.cs`, `AutoReconnectService.cs`, `RobloxHbaService*.cs`, `Account.HbaBinding.cs`):** Synchronize `RobloxCookies.dat` backup/injection/restore under `_cookieLock` with sentinel tracking when no pre-Shiro cookie file existed and automatic restore on last process exit; route `PreferDirectLaunch` Home launches through `StartNativeHomeAsync`; suppress superseded `ReconnectSession` instances; derive missing SPKI public keys for WebView2 HBA injection and sign form-urlencoded POST bodies.
5. **Log Reader Concurrency, Process Watcher Cache, Nexus Reconnect & WebAuth Origin (`RobloxProcess.cs`, `RobloxWatcher.cs`, `NexusWebSocketServer.cs`, `ControlledAccount.cs`, `WebAuthPolicy.cs`):** Lock per-instance log reads and preserve incomplete trailing lines without `\n`; avoid caching empty command lines in `_pidMetadataCache`; pass `expectedSocket` into `ControlledAccount.Disconnect`; and enforce `IsTrustedLocalOrigin` even when `AllowExternal` is enabled.
6. **UI Thread Affinity, PlaceId State & Native AOT JSON (`ServerListViewModel*.cs`, `BulkImportViewModel.cs`, `AccountUtilsViewModel.cs`, `ProxyManagerViewModel.cs`, `ServerBrowserStateService.cs`, `JsonExtensions.cs`, `ShiroAppJson.cs`, `GameClass.cs`, `Batch.cs`):** Marshal background ViewModel property/progress updates to `AppDispatcher`, allow clearing `CurrentPlaceId` for per-account `SavedPlaceId` fallback, guard `DefaultJsonTypeInfoResolver` under Native AOT, attach `FlexibleStringConverter` to `ShiroAppJson`, and replace anonymous `AddJsonBody` payloads with source-generated records.

**Tech Stack:** .NET 10, C# 13, Avalonia UI 11, WebView2, ASP.NET Core Kestrel, Windows WASAPI COM Interop, `System.Text.Json` Source Generation, xUnit.

**Spec:** Conversation audit report of 17 verified critical/severe bugs (`2026-11-04`).

## Global Constraints

- Target framework: `net10.0-windows10.0.19041.0` (`win-x64`), C# 13, `<Nullable>enable</Nullable>`.
- All existing 1,696 unit tests in `tests/Shiro.Tests/Shiro.Tests.csproj` must continue to pass plus new regression tests for each task.
- Preserve existing public APIs and comments unrelated to the bug fixes.
- Must remain compatible with `<PublishAot>true</PublishAot>` (`JsonSerializer.IsReflectionEnabledByDefault == false`).

## Review Focus

- Transient `IOException` or 0-byte `vault.key` when `vault.key.bak` holds the valid DPAPI-protected key: `GetOrCreateVaultKey()` must recover the key from `vault.key.bak` and restore `vault.key` instead of generating a new random key.
- Calling `MessageBoxDialog.Show` or `AvaloniaDialogService.OpenFastLoginDialog` while `MainWindow.IsVisible == false` (minimized/hidden to system tray): must open as a top-level window (`owner = null`) without throwing `InvalidOperationException` or deadlocking `PushFrame`.
- Launching Account A then Account B when `%LOCALAPPDATA%\Roblox\LocalStorage\RobloxCookies.dat` did not exist before Account A: must sync Account A's rotated cookie and must delete `RobloxCookies.dat` on `RestoreOriginalCookies()` rather than restoring Account A's cookie from `.shiro-orig`.
- Roblox flushing a partial log line without a trailing `\n` during `FileSystemWatcher` notification followed by the rest of the line 50ms later: `ReadAppendedLines` must wait for `\n` on `FileStream` and return the complete unbroken line on the next read.
- Nexus client sending numeric/boolean JSON values in `Command.Payload` (e.g. `{"Name":"ping","Payload":{"fps":60,"ping":45}}`) and reconnecting on teleport before the previous WebSocket closes: `TryParseJson<Command>` must succeed via `FlexibleStringConverter` and the old socket's `finally` block must not disconnect the new socket.

---

### Task 1: Vault Key Protection, Backup Integrity, Master-Password Lock Guards & `Account.Fields` Thread-Safety

**Files:**
- Modify: `src/Shiro/Services/Storage/StoragePaths.VaultKey.cs:89-167`
- Modify: `src/Shiro/AccountManager.Persistence.cs:35-95, 130-185, 231-288, 364-455`
- Modify: `src/Shiro/AccountManager.BackupRecovery.cs:37-126`
- Modify: `src/Shiro/Services/Storage/VaultKeyRecoveryService.AccountMerge.cs:13-181`
- Modify: `src/Shiro/Classes/Account.cs:37-125, 170-220, 408-463`
- Modify: `src/Shiro/Classes/Account.HbaBinding.cs:85-111`
- Modify: `src/Shiro/Services/Storage/AccountDataSerializer.cs:55-105`
- Test: `tests/Shiro.Tests/CriticalStorageAndVaultBugTests.cs`

**Interfaces:**
- Produces:
  - `AccountManager.IsVaultLockedPendingMasterPassword` (`public static bool`)
  - `Account.BeginSuppressDeferredSaveScope()` (`internal static IDisposable`)
  - `StoragePaths.TryReadAndUnprotectKeyFile(string path, out byte[]? key)` (`internal static bool`)

- [ ] **Step 1: Write the failing regression tests in `tests/Shiro.Tests/CriticalStorageAndVaultBugTests.cs`**

```csharp
using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using System.Threading.Tasks;
using Shiro.Classes;
using Shiro.Services;
using Xunit;

namespace Shiro.Tests
{
    public class CriticalStorageAndVaultBugTests
    {
        [Fact]
        public void TryUnprotectOrRecoverKey_WhenPrimaryCorrupt_RestoresFromBakInsteadOfOverwriting()
        {
            string tempDir = Path.Combine(Path.GetTempPath(), "ShiroVaultKeyTest_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(tempDir);
            try
            {
                string keyFile = Path.Combine(tempDir, "vault.key");
                string bakFile = keyFile + ".bak";
                byte[] originalKey = RandomNumberGenerator.GetBytes(32);
                byte[] validPayload = StoragePaths.BuildProtectedVaultKeyPayload(originalKey);
                File.WriteAllBytes(bakFile, validPayload);
                File.WriteAllBytes(keyFile, new byte[] { 0x00, 0x01, 0x02 }); // corrupt primary

                byte[] resolved = StoragePaths.LoadOrCreateVaultKeyAtPath(keyFile);

                Assert.Equal(originalKey, resolved);
                Assert.Equal(validPayload, File.ReadAllBytes(bakFile));
                Assert.True(File.Exists(keyFile));
            }
            finally
            {
                try { Directory.Delete(tempDir, true); } catch { }
            }
        }

        [Fact]
        public void AccountDataSerializer_Deserialize_DoesNotTriggerDeferredSaveOrDirtyFlags()
        {
            byte[] key = RandomNumberGenerator.GetBytes(32);
            var source = new List<Account>
            {
                new Account
                {
                    Username = "TestUser",
                    UserID = 12345,
                    Group = "Main",
                    Alias = "MyAlias",
                    Description = "Desc",
                    Password = "Secret",
                    Valid = true
                }
            };
            byte[] encrypted = AccountDataSerializer.Serialize(source, key);
            var decoded = AccountDataSerializer.Deserialize(
                CryptoHelper.DecryptBytes(encrypted, key),
                out _);

            Assert.NotNull(decoded);
            Assert.Single(decoded!);
            Assert.False(decoded[0].IsDirty);
        }

        [Fact]
        public async Task AccountFields_ConcurrentProtectedAndStandardFieldAccess_DoesNotThrowDuringSerialize()
        {
            var acc = new Account { Username = "RaceUser", UserID = 999 };
            byte[] key = RandomNumberGenerator.GetBytes(32);
            using var cts = new System.Threading.CancellationTokenSource(TimeSpan.FromMilliseconds(400));

            Task writer1 = Task.Run(() =>
            {
                int i = 0;
                while (!cts.IsCancellationRequested)
                {
                    acc.SetField("Key_" + (i++ % 16), "Val");
                    acc.AccountStatusIssue = (i % 2 == 0) ? "LOCKED" : string.Empty;
                }
            });

            Task writer2 = Task.Run(() =>
            {
                int i = 0;
                while (!cts.IsCancellationRequested)
                {
                    acc.HbaTemplate = "tpl_" + (i++ % 8);
                    _ = acc.HbaTemplate;
                }
            });

            Task serializer = Task.Run(() =>
            {
                var list = new List<Account> { acc };
                while (!cts.IsCancellationRequested)
                {
                    byte[] bytes = AccountDataSerializer.Serialize(list, key);
                    Assert.NotEmpty(bytes);
                }
            });

            await Task.WhenAll(writer1, writer2, serializer);
        }
    }
}
```

- [ ] **Step 2: Run the new test class to verify it fails**

Run: `dotnet test tests/Shiro.Tests/Shiro.Tests.csproj --filter "FullyQualifiedName~CriticalStorageAndVaultBugTests" --no-restore`
Expected: FAIL (`BuildProtectedVaultKeyPayload` / `LoadOrCreateVaultKeyAtPath` not defined, `decoded[0].IsDirty` is `true`, and `AccountFields_ConcurrentProtectedAndStandardFieldAccess_DoesNotThrowDuringSerialize` throws `InvalidOperationException`).

- [ ] **Step 3: Implement vault key recovery, corrupt primary eviction, Master-Password lock guard, and `Account.Fields` synchronization**

1. In `src/Shiro/Services/Storage/StoragePaths.VaultKey.cs`:
   - Extract `internal static byte[] BuildProtectedVaultKeyPayload(byte[] rawKey)` and `internal static byte[] LoadOrCreateVaultKeyAtPath(string keyFile, bool hasExistingEncryptedVault = false)`.
   - In `LoadOrCreateVaultKeyAtPath`: try reading/unprotecting `keyFile` (with 3 retries). If that fails, try reading/unprotecting `keyFile + ".bak"`. If `.bak` succeeds, atomically restore `keyFile` from `.bak` and return the recovered 32-byte key.
   - If `keyFile` exists and is corrupt, copy it to `$"{keyFile}.corrupt-{DateTime.UtcNow:yyyyMMdd_HHmmss}.bak"` (never overwriting `keyFile + ".bak"`).
   - Only write the newly generated key to `keyFile + ".bak"` if `!File.Exists(keyFile + ".bak") && !hasExistingEncryptedVault`.
2. In `src/Shiro/AccountManager.Persistence.cs` & `src/Shiro/AccountManager.BackupRecovery.cs`:
   - Add `public static bool IsVaultLockedPendingMasterPassword { get; internal set; }`.
   - Set `IsVaultLockedPendingMasterPassword = true` when `LoadAccounts` hits `AccountDecodeStatus.NeedsMasterPassword`, and set `IsVaultLockedPendingMasterPassword = false` when `LoadAccounts` succeeds or in `MoveCorruptFileToBackup`.
   - In `SaveAccounts`, `ResetEncryptionAsync`, and `TryRestoreReadableBackup`, return early with a warning log if `IsVaultLockedPendingMasterPassword` is `true`.
   - In `TryLoadFromBackup` and `TryRestoreReadableBackup`, move (`File.Move(SaveFilePath, brokenBackupPath, overwrite: true)`) the corrupt/locked primary `AccountData.json` before calling `SaveAccounts` so `AccountStore.WriteWithBackup` does not copy the corrupt file over `AccountData.json.backup` or into `Backups/`.
3. In `src/Shiro/Services/Storage/VaultKeyRecoveryService.AccountMerge.cs`:
   - Sort backup files by `File.GetLastWriteTimeUtc(f)` descending.
   - In `MergeList`, update `existing.SecurityToken` when `string.IsNullOrWhiteSpace(existing.SecurityToken) || (acc.LastUse > existing.LastUse && !string.IsNullOrWhiteSpace(acc.SecurityToken))`, and merge missing keys from `acc.Fields` into `existing.Fields`.
   - Before writing in `MergeAllKnownAccountsCli()`, if `File.Exists(StoragePaths.AccountDataFile)` and Step 1 failed to decrypt it and `finalList.Count(a => !string.IsNullOrWhiteSpace(a.SecurityToken)) == 0`, abort without overwriting `AccountData.json` or `.backup`. Otherwise copy `AccountData.json` to `Backups/AccountData_pre_merge_{timestamp}.json.bak` before writing.
4. In `src/Shiro/Classes/Account.cs`, `src/Shiro/Classes/Account.HbaBinding.cs`, and `src/Shiro/Services/Storage/AccountDataSerializer.cs`:
   - Add `[ThreadStatic] private static int _suppressDeferredSaveDepth;` and `internal static IDisposable BeginSuppressDeferredSaveScope()` on `Account`. When `_suppressDeferredSaveDepth > 0`, property setters on `Account` (`Group`, `Proxy`, `Alias`, `Description`, `Password`) assign the backing field without setting `IsDirty = true` or calling `AppServices.AccountRepository.QueueDeferredSave()`.
   - Wrap `JsonSerializer.Deserialize` in `AccountDataSerializer.Deserialize` with `using (Account.BeginSuppressDeferredSaveScope())`.
   - Change `GetProtectedField` and `SetProtectedField` in `Account.HbaBinding.cs` to lock `_fieldsLock` (matching `GetField`/`SetField`/`RemoveField`), and make `internal object FieldsSyncRoot => _fieldsLock;` so `AccountDataSerializer.Serialize` locks `acc.FieldsSyncRoot` while cloning/serializing or serializes under lock.

- [ ] **Step 4: Run the tests to verify they pass**

Run: `dotnet test tests/Shiro.Tests/Shiro.Tests.csproj --filter "FullyQualifiedName~CriticalStorageAndVaultBugTests" --no-restore`
Expected: PASS (3 passed, 0 failed).

---

### Task 2: Native COM Audio Session Lifetime & Thread-Safety in `RobloxAudioService`

**Files:**
- Modify: `src/Shiro/Services/Windows/RobloxAudioService.cs:69-74, 114-140, 180-318`
- Modify: `src/Shiro/Services/Windows/RobloxAudioService.Wasapi.cs:15-102`
- Test: `tests/Shiro.Tests/RobloxAudioServiceConcurrencyTests.cs`

**Interfaces:**
- Consumes: `_gate` (`System.Threading.Lock`) in `RobloxAudioService`
- Produces: Thread-safe `InvalidateCachedAudioSession(int pid)` and `ClearAudioSessionCache()` synchronized under `_gate`, and MTA-initialized `TryForEachCachedAudioSession`

- [ ] **Step 1: Write the failing test in `tests/Shiro.Tests/RobloxAudioServiceConcurrencyTests.cs`**

```csharp
using System;
using System.Threading;
using System.Threading.Tasks;
using Shiro.Services;
using Xunit;

namespace Shiro.Tests
{
    public class RobloxAudioServiceConcurrencyTests
    {
        [Fact]
        public async Task ConcurrentInvalidateAndClearCache_WithSmartMuteToggle_DoesNotThrowOrDeadlock()
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(300));
            Task t1 = Task.Run(() =>
            {
                int pid = 1000;
                while (!cts.IsCancellationRequested)
                {
                    RobloxAudioService.InvalidateCachedAudioSession(pid++);
                }
            });
            Task t2 = Task.Run(() =>
            {
                while (!cts.IsCancellationRequested)
                {
                    RobloxAudioService.ClearAudioSessionCache();
                }
            });
            Task t3 = Task.Run(() =>
            {
                while (!cts.IsCancellationRequested)
                {
                    RobloxAudioService.ConfigureSmartMute(false);
                }
            });

            await Task.WhenAll(t1, t2, t3);
        }
    }
}
```

- [ ] **Step 2: Run test to verify baseline behavior**

Run: `dotnet test tests/Shiro.Tests/Shiro.Tests.csproj --filter "FullyQualifiedName~RobloxAudioServiceConcurrencyTests" --no-restore`
Expected: PASS/compile check before hardening `RobloxAudioService.Wasapi.cs`.

- [ ] **Step 3: Synchronize COM pointer cache and MTA lifecycle in `RobloxAudioService.cs` and `RobloxAudioService.Wasapi.cs`**

1. In `src/Shiro/Services/Windows/RobloxAudioService.Wasapi.cs`:
   - Acquire `lock (_gate)` inside `InvalidateCachedAudioSession(int pid)` and `ClearAudioSessionCache()` before removing pointers from `_cachedVolumePointers` and calling `ComRelease(ref ptr)`.
   - In `TryForEachCachedAudioSession`, ensure `CoInitializeEx(IntPtr.Zero, COINIT_MULTITHREADED)` is called before invoking `nativeVolume.TryGetMute` or `onSession(pid, nativeVolume)` (and `CoUninitialize()` in `finally` when `hrInit == 0 || hrInit == 1`).
2. In `src/Shiro/Services/Windows/RobloxAudioService.cs`:
   - In `ForEachRobloxAudioSession` (around line `284`), if `_cachedVolumePointers.TryRemove((int)pid, out IntPtr oldPtr) && oldPtr != IntPtr.Zero`, call `ComRelease(ref oldPtr)` before storing the fresh `pVolume` via `_cachedVolumePointers[(int)pid] = pVolume`.
   - Add a `bool allowEarlyBreak` parameter or track whether the loop exited early (`break` in `IsAnyRobloxUnmuted`) so a partial enumeration does not leave stale/partial entries pretending all sessions were cached.

- [ ] **Step 4: Run audio service tests to verify they pass**

Run: `dotnet test tests/Shiro.Tests/Shiro.Tests.csproj --filter "FullyQualifiedName~RobloxAudioService" --no-restore`
Expected: PASS.

---

### Task 3: Avalonia Dialog Owner Visibility Deadlock & `WebView2Control` / `FastLoginDialog` Lifecycle Guards

**Files:**
- Modify: `src/Shiro/AvaloniaUI/Views/MessageBoxDialog.axaml.cs:47-58, 178-258`
- Modify: `src/Shiro/AvaloniaUI/Views/InputDialog.axaml.cs:21-32, 57-120`
- Modify: `src/Shiro/Services/Ui/AvaloniaDialogService.cs:16-36, 89-236`
- Modify: `src/Shiro/AvaloniaUI/Controls/WebView2Control.cs:82-104, 158-226`
- Modify: `src/Shiro/AvaloniaUI/Views/FastLoginDialog.axaml.cs:272-297`
- Modify: `src/Shiro/AvaloniaUI/Views/FastLoginDialog.SessionCapture.cs:89-155, 300-384`
- Test: `tests/Shiro.Tests/DialogOwnerAndLifecycleTests.cs`

**Interfaces:**
- Produces:
  - `MessageBoxDialog.IsValidDialogOwner(Window? candidate)` (`internal static bool` — returns `candidate is { IsVisible: true }`)
  - `FastLoginDialog._isClosed` lifecycle guard

- [ ] **Step 1: Write the failing test in `tests/Shiro.Tests/DialogOwnerAndLifecycleTests.cs`**

```csharp
using Shiro.AvaloniaUI.Views;
using Xunit;

namespace Shiro.Tests
{
    public class DialogOwnerAndLifecycleTests
    {
        [Fact]
        public void IsValidDialogOwner_NullWindow_ReturnsFalse()
        {
            Assert.False(MessageBoxDialog.IsValidDialogOwner(null));
        }
    }
}
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test tests/Shiro.Tests/Shiro.Tests.csproj --filter "FullyQualifiedName~DialogOwnerAndLifecycleTests" --no-restore`
Expected: FAIL (`IsValidDialogOwner` does not exist).

- [ ] **Step 3: Implement dialog owner visibility checks and WebView2/FastLoginDialog lifecycle guards**

1. In `src/Shiro/AvaloniaUI/Views/MessageBoxDialog.axaml.cs` and `src/Shiro/AvaloniaUI/Views/InputDialog.axaml.cs`:
   - Add `internal static bool IsValidDialogOwner(Window? window) => window is { IsVisible: true };`.
   - In `GetActiveWindow()`, check `if (win.IsActive && win.IsVisible) return win;` and for fallback return `IsValidDialogOwner(desktop.MainWindow) ? desktop.MainWindow : null;`.
   - In `ShowAsync` / `PromptAsync` and `Show` / `Prompt`, normalize `if (!IsValidDialogOwner(owner)) owner = null;`.
   - In `Show` (`MessageBoxDialog.axaml.cs:225-234`) and `Prompt` (`InputDialog.axaml.cs:87-96`), attach a fault continuation on `dialog.ShowDialog(owner)` (`_ = dialog.ShowDialog(owner).ContinueWith(_ => frame.Continue = false, TaskScheduler.FromCurrentSynchronizationContext());`) so even if `ShowDialog` faults, `Dispatcher.UIThread.PushFrame(frame)` immediately exits instead of deadlocking the UI thread.
2. In `src/Shiro/Services/Ui/AvaloniaDialogService.cs`:
   - In `ConfirmAsync` and `AlertAsync`, if `!Dispatcher.UIThread.CheckAccess()`, marshal the call via `await Dispatcher.UIThread.InvokeAsync(...)`.
   - Wrap all `AppDispatcher.Post(async () => ...)` lambdas (`OpenFastLoginDialog`, `OpenFastLoginDialogBatch`, `OpenBulkImportDialog`, `OpenAltGenDialog`, `OpenProxyManagerDialog`, `OpenAboutDialog`, `OpenAccountFieldsDialog`, `OpenAccountUtilsDialog`, `OpenMissingAssetsDialog`) in `try/catch (Exception ex)` blocks with `Program.Logger?.Error(...)`, and only call `ShowDialog(owner)` / `Show(owner)` when `MessageBoxDialog.IsValidDialogOwner(owner)` is `true`.
3. In `src/Shiro/AvaloniaUI/Controls/WebView2Control.cs`:
   - In `ResetToFreshProfileAsync` and `InitializeWebView2Async`, after `var controller = await CreateIsolatedControllerCoreAsync();`, check `if (_childHwnd == IntPtr.Zero) { SafeExecuteHelper.Try(controller.Close); return null; }` before assigning `_controller = controller`.
4. In `src/Shiro/AvaloniaUI/Views/FastLoginDialog.axaml.cs` and `src/Shiro/AvaloniaUI/Views/FastLoginDialog.SessionCapture.cs`:
   - Add `private bool _isClosed;` set to `true` at the start of `OnClosing`.
   - Wrap `AdvanceToNextAccountOrCloseAsync` in `try/catch (Exception ex)` and check `if (_isClosed) return;` at the start of `AdvanceToNextAccountOrCloseAsync`, `CheckAndCaptureSessionAsync`, and after `await` points in `CheckAndCaptureSessionAsync`.

- [ ] **Step 4: Run test to verify it passes**

Run: `dotnet test tests/Shiro.Tests/Shiro.Tests.csproj --filter "FullyQualifiedName~DialogOwnerAndLifecycleTests" --no-restore`
Expected: PASS.

---

### Task 4: CookieGuard Isolation, Direct Home Launch Gate, Auto-Reconnect Suppression & HBA Signing Fixes

**Files:**
- Modify: `src/Shiro/Services/Launch/RobloxDirectLauncher.CookieGuard.cs:17-55, 93-190, 215-298, 436-468`
- Modify: `src/Shiro/Services/Launch/AccountLaunchService.Home.cs:14-41, 144-160`
- Modify: `src/Shiro/Services/Launch/AccountLaunchService.cs:225-240`
- Modify: `src/Shiro/Services/Launch/AutoReconnectService.cs:140-185, 220-230, 365-485`
- Modify: `src/Shiro/Services/Auth/RobloxHbaService.cs:40-55, 180-235`
- Modify: `src/Shiro/Services/Auth/RobloxHbaService.Binding.cs:175-247`
- Modify: `src/Shiro/Classes/Account.HbaBinding.cs:260-288`
- Test: `tests/Shiro.Tests/LaunchCookieAndHbaBugTests.cs`

**Interfaces:**
- Produces:
  - `RobloxHbaService.TryDeriveSpkiPublicKeyBase64(string? pkcs8PrivateKeyBase64, out string spkiPublicKeyBase64)` (`internal static bool`)
  - `RobloxDirectLauncher.NotifyRobloxProcessExited()` (`internal static void`)

- [ ] **Step 1: Write the failing regression tests in `tests/Shiro.Tests/LaunchCookieAndHbaBugTests.cs`**

```csharp
using System;
using System.IO;
using System.Security.Cryptography;
using Shiro.Classes;
using Shiro.Http;
using Shiro.Services;
using Xunit;

namespace Shiro.Tests
{
    public class LaunchCookieAndHbaBugTests
    {
        [Fact]
        public void GetPersistedKeyInjectionScript_WhenPublicKeyEmpty_DerivesSpkiPublicKeyFromPrivateKey()
        {
            using var ecdsa = ECDsa.Create(ECCurve.NamedCurves.nistP256);
            string privB64 = Convert.ToBase64String(ecdsa.ExportPkcs8PrivateKey());
            string expectedPubB64 = Convert.ToBase64String(ecdsa.ExportSubjectPublicKeyInfo());

            string script = RobloxHbaService.GetPersistedKeyInjectionScript(privB64, publicKeySpkiBase64: "", tokenTemplate: null);

            Assert.Contains(expectedPubB64, script);
        }

        [Fact]
        public void CookieGuard_WhenNoInitialCookiesDat_SecondInjectionDoesNotBackUpFirstAccountsCookie()
        {
            string tempDir = Path.Combine(Path.GetTempPath(), "ShiroCookieGuardTest_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(tempDir);
            try
            {
                bool first = RobloxDirectLauncher.InjectAccountCookieToLocalStorage(
                    "_|WARNING:-DO-NOT-SHARE-THIS.--tokenA", "11111111111", tempDir, "UserA");
                Assert.True(first);

                bool second = RobloxDirectLauncher.InjectAccountCookieToLocalStorage(
                    "_|WARNING:-DO-NOT-SHARE-THIS.--tokenB", "22222222222", tempDir, "UserB");
                Assert.True(second);

                string backupPath = Path.Combine(tempDir, "RobloxCookies.dat.shiro-orig");
                Assert.False(File.Exists(backupPath));
            }
            finally
            {
                try { Directory.Delete(tempDir, true); } catch { }
            }
        }

        [Fact]
        public void AutoReconnect_RegisterSession_SuppressesReplacedSession()
        {
            var svc = AutoReconnectService.Instance;
            var acc = new Account { Username = "ReconnectUser", UserID = 42 };
            svc.RegisterSession(acc, 100, "job-1", 1111);
            Assert.True(svc.TryGetSession("ReconnectUser", out var firstSession));

            svc.RegisterSession(acc, 100, "job-2", 2222);
            Assert.True(firstSession!.IsSuppressed);
            svc.UnregisterSession("ReconnectUser");
        }
    }
}
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test tests/Shiro.Tests/Shiro.Tests.csproj --filter "FullyQualifiedName~LaunchCookieAndHbaBugTests" --no-restore`
Expected: FAIL (`GetPersistedKeyInjectionScript` leaves `pubB64 = ''`, and `firstSession.IsSuppressed` is `false`).

- [ ] **Step 3: Implement CookieGuard locking/sentinel, Home launch gate fix, Auto-Reconnect session suppression, and HBA public-key derivation & form-body signing**

1. In `src/Shiro/Services/Launch/RobloxDirectLauncher.CookieGuard.cs`:
   - Track `private static bool _noOriginalCookiesDatExisted;` under `_cookieLock`.
   - Wrap the file I/O in `InjectAccountCookieToLocalStorage` and `RestoreOriginalCookies` inside `lock (_cookieLock)`.
   - In `InjectAccountCookieToLocalStorage`: when `localStorageDirectoryOverride == null`:
     - If `!File.Exists(cookiesDatPath)` and `!File.Exists(backupPath)` and `_lastInjectedUsername == null`, set `_noOriginalCookiesDatExisted = true`.
     - If `File.Exists(cookiesDatPath)` and `!File.Exists(backupPath)` and `!_noOriginalCookiesDatExisted` and `_lastInjectedUsername == null`, copy `cookiesDatPath` to `backupPath`.
     - Whenever `File.Exists(cookiesDatPath)` and (`File.Exists(backupPath)` || `_noOriginalCookiesDatExisted` || `_lastInjectedUsername != null`), call `SyncRotatedClientCookieIfApplicable(cookiesDatPath, File.Exists(backupPath) ? backupPath : string.Empty)` so Account A's rotated token is always synced before Account B overwrites `RobloxCookies.dat` (update `SyncRotatedClientCookieIfApplicable` so `backupPath` is optional when `_noOriginalCookiesDatExisted` is true).
   - In `RestoreOriginalCookies`: if `_noOriginalCookiesDatExisted` is `true` and `!File.Exists(backupPath)`, delete `cookiesDatPath` and reset `_noOriginalCookiesDatExisted = false`.
   - In `ScheduleOriginalCookieRestore` and `SchedulePostSpawnCookieRestore`, if `RobloxProcessService.IsAnyRobloxRunning()` is `true`, set `_hasPendingBackupRestore = true` and re-arm `_restoreTimer?.Change(5000, Timeout.Infinite)` so it checks again until all Roblox processes exit.
2. In `src/Shiro/Services/Launch/AccountLaunchService.Home.cs` and `src/Shiro/Services/Launch/AccountLaunchService.cs`:
   - Remove the duplicate unguarded `if (preferDirect && !string.IsNullOrWhiteSpace(account.SecurityToken))` block at lines `144–156` of `AccountLaunchService.Home.cs` so all native Home launches go through `CloseAccountRobloxProcess(account)` and `StartNativeHomeAsync` (which acquires `NativeLaunchGate`, sweeps the singleton mutex, and calls `RobloxDirectLauncher.LaunchHomeDirect`).
   - Dispose `directResult.Process` in `StartNativeHomeAsync` (`AccountLaunchService.Home.cs:31`) and `StartNativeGameAsync` (`AccountLaunchService.cs:234`).
3. In `src/Shiro/Services/Launch/AutoReconnectService.cs`:
   - Add `internal bool TryGetSession(string key, out ReconnectSession? session) => _sessions.TryGetValue(key, out session);`.
   - In `RegisterSession`, if `_sessions.TryGetValue(sessionKey, out var previous)` succeeds, set `previous.IsSuppressed = true`.
   - In `HandleProcessExit`, check `_sessions.TryGetValue(GetSessionKey(session.Account), out var active) && ReferenceEquals(active, session)` after `Task.Delay` and after `_reconnectGate.WaitAsync()`, reset `session.ObservedProcessExited = false` on reconnect, and replace all `_sessions.TryRemove(session.Account.Username, out _)` calls with `_sessions.TryRemove(GetSessionKey(session.Account), out _)`.
4. In `src/Shiro/Services/Auth/RobloxHbaService.cs`, `src/Shiro/Services/Auth/RobloxHbaService.Binding.cs`, and `src/Shiro/Classes/Account.HbaBinding.cs`:
   - In `RobloxHbaService.Binding.cs` (`GetPersistedKeyInjectionScript`), if `string.IsNullOrWhiteSpace(publicKeySpkiBase64)`, derive it from `privateKeyPkcs8Base64` using `ECDsa.ImportPkcs8PrivateKey` + `ExportSubjectPublicKeyInfo()`, and in the JS `generateKey` override allow returning `{ privateKey: imported.privateKey, publicKey: imported.publicKey }` when `imported.privateKey` is valid.
   - In `Account.HbaBinding.cs` (`ApplySessionFingerprintAndHbaToRestRequest`, line `278`), add:
     ```csharp
     else if (request.Parameters.Count > 0)
     {
         bodyStr = string.Join("&", request.Parameters.Select(p => $"{Uri.EscapeDataString(p.Key)}={Uri.EscapeDataString(p.Value)}"));
     }
     ```
   - In `RobloxHbaService.ClearKeyCache()`, simply call `CachedEcParameters.Clear()` without mutating `kvp.Value.D` in-place while concurrent callers may be passing `ecParams` to `ECDsa.Create(ecParams)`.

- [ ] **Step 4: Run tests to verify they pass**

Run: `dotnet test tests/Shiro.Tests/Shiro.Tests.csproj --filter "FullyQualifiedName~LaunchCookieAndHbaBugTests" --no-restore`
Expected: PASS (3 passed, 0 failed).

---

### Task 5: `RobloxProcess` Log Reader Synchronization, `RobloxWatcher` PID Cache, Nexus Reconnect & `WebAuthPolicy` Origin Hardening

**Files:**
- Modify: `src/Shiro/Classes/RobloxProcess.cs:30-38, 130-155, 174-228, 430-465`
- Modify: `src/Shiro/Classes/RobloxWatcher.cs:160-180, 230-260, 351-384`
- Modify: `src/Shiro/Nexus/NexusWebSocketServer.cs:110-130, 144-206`
- Modify: `src/Shiro/Nexus/ControlledAccount.cs:132-203`
- Modify: `src/Shiro/Services/Auth/WebAuthPolicy.cs:80-120`
- Test: `tests/Shiro.Tests/LogWatcherNexusAndWebAuthTests.cs`

**Interfaces:**
- Produces:
  - `RobloxProcess.ReadAppendedLines(Stream stream, ref long lastPosition)` (updated to hold incomplete trailing lines on `FileStream` when `lastNewLine < 0`)
  - `ControlledAccount.Disconnect(WebSocket? expectedSocket = null)`

- [ ] **Step 1: Write the failing regression tests in `tests/Shiro.Tests/LogWatcherNexusAndWebAuthTests.cs`**

```csharp
using System;
using System.IO;
using System.Net.WebSockets;
using System.Text;
using Shiro.Classes;
using Shiro.Nexus;
using Shiro.Services;
using Xunit;

namespace Shiro.Tests
{
    public class LogWatcherNexusAndWebAuthTests
    {
        [Fact]
        public void ReadAppendedLines_FileStreamWithHalfWrittenLineWithoutNewline_WaitsUntilNewlineFlushed()
        {
            string tempFile = Path.Combine(Path.GetTempPath(), "ShiroLogTest_" + Guid.NewGuid().ToString("N") + ".log");
            try
            {
                using var writer = new FileStream(tempFile, FileMode.Create, FileAccess.Write, FileShare.ReadWrite);
                using var reader = new FileStream(tempFile, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                long pos = 0;

                byte[] part1 = Encoding.UTF8.GetBytes("2026-11-04T12:00:00.000Z [FLog::Network] Connection lost");
                writer.Write(part1, 0, part1.Length);
                writer.Flush();

                var lines1 = RobloxProcess.ReadAppendedLines(reader, ref pos);
                Assert.Empty(lines1);
                Assert.Equal(0, pos);

                byte[] part2 = Encoding.UTF8.GetBytes(" with reason 267\r\n");
                writer.Write(part2, 0, part2.Length);
                writer.Flush();

                var lines2 = RobloxProcess.ReadAppendedLines(reader, ref pos);
                Assert.Single(lines2);
                Assert.Equal("2026-11-04T12:00:00.000Z [FLog::Network] Connection lost with reason 267", lines2[0]);
            }
            finally
            {
                try { File.Delete(tempFile); } catch { }
            }
        }

        [Fact]
        public void ControlledAccount_DisconnectWithStaleSocket_DoesNotDisconnectActiveNewSocket()
        {
            var acc = new ControlledAccount { Username = "TeleportUser" };
            using var ws1 = new ClientWebSocket();
            using var ws2 = new ClientWebSocket();

            acc.Connect(ws1);
            acc.Connect(ws2);

            acc.Disconnect(ws1);

            Assert.Equal(AccountStatus.Online, acc.Status);
            Assert.Same(ws2, acc.WebSocket);
        }

        [Fact]
        public void WebAuthPolicy_WhenAllowExternalTrue_StillBlocksUntrustedBrowserOrigin()
        {
            var settings = new AuthSettings(AllowExternal: true, EveryRequestRequiresPassword: false, ConfiguredPassword: "StrongPassword123");
            var req = new AuthRequest(
                Scope: AuthScope.NexusControl,
                IsLoopbackConnection: true,
                Host: "localhost",
                Origin: "https://evil.example.com",
                HeaderPassword: "StrongPassword123",
                QueryPassword: null);

            var decision = WebAuthPolicy.Evaluate(req, settings);
            Assert.Equal(AuthDecision.ForbiddenOrigin, decision);
        }
    }
}
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test tests/Shiro.Tests/Shiro.Tests.csproj --filter "FullyQualifiedName~LogWatcherNexusAndWebAuthTests" --no-restore`
Expected: FAIL (`lines1` consumes the half-written line, `acc.Disconnect(ws1)` does not compile, and `WebAuthPolicy.Evaluate` returns `Allow` for `https://evil.example.com`).

- [ ] **Step 3: Implement `RobloxProcess` log locking & partial-line guard, `RobloxWatcher` metadata fix, `ControlledAccount.Disconnect(expectedSocket)`, and `WebAuthPolicy` origin check**

1. In `src/Shiro/Classes/RobloxProcess.cs`:
   - Add `private readonly System.Threading.Lock _logSync = new();`.
   - In `ReadAppendedLines` (`lines 196-205`):
     ```csharp
     int consumeLength = length;
     if (stream is FileStream && rented[length - 1] != (byte)'\n')
     {
         int lastNewLine = Array.LastIndexOf(rented, (byte)'\n', length - 1, length);
         if (lastNewLine < 0)
         {
             return Array.Empty<string>();
         }
         consumeLength = lastNewLine + 1;
     }
     ```
   - In `ReadLogFile`, acquire `lock (_logSync)` around reading `LogStream` and mutating `LastPosition`/`LastLine`, and acquire `lock (_logSync)` when disposing `LogStream` in `KillProcess` and `CleanupProcessExit`.
   - In `WaitForLogPathAsync`, if `Volatile.Read(ref _cleanedUp) != 0` when a stream is opened, dispose it immediately and return.
2. In `src/Shiro/Classes/RobloxWatcher.cs`:
   - In `CheckProcesses` (`line 247`), only write `_pidMetadataCache[pid]` when `!string.IsNullOrEmpty(cmdLine)`.
   - In `PruneExitedMetadataUnderLock` (`line 353`), remove the faulty `if (_pidMetadataCache.Count <= activePids.Count && Seen.Count <= activePids.Count) return;` early return so exited PIDs are always pruned when `activePids` is provided.
3. In `src/Shiro/Nexus/ControlledAccount.cs` and `src/Shiro/Nexus/NexusWebSocketServer.cs`:
   - Update `public void Disconnect(WebSocket? expectedSocket = null)` in `ControlledAccount.cs`: if `expectedSocket != null && !ReferenceEquals(this.WebSocket, expectedSocket)`, return immediately without disconnecting.
   - In `NexusWebSocketServer.cs:204`, call `account.Disconnect(webSocket);`.
   - Synchronize `modernNexus.Accounts` lookup and insertion in `NexusWebSocketServer.cs:111-127` using a lock so concurrent connections cannot enumerate `modernNexus.Accounts` while it is being mutated or create duplicate `ControlledAccount` instances.
4. In `src/Shiro/Services/Auth/WebAuthPolicy.cs`:
   - Always check `if (!AccountApiDispatcher.IsTrustedLocalOrigin(request.Origin ?? string.Empty)) return AuthDecision.ForbiddenOrigin;` regardless of `settings.AllowExternal`.
   - When `settings.AllowExternal` is `true`, require `IsPasswordConfigured(settings.ConfiguredPassword)` for `AuthScope.NexusControl` as well as `AuthScope.Api`.

- [ ] **Step 4: Run tests to verify they pass**

Run: `dotnet test tests/Shiro.Tests/Shiro.Tests.csproj --filter "FullyQualifiedName~LogWatcherNexusAndWebAuthTests" --no-restore`
Expected: PASS (3 passed, 0 failed).

---

### Task 6: UI Thread Affinity in ViewModels, `CurrentPlaceId` Clearability, and Native AOT JSON Serialization

**Files:**
- Modify: `src/Shiro/ViewModels/ServerListViewModel.ServerOperations.cs:169-213`
- Modify: `src/Shiro/ViewModels/ServerListViewModel.cs:339-364`
- Modify: `src/Shiro/ViewModels/ServerListViewModel.PlayerSearch.cs:170-175`
- Modify: `src/Shiro/ViewModels/Dialogs/BulkImportViewModel.cs:145-161, 200-287`
- Modify: `src/Shiro/ViewModels/Dialogs/AccountUtilsViewModel.cs:89-150`
- Modify: `src/Shiro/ViewModels/Dialogs/ProxyManagerViewModel.cs:140-166`
- Modify: `src/Shiro/Services/Storage/ServerBrowserStateService.cs:15-50`
- Modify: `src/Shiro/Services/Storage/JsonExtensions.cs:55-74, 113-130`
- Modify: `src/Shiro/Services/ShiroAppJson.cs:14-96`
- Modify: `src/Shiro/Classes/GameClass.cs:153-178`
- Modify: `src/Shiro/Classes/Batch.cs:100-116, 238`
- Modify: `src/Shiro/AccountManager.Accounts.cs:75`
- Modify: `src/Shiro/Classes/Account.SessionState.cs:496`
- Modify: `src/Shiro/Services/Auth/BloxGenService.cs:127-133`
- Modify: `src/Shiro/Services/Auth/ReloginQueueStore.cs:35, 55`
- Modify: `src/Shiro/Services/Launch/AccountLaunchService.cs:300`
- Modify: `src/Shiro/Services/Network/AssetCommerceService.cs:40`
- Modify: `src/Shiro/Services/Network/RobloxServerQueryService.cs:92`
- Modify: `src/Shiro/Services/Network/RobloxSettingsApiService.cs:117, 161, 273, 336, 354, 407`
- Modify: `src/Shiro/Services/Network/RobloxSocialApiService.cs:159, 236, 259`
- Test: `tests/Shiro.Tests/AotJsonAndViewModelStateTests.cs`

**Interfaces:**
- Produces:
  - Source-generated JSON request DTO records in `ShiroAppJson.cs`
  - `FlexibleStringConverter` registered on `ShiroAppJson`

- [ ] **Step 1: Write the failing regression tests in `tests/Shiro.Tests/AotJsonAndViewModelStateTests.cs`**

```csharp
using Shiro.Nexus;
using Shiro.Services;
using Xunit;

namespace Shiro.Tests
{
    public class AotJsonAndViewModelStateTests
    {
        [Fact]
        public void FastJson_TryDeserializeCommand_WithNumericAndBooleanPayloadValues_SucceedsViaShiroAppJson()
        {
            string raw = "{\"Name\":\"ping\",\"Payload\":{\"fps\":60,\"ping\":42,\"active\":true}}";
            bool ok = Utilities.FastJson.TryDeserialize<Command>(raw, out var cmd);

            Assert.True(ok);
            Assert.NotNull(cmd);
            Assert.NotNull(cmd!.Payload);
            Assert.Equal("60", cmd.Payload!["fps"]);
            Assert.Equal("42", cmd.Payload["ping"]);
            Assert.Equal("true", cmd.Payload["active"]);
        }

        [Fact]
        public void ServerBrowserStateService_CurrentPlaceId_WhenClearedToEmpty_RemainsEmptyInsteadOfFallback()
        {
            var ini = new IniSection("General");
            ini.Set("SavedPlaceId", "123456789");
            var svc = new ServerBrowserStateService(() => ini, () => null);

            Assert.Equal("123456789", svc.CurrentPlaceId);

            svc.CurrentPlaceId = string.Empty;
            Assert.Equal(string.Empty, svc.CurrentPlaceId);
        }
    }
}
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test tests/Shiro.Tests/Shiro.Tests.csproj --filter "FullyQualifiedName~AotJsonAndViewModelStateTests" --no-restore`
Expected: FAIL (`FastJson.TryDeserialize<Command>` returns `false` on numeric/boolean `Payload` values because `ShiroAppJson` lacks `FlexibleStringConverter`, and `svc.CurrentPlaceId` returns `"123456789"` after being cleared to `string.Empty`).

- [ ] **Step 3: Implement `ShiroAppJson` converter & DTO registrations, `ServerBrowserStateService` clearability, and ViewModel UI-thread marshaling**

1. In `src/Shiro/Services/ShiroAppJson.cs` and `src/Shiro/Services/Storage/JsonExtensions.cs`:
   - Add `Converters = new[] { typeof(FlexibleStringConverter) }` to `[JsonSourceGenerationOptions(...)]` in `ShiroAppJson.cs`.
   - Add a second source-generated context or `Indented` instance (`ShiroAppJsonIndented`) or resolve type info safely in `FastJson.CreateOptions` by only adding `new DefaultJsonTypeInfoResolver()` when `JsonSerializer.IsReflectionEnabledByDefault` is `true`.
   - In `GameClass.cs:165`, remove `[JsonConstructor]` from `public FavoriteGame(long PlaceID, Action? Callback = null)` and place `[JsonConstructor]` on `public FavoriteGame() : base(0)`.
   - Make `Batch.BatchRequestPayload` `internal` and register `[JsonSerializable(typeof(Batch.BatchRequestPayload[]))]` and `[JsonSerializable(typeof(long[]))]` in `ShiroAppJson.cs`.
   - Replace anonymous `AddJsonBody(new { ... })` / `JsonSerializer.Serialize(new { ... })` calls in `AccountManager.Accounts.cs`, `Account.SessionState.cs`, `BloxGenService.cs`, `ReloginQueueStore.cs`, `AccountLaunchService.cs`, `AssetCommerceService.cs`, `RobloxServerQueryService.cs`, `RobloxSettingsApiService.cs`, and `RobloxSocialApiService.cs` with typed `internal readonly record struct` payloads registered in `ShiroAppJson.cs`.
2. In `src/Shiro/Services/Storage/ServerBrowserStateService.cs`:
   - Add `private bool _placeIdInitialized;`. On the first `CurrentPlaceId` getter access when `!_placeIdInitialized`, set `_placeIdInitialized = true` and load from `general.Get("SavedPlaceId")` (or `"5315046213"`). When the setter is called, set `_placeIdInitialized = true` and `_currentPlaceId = value?.Trim() ?? string.Empty` so explicitly clearing `CurrentPlaceId` returns `string.Empty`.
3. In `src/Shiro/ViewModels/ServerListViewModel.ServerOperations.cs`, `ServerListViewModel.cs`, `ServerListViewModel.PlayerSearch.cs`, `BulkImportViewModel.cs`, `AccountUtilsViewModel.cs`, and `ProxyManagerViewModel.cs`:
   - In `LoadRegionCoreAsync` (`ServerListViewModel.ServerOperations.cs:169-213`), marshal assignments to `server` properties (`IsBusyPinging`, `Ip`, `Ping`, `CountryCode`, `FlagEmoji`, `Region`) via `AppDispatcher.Post(...)`.
   - In `RefreshServersAsync` (`ServerListViewModel.cs:339-364`), check `if (!ReferenceEquals(_operationCts, cts)) return;` inside the `AppDispatcher.Post` callbacks in `catch (OperationCanceledException)` and `finally`.
   - In `ProduceServerBatchesAsync` (`ServerListViewModel.PlayerSearch.cs:172-173`) and `BulkImportViewModel.StartImportAsync` (`BulkImportViewModel.cs:147-148`), marshal `ProgressValue` / `SetStatus` / `PlayerSearchStatus` / `LoadingStatus` via `AppDispatcher.Post`, and use `.ConfigureAwait(true)` on ViewModel command methods in `BulkImportViewModel`, `AccountUtilsViewModel`, and `ProxyManagerViewModel`.

- [ ] **Step 4: Run the full test suite to verify all existing and new tests pass**

Run: `dotnet test tests/Shiro.Tests/Shiro.Tests.csproj --no-restore -v minimal`
Expected: PASS (1,706+ passed, 0 failed).
