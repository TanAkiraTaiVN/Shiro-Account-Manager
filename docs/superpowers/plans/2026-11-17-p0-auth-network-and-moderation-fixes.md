> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

# P0 Auth, Session Auditor, Proxy Leak, Commerce & Moderation Fixes

**Goal:** Eliminate cookie-loss and stale-selection bugs in FastLogin/Account creation, harden `AccountStatusIssue` & HBA key clearing, fix session auditor identity/HTML/JSON/cancel gaps, stop 429 double-counting and proxy IP leaks in `GetNoRedirectClient`, fix resale `SellerId` purchase targeting and `WebAuthPolicy` dictionary eviction, and un-scramble `Moderation.*` localization/HTML parsing.

**Architecture:** Fix each authentication, HTTP pipeline, and parser defect behind deterministic HTTP mock factories (`Account.TestHttpClientFactory`, `SessionAuditorService.TestHttpClientFactory`, `FakeHttpHandler`) with strict partial-file line budgets (`SessionAuditorService.cs` `469/485`, `FastLoginDialog.SessionCapture.cs` `452/485`, `RobloxHbaService` partials `1098/1150`) and preserved coverage floors.

**Tech Stack:** .NET 10 (`net10.0-windows10.0.19041.0`), `System.Net.Http`, `RestSharp`, `System.Text.Json`, `System.Security.Cryptography`, `xUnit`.

**Spec:** System Audit Report (`C:/Users/lenovo/.gemini/antigravity/brain/d3d9db7e-c20c-4d8a-b056-391721b18014/system-audit-report.md`) - Section 2 (P0.4, P0.5 partial) & Section 3.2/3.5.

**Global Constraints:**
- `TreatWarningsAsErrors=true`, `<Nullable>enable</Nullable>`, strict ASCII English in ALL C# comments, docstrings, and logs.
- Source files `<= 485` lines (`RobloxHbaService` class total `<= 1150` lines); test files `<= 550` lines.
- No empty or untyped `catch` blocks; never place `{` or `}` inline in `[Fact]`/`[Theory]` string literals (hoist JSON fixtures to `private static readonly`).
- `en.json` and `vi.json` keys must remain 100% 1:1 symmetric (`LocalizationServiceTests`).
- Preserve branch floor in `tests/coverage-floors.json`: `Services/Network/RobloxModerationService.cs >= 67%`.
- Stage only explicit files per commit (never `git add -A`).

**Review Focus:**
- **Task 1:** Verify `FastLogin` retry path does not delete live sibling cookies or poison `pendingCapturedFp`, `Account.CreateAsync` only sets `LastValidAccount` AFTER username mismatch check passes, `"AccountStatusIssue"` is included in `IsProtectedInternalField`, and `Account.ClearSensitiveMemory()` evicts and zeros the matching HBA `ECParameters` cache entry cleanly.
- **Task 2:** Verify `SessionAuditorService` detects user ID mismatch, HTML `200 OK` challenges, empty `{}` friends payload, and caller `OperationCanceledException`; verify `Account.Api` increments HTTP 429 backoff once (not twice); verify `CookieRotation` detects `; max-age=0` deletion cookies and enforces cookie name boundary before `.ROBLOSECURITY=`.
- **Task 3:** Verify `ProxyBindingService.GetNoRedirectClient(Account)` honors sticky distributed proxies (stopping real IP leak while preserving shared client when distribution is off), `AssetDetailsParser` adopts `lowestAvailableResaleSellerId` alongside `lowestAvailableResaleProductId`, and `WebAuthPolicy` bounds non-locked `ClientFailures` entries above 512.
- **Task 4:** Verify `en.json`/`vi.json` `Moderation.*` keys match semantic meanings with 1:1 parity, `ParseModerationText` uses canonical English markers instead of `LocalizationService.Get(...)`, and standalone `"warning"` without `hasModerationMarker` returns not-banned.

---

### Task 1: Fix `FastLogin` Cookie Capture Retry, `LastValidAccount` Mismatch Leak, `AccountStatusIssue` Protection & HBA Cache Clear

