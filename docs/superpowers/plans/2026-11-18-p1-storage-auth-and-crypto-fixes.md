# P1 Storage Recovery, Crypto, Auth, HBA & FastLogin Fixes Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.
> **Synced against live working tree (`107a1d7` + working edits):** Builds directly on existing WT changes (`Account` `2,182/2,300`, `FastLoginDialog` **`1,148/1,150` — ONLY 2 lines headroom**, `LogRedactor` header/webhook additions, `RobloxRateLimitCoordinator` `_categoryBackoffs` + retry `while (true)`). Already-completed sub-items in WT are marked `[x]`.

**Goal:** Eliminate remaining P1 vault recovery/hydration/pruning flaws, portable-mode DPAPI secret loss, unzeroed plaintext secrets & UI-blocking/unbounded vault export/import, `FastLogin` locale-coupled DOM parsing & dialog races, HBA stream-rewind/SPKI mismatches, `SessionAuditor` locking/dirty/pacing gaps, `RestClient` POST+query drops, `Presence` proxy gaps, stale pool-removed proxies, and `RobloxRateLimitCoordinator` proxy scoping / alert lock contention.

**Architecture:** Extract static domain logic cleanly outside tight partial budgets (**critical**: `FastLoginDialog` has only `2` lines headroom across 4 files, so new helper code goes into `FastLoginCoordinator*.cs` / `FastLoginScripts.cs`).
- **Policy 1 Decision (`FastLoginCoordinator`):** Completely decouple `ClassifyDomIssueCode` from `LocalizationService.Get("FastLogin.*Keyword")`; match invariant English DOM tokens + `ContainsExactNumber` (`FastLoginCoordinator.Classification.cs:204`), while keeping `MapDomIssueCode` localized for UI badges.
- **Conflict #2 Ruling (`ProxyBindingService`):** Preserve manual per-account proxies inside `DistributeProxiesSticky(accounts, proxies)` so `tests/Shiro.Tests/P1NetworkRegressionTests.cs:394-398` (`ManualProxy = "127.0.0.99:9999"`) remains intact; instead add `RebalanceRemovedPoolProxies(...)` (plus opt-in `reassignMissingFromPool = false`) invoked when proxies are removed from `proxies.txt`.

**Tech Stack:** .NET 10 (`win-x64`), C# 14, AES-256-GCM / DPAPI / PBKDF2-SHA256, ECDSA P-256 HBA, `System.Net.Http`, `System.Text.Json`, xUnit, NSubstitute.

