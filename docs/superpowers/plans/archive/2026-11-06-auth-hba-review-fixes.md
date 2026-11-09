# Auth & HBA Review Fixes Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Fix the 5 Important and 2 Minor defects identified in the review of commit `1a8eba7` across HBA key caching, network session synchronization, form-body signing, browser template calibration, moderation duration parsing, and webhook alert deduplication.

**Architecture:** Five focused, independent tasks, each with its own TDD cycle and commit. Each task addresses a specific defect in the authentication, HBA signing, or moderation pipeline without altering external schemas or UI workflows.

**Tech Stack:** .NET 10, xUnit, NSubstitute, `System.Security.Cryptography.ECDsa`, `System.Text.Json`.

**Spec:** Code review of commit `1a8eba7` in conversation 00fcecb2 (2026-11-06).

## Global Constraints
- `dotnet build Shiro.sln -c Release`: 0 errors, 0 warnings.
- `dotnet test tests/Shiro.Tests/Shiro.Tests.csproj -c Release`: 0 failed; total test count must increase with each task.
- Coverage gate unchanged: `./scripts/Check-Coverage.ps1 -MinOverall 75.0 -MinBranch 60.0 -MinCore 92.0`.
- `ArchitectureTests.NoSourceCodeFiles_ShouldExceed500Lines` stays green (every source file ≤ 500 lines).
- Conventional Commit prefixes (`fix(hba):`, `fix(launch):`, `fix(moderation):`).

## Review Focus
1. **Concurrent `ClearKeyCache()` during active HBA signing:** Calling `ClearKeyCache()` while threads are running `TryBuildBoundAuthToken` must not zero out an in-use `ECParameters.D` buffer or throw `CryptographicException`. Covered by `TryBuildBoundAuthToken_ConcurrentWithClearKeyCache_NeverFails` in Task 1.
2. **Form-urlencoded POST with spaces:** Signing a `RestRequest` that has form parameters containing spaces (`"hello world"`) must produce the exact same SHA-256 body hash (`HB`) that `FormUrlEncodedContent` sends over the wire (`"hello+world"`). Covered by `ApplySessionFingerprintAndHbaToRestRequest_FormParametersWithSpaces_MatchesFormUrlEncodedHash` in Task 2.
3. **Partial signature capture in JS hook:** When `recentSigns` only captured 1 of 2 expected signatures (`sigCount == 2`), `inspectBoundHeader` must not overwrite `state.tokenTemplate` with a 1-signature template. Covered by `GetBrowserCaptureScript_RequiresAllSignaturesBeforeUpdatingTemplate` in Task 2.
4. **Redundant `TryRebindSessionToCurrentNetworkAsync` calls during launch:** Launching an account after a network change when Stage 3 (`usersAuthenticatedOk == true`, Stage 2 ticket failure) occurs must not trigger `TryRebindSessionToCurrentNetworkAsync` twice in a single launch. Covered by `JoinServerResultAsync_WhenNetworkChanged_CallsRebindOnlyOnce` in Task 3.
5. **Multi-digit ban durations (`365 days`, `21 days`, `180 days`):** `ClassifyAuthOrModerationApiPayload` must not misclassify `"Ban 365 Days"` as `BAN_3_DAYS` or `"Ban 21 Days"` as `BAN_1_DAY`. Covered by `ClassifyAuthOrModerationApiPayload_MultiDigitBanDays_DoesNotMisclassifyAsShortBan` in Task 4.

---

### Task 1: Thread-safe HBA key cache without lingering PKCS#8 byte buffers

**Files:**
- Modify: `src/Shiro/Services/Auth/RobloxHbaService.cs:30-50,135-180`
- Test: `tests/Shiro.Tests/RobloxHbaAndIpPersistenceTests.cs`

**Interfaces:**
- Keeps public signatures `RobloxHbaService.ClearKeyCache()` and `RobloxHbaService.TryBuildBoundAuthToken(...)` unchanged.