**Context:**
- `FastLoginScripts.cs:190` & `FastLoginDialog.SessionCapture.cs:223,230,354,367,421`: `ResolveSettledCookieAsync` adds `fp` to `_ignoredCookieValues`, line `367` clears `pendingCapturedFp = null`, and on `RetryPolling` line `223` deletes WebView cookies indiscriminately. (`FastLoginDialog.SessionCapture.cs` is 452/485 lines; avoid exceeding 485 lines or delegate cookie settlement state to `FastLoginCoordinator`.)
- `Account.cs:360,367` (`CreateAsync`): sets `AppServices.SelectionState.LastValidAccount = account` BEFORE verifying username mismatch, leaving an invalid/rejected account as `LastValidAccount`.
- `Account.HbaBinding.cs:26-38`: `IsProtectedInternalField` omits `"AccountStatusIssue"` (used by `Account.cs:67-70`).
- `RobloxHbaService.cs:328-354` & `Account.SessionState.CookieRotation.cs:269`: `Account.ClearSensitiveMemory()` does not remove/zero cached `ECParameters.D` in `RobloxHbaService.CachedEcParameters` for that account's key/token. (`RobloxHbaService` partial class sum is ~1098/1150, so keep added eviction method concise `<= 15` lines.)

**Files:**
- Modify: `src/Shiro/AvaloniaUI/Views/FastLoginDialog.SessionCapture.cs`
- Modify: `src/Shiro/Services/Auth/FastLoginScripts.cs`
- Modify: `src/Shiro/Domain/Account.cs`
- Modify: `src/Shiro/Domain/Account.HbaBinding.cs`
- Modify: `src/Shiro/Domain/Account.SessionState.CookieRotation.cs`
- Modify: `src/Shiro/Services/Auth/RobloxHbaService.cs`
- Create: `tests/Shiro.Tests/P0AuthNetworkAndModerationTests.cs`

**Interfaces:**
- **Modified:** `FastLoginScripts.ResolveSettledCookieResult` / `ResolveSettledCookieAsync` retry decision helper (keeps captured cookie eligible on retry rather than permanently ignoring it and skips blind `DeleteCookie` on retry polling).
- **Modified:** `Account.CreateAsync(...)` ordering (`LastValidAccount` written only after mismatch check).
- **Modified:** `Account.HbaBinding.IsProtectedInternalField(string fieldName)` returning `true` for `"AccountStatusIssue"`.
- **Added:** `RobloxHbaService.ClearCachedKeyForToken(string? keyOrToken)` called from `Account.ClearSensitiveMemory()`, zeroing `D` bytes and removing the entry from `CachedEcParameters`.

**Steps:**
- [ ] **Step 1: Write failing test(s) (TDD Red)**
  - Create `tests/Shiro.Tests/P0AuthNetworkAndModerationTests.cs` (`[Collection("StaticSeams")]`):
    - Test `FastLogin` settlement retry helper (`ResolveSettledCookieAsync` / pure retry helper): given a freshly captured candidate fingerprint where an initial settlement check requests `RetryPolling`, verify `fp` is NOT added permanently to `_ignoredCookieValues` and `pendingCapturedFp` remains recoverable on retry!
    - Test `Account.CreateAsync`: when `ExpectedUsername` mismatches authenticated username, preserve previous `AppServices.SelectionState.LastValidAccount` instead of overwriting it with the rejected account!
    - Test `Account.HbaBinding.IsProtectedInternalField("AccountStatusIssue")` returns `true`, and `Account.ClearSensitiveMemory()` removes & zeros cached HBA `ECParameters` for that account's token while preserving unrelated accounts' cache entries!
- [ ] **Step 2: Run targeted tests to verify expected Red failure**
  Run:
  ```powershell
  dotnet test Shiro.sln --no-restore --nologo --filter "FullyQualifiedName~P0AuthNetworkAndModerationTests|FullyQualifiedName~P1AuthAndDomainRegressionTests|FullyQualifiedName~CryptographyAndHbaOptimizationTests|FullyQualifiedName~FastLogin"
  ```