**Spec:** System Audit Report (`C:/Users/lenovo/.gemini/antigravity/brain/d3d9db7e-c20c-4d8a-b056-391721b18014/system-audit-report.md`) — Sections 2.2 (#10–#11), 3.3 (#13–#16), 3.4 (#18, #20–#21), 3.5 (#23, #25), 3.6 (#29, #31), 4.

---

## Global Constraints

- `TreatWarningsAsErrors=true`, `<Nullable>enable</Nullable>`, strict ASCII C# comments & logs.
- Budgets (`ArchitectureTests.Budgets.cs`): single `.cs <= 485`; partials: `Account <= 2300` (`2182/2300`), `RobloxHbaService <= 1150` (`1110/1150`), `FastLoginDialog <= 1150` (`1148/1150`), `AccountPersistenceService <= 1150` (`821/1150`), `SessionAuditorService <= 1150` (`535/1150`); tests `<= 550` (`P1NetworkRegressionTests.cs` is `486/550` — add overflow Task 4 tests to `P1AuditRemediationTests.cs` / `P1AuthAndDomainRegressionTests.cs`!).
- Coverage floors (`tests/coverage-floors.json`): `Services/Storage/AccountPersistenceService.cs >= 67%` branch, plus `92.0%` Core line gate in `scripts/Check-Coverage.ps1`.

---

### Task 1: Fix Vault Recovery Fail-Closed Persistence, DPAPI Upgrade, Backup Pruning & `Account` Fields

**Files:**
- Done in WT: `src/Shiro/Services/Storage/AccountPersistenceService.Recovery.cs:86-123` (sorts candidates by `LastWriteUtc > Priority` and decrypts via `GetOrCreateVaultKeyCopy()`).
- Modify: `src/Shiro/Services/Storage/StoragePaths.VaultKey.cs:205-241`
- Modify: `src/Shiro/Services/Storage/AccountPersistenceService.Recovery.cs:166-245`
- Modify: `src/Shiro/Services/Storage/AccountFileDecoder.cs:165-177`
- Modify: `src/Shiro/Services/Storage/AccountStore.cs:98-134` (`PruneHistoricalBackups`)
- Modify: `src/Shiro/Domain/Account.cs:57-64,106-117,185-225` (`470/485`) & `.HbaBinding.cs:27-40`
- Test: `tests/Shiro.Tests/P1StorageRegressionTests.cs`, `tests/Shiro.Tests/AccountStoreBackupTests.cs:139`

**Interfaces:**
- **Modified behavior:** `StoragePaths.SaveRecoveredVaultKey(byte[] key)` zero-memories `copy` and rethrows without publishing `_cachedVaultKey` when disk write fails.
- **Modified behavior:** `AccountPersistenceService.PromoteBackupToPrimary(string saveFilePath, DateTime now)` verifies `File.Exists(backupPath)` BEFORE moving primary out of the way.
- **Modified behavior:** `Account.GetSerializedFieldsSnapshot()` excludes transient `"Window_Position_X"`, `"Window_Position_Y"`, `"Window_Width"`, and `"Window_Height"` keys (already persisted via `AccountRuntimeStateStore`).

- [x] Backup candidate sort by `LastWriteUtc > Priority` — *Verified Done in WT*
- [ ] **Step 1: Write failing tests in `P1StorageRegressionTests.cs` & `AccountStoreBackupTests.cs` (TDD Red)**
  ```csharp
  [Fact]
  public void SaveRecoveredVaultKey_WhenWriteFails_DoesNotUpdateInMemoryCache()
  {
      byte[] originalKey = StoragePaths.GetOrCreateVaultKeyCopy();
      byte[] replacement = new byte[32];
      Array.Fill(replacement, (byte)0x5A);
      using var lockHandle = new FileStream(StoragePaths.VaultKeyFile, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
      Assert.Throws<IOException>(() => StoragePaths.SaveRecoveredVaultKey(replacement));
      Assert.Equal(originalKey, StoragePaths.GetOrCreateVaultKeyCopy());
  }

  [Fact]
  public void PromoteBackupToPrimary_WhenBackupMissing_LeavesPrimaryIntact()
  {
      string primary = Path.Combine(_testDir, "AccountData.json");
      File.WriteAllText(primary, "{\"ok\":1}");
      string preserved = svc.PromoteBackupToPrimary(primary, DateTime.UtcNow);
      Assert.Equal(string.Empty, preserved);
      Assert.Equal("{\"ok\":1}", File.ReadAllText(primary));
  }

  [Fact]
  public void Account_WindowFieldsExcludedFromJsonSnapshot_AndNullGroupDoesNotDirty()
  {
      var acc = new Account { Username = "u", Group = "Default" };
      acc.IsDirty = false;
      acc.Group = null!;
      Assert.Equal("Default", acc.Group);
      Assert.False(acc.IsDirty);
      acc.SetWindowBounds(10, 20, 800, 600);
      var snapshot = acc.GetSerializedFieldsSnapshot();
      Assert.DoesNotContain("Window_Position_X", snapshot.Keys);
  }
  ```
  Plus failing tests for `AccountFileDecoder` setting `NeedsMigration = true` on `CurrentUser` DPAPI (`!legacyScope`), `TryRestoreReadableBackup` calling `VaultKeyRecoveryService.TryRecoverInProcess` / `AccountRuntimeStateStore.HydrateAccounts`, and `PruneHistoricalBackups` not evicting the newest smaller snapshot before older larger snapshots.
- [ ] **Step 2: Run targeted tests expecting Red**
  `dotnet test Shiro.sln -c Release --nologo --filter "FullyQualifiedName~P1StorageRegressionTests|FullyQualifiedName~AccountStoreBackupTests"`
- [ ] **Step 3: Implement storage & domain fixes**
  - `StoragePaths.VaultKey.cs:205-241`: Zero `copy` in `catch` and rethrow so `_cachedVaultKey` never diverges from disk.
  - `AccountPersistenceService.Recovery.cs`: Check `File.Exists(backupPath)` first inside `PromoteBackupToPrimary`; attempt `VaultKeyRecoveryService.TryRecoverInProcess` when `Cryptography.IsEncrypted(raw)` fails; hydrate recovered accounts via `AccountRuntimeStateStore.HydrateAccounts`.
  - `AccountFileDecoder.cs:175`: Set `bool needsMigration = true;` whenever `UnprotectDpapi` succeeds (upgrades `CurrentUser` DPAPI vaults to `V4` AES-GCM).
  - `AccountStore.cs:113-128`: Preserve newest backups first and avoid evicting newer valid backups before older ones (`MaxHistoricalBackups`).
  - `Account.cs:108-115`: Normalize `string nextGroup = value ?? "Default";` BEFORE comparing `_Group`; filter `"Window_*"` keys from `GetSerializedFieldsSnapshot()` and log debug truncation warnings in `Alias`/`Description`/`Password`/`SetField` when oversized values are trimmed/ignored.
- [ ] **Step 4: Run targeted tests + `AccountPersistenceService >= 67%` + `ArchitectureTests`**
  `dotnet test Shiro.sln -c Release --nologo --filter "FullyQualifiedName~P1StorageRegressionTests|FullyQualifiedName~AccountStoreBackupTests|FullyQualifiedName~ArchitectureTests"`
- [ ] **Step 5: Stage & commit Task 1**
  `git add src/Shiro/Services/Storage/StoragePaths.VaultKey.cs src/Shiro/Services/Storage/AccountPersistenceService.Recovery.cs src/Shiro/Services/Storage/AccountFileDecoder.cs src/Shiro/Services/Storage/AccountStore.cs src/Shiro/Domain/Account*.cs tests/Shiro.Tests/P1StorageRegressionTests.cs tests/Shiro.Tests/AccountStoreBackupTests.cs`
  `git commit -m "fix(storage): fail-closed vault key caching, DPAPI v4 migration, and Account field hygiene"`

---

### Task 2: Fix `CryptoHelper` Memory Zeroing & Portable Secret Encryption, Bounded/Async Vault Export & Telegram Redaction

**Files:**
- Done in WT: `src/Shiro/Services/Storage/LogRedactor.cs` (`146/485`) already redacts `x-csrf-token`, `rbx-authentication-ticket`, `Authorization: Basic`, versioned Discord webhooks, and mid-line form parameters (`P0AuditRemediationTests.cs:340`).
- Modify: `src/Shiro/Services/Storage/CryptoHelper.cs:64-147`
- Modify: `src/Shiro/Services/Storage/VaultExportService.cs:120-135`
- Modify: `src/Shiro/Services/Ui/ISettingsSideEffects.cs:164-166,219-221`
- Modify: `src/Shiro/Services/Storage/LogRedactor.cs:42`
- Test: `tests/Shiro.Tests/VaultExportTests.cs`, `tests/Shiro.Tests/LoggerAndRedactorTests.cs`, `tests/Shiro.Tests/P1StorageRegressionTests.cs`

**Interfaces:**
- **Modified:** `CryptoHelper.ProtectSecretString(string plainText)` / `UnprotectSecretString(string cipherText)` adds portable `"vault:"` AES-256-GCM envelope using `StoragePaths.GetOrCreateVaultKeyCopy()` when `StoragePaths.IsPortable` is true, and zeroes `hashA`/`hashB`/`a`/`b`/`enc` temporary byte arrays inside `finally`.
- **Modified:** `VaultExportService.ImportVault(Stream source, string passphrase)` enforces `MaxEncryptedVaultBytes = 32 * 1024 * 1024` across BOTH seekable and non-seekable streams before buffering.

- [x] Extended `LogRedactor` CSRF/ticket/BasicAuth/versioned-webhook/mid-line redactions — *Verified Done in WT*
- [ ] **Step 1: Write failing tests in `VaultExportTests.cs`, `LoggerAndRedactorTests.cs` & `P1StorageRegressionTests.cs` (TDD Red)**
  ```csharp
  [Fact]
  public void VaultExportService_NonSeekableOversizedStream_ThrowsInvalidDataException()
  {
      byte[] validHeader = VaultExportService.BuildTestHeaderBytes();
      using var stream = new OversizedNonSeekableReadStream(validHeader, payloadBytes: (32 * 1024 * 1024) + 1024);
      Assert.Throws<InvalidDataException>(() => VaultExportService.ImportVault(stream, "StrongPass123!"));
  }

  [Fact]
  public void LogRedactor_StandaloneTelegramTokenWithoutBotPrefix_IsRedacted()
  {
      string raw = "token=123456789:AAHdqTcvCH1vGWJxfSeofSAs0K5PALDsaw!";
      string redacted = LogRedactor.Redact(raw);
      Assert.DoesNotContain("AAHdqTcvCH1vGWJxfSeofSAs0K5PALDsaw", redacted);
  }
  ```
  Plus a portable-mode `CryptoHelper.ProtectSecretString`/`UnprotectSecretString` roundtrip test verifying `"vault:"` prefix when `StoragePaths.IsPortable == true` and legacy `"dpapi:"` backward compatibility.
- [ ] **Step 2: Run targeted tests expecting Red**
  `dotnet test Shiro.sln -c Release --nologo --filter "FullyQualifiedName~VaultExportTests|FullyQualifiedName~LoggerAndRedactorTests|FullyQualifiedName~P1StorageRegressionTests"`
- [ ] **Step 3: Implement crypto, vault export & redactor fixes**
  - `CryptoHelper.cs:64-147`: Zero `a`/`b`/`bufA`/`bufB`/`digestA`/`digestB`/`enc` inside `finally`; encrypt with `"vault:"` AES-256-GCM when `StoragePaths.IsPortable`, decrypt both `"vault:"` and `"dpapi:"` cleanly.
  - `VaultExportService.cs:120-135`: Enforce `32 MB` ceiling while reading chunks from non-seekable streams AND seekable streams (`remaining > MaxEncryptedVaultBytes`).
  - `ISettingsSideEffects.cs:165,220`: Offload `VaultExportService.ExportVault` / `ImportVault` PBKDF2 work to `await Task.Run(...)` so UI stays responsive.
  - `LogRedactor.cs:42`: Extend `TelegramBotUrlRegex` (or add standalone `\b\d{8,12}:[A-Za-z0-9_-]{35}\b` redaction alongside `/bot` prefix) and check `':'` trigger char.
- [ ] **Step 4: Run targeted tests + `ArchitectureTests`**
  `dotnet test Shiro.sln -c Release --nologo --filter "FullyQualifiedName~VaultExportTests|FullyQualifiedName~LoggerAndRedactorTests|FullyQualifiedName~P1StorageRegressionTests|FullyQualifiedName~ArchitectureTests"`
- [ ] **Step 5: Stage & commit Task 2**
  `git add src/Shiro/Services/Storage/CryptoHelper.cs src/Shiro/Services/Storage/VaultExportService.cs src/Shiro/Services/Ui/ISettingsSideEffects.cs src/Shiro/Services/Storage/LogRedactor.cs tests/Shiro.Tests/VaultExportTests.cs tests/Shiro.Tests/LoggerAndRedactorTests.cs tests/Shiro.Tests/P1StorageRegressionTests.cs`
  `git commit -m "fix(crypto): portable vault secret encryption, buffer zeroing, bounded vault import, and Telegram redaction"`

---

### Task 3: Decouple `FastLoginCoordinator` DOM Classifier (Policy 1), Fix `FastLoginDialog` Races & Harden HBA Binding

**Files:**
- Done in WT: `src/Shiro/AvaloniaUI/Views/FastLoginDialog.SessionCapture.cs:397,401,405` (`_isClosed` checks in mismatch reset block; `FastLoginDialog` total now **`1,148/1,150`**).
- Modify: `src/Shiro/Services/Auth/FastLoginCoordinator.cs:98-140` & `.Classification.cs` (remove ALL `LocalizationService.Get("FastLogin.*Keyword")` lookups from `ClassifyDomIssueCode`)
- Modify: `src/Shiro/AvaloniaUI/Views/FastLoginDialog.axaml.cs:156-185`, `FastLoginDialog.SessionCapture.cs:158-174`, `FastLoginDialog.Automation.cs:135-165` (**extract shared profile/reset coordination into `FastLoginCoordinator.cs` / `FastLoginScripts.cs` so `FastLoginDialog` total stays `<= 1,150`!**)
- Modify: `src/Shiro/Services/Launch/BrowserService.cs:61` (change `SignInGate` from `SemaphoreSlim(2, 2)` to `SemaphoreSlim(1, 1)`)
- Modify: `src/Shiro/Services/Auth/RobloxHbaService.cs:160-185` (`393/485`), `.Binding.cs:54-88`, & `src/Shiro/Domain/Account.HbaBinding.cs:173-180`
- Test: `tests/Shiro.Tests/FastLoginCoordinatorBehaviorTests.cs`, `FastLoginCoordinatorTests.cs`, `P1AuthAndDomainRegressionTests.cs`

**Interfaces:**
- **Modified behavior (Policy 1):** `FastLoginCoordinator.ClassifyDomIssueCode(string? combinedText, bool isModUrl)` matches lowercase invariant English DOM tokens via `ContainsExactNumber` (`FastLoginCoordinator.Classification.cs:204`) regardless of active UI `LocalizationService.CurrentLanguage` (e.g., `"vi"`), preventing `"21 days"` from colliding with `"1 day"`.
- **Modified:** `RobloxHbaService.TryAttachHbaHeader` seeks seekable `Stream` back to `0` (`CanSeek`) and returns `false` on unreadable stream instead of signing an empty body; stale `HbaPublicKey` / `PublicKeySpkiBase64` is cleared whenever a new `HbaPrivateKey` replaces it without a matching public key.

- [x] Partial `_isClosed` guards in `UsernameMismatch` reset (`FastLoginDialog.SessionCapture.cs:397-405`) — *Verified Done in WT*
- [ ] **Step 1: Write failing tests in `FastLoginCoordinatorBehaviorTests.cs` & `P1AuthAndDomainRegressionTests.cs` (TDD Red)**
  ```csharp
  [Fact]
  public void ClassifyDomIssueCode_WhenUiIsVietnamese_MatchesEnglishDomWithoutSubstringCollisions()
  {
      Assert.Equal("BAN_1_DAY", FastLoginCoordinator.ClassifyDomIssueCode("Banned for 1 day", isModUrl: true));
      Assert.NotEqual("BAN_1_DAY", FastLoginCoordinator.ClassifyDomIssueCode("Banned for 21 days", isModUrl: true));
      Assert.Equal("WRONG_PASSWORD", FastLoginCoordinator.ClassifyDomIssueCode("Incorrect username or password.", isModUrl: false));
  }

  [Fact]
  public void HbaService_RewindsSeekableStreamAndClearsMismatchedSpkiOnKeyRotation()
  {
      using var ms = new MemoryStream(System.Text.Encoding.UTF8.GetBytes("{\"k\":1}"));
      ms.Position = ms.Length;
      using var req = new HttpRequestMessage(HttpMethod.Post, "https://apis.roblox.com/v1/test") { Content = new StreamContent(ms) };
      Assert.True(RobloxHbaService.TryAttachHbaHeader(req, acc));
      acc.ApplyHbaCapture(new RobloxHbaService.CaptureSnapshot { PrivateKeyPkcs8Base64 = newPrivKey, PublicKeySpkiBase64 = string.Empty });
      Assert.Equal(string.Empty, acc.HbaPublicKey);
  }
  ```
  Plus failing tests for `BrowserService.SignInGate` (`CurrentCount <= 1`), `HandleWebViewInitializedAsync` aborting post-await when `_isClosed`, and preserving `_password` across mismatch resets.
- [ ] **Step 2: Run targeted tests expecting Red**
  `dotnet test Shiro.sln -c Release --nologo --filter "FullyQualifiedName~FastLogin|FullyQualifiedName~P1AuthAndDomainRegressionTests"`
- [ ] **Step 3: Implement auth, FastLogin & HBA fixes**
  - `FastLoginCoordinator.cs:109-137`: Remove all `LocalizationService.Get(...)` calls; use `ContainsExactNumber(combined, 14/7/3/1)` for ban durations (`"14 day"`, `"7 day"`, etc.).
  - `FastLoginDialog.axaml.cs:158-185`: Check `if (_isClosed) return;` after `await ConfigureCoreWebViewAsync(core)` BEFORE starting `_pollTimer`; delegate heavyweight profile/session reset helpers to `FastLoginCoordinator` / `FastLoginScripts` so `FastLoginDialog <= 1150`.
  - `BrowserService.cs:61`: Change `new SemaphoreSlim(2, 2)` -> `new SemaphoreSlim(1, 1)`.
  - `RobloxHbaService.Binding.cs:59-71`, `RobloxHbaService.cs:174-183`, `Account.HbaBinding.cs:173-180`: Fail closed (`return false`) when non-seekable/unreadable `HttpContent` copy fails; rewind seekable `Stream` before copying; clear `snapshot.PublicKeySpkiBase64` / `HbaPublicKey` when `PrivateKey` changes without a matching public key.
- [ ] **Step 4: Run targeted tests + `ArchitectureTests` (verify `FastLoginDialog <= 1150`!)**
  `dotnet test Shiro.sln -c Release --nologo --filter "FullyQualifiedName~FastLogin|FullyQualifiedName~P1AuthAndDomainRegressionTests|FullyQualifiedName~ArchitectureTests"`
- [ ] **Step 5: Stage & commit Task 3**
  `git add src/Shiro/Services/Auth/FastLogin*.cs src/Shiro/AvaloniaUI/Views/FastLoginDialog*.cs src/Shiro/Services/Launch/BrowserService.cs src/Shiro/Services/Auth/RobloxHbaService*.cs src/Shiro/Domain/Account.HbaBinding.cs tests/Shiro.Tests/FastLogin*.cs tests/Shiro.Tests/P1AuthAndDomainRegressionTests.cs`
  `git commit -m "fix(auth): decouple FastLogin DOM classifier from locale, harden dialog lifecycle, and fix HBA SPKI/stream handling"`

---

### Task 4: Harden `SessionAuditorService`, `RestClient` Query/Body Split, Per-Account `Presence`, `ProxyBindingService` & `RobloxRateLimitCoordinator`

**Files:**
- Done in WT: `src/Shiro/Services/Network/RobloxRateLimitCoordinator.cs:248-271` already uses `_categoryBackoffs` and `while (true)` wait re-check (`P0AuditRemediationTests.cs:402-421`).
- Modify: `src/Shiro/Services/Auth/SessionAuditorService.cs:88-348` (`535/1150`) & `src/Shiro/Domain/Account.cs:77` (synchronize audit updates via `account._sessionGate` + rate-limit 429 notification + dirty sync)
- Modify: `src/Shiro/Http/RestClient.cs:87,116-128,203-221,258-296,411-445` (`468/485`) & `src/Shiro/Domain/Account.HbaBinding.cs:285` (separate `QueryParameters` from form `Parameters` + add `clientOverride` overload to `ExecuteJsonAsync<T>`)
- Modify: `src/Shiro/Services/Network/Presence.cs:75-100,158-237` (group batches by proxy client key across `UpdatePresence`/`GetPresence`/`PopulateGameNames`, routing proxied calls via `ExecuteJsonAsync(..., clientOverride, ct)`)
- Modify: `src/Shiro/Services/Network/ProxyBindingService.cs:251-302` (`414/485` — preserve manual proxies in `DistributeProxiesSticky`; add `RebalanceRemovedPoolProxies`) & wire from `ProxyManagerService` on pool deletion
- Modify: `src/Shiro/Services/Network/RobloxRateLimitCoordinator.cs:185-190` (`339/485` — add `proxyScope` to key + call `WebhookNotificationService.EnqueueAlert` OUTSIDE `_backoffLock`) & `src/Shiro/Services/Network/SharedHttpClient.cs:157-162` (`326/485` — notify `RobloxRateLimitCoordinator.Instance.RecordRateLimitHit` on 429)
- Test (keep `P1NetworkRegressionTests.cs <= 550` — add overflow tests to `P1AuditRemediationTests.cs` / `P1AuthAndDomainRegressionTests.cs`): `tests/Shiro.Tests/P1AuthAndDomainRegressionTests.cs`, `P1AuditRemediationTests.cs`, `RobloxRateLimitCoordinatorTests.cs`, `ProxyBindingServiceTests.cs`, `SessionAuditorTests.cs`

**Interfaces:**
- **Added on `RestRequest` (`RestClient.cs`):** `public List<KeyValuePair<string, string>> QueryParameters { get; } = new();` (`AddQueryParameter` writes to `QueryParameters`; `AddParameter` writes to `Parameters`).
- **Added on `RestClient`:** `public Task<(bool Success, T? Data, HttpStatusCode StatusCode, string? ErrorMessage)> ExecuteJsonAsync<T>(RestRequest request, JsonTypeInfo<T> jsonTypeInfo, HttpClient? clientOverride, CancellationToken cancellationToken = default)`.
- **Added on `ProxyBindingService`:** `public static int RebalanceRemovedPoolProxies(IEnumerable<Account> accounts, IReadOnlyCollection<string> removedProxies, IEnumerable<string> activeProxies)`.

- [x] `RobloxRateLimitCoordinator` per-category `_categoryBackoffs` + `while (true)` wait loop — *Verified Done in WT*
- [ ] **Step 1: Write failing tests preserving `P1NetworkRegressionTests.cs:394-398` manual proxy (TDD Red)**
  ```csharp
  [Fact]
  public void RestClient_PostWithQueryAndJsonBody_PreservesQueryStringAndMatchesHbaUri()
  {
      var req = new RestRequest("v1/items", Method.Post)
          .AddQueryParameter("cursor", "abc")
          .AddJsonBody(new { ok = true });
      using var msg = new RestClient("https://apis.roblox.com/").CreateHttpRequestMessage(req);
      Assert.Contains("cursor=abc", msg.RequestUri!.Query);
      Assert.NotNull(msg.Content);
  }

  [Fact]
  public void ProxyBindingService_RebalanceRemovedPoolProxies_ReassignsRemovedButKeepsManualProxy()
  {
      var manual = new Account { Username = "Manual", Proxy = "127.0.0.99:9999" };
      var removed = new Account { Username = "PoolOld", Proxy = "10.0.0.1:8080" };
      int changed = ProxyBindingService.RebalanceRemovedPoolProxies(
          new[] { manual, removed },
          new[] { "10.0.0.1:8080" },
          new[] { "10.0.0.2:8080" });
      Assert.Equal(1, changed);
      Assert.Equal("127.0.0.99:9999", manual.Proxy);
      Assert.Equal("10.0.0.2:8080", removed.Proxy);
  }
  ```
  Plus failing tests for `SessionAuditorService` pacing/locking, `Presence` grouping users by proxy identity, `RobloxRateLimitCoordinator` proxy-keyed backoff + unlocked `EnqueueAlert`, and `SharedHttpClient` reporting HTTP 429.
- [ ] **Step 2: Run targeted tests expecting Red**
  `dotnet test Shiro.sln -c Release --nologo --filter "FullyQualifiedName~P1AuthAndDomainRegressionTests|FullyQualifiedName~P1NetworkRegressionTests|FullyQualifiedName~RobloxRateLimitCoordinatorTests|FullyQualifiedName~ProxyBindingServiceTests|FullyQualifiedName~SessionAuditorTests"`
- [ ] **Step 3: Implement storage/auth/network hardening fixes**
  - `SessionAuditorService.cs` & `Account.cs`: Acquire `account` session gate around audit mutation, notify `RobloxRateLimitCoordinator` on 429, enqueue deferred save when validity/balance changes.
  - `RestClient.cs` & `Account.HbaBinding.cs`: Append `request.QueryParameters` unconditionally to `finalUri` (all HTTP methods, including `POST`/`PUT`/`PATCH`) and mirror identically inside `ApplySessionFingerprintAndHbaToRestRequest`.
  - `Presence.cs`: Partition `UniqueUserIds` batches by effective proxy string (`Direct` vs proxy endpoint) so proxied accounts never leak over direct IP or each other's proxies; route via `ExecuteJsonAsync(..., clientOverride, ct)`.
  - `ProxyBindingService.cs`: Keep `DistributeProxiesSticky` manual-preserving (`P1NetworkRegressionTests:394-398`); add `RebalanceRemovedPoolProxies` and invoke when proxies are deleted.
  - `RobloxRateLimitCoordinator.cs` & `SharedHttpClient.cs`: Partition backoff by `(RateLimitCategory, proxyScope)`, move `EnqueueAlert` outside `lock (_backoffLock)`, and notify coordinator inside `SharedHttpClient.SendAsync` when `StatusCode == 429`.
- [ ] **Step 4: Run Release `-warnaserror` build + targeted suites + `ArchitectureTests` + `Check-Coverage.ps1`**
  ```powershell
  dotnet build Shiro.sln --configuration Release --nologo -warnaserror
  dotnet test Shiro.sln --configuration Release --no-build --nologo --filter "FullyQualifiedName~P1|FullyQualifiedName~Storage|FullyQualifiedName~Auth|FullyQualifiedName~Network|FullyQualifiedName~ArchitectureTests"
  powershell.exe -NoProfile -ExecutionPolicy Bypass -File .\scripts\Check-Coverage.ps1
  ```
  Expected: `0 Warning(s)`, `0 Error(s)`, `AccountPersistenceService.cs >= 67%` branch, Core `>= 92.0%`.
- [ ] **Step 5: Stage & commit Task 4**
  `git add src/Shiro/Services/Auth/SessionAuditorService.cs src/Shiro/Domain/Account*.cs src/Shiro/Http/RestClient.cs src/Shiro/Services/Network/Presence.cs src/Shiro/Services/Network/ProxyBindingService.cs src/Shiro/Services/Network/RobloxRateLimitCoordinator.cs src/Shiro/Services/Network/SharedHttpClient.cs tests/Shiro.Tests/P1AuthAndDomainRegressionTests.cs tests/Shiro.Tests/P1AuditRemediationTests.cs tests/Shiro.Tests/RobloxRateLimitCoordinatorTests.cs tests/Shiro.Tests/ProxyBindingServiceTests.cs tests/Shiro.Tests/SessionAuditorTests.cs`
  `git commit -m "fix(network): split RestRequest query/body params, isolate Presence proxies, and rebalance removed pool proxies"`

---

## Execution Handoff

Plan complete. Recommend committing or snapshotting the verified 96-file WT baseline first, then executing Wave P1 sequentially with either:
1. **Subagent-driven execution** (`superpowers:subagent-driven-development`) *(recommended)*
2. **Inline execution** (`superpowers:executing-plans`)
