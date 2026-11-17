> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

# P0 Storage, Vault Security, Runtime State & Redaction Fixes

**Goal:** Resolve vault-locking deadlocks and fail-open key persistence in `AccountPersistenceService` / `StoragePaths.VaultKey`, eliminate data-loss races in `LoadAccounts`, `SaveAccounts`, `Recovery`, and `AccountRuntimeStateStore`, and close critical redaction and INI serialization gaps in `LogRedactor` and `IniFile`.

**Architecture:** Enforce fail-closed cryptographic key creation, ordered dirty-flag clearing and backup ranking inside `AccountPersistenceService` partials (keeping `AccountPersistenceService.cs <= 485` lines via helper extraction if required), serialize `AccountRuntimeStateStore` file writes inside `_sync` while isolating `filePathOverride` scopes, and harden regex/parser paths in `LogRedactor` and `IniFile`.

**Tech Stack:** .NET 10 (`net10.0-windows10.0.19041.0`), `System.Security.Cryptography` (`ProtectedData`, `CryptographicOperations`), `System.Text.Json`, `[GeneratedRegex]`, `xUnit`.

**Spec:** System Audit Report (`C:/Users/lenovo/.gemini/antigravity/brain/d3d9db7e-c20c-4d8a-b056-391721b18014/system-audit-report.md`) - Section 2 (P0.3) & Section 3.3.

**Global Constraints:**
- `TreatWarningsAsErrors=true`, `<Nullable>enable</Nullable>`, AOT-compatible JSON (`ShiroAppJson`), strict ASCII English in all C# comments, docstrings, and log messages.
- Source files `<= 485` lines (`AccountPersistenceService.cs` is currently 476/485!); test files `<= 550` lines.
- No empty or untyped `catch` blocks (`AppLogger` only). Never put `{` or `}` inline in test method string literals (hoist JSON/regex test strings to `private static readonly`).
- Preserve branch floor in `tests/coverage-floors.json`: `Services/Storage/AccountPersistenceService.cs >= 67%`.
- Stage only explicit files per commit (never `git add -A`).

**Review Focus:**
- **Task 1:** Verify clearing Master Password (`newPass == ""`) rotates safely to a DPAPI-protected `vault.key` without throwing `"Encrypted vault exists without vault.key"`, whitespace-only passphrases (`"        "`) are rejected, failed `AtomicWriteBytes` of `vault.key` throws instead of returning an unpersisted key, and `SaveRecoveredVaultKey` zeros old `_cachedVaultKey`.
- **Task 2:** Verify `LoadAccounts` holds `saveLock`, only clears `IsVaultLockedPendingMasterPassword` after decoding succeeds, writes failed corrupt payloads to a distinct `.corrupt-*.bak` path rather than overwriting valid backups, `SaveAccounts` clears `IsDirty` ONLY after write succeeds, `AccountFileDecoder` distinguishes wrong password vs corrupt ciphertext when `masterPassword != null`, and `Recovery` prioritizes newest valid backups.
- **Task 3:** Verify `AccountRuntimeStateStore.Flush` executes `AtomicWriteBytes` inside `lock (_sync)` and `filePathOverride` operations never mutate global `_document`/`_loadedFilePath`.
- **Task 4:** Verify `LogRedactor` redacts Discord webhooks, quoted `.ROBLOSECURITY` cookies, and start-of-string form secrets (`MayContainSensitiveData` included), and `IniFile.Set` retains `""` while `LoadFrom` accepts comments and trailing commas in JSON inputs.

---

### Task 1: Fix Master Password Removal Deadlock & Fail-Closed `vault.key` Persistence

**Context:**
- `src/Shiro/Services/Storage/AccountPersistenceService.cs:122-125`: removing an existing Master Password calls `ClearPasswordHash(); SaveAccounts(true, true);`, which calls `StoragePaths.GetOrCreateVaultKeyCopy()` -> `LoadOrCreateVaultKeyAtPath` (`StoragePaths.VaultKey.cs:155-159`), throwing `CryptographicException("Encrypted vault exists...")` because `AccountData.json` already exists on disk without `vault.key`. Additionally, whitespace-only passphrases like `"        "` pass length checks before `string.IsNullOrWhiteSpace(newPass)` mistakenly clears the password.
- `src/Shiro/Services/Storage/StoragePaths.VaultKey.cs:161-176`: when `AtomicWriteBytes(keyFile, ...)` fails, the catch block logs and returns the unpersisted `newKey` anyway, producing an unreadable vault on next startup. Line `218` (`SaveRecoveredVaultKey`) overwrites `_cachedVaultKey` without zeroing the previous array.