- [ ] **Step 3: Implement minimal fixes**
  - Edit `src/Shiro/AvaloniaUI/Views/FastLoginDialog.SessionCapture.cs` & `FastLoginScripts.cs`:
    - On `RetryPolling`, keep `pendingCapturedFp` intact and do NOT add it permanently to `_ignoredCookieValues`; avoid destructive cookie deletion during active retry polling (preserve clear-on-explicit-reset / different-account flow). Extract helper into `FastLoginCoordinator` / `FastLoginScripts` if needed to keep `FastLoginDialog.SessionCapture.cs <= 485` lines.
  - Edit `src/Shiro/Domain/Account.cs:360-367`: move `AppServices.SelectionState.LastValidAccount = account;` to execute ONLY after `UsernameMismatch` validation succeeds.
  - Edit `src/Shiro/Domain/Account.HbaBinding.cs:26-38`: include `string.Equals(fieldName, "AccountStatusIssue", StringComparison.Ordinal)` in `IsProtectedInternalField`.
  - Edit `src/Shiro/Services/Auth/RobloxHbaService.cs` & `src/Shiro/Domain/Account.SessionState.CookieRotation.cs`: add `internal static bool ClearCachedKey(string? hbaPrivateKey)` (accepting private key string/trimmed key used by `CachedEcParameters`), zero `ecParams.D`, remove from `CachedEcParameters`, and invoke it from `Account.ClearSensitiveMemory()` before zeroing/clearing `SecurityToken` and HBA key fields.
- [ ] **Step 4: Run Auth/HBA tests & ArchitectureTests to verify Green pass**
  Run:
  ```powershell
  dotnet test Shiro.sln --no-restore --nologo --filter "FullyQualifiedName~P0AuthNetworkAndModerationTests|FullyQualifiedName~P1AuthAndDomainRegressionTests|FullyQualifiedName~CryptographyAndHbaOptimizationTests|FullyQualifiedName~FastLogin|FullyQualifiedName~AccountPersistenceTests|FullyQualifiedName~ArchitectureTests"
  ```
  Verify `FastLoginDialog.SessionCapture.cs <= 485`, `Account.cs <= 485`, and `RobloxHbaService` total partial class lines `<= 1150`.
- [ ] **Step 5: Commit**
  ```powershell
  git add src/Shiro/AvaloniaUI/Views/FastLoginDialog.SessionCapture.cs src/Shiro/Services/Auth/FastLoginScripts.cs src/Shiro/Domain/Account.cs src/Shiro/Domain/Account.HbaBinding.cs src/Shiro/Domain/Account.SessionState.CookieRotation.cs src/Shiro/Services/Auth/RobloxHbaService.cs tests/Shiro.Tests/P0AuthNetworkAndModerationTests.cs
  git commit -m "fix(auth): fix FastLogin retry cookie preservation, LastValidAccount guard, and HBA key eviction"
  ```

---

### Task 2: Fix `SessionAuditorService`, `Account.Api` 429 Double-Count & `CookieRotation` Max-Age=0

**Context:**
- `SessionAuditorService.cs:257-279,444-463` (469/485 lines): skips `authUser.Id != account.UserID` mismatch verification, accepts HTML `200 OK` (captive portal / login challenge HTML) when non-empty, accepts arbitrary JSON `{}` from `FastJson.TryDeserialize<RobloxFriendsCountResponse>`, and swallows `OperationCanceledException` even when caller's `cancellationToken` was explicitly cancelled.
- `Account.Api.cs:76-78,215,225`: invokes both `RecordRateLimitHit` and `MarkNativeAuthRateLimited` (which calls `RecordRateLimitHit` a second time), double-counting every HTTP 429!
- `Account.SessionState.CookieRotation.cs:60,99`: inspects `RestResponse.Cookies` (which omits `; max-age=0` deletion directives) bypassing expiry checks, and `IndexOf(".ROBLOSECURITY=")` matches bogus prefixes like `FAKE.ROBLOSECURITY=`.