- [ ] **Step 1: Write failing / regression concurrency test** in `tests/Shiro.Tests/RobloxHbaAndIpPersistenceTests.cs`:
```csharp
[Fact]
public async Task TryBuildBoundAuthToken_ConcurrentWithClearKeyCache_NeverFails()
{
    var (privB64, _) = GenerateP256KeyPair();
    using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(600));
    var signTasks = Enumerable.Range(0, 4).Select(_ => Task.Run(() =>
    {
        while (!cts.IsCancellationRequested)
        {
            bool ok = RobloxHbaService.TryBuildBoundAuthToken(
                privB64,
                "https://auth.roblox.com/v1/authentication-ticket",
                "POST",
                "{}",
                RobloxHbaService.DefaultTemplate,
                1700000000L,
                out string token);
            Assert.True(ok);
            Assert.False(string.IsNullOrWhiteSpace(token));
        }
    }));
    var clearTask = Task.Run(async () =>
    {
        while (!cts.IsCancellationRequested)
        {
            RobloxHbaService.ClearKeyCache();
            await Task.Delay(5);
        }
    });
    await Task.WhenAll(signTasks.Append(clearTask));
}
```
- [ ] **Step 2: Run test.** `dotnet test tests/Shiro.Tests/Shiro.Tests.csproj -c Release --filter "FullyQualifiedName~TryBuildBoundAuthToken_ConcurrentWithClearKeyCache_NeverFails"`
- [ ] **Step 3: Fix `RobloxHbaService.cs`.**
  - Remove `CachedPkcs8Keys` completely.
  - When importing `trimmedKey`: decode `byte[] keyBytes = Convert.FromBase64String(trimmedKey)` inside a `try / finally` that calls `CryptographicOperations.ZeroMemory(keyBytes)` immediately after `parserEcdsa.ExportParameters(true)`.
  - In `ClearKeyCache()`, call `CachedEcParameters.Clear()` without mutating `kvp.Value.D` in-place while readers may hold the struct reference, OR clone `ecParams.D` before passing to `ECDsa.Create` while synchronizing cache eviction under a lock.
- [ ] **Step 4: Run test to verify it passes.**
- [ ] **Step 5: Commit.** `fix(hba): eliminate lingering PKCS8 cache and race condition in ClearKeyCache`

---

### Task 2: Align RestRequest HBA body signing, tracker timestamp, and JS template guard

**Files:**
- Modify: `src/Shiro/Classes/Account.HbaBinding.cs:128-141,229-299`
- Modify: `src/Shiro/Services/Auth/RobloxHbaService.BrowserHooks.cs:251-266`
- Test: `tests/Shiro.Tests/RobloxHbaAndIpPersistenceTests.cs`

**Interfaces:**
- Consumes `Account.ApplySessionFingerprintAndHbaToRestRequest(RestRequest, string)` and `RobloxHbaService.GetBrowserCaptureScript()`.

- [ ] **Step 1: Write failing tests** in `tests/Shiro.Tests/RobloxHbaAndIpPersistenceTests.cs`:
  - `ApplySessionFingerprintAndHbaToRestRequest_FormParametersWithSpaces_MatchesFormUrlEncodedHash`: create an `Account` with HBA key and `BrowserTrackerID = "555666"`, create a `RestRequest("/v1/test", Method.Post)` with `.AddParameter("note", "hello world")`, call `acc.ApplySessionFingerprintAndHbaToRestRequest(req, "https://www.roblox.com/")`. Assert that the `x-bound-auth-token` header's second segment (`HB`) equals `Convert.ToBase64String(SHA256.HashData(Encoding.UTF8.GetBytes("note=hello+world")))`, and that the `Cookie` header does not contain the hardcoded string `"01/01/2026 00:00:00"`.
  - `GetBrowserCaptureScript_RequiresAllSignaturesBeforeUpdatingTemplate`: assert `RobloxHbaService.GetBrowserCaptureScript()` contains `matched.length === sigCount`.
- [ ] **Step 2: Run tests and confirm they fail.**
- [ ] **Step 3: Implement fixes.**
  - In `Account.HbaBinding.cs:234-236`: replace hardcoded `CreateDate=01/01/2026 00:00:00` with `DateTime.UtcNow.ToString("MM/dd/yyyy HH:mm:ss", CultureInfo.InvariantCulture)`.
  - In `Account.HbaBinding.cs:294-297`: serialize form parameters using `using var formContent = new FormUrlEncodedContent(request.Parameters); bodyStr = formContent.ReadAsStringAsync().GetAwaiter().GetResult();` (in-memory synchronous byte formatting on `FormUrlEncodedContent`) so the signed string is byte-for-byte identical to what `RestClient` sends.
  - In `RobloxHbaService.BrowserHooks.cs:252`: change `if (matched.length > 0)` to `if (matched.length === sigCount)` and require `specs.every(Boolean)` before assigning `state.tokenTemplate`.
- [ ] **Step 4: Run tests and confirm they pass.**
- [ ] **Step 5: Commit.** `fix(hba): match FormUrlEncoded body hash and guard JS template calibration`

---

### Task 3: Deduplicate network session rebind during launch

**Files:**
- Modify: `src/Shiro/Services/Launch/AccountLaunchService.cs:220-228`
- Modify: `src/Shiro/Services/Launch/AccountLaunchService.Home.cs:145-151`
- Modify: `src/Shiro/Classes/Account.Api.cs:128-143`
- Test: `tests/Shiro.Tests/IpSwitchSessionPersistenceTests.cs`

**Interfaces:**
- Keeps `AccountLaunchService.JoinServerResultAsync`, `LaunchAppHomeResultAsync`, and `Account.GetAuthTicketAsync` signatures unchanged.