**Files:**
- Modify: `src/Shiro/Services/Storage/AccountPersistenceService.cs`
- Modify: `src/Shiro/Services/Storage/StoragePaths.VaultKey.cs`
- Create (if needed to keep `<= 485` lines): `src/Shiro/Services/Storage/AccountPersistenceService.VaultSecurity.cs`
- Create: `tests/Shiro.Tests/P0StorageAndRedactionRegressionTests.cs`

**Interfaces:**
- **Added (internal):** `StoragePaths.EnsureOrCreateVaultKeyForUnprotectedMigrationCopy()` (or optional flag `allowCreateWhenEncryptedVaultExists` gated strictly to authenticated in-memory migration flows) in `StoragePaths.VaultKey.cs`.
- **Modified:** `AccountPersistenceService` master password update path rejecting `string.IsNullOrWhiteSpace(newPass)` when `!string.IsNullOrEmpty(newPass)`, and explicitly creating/persisting the DPAPI `vault.key` before re-saving unlocked accounts.

**Steps:**
- [ ] **Step 1: Write failing test(s) (TDD Red)**
  - Create `tests/Shiro.Tests/P0StorageAndRedactionRegressionTests.cs` (using isolated temp directories and `StoragePaths` / `AccountPersistenceService` test constructors/overrides):
    - Test: Create an encrypted vault protected ONLY by Master Password (no `vault.key` on disk), load/unlock it, then invoke Change/Remove Master Password with `string.Empty`: assert removal succeeds, `vault.key` is persisted on disk, and reloading `AccountPersistenceService` with `Hash = null` succeeds cleanly!
    - Test: Invoke Master Password update with `"        "` (8 spaces, non-empty whitespace) and assert it fails validation without clearing the existing password!
    - Test: Call `StoragePaths.VaultKey` creation when `AtomicWriteBytes` / target `keyFile` path is read-only or locked (or unwritable directory) and assert it throws `IOException`/`UnauthorizedAccessException` rather than returning a transient unpersisted key!
    - Test: Inspect/verify `SaveRecoveredVaultKey` zeros the previous `_cachedVaultKey` buffer via a test hook or verifiable previous buffer reference.
- [ ] **Step 2: Run targeted tests to verify expected Red failure**
  Run:
  ```powershell
  dotnet test Shiro.sln --no-restore --nologo --filter "FullyQualifiedName~P0StorageAndRedactionRegressionTests|FullyQualifiedName~CriticalStorageAndVaultBugTests|FullyQualifiedName~VaultKey"
  ```
  Confirm Red failure on `CryptographicException("Encrypted vault exists...")` and whitespace password acceptance.
- [ ] **Step 3: Implement minimal fixes**
  - Edit `src/Shiro/Services/Storage/StoragePaths.VaultKey.cs`:
    - In `LoadOrCreateVaultKeyAtPath` (`lines 161-176`): remove fail-open fallback on `AtomicWriteBytes` failure; zero `newKey` in `catch` / `finally` on failure and rethrow so key creation fails closed.
    - Add internal `EnsureVaultKeyForUnlockedVaultCopy(string? customDataDir = null)` that safely generates & persists DPAPI `vault.key` when an authenticated session transitions from Master Password encryption to auto-vault-key encryption.
    - In `SaveRecoveredVaultKey` (`line 218`): zero existing `_cachedVaultKey` (`CryptographicOperations.ZeroMemory`) under lock before replacing it.
  - Edit `src/Shiro/Services/Storage/AccountPersistenceService.cs` (and move password-change UI/validation flow to `AccountPersistenceService.VaultSecurity.cs` if line count approaches 485):
    - Reject non-empty whitespace passphrases (`!string.IsNullOrEmpty(newPass) && string.IsNullOrWhiteSpace(newPass)`).
    - When user explicitly clears Master Password (`newPass.Length == 0`): provision/persist `vault.key` via `StoragePaths` authenticated helper before/while saving re-encrypted vault under the new vault key cleanly.