**Files:**
- Modify: `src/Shiro/Services/Auth/SessionAuditorService.cs`
- Create (if needed to stay `<= 485` lines): `src/Shiro/Services/Auth/SessionAuditorService.Helpers.cs`
- Modify: `src/Shiro/Domain/Account.Api.cs`
- Modify: `src/Shiro/Domain/Account.SessionState.CookieRotation.cs`
- Modify: `tests/Shiro.Tests/P0AuthNetworkAndModerationTests.cs`

**Interfaces:**
- **Modified:** `SessionAuditorService.AuditAccountAsync(Account, CancellationToken)` (checks `authUser.Id == account.UserID` if `account.UserID > 0`, rejects HTML pages and empty `{}` JSON error payloads without explicit `count` property, rethrows `OperationCanceledException` when `cancellationToken.IsCancellationRequested`).
- **Modified:** `Account.Api` 429 handler (single rate-limit hit recording path).
- **Modified:** `Account.SessionState.CookieRotation` `Set-Cookie` scanner honoring `max-age=0` / past `expires` AND strict cookie name boundary (`^` or `;` or whitespace before `.ROBLOSECURITY=`).

**Steps:**
- [ ] **Step 1: Write failing test(s) (TDD Red)**
  - In `tests/Shiro.Tests/P0AuthNetworkAndModerationTests.cs`:
    - `SessionAuditorService` tests (using `SessionAuditorService.TestHttpClientFactory` & hoisted static readonly JSON/HTML strings):
      1. Authenticated response returns wrong `id` (`999` vs `account.UserID = 123`) -> marks mismatch/invalid rather than returning `Valid`!
      2. Authenticated endpoint returns `200 OK` HTML page -> rejected as invalid/network/challenge rather than `Valid`!
      3. Friends fallback returns `{}` without `count` -> rejected as invalid!
      4. Cancelled caller `CancellationToken` throws `OperationCanceledException` instead of swallowing it!
    - `Account.Api` 429 test: HTTP 429 increments `RobloxRateLimitCoordinator` backoff tier ONCE, not twice!
    - `CookieRotation` test: `Set-Cookie: .ROBLOSECURITY=deleted; max-age=0` does NOT overwrite cookie with deleted token (and honors `max-age=0` clearing/rejection), and `Set-Cookie: FAKE.ROBLOSECURITY=evilToken` does NOT match `.ROBLOSECURITY=`!
- [ ] **Step 2: Run targeted tests to verify expected Red failure**
  Run:
  ```powershell
  dotnet test Shiro.sln --no-restore --nologo --filter "FullyQualifiedName~P0AuthNetworkAndModerationTests|FullyQualifiedName~SessionAuditor|FullyQualifiedName~CookieRotation|FullyQualifiedName~AccountApi"
  ```
- [ ] **Step 3: Implement minimal fixes**
  - Edit `src/Shiro/Services/Auth/SessionAuditorService.cs` (and extract HTTP response validation helpers into `SessionAuditorService.Helpers.cs` if line count exceeds 475):
    - Validate `authUser.Id == account.UserID` whenever `account.UserID > 0 && authUser.Id > 0`.
    - Reject HTML payloads (`trimmed.StartsWith('<')` or `IsLoginPageHtml`) in `200 OK` responses cleanly.
    - Check explicit `"count"` property via `JsonDocument` on friends response before accepting `{}` payloads.
    - Add `catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }` BEFORE fallback catches.
  - Edit `src/Shiro/Domain/Account.Api.cs:76-78,215,225`: ensure `RecordRateLimitHit` runs once per HTTP 429 response (remove duplicate call when `MarkNativeAuthRateLimited` already triggers or vice versa).
  - Edit `src/Shiro/Domain/Account.SessionState.CookieRotation.cs:60,99`: inspect raw `Set-Cookie` header strings for `max-age=0` / negative `max-age` deletion markers BEFORE updating tokens, and validate cookie boundary (`idx == 0 || header[idx - 1] == ';' || char.IsWhiteSpace(header[idx - 1])`) when locating `.ROBLOSECURITY=`.