- [ ] **Step 1: Write failing test** in `tests/Shiro.Tests/IpSwitchSessionPersistenceTests.cs`:
  - `JoinServerResultAsync_WhenNetworkChangedAndStage2TicketFailsOnce_DoesNotRunRebindTwice`: set up an HBA-bound account with `LastBoundIpSignature = "old_vpn_sig"`, count calls to `users.roblox.com/v1/users/authenticated` when `JoinServerResultAsync` runs with real `GetAuthTicketAsync` (`TestGetAuthTicketOverride = null`). When `users/authenticated` returns `200 OK` without `Set-Cookie` and `authentication-ticket` returns `200 OK` with ticket, assert `users/authenticated` was called at most once during the launch.
- [ ] **Step 2: Run the test and observe `users/authenticated` being called twice** (once from `AccountLaunchService` and once from `GetAuthTicketCoreAsync` when Stage 3 does not update `LastBoundIpSignature`).
- [ ] **Step 3: Implement fix.**
  - In `AccountLaunchService.cs` and `AccountLaunchService.Home.cs`: only invoke `await account.TryRebindSessionToCurrentNetworkAsync(force: shouldForceRebind, cancellationToken)` before ticket acquisition when `TestGetAuthTicketOverride != null`; when `TestGetAuthTicketOverride == null`, `account.GetAuthTicketAsync(cancellationToken)` is the single owner of pre-ticket network synchronization.
  - In `Account.Api.cs` (`GetAuthTicketCoreAsync`): if `string.IsNullOrEmpty(LastBoundIpSignature) && currentSigAtStart is not ("offline" or "unknown")`, initialize `LastBoundIpSignature = currentSigAtStart; AppServices.AccountRepository.QueueDeferredSave();`.
- [ ] **Step 4: Run tests to verify pass.**
- [ ] **Step 5: Commit.** `fix(launch): prevent duplicate network session rebind during ticket acquisition`

---

### Task 4: Fix multi-digit ban duration classification and duplicate webhook alerts

**Files:**
- Modify: `src/Shiro/Services/Auth/FastLoginCoordinator.Classification.cs:123-141`
- Modify: `src/Shiro/Services/Auth/SessionAuditorService.cs:151-195`
- Test: `tests/Shiro.Tests/Network/RobloxModerationServiceBehaviorTests.cs`

**Interfaces:**
- Keeps `FastLoginCoordinator.ClassifyAuthOrModerationApiPayload` and `SessionAuditorService.AuditAccountAsync` signatures unchanged.

- [ ] **Step 1: Write failing tests** in `tests/Shiro.Tests/Network/RobloxModerationServiceBehaviorTests.cs`:
  - `ClassifyAuthOrModerationApiPayload_MultiDigitBanDays_DoesNotMisclassifyAsShortBan`:
    - `{"punishmentType":"Ban 365 Days"}` → `"ACCOUNT_BANNED"` (not `"BAN_3_DAYS"`).
    - `{"punishmentType":"Ban 21 Days"}` → `"ACCOUNT_BANNED"` (not `"BAN_1_DAY"`).
    - `{"punishmentType":"Ban 14 Days"}` → `"BAN_14_DAYS"`.
    - `{"punishmentType":"Ban 7 Days"}` → `"BAN_7_DAYS"`.
    - `{"punishmentType":"Ban 3 Days"}` → `"BAN_3_DAYS"`.
    - `{"punishmentType":"Ban 1 Day"}` → `"BAN_1_DAY"`.
  - `AuditAccountAsync_WhenModeratedOn403_DoesNotDuplicateModerationCheckOrWebhook`: verify that when `responseBody` on 403 already classifies as moderated (`modCode` is a ban code), `AuditAccountAsync` does not unnecessarily call `RobloxModerationService.Instance.CheckModerationStatusAsync` a second time, and when `CheckModerationStatusAsync` is called and returns `probedBan == true`, `WebhookNotificationService.EnqueueAlert` is not called a second time in `SessionAuditorService`.
- [ ] **Step 2: Run the tests and confirm they fail.**
- [ ] **Step 3: Implement fixes.**
  - In `FastLoginCoordinator.Classification.cs:123-140`: extract the integer day count from `punishmentType` (or match exact tokens with regex/digit boundary so `14`, `7`, `3`, `1` only match when no adjacent digits exist).
  - In `SessionAuditorService.cs:151-195`:
    - Only probe `RobloxModerationService.Instance.CheckModerationStatusAsync` when `status != AccountSessionStatus.Moderated` (i.e. when the 403 body did not already classify the moderation status).
    - Track `bool alertAlreadyDispatched = probedBan;` so lines 187-193 only call `WebhookNotificationService.EnqueueAlert` if `!alertAlreadyDispatched`.
- [ ] **Step 4: Run the full test suite and verify all tests pass.**
- [ ] **Step 5: Commit.** `fix(moderation): fix multi-digit ban classification and deduplicate webhook alerts`