- [ ] **Step 4: Run Storage/Vault tests & ArchitectureTests to verify Green pass**
  Run:
  ```powershell
  dotnet test Shiro.sln --no-restore --nologo --filter "FullyQualifiedName~P0StorageAndRedactionRegressionTests|FullyQualifiedName~CriticalStorageAndVaultBugTests|FullyQualifiedName~AccountPersistenceTests|FullyQualifiedName~Vault|FullyQualifiedName~ArchitectureTests"
  ```
  Verify `AccountPersistenceService.cs` `<= 485` lines and `>= 67%` branch coverage.
- [ ] **Step 5: Commit**
  ```powershell
  git add src/Shiro/Services/Storage/AccountPersistenceService.cs src/Shiro/Services/Storage/StoragePaths.VaultKey.cs tests/Shiro.Tests/P0StorageAndRedactionRegressionTests.cs
  git commit -m "fix(vault): resolve master password removal deadlock and fail-closed vault key writes"
  ```
  *(Also stage `AccountPersistenceService.VaultSecurity.cs` if created.)*

---

### Task 2: Fix `LoadAccounts` Locking, Corrupt Backup Overwrite, `SaveAccounts` Dirty-Flag Clear & Recovery Ranking

**Context:**
- `AccountPersistenceService.cs:158-216`: `LoadAccounts` runs without acquiring `saveLock` and resets `IsVaultLockedPendingMasterPassword = false` at `line 162` before `AccountFileDecoder.Decode` succeeds; at `line 210` (`Corrupt` branch) it overwrites `SaveFilePath + ".bak"`, potentially destroying a valid `.bak`; and `SaveAccounts` (`line 393`) calls `TakeDirtyAccounts` (clearing `IsDirty = false`) BEFORE `Serialize` and `WriteWithBackup` succeed.
- `AccountFileDecoder.cs:52-55`: catches `InvalidDataException` and returns `NeedsMasterPassword` even when `masterPassword != null` (wrong password vs genuinely corrupt/truncated file ambiguity).
- `AccountPersistenceService.Recovery.cs:107`: selects best backup solely via `restored.Count > bestCount`, preferring an ancient stale backup with more deleted accounts over a newer valid backup!

**Files:**
- Modify: `src/Shiro/Services/Storage/AccountPersistenceService.cs`
- Modify: `src/Shiro/Services/Storage/AccountPersistenceService.Recovery.cs`
- Modify: `src/Shiro/Services/Storage/AccountFileDecoder.cs`
- Modify: `tests/Shiro.Tests/P0StorageAndRedactionRegressionTests.cs`

**Interfaces:**
- **Modified:** `AccountPersistenceService.LoadAccounts` (`saveLock` coordination, atomic `IsVaultLockedPendingMasterPassword` update on outcome, quarantine `.corrupt-<timestamp>.bak` path).
- **Modified:** `AccountPersistenceService.SaveAccounts` (peek dirty accounts and clear `IsDirty = false` ONLY after `Store.WriteWithBackup` / write succeeds).
- **Modified:** `AccountPersistenceService.Recovery.TryRestoreReadableBackup()` (sort candidates by `File.GetLastWriteTimeUtc(path)` descending — preferring newest valid backup, with explicit primary backup priority `.backup` / `.previous` tie-breaking cleanly).

**Steps:**
- [ ] **Step 1: Write failing test(s) (TDD Red)**
  - In `tests/Shiro.Tests/P0StorageAndRedactionRegressionTests.cs`:
    - Test `SaveAccounts`: when disk write fails (`Store` / readonly target throws IO error), modified account keeps `IsDirty == true` so subsequent save still persists it.
    - Test `LoadAccounts` `Corrupt` branch: pre-create a valid `SaveFilePath + ".bak"` sentinel file, feed corrupt bytes to `SaveFilePath`, call `LoadAccounts()`, and assert the pre-existing `.bak` content is preserved intact (quarantine written to `.corrupt-*.bak` instead).
    - Test `AccountFileDecoder.Decode(corruptBytes, masterPasswordHash, ...)` with non-null `masterPassword` throws/returns `AccountDecodeStatus.Corrupt` on `InvalidDataException` (truncated/non-encrypted payload with valid-looking wrapper or corrupt frame) vs `NeedsMasterPassword` when appropriate.
    - Test `TryRestoreReadableBackup`: given an older backup (mtime `T - 1 day`, 5 accounts) and a newer valid backup (mtime `T - 1 min`, 2 accounts after deletion), assert the newer 2-account backup is restored instead of resurrecting deleted accounts!