- [ ] **Step 4: Run Auth/Auditor tests & ArchitectureTests to verify Green pass**
  Run:
  ```powershell
  dotnet test Shiro.sln --no-restore --nologo --filter "FullyQualifiedName~P0AuthNetworkAndModerationTests|FullyQualifiedName~SessionAuditor|FullyQualifiedName~AuditSessionAuditor|FullyQualifiedName~PlayerSearchPipelineAndAuditor|FullyQualifiedName~CookieRotation|FullyQualifiedName~ValidateSession|FullyQualifiedName~ArchitectureTests"
  ```
  Verify `SessionAuditorService.cs <= 485` lines.
- [ ] **Step 5: Commit**
  ```powershell
  git add src/Shiro/Services/Auth/SessionAuditorService.cs src/Shiro/Domain/Account.Api.cs src/Shiro/Domain/Account.SessionState.CookieRotation.cs tests/Shiro.Tests/P0AuthNetworkAndModerationTests.cs
  git commit -m "fix(auth): harden SessionAuditor validation, stop 429 double-count, and guard Set-Cookie rotation"
  ```
  *(Also stage `SessionAuditorService.Helpers.cs` if created.)*

---

### Task 3: Fix `ProxyBindingService.GetNoRedirectClient` Proxy Leak, `AssetDetailsParser` Reseller SellerId & `WebAuthPolicy` Eviction

**Context:**
- `ProxyBindingService.cs:144-184`: `GetNoRedirectClient(Account?)` ignores sticky proxy pool distribution (`DistributeProxiesSticky`) when `account.Proxy` is empty (unlike `GetClient(Account)` at `lines 107-133`), leaking the host IP on no-redirect moderation/auth calls!
- `AssetDetailsParser.cs:93-101,137-160`: uses `lowestAvailableResaleProductId` when `hasResellers` is true, but ignores `lowestAvailableResaleSellerId`, leaving `SellerId = creatorId`, causing `AssetCommerceService.PurchaseAsync` to submit the wrong `expectedSellerId`.
- `WebAuthPolicy.cs:80-87`: `ClientFailures` eviction loop when `Count > 512` ignores entries with `LockedUntilUtc == DateTimeOffset.MinValue` (1-4 failures), allowing unbounded memory growth.

**Files:**
- Modify: `src/Shiro/Services/Network/ProxyBindingService.cs`
- Modify: `src/Shiro/ViewModels/Dialogs/AssetDetailsParser.cs`
- Modify: `src/Shiro/Services/Auth/WebAuthPolicy.cs`
- Modify: `tests/Shiro.Tests/ProxyBindingServiceTests.cs`
- Modify: `tests/Shiro.Tests/Dialogs/AssetDetailsParserTests.cs`
- Modify: `tests/Shiro.Tests/P0AuthNetworkAndModerationTests.cs`

**Interfaces:**
- **Extracted (private):** `ProxyBindingService.ResolveEffectiveProxy(Account? account)` shared cleanly by BOTH `GetClient(Account?)` and `GetNoRedirectClient(Account?)`.
- **Modified:** `AssetDetailsParser.Parse(...)` setting `SellerId = resaleSellerId` when `lowestAvailableResaleProductId` is non-empty and `lowestAvailableResaleSellerId > 0`.
- **Modified:** `WebAuthPolicy` tracking entry last-seen UTC (or evicting `MinValue` / oldest non-locked entries when `ClientFailures.Count > 512`).

**Steps:**
- [ ] **Step 1: Write failing test(s) (TDD Red)**
  - In `tests/Shiro.Tests/ProxyBindingServiceTests.cs` (preserving `GetNoRedirectClient_AccountNullOrNoProxy_ReturnsSharedNoRedirectClient` when distribution is disabled!):
    - Configure `DistributeProxies = true` with a proxy pool entry, pass an `Account` with empty `account.Proxy`, and assert `ProxyBindingService.GetNoRedirectClient(acc)` returns a proxied `HttpClient` matching the sticky distributed proxy instead of `SharedHttpClient.GetNoRedirect()`!
  - In `tests/Shiro.Tests/Dialogs/AssetDetailsParserTests.cs`:
    - Add a fixture with `hasResellers: true`, `lowestAvailableResaleProductId: "resale-prod-1"`, `lowestAvailableResaleSellerId: 77777`, `creatorId: 99999`, and assert `details.CollectibleProductId == "resale-prod-1"` AND `details.SellerId == 77777`!
  - In `tests/Shiro.Tests/P0AuthNetworkAndModerationTests.cs`:
    - Test `WebAuthPolicy`: record single failures for >520 distinct client keys without triggering lockouts (`LockedUntilUtc == DateTimeOffset.MinValue`), trigger cleanup threshold, and assert stale non-locked entries are bounded/evicted instead of growing unboundedly!
- [ ] **Step 2: Run targeted tests to verify expected Red failure**
  Run:
  ```powershell
  dotnet test Shiro.sln --no-restore --nologo --filter "FullyQualifiedName~ProxyBindingServiceTests|FullyQualifiedName~AssetDetailsParserTests|FullyQualifiedName~P0AuthNetworkAndModerationTests|FullyQualifiedName~WebAuthPolicy|FullyQualifiedName~P1NetworkRegressionTests"
  ```
- [ ] **Step 3: Implement minimal fixes**
  - Edit `src/Shiro/Services/Network/ProxyBindingService.cs`: extract shared `ResolveEffectiveProxy(Account? account)` logic used by `GetClient(Account?)` (`lines 107-133`) and call it inside `GetNoRedirectClient(Account?)` (`lines 144-184`), normalizing and pooling no-redirect clients identically.
  - Edit `src/Shiro/ViewModels/Dialogs/AssetDetailsParser.cs:70-101,137-160`: parse `lowestAvailableResaleSellerId` (default `0`) alongside `lowestAvailableResaleProductId`; when selecting `lowestAvailableResaleProductId`, override `SellerId` with `lowestAvailableResaleSellerId` if `> 0`.
  - Edit `src/Shiro/Services/Auth/WebAuthPolicy.cs:42,80-87`: store `LastFailureUtc` in the tuple `(int Failures, DateTimeOffset LockedUntilUtc, DateTimeOffset LastFailureUtc)` and evict non-locked entries whose `(now - LastFailureUtc) > AttemptWindow` (plus excess oldest non-locked entries if still `> 512`).
- [ ] **Step 4: Run Network/Commerce/WebAuth tests & ArchitectureTests to verify Green pass**
  Run:
  ```powershell
  dotnet test Shiro.sln --no-restore --nologo --filter "FullyQualifiedName~ProxyBindingServiceTests|FullyQualifiedName~AssetDetailsParserTests|FullyQualifiedName~P0AuthNetworkAndModerationTests|FullyQualifiedName~WebAuthPolicy|FullyQualifiedName~P1NetworkRegressionTests|FullyQualifiedName~AutoReconnectAndProxyTests|FullyQualifiedName~ArchitectureTests"
  ```
- [ ] **Step 5: Commit**
  ```powershell
  git add src/Shiro/Services/Network/ProxyBindingService.cs src/Shiro/ViewModels/Dialogs/AssetDetailsParser.cs src/Shiro/Services/Auth/WebAuthPolicy.cs tests/Shiro.Tests/ProxyBindingServiceTests.cs tests/Shiro.Tests/Dialogs/AssetDetailsParserTests.cs tests/Shiro.Tests/P0AuthNetworkAndModerationTests.cs
  git commit -m "fix(network): close no-redirect proxy leak, use reseller SellerId, and evict WebAuthPolicy failures"
  ```

---

### Task 4: Fix `Moderation.*` i18n Key Scramble & Decouple HTML Parser from Localization