- [ ] **Step 2: Run targeted tests to verify expected Red failure**
  Run:
  ```powershell
  dotnet test Shiro.sln --no-restore --nologo --filter "FullyQualifiedName~P0StorageAndRedactionRegressionTests|FullyQualifiedName~AccountPersistenceTests|FullyQualifiedName~CriticalStorageAndVaultBugTests"
  ```
- [ ] **Step 3: Implement minimal fixes**
  - Edit `src/Shiro/Services/Storage/AccountPersistenceService.cs`:
    - Guard `LoadAccounts` cleanly under `saveLock` (careful not to deadlock nested `SaveAccounts` during `accountDataNeedsMigration` / `TryRestoreReadableBackup`, since C# `lock` is re-entrant on same thread — verify re-entrancy with `saveLock`). Only assign `IsVaultLockedPendingMasterPassword` at decode/recovery outcome (`false` on success/empty, `true` on `NeedsMasterPassword` / `PromotedNeedsPassword`).
    - In `Corrupt` branch (`line 210`): quarantine to `$"{SaveFilePath}.corrupt-{DateTime.UtcNow:yyyyMMdd_HHmmss}.bak"` cleanly instead of overwriting `SaveFilePath + ".bak"`.
    - In `SaveAccounts` (`line 393`): snapshot dirty flags BEFORE serialization and clear `IsDirty = false` ONLY after `Store.WriteWithBackup` / `StoragePaths.AtomicWriteBytes` succeeds (leaving `IsDirty = true` on exception/early abort).
  - Edit `src/Shiro/Services/Storage/AccountFileDecoder.cs:52-55`: distinguish `NeedsMasterPassword` (`masterPassword == null` or authentication tag mismatch / wrong passphrase with intact header) from `Corrupt` (`InvalidDataException` when `masterPassword != null`!).
  - Edit `src/Shiro/Services/Storage/AccountPersistenceService.Recovery.cs:73-113`: sort valid `File.Exists(path)` candidates by `File.GetLastWriteTimeUtc(path)` descending (tie-break by preference order / count) and take the newest readable non-empty backup first!
- [ ] **Step 4: Run Storage suite & ArchitectureTests to verify Green pass**
  Run:
  ```powershell
  dotnet test Shiro.sln --no-restore --nologo --filter "FullyQualifiedName~P0StorageAndRedactionRegressionTests|FullyQualifiedName~AccountPersistenceTests|FullyQualifiedName~CriticalStorageAndVaultBugTests|FullyQualifiedName~ArchitectureTests"
  ```
- [ ] **Step 5: Commit**
  ```powershell
  git add src/Shiro/Services/Storage/AccountPersistenceService.cs src/Shiro/Services/Storage/AccountPersistenceService.Recovery.cs src/Shiro/Services/Storage/AccountFileDecoder.cs tests/Shiro.Tests/P0StorageAndRedactionRegressionTests.cs
  git commit -m "fix(storage): protect load lock, defer dirty reset, preserve backups, and rank newest recovery"
  ```

---

### Task 3: Fix `AccountRuntimeStateStore.Flush` Write Race & `filePathOverride` Isolation

**Context:**
- `src/Shiro/Services/Storage/AccountRuntimeStateStore.cs:191-216`: `Flush` builds `payloadToWrite` inside `lock (_sync)` then releases `_sync` and executes `StoragePaths.AtomicWriteBytes(targetPath, payloadToWrite, durable: false)` outside the lock, allowing two concurrent `Flush()` threads to overwrite newer runtime state with an older payload. Additionally, `EnsureLoadedUnderLock(filePathOverride)` overwrites static `_document` and `_loadedFilePath` globally.

**Files:**
- Modify: `src/Shiro/Services/Storage/AccountRuntimeStateStore.cs`
- Modify: `tests/Shiro.Tests/P0StorageAndRedactionRegressionTests.cs`

**Interfaces:**
- **Modified:** `AccountRuntimeStateStore.Flush(string? filePathOverride = null)` and `HydrateAccounts(..., string? filePathOverride = null)` / `RecordLastUse(..., string? filePathOverride = null)`. When `filePathOverride != null`, read/modify/write an isolated per-override document (never clobbering global `_document`/`_loadedFilePath`), AND perform `StoragePaths.AtomicWriteBytes` inside `lock (_sync)` (matching `_dirty` reset cleanly after write succeeds!).

**Steps:**
- [ ] **Step 1: Write failing test(s) (TDD Red)**
  - In `tests/Shiro.Tests/P0StorageAndRedactionRegressionTests.cs`:
    - Test `filePathOverride` isolation: hydrate/record on default path `docA`, then call `RecordLastUse(..., filePathOverride: fileB)` and `Flush(filePathOverride: fileB)`, and assert default path state (`fileA` cache) was NOT clobbered and subsequent `RecordLastUse`/`Flush` on default path writes to `fileA` with `docA` intact!
    - Concurrency stress test on `RecordLastUse` + `Flush`: spawn multiple parallel tasks mutating and flushing monotonically increasing timestamps to an isolated temp path; assert final JSON on disk strictly contains the latest timestamp and zero out-of-order stale overwrites!
- [ ] **Step 2: Run targeted tests to verify expected Red failure**
  Run:
  ```powershell
  dotnet test Shiro.sln --no-restore --nologo --filter "FullyQualifiedName~P0StorageAndRedactionRegressionTests|FullyQualifiedName~P1StorageRegressionTests|FullyQualifiedName~RenovationAndPerformanceTests"
  ```
- [ ] **Step 3: Implement minimal fixes**
  - Edit `src/Shiro/Services/Storage/AccountRuntimeStateStore.cs`:
    - Maintain a dedicated cache or isolated load/save path for `filePathOverride` (`ConcurrentDictionary<string, RuntimeStateDocument>` or per-path load/serialize under `lock (_sync)`) so overriding never mutates the global default `_document`/`_loadedFilePath` slot! (Preserve exact `P1StorageRegressionTests.cs:109-134` behavior where `RecordLastUse(..., filePathOverride: fileA)` + `Flush(filePathOverride: fileA)` persists accurately.)
    - Move `StoragePaths.AtomicWriteBytes(targetPath, payloadToWrite, durable: false)` inside `lock (_sync)` and reset `_dirty = false` only after `AtomicWriteBytes` succeeds.
- [ ] **Step 4: Run RuntimeStateStore suite & ArchitectureTests to verify Green pass**
  Run:
  ```powershell
  dotnet test Shiro.sln --no-restore --nologo --filter "FullyQualifiedName~P0StorageAndRedactionRegressionTests|FullyQualifiedName~P1StorageRegressionTests|FullyQualifiedName~RenovationAndPerformanceTests|FullyQualifiedName~ArchitectureTests"
  ```
- [ ] **Step 5: Commit**
  ```powershell
  git add src/Shiro/Services/Storage/AccountRuntimeStateStore.cs tests/Shiro.Tests/P0StorageAndRedactionRegressionTests.cs
  git commit -m "fix(storage): serialize AccountRuntimeStateStore flushes under lock and isolate path overrides"
  ```

---

### Task 4: Fix `LogRedactor` Gaps & `IniFile` Empty-Value / JSON Parsing Bugs

**Context:**
- `src/Shiro/Services/Storage/LogRedactor.cs:10-105`: lacks Discord Webhook URL redaction (`https://(?:discord|discordapp)\.com/api/webhooks/\d+/[A-Za-z0-9_\-]+`), fails to strip quoted `.ROBLOSECURITY="..."` cookies, and query-param redaction requires leading `[?&]` (missing raw index-0 form bodies like `token=secret&foo=bar`). Also check `MayContainSensitiveData(ReadOnlySpan<char>)` so new tokens aren't short-circuited!
- `src/Shiro/Services/Storage/IniFile.cs:214-218`: `Set(string name, string value)` executes `if (string.IsNullOrWhiteSpace(value)) { _properties.Remove(name); return; }`, deleting intentionally cleared settings like `WebServer.Password = ""`; `line 301` parses JSON using `JsonDocument.Parse(trimmed)` without `CommentHandling = Skip, AllowTrailingCommas = true`.

**Files:**
- Modify: `src/Shiro/Services/Storage/LogRedactor.cs`
- Modify: `src/Shiro/Services/Storage/IniFile.cs`
- Modify: `tests/Shiro.Tests/LoggerAndRedactorTests.cs`
- Modify: `tests/Shiro.Tests/P0StorageAndRedactionRegressionTests.cs`

**Interfaces:**
- **Modified:** `LogRedactor.MayContainSensitiveData(ReadOnlySpan<char>)` & `LogRedactor.Redact(string?)` (keep `private static readonly` / `[GeneratedRegex]` partial helpers AOT-safe).
- **Modified:** `IniFile.Set(string name, string value)` & `IniFile` JSON load options.

**Steps:**
- [ ] **Step 1: Write failing test(s) (TDD Red)**
  - In `tests/Shiro.Tests/LoggerAndRedactorTests.cs`:
    - Assert `LogRedactor.MayContainSensitiveData` and `Redact` handle:
      1. `https://discord.com/api/webhooks/1234567890/AbCdEfGh_1234-xyz` and `discordapp.com/api/webhooks/...` -> token segment redacted (`[REDACTED]` / `[REDACTED_WEBHOOK]`).
      2. Quoted cookie `Cookie: .ROBLOSECURITY="_|WARNING:-DO-NOT-SHARE-THIS.--Abc123"` and `.ROBLOSECURITY="rawSecretValue"` -> redacted cleanly!
      3. Index-0 form secret `token=mySecret123&grant_type=client_credentials` / `password=Pass123!` -> redacted cleanly!
  - In `tests/Shiro.Tests/P0StorageAndRedactionRegressionTests.cs`:
    - Assert `IniFile.Set("Password", "")` preserves key `"Password"` (`Exists("Password") == true`, `Get<string>("Password") == ""`), persists cleanly via `Save`/`Load`, while clearing previously non-empty `Password` does NOT erase key or silently revert on merge/save (verify `AccountManagerBehaviorTests.cs` still passes!).
    - Assert `IniFile` JSON parsing loads JSON containing `// comments` and trailing commas cleanly.
- [ ] **Step 2: Run targeted tests to verify expected Red failure**
  Run:
  ```powershell
  dotnet test Shiro.sln --no-restore --nologo --filter "FullyQualifiedName~LoggerAndRedactorTests|FullyQualifiedName~P0StorageAndRedactionRegressionTests|FullyQualifiedName~AccountManagerBehaviorTests|FullyQualifiedName~P1StorageRegressionTests"
  ```
- [ ] **Step 3: Implement minimal fixes**
  - Edit `src/Shiro/Services/Storage/LogRedactor.cs`:
    - Update `MayContainSensitiveData` to recognize `"webhooks/"` and index-0 secret keys (e.g. `"token="`, `"password="`, `"secret="`, etc.) efficiently without allocations.
    - Add Discord Webhook `[GeneratedRegex]` (`https://(?:discord|discordapp)\.com/api/webhooks/\d+/[A-Za-z0-9_\-]+`), update `.ROBLOSECURITY=` regex to capture optional surrounding quotes (`"?([^";\r\n\s]+)"?` or preserve quote framing cleanly), and update key-value query/form regex from `[?&]` to `(?:^|[?&])`.
  - Edit `src/Shiro/Services/Storage/IniFile.cs`:
    - In `Set(string name, string value)`: if `value is null`, remove or treat safely (`value ?? string.Empty`); when `value` is `""` (empty string), store `string.Empty` in `_properties[name]` instead of calling `_properties.Remove(name)` (also ensure save writes `Key=` cleanly and load parses `Key=` as `string.Empty`).
    - Pass `new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true }` at `line 301`.
- [ ] **Step 4: Run Storage/Redactor/Ini tests & ArchitectureTests to verify Green pass**
  Run:
  ```powershell
  dotnet test Shiro.sln --no-restore --nologo --filter "FullyQualifiedName~LoggerAndRedactorTests|FullyQualifiedName~P0StorageAndRedactionRegressionTests|FullyQualifiedName~AccountManagerBehaviorTests|FullyQualifiedName~P1StorageRegressionTests|FullyQualifiedName~ArchitectureTests"
  ```
- [ ] **Step 5: Commit**
  ```powershell
  git add src/Shiro/Services/Storage/LogRedactor.cs src/Shiro/Services/Storage/IniFile.cs tests/Shiro.Tests/LoggerAndRedactorTests.cs tests/Shiro.Tests/P0StorageAndRedactionRegressionTests.cs
  git commit -m "fix(storage): close LogRedactor webhook/cookie/form gaps and preserve empty IniFile settings"
  ```

---

## Self-Review & Execution Gate
- `AccountPersistenceService.cs` stays `<= 485` lines (`>= 67%` branch floor) across Tasks 1-2.
- Check existing callers of `IniFile.Set(..., "")` across `AccountManagerBehaviorTests` to confirm none relied on key deletion.