**Context:**
- `src/Shiro/Resources/Strings/en.json:748-757`, `vi.json:748-757`, and `src/Shiro/Services/Network/RobloxModerationService.cs:273,305-327`:
  - The 10 `Moderation.*` keys contain completely scrambled text (`Moderation.ConnectionError` holds `"Account banned for 7 days..."`, `Moderation.CannotGetSessionForNotice` holds `"Account banned for 3 days..."`, `Moderation.WarningReceived` holds `"Account banned for 1 day..."`, `Moderation.LoadNoticeError` holds `"account locked"`/`"khóa bảo mật"`, `Moderation.ReactivateFailed` holds `"reason:"`/`"lý do:"`, `Moderation.ReactivatedSuccess` holds `"community standards"`/`"tiêu chuẩn cộng đồng"`).
  - `RobloxModerationService.ParseModerationText` calls `LocalizationService.Get("Moderation.ReactivatedSuccess")` / `ReactivateFailed` / `LoadNoticeError` inside HTML body checks, meaning when the UI language is Vietnamese, Roblox's English HTML page isn't even matched against English strings unless accidentally substring-matched! Also `text.Contains("warning")` matches unguarded HTML outside `hasModerationMarker`.
  - Note: Check `LocalizationServiceTests`, `RobloxModerationServiceBehaviorTests.cs`, and `ValidateSessionTests.cs:288,439` so badge assertions (`FastLogin.BanThreeDaysShort` / `"3 ngày"` / `"7"`) continue to match cleanly!

**Files:**
- Modify: `src/Shiro/Resources/Strings/en.json`
- Modify: `src/Shiro/Resources/Strings/vi.json`
- Modify: `src/Shiro/Services/Network/RobloxModerationService.cs`
- Modify: `tests/Shiro.Tests/Network/RobloxModerationServiceBehaviorTests.cs`
- Modify: `tests/Shiro.Tests/P0AuthNetworkAndModerationTests.cs`

**Interfaces:**
- **Updated localization keys (`en.json` & `vi.json`):**
  - Un-scramble semantic moderation messages: add clean explicit keys `Moderation.BanSevenDaysReason`, `Moderation.BanThreeDaysReason`, `Moderation.BanOneDayReason`, `Moderation.BanFourteenDaysReason`, `Moderation.BanPermanentReason`, `Moderation.WarningConfirmedReason`, `Moderation.AccountLockedReason` (mapping exact English/Vietnamese user messages formerly stuffed under misnamed keys), AND fix the original error keys (`Moderation.ConnectionError`, `Moderation.CannotGetSessionForNotice`, `Moderation.CannotGetSessionInfo`, `Moderation.LoadNoticeError`, `Moderation.ReactivateFailed`, `Moderation.ReactivatedSuccess`, `Moderation.WarningReceived`, `Moderation.AccountBannedGeneric`, `Moderation.AccountBannedWithDuration`, `Moderation.NoSpecificViolationFound`) cleanly so every key name matches its text 1:1 in both `en.json` and `vi.json`!
- **Modified:** `RobloxModerationService.ParseModerationText(string content, string? url = null)`: pure invariant English HTML token checks (`community standards`, `reason:`, `account locked`, `moderator note`, etc.) + gate `"warning"` behind `hasModerationMarker`.

**Steps:**
- [ ] **Step 1: Write failing test(s) (TDD Red)**
  - In `tests/Shiro.Tests/P0AuthNetworkAndModerationTests.cs`:
    - Switch `LocalizationService` temporarily to `AppLanguage.Vietnamese` (`vi`), then pass canonical English Roblox HTML (`"<div>Community Standards violation</div>"`, `"<div>Account Locked - Reset your password</div>"`, `"<div>Moderator Note: Ban 7 Days</div>"`) to `RobloxModerationService.ParseModerationText(...)` and assert detection still succeeds (`IsBanned == true`) instead of depending on Vietnamese localized HTML tokens!
    - Pass benign non-moderation HTML containing the word `"warning"` (`"<div class='banner'>Warning: browser extension detected</div>"`) WITHOUT any moderation marker (`moderator note`, `community standards`, `reviewed`, `not approved`, etc.) and assert `ParseModerationText` returns `IsBanned == false`!
    - Assert semantic sanity of `en.json`/`vi.json` `Moderation.ConnectionError` (must not contain `"banned for 7 days"`), `Moderation.CannotGetSessionForNotice` (must not contain `"banned for 3 days"`), and new `Moderation.Ban*Reason` keys match `RobloxModerationService.ParseModerationText` `Reason` output!
- [ ] **Step 2: Run targeted tests to verify expected Red failure**
  Run:
  ```powershell
  dotnet test Shiro.sln --no-restore --nologo --filter "FullyQualifiedName~P0AuthNetworkAndModerationTests|FullyQualifiedName~RobloxModerationServiceBehaviorTests|FullyQualifiedName~ValidateSessionTests|FullyQualifiedName~LocalizationServiceTests"
  ```
- [ ] **Step 3: Implement minimal fixes**
  - Edit `src/Shiro/Resources/Strings/en.json` and `src/Shiro/Resources/Strings/vi.json` (1:1 symmetric keys):
    - Restore accurate messages to `Moderation.ConnectionError`, `Moderation.CannotGetSessionForNotice`, `Moderation.CannotGetSessionInfo`, `Moderation.LoadNoticeError`, `Moderation.ReactivateFailed`, `Moderation.ReactivatedSuccess`, `Moderation.WarningReceived`, `Moderation.AccountBannedGeneric`, `Moderation.AccountBannedWithDuration`, `Moderation.NoSpecificViolationFound`.
    - Add semantic reason keys `Moderation.AccountLockedReason`, `Moderation.BanOneDayReason`, `Moderation.BanThreeDaysReason`, `Moderation.BanSevenDaysReason`, `Moderation.BanFourteenDaysReason`, `Moderation.BanPermanentReason`, `Moderation.WarningNoticeReason` carrying exact user-facing ban descriptions (`"Account banned for 7 days for violating Community Standards"` / Vietnamese equivalents).
  - Edit `src/Shiro/Services/Network/RobloxModerationService.cs:273,305-327`:
    - Replace `LocalizationService.Get("Moderation.ReactivatedSuccess")`, `ReactivateFailed`, `LoadNoticeError` inside `hasModerationMarker` / locked HTML detection with canonical lowercase English literals (`"community standards"`, `"reason:"`, `"account locked"`, `"moderator note"`, `"reviewed:"`).
    - Require `hasModerationMarker` before categorizing `"warning"` (`hasModerationMarker && (text.Contains("warning") || ...)`).
    - Map returned `Reason` strings to the semantic `Moderation.*Reason` keys while keeping `IssueBadge` on `FastLogin.*` keys (preserving `RobloxModerationServiceBehaviorTests` and `ValidateSessionTests` contracts).
- [ ] **Step 4: Run Moderation, Localization, ValidateSession & ArchitectureTests to verify Green pass**
  Run:
  ```powershell
  dotnet test Shiro.sln --no-restore --nologo --filter "FullyQualifiedName~P0AuthNetworkAndModerationTests|FullyQualifiedName~RobloxModerationServiceBehaviorTests|FullyQualifiedName~ValidateSessionTests|FullyQualifiedName~LocalizationServiceTests|FullyQualifiedName~ArchitectureTests"
  ```
  Verify `RobloxModerationService.cs >= 67%` branch coverage and `en.json`/`vi.json` key parity.
- [ ] **Step 5: Commit**
  ```powershell
  git add src/Shiro/Resources/Strings/en.json src/Shiro/Resources/Strings/vi.json src/Shiro/Services/Network/RobloxModerationService.cs tests/Shiro.Tests/P0AuthNetworkAndModerationTests.cs
  git commit -m "fix(i18n,moderation): unscramble Moderation strings and decouple HTML moderation parsing from UI language"
  ```

---

## Self-Review & Execution Gate
- Confirm all 4 tasks maintain 1:1 `en.json`/`vi.json` symmetry, partial class line limits (`Account <= 2300`, `RobloxHbaService <= 1150`, individual `.cs <= 485`), and `RobloxModerationService.cs >= 67%` branch floor.
