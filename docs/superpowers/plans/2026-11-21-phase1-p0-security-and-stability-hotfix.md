# Phase 1 — P0 Security, CI Gate, Moderation & Hot-Path Perf Hotfix Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use `subagent-driven-development` (recommended) or `executing-plans` to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Eliminate all Phase 1 P0 defects in Shiro Account Manager v5.0.0: enforce CI test/coverage exit codes, close HTTP/WebSocket Gateway authorization/redaction/DoS/validation gaps (while keeping `GET`/`POST` script backwards compatibility), remove plaintext secrets from static caches/browser globals/logs, block GeoIP SSRF redirects, eliminate false-positive moderation bans and 407/408 session invalidation, and remove per-read AES-GCM secret decryption from UI and background hot paths.

**Architecture:** All fixes stay strictly inside existing service/domain boundaries (`Shiro.Services`, `Shiro`, `Shiro.ViewModels`) without introducing new NuGet packages or breaking backwards-compatible Gateway routes. Sensitive in-memory values use existing `InMemorySecretProtector` (AES-256-GCM) or SHA-256 digest cache keys; UI/maintenance hot paths read cached non-secret metadata flags on `Account` instead of decrypting secrets.

**Tech Stack:** .NET 10 (`net10.0-windows10.0.19041.0`), C# 14, ASP.NET Core Kestrel, Avalonia UI, xUnit, PowerShell (`pwsh`), GitHub Actions.

**Spec:** Phase 1 P0 findings from the 2026-11-21 comprehensive codebase audit; Gateway backwards-compatibility decision confirmed by user (keep `GET`/`POST` endpoints functioning while enforcing `AllowAccountEditing`, redacting `GetProxy` credentials unless `AllowGetCookie` is enabled, bounding lockout/WS resources, and returning HTTP 400 on oversized alias/description writes).

## Global Constraints

- **Architecture Guardrails (verified by `ArchitectureTests*.cs`):**
  - Every `.cs` file in `src/Shiro/` MUST stay `<= 485` lines (`NoSourceCodeFiles_ShouldExceed500Lines`).
  - Every `.cs` file in `tests/Shiro.Tests/` MUST stay `<= 550` lines (`NoTestFiles_ShouldExceed550Lines`).
  - Every `partial class` total across files MUST stay `<= 1150` lines.
  - Zero compiler warnings (`-warnaserror`), zero empty `catch` blocks, zero untyped `catch` blocks (except `AppLogger.cs`), zero Vietnamese diacritics in C# comments/string literals outside `LocalizationService*`, zero `.GetAwaiter().GetResult()`, zero `[DllImport]`.
  - Every `[Fact]`/`[Theory]` MUST assert concrete values (not only `Assert.NotNull`/`Assert.NotEmpty`/`Assert.True(x >= 0)`).
  - Preserve all `.github/workflows/release.yml` substrings asserted by `ArchitectureTests.Ratchets.cs:190-196` and `ArchitectureTests.cs:250` (`--runtime win-x64 /p:SelfContained=true --locked-mode`, `-warnaserror`, `--no-restore`, `if: failure()`, `path: coverage/`, `-MinOverall 75.0 -MinBranch 60.0 -MinCore 92.0`).
- **Build & Test Environment Notes:**
  - If an incremental Debug build hits `CS2001` on a generated `.editorconfig`, add `--no-incremental` to `dotnet test` / `dotnet build`.
  - Do **NOT** terminate any running user `Shiro.exe` process. While a live `Shiro.exe` holds `ROBLOX_singletonMutex`, 2 pre-existing mutex tests (`P0Win32AndLaunchRegressionTests` L288 and `P1WindowsAndUiRegressionTests` L211) fail due to OS mutex contention; all other tests and all `ArchitectureTests` must pass 100%.
- **Tight File Line Budgets to Watch:**
  - `RobloxServerQueryService.cs` (currently **484 / 485 lines**): **0 net lines allowed** — add helper in `SharedHttpClient.cs`, only replace 2 call sites in `RobloxServerQueryService.cs`.
  - `RobloxDirectLauncher.CookieGuard.cs` (currently **479 / 485 lines**): keep net diff `<= +2` lines.
  - `AccountApiDispatcher.Endpoints.cs` (currently **465 / 485 lines**): put the reusable `CheckAccountEditingDenied` helper in `AccountApiDispatcher.cs` (435 lines) so `Endpoints.cs` stays `<= 478` lines.
  - `ProxyBindingService.cs` (currently **465 / 485 lines**): keep net diff `<= +10` lines (`<= 475` lines).

## Review Focus

1. **CI release gate cannot silently publish broken builds:** Both `dotnet test` and `./scripts/Check-Coverage.ps1` fail the workflow immediately on non-zero `$LASTEXITCODE`, while keeping every substring expected by `ArchitectureTests.Ratchets.cs` intact — pinned by Task 1 (`tests/Shiro.Tests/ArchitectureTests.Ratchets.cs`).
2. **Gateway authorization, proxy redaction & validation without breaking existing scripts:** All 6 mutating endpoints (`BlockUser`, `UnblockUser`, `UnblockEveryone`, `SetServer`, `SetRecommendedServer`, `SetAvatar`) still accept `GET`/`POST` but fail closed with HTTP 401 unless `AllowAccountEditing` is `true`; `GetProxy` redacts `user:pass` when `!AllowGetCookie` and returns raw proxy when `AllowGetCookie`; `SetAlias`/`SetDescription`/`AppendDescription` return HTTP 400 when exceeding `Account.MaxAliasLength` / `Account.MaxDescriptionLength` — pinned by Task 2 (`tests/Shiro.Tests/Phase1GatewayHardeningTests.cs` + updated existing Gateway tests).
3. **Auth lockout & WebSocket resource caps under hostile load:** `WebAuthPolicy.EvictClientFailuresIfNeeded` hard-caps `ClientFailures.Count <= MaxTrackedClientFailures` even when 100% of tracked IPs are locked out; `KestrelWebServer` caps active WebSockets at 32 (HTTP 503 on #33), closes frames >16 KB with `WebSocketCloseStatus.MessageTooBig`, and ref-counts static event subscriptions across `StartAsync`/`StopAsync` — pinned by Task 3 (`tests/Shiro.Tests/WebAuthPolicyTests.cs`, `tests/Shiro.Tests/WebSocketGatewayTests.cs`).
4. **Zero plaintext secrets in static caches, browser globals, logs, or GeoIP redirect hops:** SHA-256 keys in `RobloxHbaService` and `ProxyBindingService`; `InMemorySecretProtector` for `_protectedLastInjectedToken`; closure-only `privateKeyPkcs8` in `HbaBrowserScripts`; all 5 `LogRedactor` patterns covered; `SharedHttpClient.GetStringNoRedirectAsync` rejects 3xx responses on GeoIP queries — pinned by Tasks 4, 5, 6 (`tests/Shiro.Tests/Phase1SecretHygieneTests.cs`).
5. **No moderation false positives, no 407/408 session wipes, and zero AES-GCM decrypts on UI/maintenance hot paths:** `ParseModerationText` ignores `"Community Standards"`/`"I Agree"`/`"Reason:"` and distinguishes `21 days`/`17 days`/`14 days` from `1 day`/`7 day`; `CheckModerationStatusAsync` never parses 200 OK bodies when `finalUrl` is empty or non-moderation; `ValidateSessionAsync` preserves `account.Valid` on HTTP 407 and 408; `AccountViewModel.Valid`, styling, `Account.HasHbaBinding`, and `AccountSessionMaintenanceService.RefreshStaleCookiesAsync` trigger `0` `InMemorySecretProtector.Unprotect` calls on cached metadata — pinned by Tasks 7, 8 (`tests/Shiro.Tests/Network/RobloxModerationServiceBehaviorTests.cs`, `tests/Shiro.Tests/ValidateSessionTests.cs`, `tests/Shiro.Tests/Phase1HotPathMetadataTests.cs`).

---

### Task 1: Guard CI/CD Release Gate Exit Codes

**Files:**
- Modify: `.github/workflows/release.yml:36-41`
- Modify: `tests/Shiro.Tests/ArchitectureTests.Ratchets.cs`

- [ ] **Step 1: Write failing architecture regression test asserting exit-code guards in `release.yml`**

Open `tests/Shiro.Tests/ArchitectureTests.Ratchets.cs` around line 190 (inside the existing `release.yml` contract test) and add assertions verifying that `release.yml` either splits `dotnet test` and `Check-Coverage.ps1` into separate steps OR explicitly checks `$LASTEXITCODE` after `dotnet test` and fails the step with `exit $LASTEXITCODE`:

```csharp
[Fact]
public void ReleaseWorkflow_UnitTestsAndCoverageGate_MustFailOnNonZeroTestExitCode()
{
    string workflowPath = Path.Combine(GetRepoRoot(), ".github", "workflows", "release.yml");
    string content = File.ReadAllText(workflowPath);

    // Must not execute Check-Coverage.ps1 in the same pwsh script block after dotnet test
    // without an explicit $LASTEXITCODE guard that aborts on test failure.
    bool hasExplicitExitGuard =
        content.Contains("$LASTEXITCODE -ne 0", StringComparison.Ordinal) &&
        content.Contains("exit $LASTEXITCODE", StringComparison.Ordinal);

    Assert.True(
        hasExplicitExitGuard,
        "release.yml must explicitly fail when dotnet test exits non-zero before running Check-Coverage.ps1.");
}
```

- [ ] **Step 2: Run test to verify it fails**

Run:
```powershell
dotnet test "tests/Shiro.Tests/Shiro.Tests.csproj" --no-incremental --filter "FullyQualifiedName~ReleaseWorkflow_UnitTestsAndCoverageGate_MustFailOnNonZeroTestExitCode"
```
Expected: FAIL with `"release.yml must explicitly fail when dotnet test exits non-zero..."`.

- [ ] **Step 3: Update `.github/workflows/release.yml:36-41` with explicit `$LASTEXITCODE` guards**

Replace lines 36–41 of `.github/workflows/release.yml`:
```yaml
      - name: Run Unit Tests & Coverage Gate
        shell: pwsh
        run: |
          $ErrorActionPreference = 'Stop'
          dotnet test "tests/Shiro.Tests/Shiro.Tests.csproj" --configuration Release --no-restore --collect:"XPlat Code Coverage" --settings "tests/coverage.runsettings" --results-directory "coverage" --logger "console;verbosity=normal"
          if ($LASTEXITCODE -ne 0) {
            Write-Error "Unit tests failed with exit code $LASTEXITCODE"
            exit $LASTEXITCODE
          }
          ./scripts/Check-Coverage.ps1 -CoverageDir "coverage" -MinOverall 75.0 -MinBranch 60.0 -MinCore 92.0
          if ($LASTEXITCODE -ne 0) {
            Write-Error "Coverage gate failed with exit code $LASTEXITCODE"
            exit $LASTEXITCODE
          }
```
Notice this keeps every substring asserted by `ArchitectureTests.Ratchets.cs` and `ArchitectureTests.cs` intact, and triggers the `Upload coverage on failure` step (`if: failure()`) whether `dotnet test` OR `Check-Coverage.ps1` fails.

- [ ] **Step 4: Run all `ArchitectureTests` to verify pass**

Run:
```powershell
dotnet test "tests/Shiro.Tests/Shiro.Tests.csproj" --no-incremental --filter "FullyQualifiedName~ArchitectureTests"
```
Expected: PASS (0 failures).

- [ ] **Step 5: Commit**

```powershell
git add .github/workflows/release.yml tests/Shiro.Tests/ArchitectureTests.Ratchets.cs
git commit -m "fix(ci): fail release workflow immediately when dotnet test or coverage gate exits non-zero"
```

---

### Task 2: Gateway Authorization (`AllowAccountEditing`), `GetProxy` Credential Redaction & Alias/Description Length Validation

**Files:**
- Modify: `src/Shiro/Domain/Account.cs:159-196` (expose `internal const int MaxAliasLength = 50;` and `internal const int MaxDescriptionLength = 5000;`)
- Modify: `src/Shiro/Services/Network/AccountApiDispatcher.cs` (add 4-line `CheckAccountEditingDenied` helper to keep `Endpoints.cs <= 485` lines)
- Modify: `src/Shiro/Services/Network/AccountApiDispatcher.Endpoints.cs:179-460`
- Create: `tests/Shiro.Tests/Phase1GatewayHardeningTests.cs`
- Modify existing tests that call the 6 endpoints without enabling `AllowAccountEditing`:
  - `tests/Shiro.Tests/AccountApiDispatcherEndpointsBehaviorTests.cs:223-306`
  - `tests/Shiro.Tests/GatewayApiAndOpenApiTests.cs:164-225` and `286-355`
  - `tests/Shiro.Tests/BugHuntRound2Tests.cs:160-224`
  - `tests/Shiro.Tests/P1GatewayRegressionTests.cs:160-196` and `368-407`
  - `tests/Shiro.Tests/Plan3FinalFixRegressionTests.cs:358-396`
  - `tests/Shiro.Tests/WebServerWorkflowBehaviorTests.cs:172-250`

- [ ] **Step 1: Write failing tests in `tests/Shiro.Tests/Phase1GatewayHardeningTests.cs`**

Create `tests/Shiro.Tests/Phase1GatewayHardeningTests.cs` (keep under 250 lines) covering:
1. All 6 mutating endpoints (`/BlockUser`, `/UnblockUser`, `/UnblockEveryone`, `/SetServer`, `/SetRecommendedServer`, `/SetAvatar`) return HTTP `401` and `"not allowed"` with `0` upstream HTTP calls when `AllowAccountEditing` is `false`.
2. `/GetProxy` returns `http://1.2.3.4:8080` (credentials stripped via `ProxyManagerService.RedactProxy`) when `AllowGetCookie` is `false`, and returns the full `http://alice:s3cr3t@1.2.3.4:8080` when `AllowGetCookie` is `true` (and empty string when `account.Proxy` is null/empty).
3. `/SetAlias` with 51 chars returns HTTP `400` (`"exceeds maximum length"`) and leaves `account.Alias` unchanged; `/SetDescription` with 5001 chars returns HTTP `400`; `/AppendDescription` when existing (4995 chars) + body (10 chars) > 5000 returns HTTP `400` and leaves `account.Description` unchanged.

```csharp
#nullable enable
using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Http;
using System.Threading.Tasks;
using Shiro.Services;
using Shiro.Tests.TestInfrastructure;
using Xunit;

namespace Shiro.Tests
{
    [Collection("SerialGlobalState")]
    public sealed class Phase1GatewayHardeningTests : IDisposable
    {
        private readonly string _prevPassword;
        private readonly bool _prevAllowEdit;
        private readonly bool _prevAllowGetCookie;
        private readonly IReadOnlyList<Account> _prevAccounts;
        private readonly Func<Account?, HttpClient>? _prevHttpFactory;

        public Phase1GatewayHardeningTests()
        {
            AppSettings.ActiveIniFile ??= new IniFile();
            _prevPassword = AppSettings.WebSocketPassword ?? string.Empty;
            _prevAllowEdit = AppSettings.WebServer.Get<bool>("AllowAccountEditing");
            _prevAllowGetCookie = AppSettings.WebServer.Get<bool>("AllowGetCookie");
            _prevAccounts = AppServices.AccountRepository.GetAll();
            _prevHttpFactory = RobloxAccountSessionClient.TestHttpClientFactory;
        }

        public void Dispose()
        {
            AppSettings.WebSocketPassword = _prevPassword;
            AppSettings.WebServer.Set("AllowAccountEditing", _prevAllowEdit ? "true" : "false");
            AppSettings.WebServer.Set("AllowGetCookie", _prevAllowGetCookie ? "true" : "false");
            AccountManager.ResetAccountsForTests(_prevAccounts);
            RobloxAccountSessionClient.TestHttpClientFactory = _prevHttpFactory;
        }

        private static AccountManager.ApiRequestContext CreateReq(
            string path,
            string password,
            Dictionary<string, string> query,
            Action<int> setStatus,
            string body = "")
        {
            query["Password"] = password;
            return new AccountManager.ApiRequestContext
            {
                AbsolutePath = path,
                IsV2 = true,
                IsLocal = true,
                Host = "localhost",
                Origin = string.Empty,
                Body = body,
                GetQuery = k => query.TryGetValue(k, out string? v) ? v : null,
                GetHeader = _ => null,
                SetStatusCode = setStatus
            };
        }

        [Theory]
        [InlineData("/BlockUser", "12345", "", "", "")]
        [InlineData("/UnblockUser", "12345", "", "", "")]
        [InlineData("/UnblockEveryone", "", "", "", "")]
        [InlineData("/SetServer", "", "1818", "job-1", "")]
        [InlineData("/SetRecommendedServer", "", "1818", "", "")]
        [InlineData("/SetAvatar", "", "", "", "{\"playerAvatarType\":\"R15\"}")]
        public async Task MutatingEndpoints_Deny401_WhenAllowAccountEditingIsFalse(
            string path, string userId, string placeId, string jobId, string body)
        {
            const string pass = "Phase1GatePass12";
            AppSettings.WebSocketPassword = pass;
            AppSettings.WebServer.Set("AllowAccountEditing", "false");
            var mgr = AccountManager.Instance ?? new AccountManager();
            var acc = new Account { Username = "GateUser", UserID = 5001, SecurityToken = "_|WARNING:-DO-NOT-SHARE-THIS.--tok", Valid = true };
            AccountManager.ResetAccountsForTests(new[] { acc });

            int httpCalls = 0;
            RobloxAccountSessionClient.TestHttpClientFactory = _ => new HttpClient(new FakeHttpHandler(_ =>
            {
                httpCalls++;
                return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("{}") };
            }));

            var q = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { ["Account"] = "GateUser" };
            if (!string.IsNullOrEmpty(userId)) q["UserId"] = userId;
            if (!string.IsNullOrEmpty(placeId)) q["PlaceId"] = placeId;
            if (!string.IsNullOrEmpty(jobId)) q["JobId"] = jobId;

            int status = 200;
            string resp = await mgr.ProcessApiRequestAsync(CreateReq(path, pass, q, s => status = s, body));

            Assert.Equal(401, status);
            Assert.Contains("not allowed", resp, StringComparison.OrdinalIgnoreCase);
            Assert.Equal(0, httpCalls);
        }

        [Fact]
        public async Task GetProxy_RedactsCredentialsUnlessAllowGetCookieIsEnabled()
        {
            const string pass = "Phase1GatePass12";
            AppSettings.WebSocketPassword = pass;
            var mgr = AccountManager.Instance ?? new AccountManager();
            var acc = new Account { Username = "ProxyUser", UserID = 5002, Proxy = "http://alice:s3cr3t@1.2.3.4:8080" };
            AccountManager.ResetAccountsForTests(new[] { acc });

            AppSettings.WebServer.Set("AllowGetCookie", "false");
            int redactedStatus = 200;
            string redacted = await mgr.ProcessApiRequestAsync(CreateReq(
                "/GetProxy", pass, new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { ["Account"] = "ProxyUser" }, s => redactedStatus = s));
            Assert.Equal(200, redactedStatus);
            Assert.Contains("http://1.2.3.4:8080", redacted, StringComparison.Ordinal);
            Assert.DoesNotContain("alice", redacted, StringComparison.Ordinal);
            Assert.DoesNotContain("s3cr3t", redacted, StringComparison.Ordinal);

            AppSettings.WebServer.Set("AllowGetCookie", "true");
            int fullStatus = 200;
            string full = await mgr.ProcessApiRequestAsync(CreateReq(
                "/GetProxy", pass, new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { ["Account"] = "ProxyUser" }, s => fullStatus = s));
            Assert.Equal(200, fullStatus);
            Assert.Contains("http://alice:s3cr3t@1.2.3.4:8080", full, StringComparison.Ordinal);
        }

        [Fact]
        public async Task SetAlias_SetDescription_AppendDescription_RejectOversizedPayloadsWith400()
        {
            const string pass = "Phase1GatePass12";
            AppSettings.WebSocketPassword = pass;
            AppSettings.WebServer.Set("AllowAccountEditing", "true");
            var mgr = AccountManager.Instance ?? new AccountManager();
            var acc = new Account { Username = "LenUser", UserID = 5003, Alias = "Orig", Description = new string('a', 4995) };
            AccountManager.ResetAccountsForTests(new[] { acc });

            int s1 = 200;
            string r1 = await mgr.ProcessApiRequestAsync(CreateReq(
                "/SetAlias", pass, new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { ["Account"] = "LenUser" }, s => s1 = s, new string('x', 51)));
            Assert.Equal(400, s1);
            Assert.Contains("50", r1, StringComparison.Ordinal);
            Assert.Equal("Orig", acc.Alias);

            int s2 = 200;
            string r2 = await mgr.ProcessApiRequestAsync(CreateReq(
                "/SetDescription", pass, new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { ["Account"] = "LenUser" }, s => s2 = s, new string('y', 5001)));
            Assert.Equal(400, s2);
            Assert.Contains("5000", r2, StringComparison.Ordinal);
            Assert.Equal(4995, acc.Description.Length);

            int s3 = 200;
            string r3 = await mgr.ProcessApiRequestAsync(CreateReq(
                "/AppendDescription", pass, new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { ["Account"] = "LenUser" }, s => s3 = s, new string('z', 10)));
            Assert.Equal(400, s3);
            Assert.Contains("5000", r3, StringComparison.Ordinal);
            Assert.Equal(4995, acc.Description.Length);
        }
    }
}
```

- [ ] **Step 2: Run new tests to verify failure**

Run:
```powershell
dotnet test "tests/Shiro.Tests/Shiro.Tests.csproj" --no-incremental --filter "FullyQualifiedName~Phase1GatewayHardeningTests"
```
Expected: FAIL on all 3 test methods.

- [ ] **Step 3: Implement changes in `Account.cs`, `AccountApiDispatcher.cs`, and `AccountApiDispatcher.Endpoints.cs`**

1. In `src/Shiro/Domain/Account.cs`, right above `Alias` (line 158), add constants and use them in `Alias` and `Description` setters:
```csharp
internal const int MaxAliasLength = 50;
internal const int MaxDescriptionLength = 5000;
```
Update `ExceedsMaxLength(nameof(Alias), value, MaxAliasLength)` and `ExceedsMaxLength(nameof(Description), value, MaxDescriptionLength)`.

2. In `src/Shiro/Services/Network/AccountApiDispatcher.cs` (near `WebServerResponse`, line 71), add the reusable helper:
```csharp
internal static string? CheckAccountEditingDenied(
    ISettingsService? webServer,
    string method,
    Func<string, bool, int, string?, string> reply) =>
    webServer != null && webServer.Get<bool>("AllowAccountEditing")
        ? null
        : reply($"Method `{method}` not allowed", false, 401, "Method not allowed");
```

3. In `src/Shiro/Services/Network/AccountApiDispatcher.Endpoints.cs`:
   - Replace line 179 (`GetProxy`):
     ```csharp
     if (IsRoute(method, "GetProxy"))
     {
         string rawProxy = account.Proxy ?? string.Empty;
         bool allowRaw = webServer != null && webServer.Get<bool>("AllowGetCookie");
         return reply(allowRaw ? rawProxy : ProxyManagerService.RedactProxy(rawProxy), true, -1, null);
     }
     ```
   - Simplify existing `SetField`, `RemoveField`, `SetAlias`, `SetDescription`, `AppendDescription` guards using `if (CheckAccountEditingDenied(webServer, method, reply) is string editErr) return editErr;` (saves 5 lines!).
   - In `SetAlias` (after `string.IsNullOrEmpty(body)` check):
     ```csharp
     if (body.Length > Account.MaxAliasLength)
         return reply($"Alias exceeds maximum length ({Account.MaxAliasLength} chars)", false, 400, "Alias too long");
     ```
   - In `SetDescription` (after `string.IsNullOrEmpty(body)` check):
     ```csharp
     if (body.Length > Account.MaxDescriptionLength)
         return reply($"Description exceeds maximum length ({Account.MaxDescriptionLength} chars)", false, 400, "Description too long");
     ```
   - In `AppendDescription` (after `string.IsNullOrEmpty(body)` check):
     ```csharp
     if ((account.Description?.Length ?? 0) + body.Length > Account.MaxDescriptionLength)
         return reply($"Description exceeds maximum length ({Account.MaxDescriptionLength} chars)", false, 400, "Description too long");
     ```
   - Add `if (CheckAccountEditingDenied(webServer, method, reply) is string editErr) return editErr;` at the top of each of the 6 routes: `BlockUser` (L321), `UnblockUser` (L339), `UnblockEveryone` (L372), `SetServer` (L379), `SetRecommendedServer` (L391), `SetAvatar` (L452) — **BEFORE** input parsing or `TryEnsureCsrfAsync`.

- [ ] **Step 4: Update existing Gateway tests that expect those 6 endpoints to run when authorized**

Update each of these 6 existing test files so they explicitly enable `AllowAccountEditing = "true"` (and restore previous value in `finally`):
1. `tests/Shiro.Tests/AccountApiDispatcherEndpointsBehaviorTests.cs`: right before line 279 (`BlockUser`), set `AppSettings.WebServer.Set("AllowAccountEditing", "true");` (lines 223–278 already verify denied paths while it is `"false"`).
2. `tests/Shiro.Tests/GatewayApiAndOpenApiTests.cs`: in `Dispatcher_SetRecommendedServer_ValidatesCachedServersPlaceId` (L164) and `Dispatcher_SetAvatar_ReflectsTaskBoolResultAccurately` (L286), save/set/restore `AllowAccountEditing = "true"`.
3. `tests/Shiro.Tests/BugHuntRound2Tests.cs`: in `AccountApiDispatcher_SetRecommendedServer_MissingOrInvalidCurrentPlaceId_Returns400BeforeCsrf` (L160), save/set/restore `AllowAccountEditing = "true"`.
4. `tests/Shiro.Tests/P1GatewayRegressionTests.cs`: in `HighE5_BlockAndUnblockUser_RejectsNonPositiveOrTraversalUserIdBeforeUpstreamCall` (L165) and `HighE10_BranchCoverage_ExportFormat_SetServer_SetRecommendedServer_Validation` (L368), save/set/restore `AllowAccountEditing = "true"`.
5. `tests/Shiro.Tests/Plan3FinalFixRegressionTests.cs`: in `AccountApiDispatcher_SetRecommendedServer_Returns400OnCachedServersPlaceIdMismatchEvenWhenPopulated` (L358), save/set/restore `AllowAccountEditing = "true"`.
6. `tests/Shiro.Tests/WebServerWorkflowBehaviorTests.cs`: in `BlockUserEndpoint_WhenAccountThrows_Returns500WithoutExceptionMessage` (L172), save/set/restore `AllowAccountEditing = "true"`.

- [ ] **Step 5: Run new + affected Gateway tests + ArchitectureTests**

Run:
```powershell
dotnet test "tests/Shiro.Tests/Shiro.Tests.csproj" --no-incremental --filter "FullyQualifiedName~Phase1GatewayHardeningTests|FullyQualifiedName~AccountApiDispatcherEndpointsBehaviorTests|FullyQualifiedName~GatewayApiAndOpenApiTests|FullyQualifiedName~BugHuntRound2Tests|FullyQualifiedName~P1GatewayRegressionTests|FullyQualifiedName~Plan3FinalFixRegressionTests|FullyQualifiedName~WebServerEndpointsAndWorkflowTests|FullyQualifiedName~SecurityHardeningTests|FullyQualifiedName~ArchitectureTests"
```
Expected: PASS (0 failures, `AccountApiDispatcher.Endpoints.cs <= 485` lines).

- [ ] **Step 6: Commit**

```powershell
git add src/Shiro/Domain/Account.cs src/Shiro/Services/Network/AccountApiDispatcher.cs src/Shiro/Services/Network/AccountApiDispatcher.Endpoints.cs tests/Shiro.Tests/Phase1GatewayHardeningTests.cs tests/Shiro.Tests/AccountApiDispatcherEndpointsBehaviorTests.cs tests/Shiro.Tests/GatewayApiAndOpenApiTests.cs tests/Shiro.Tests/BugHuntRound2Tests.cs tests/Shiro.Tests/P1GatewayRegressionTests.cs tests/Shiro.Tests/Plan3FinalFixRegressionTests.cs tests/Shiro.Tests/WebServerWorkflowBehaviorTests.cs
git commit -m "fix(gateway): enforce AllowAccountEditing, redact GetProxy credentials, and validate alias/description length"
```

---

### Task 3: Fix `WebAuthPolicy` Lockout Memory DoS, `Proxy=` Query Redaction & WebSocket Hardening

**Files:**
- Modify: `src/Shiro/Services/Auth/WebAuthPolicy.cs:70-104` and `154-164`
- Modify: `src/Shiro/Services/Network/KestrelWebServer.cs:30-37`, `142-151`, `177-205`
- Modify: `src/Shiro/Services/Network/KestrelWebServer.WebSockets.cs:17-20`, `95-157`
- Modify: `tests/Shiro.Tests/WebAuthPolicyTests.cs`
- Modify: `tests/Shiro.Tests/WebSocketGatewayTests.cs`

- [ ] **Step 1: Write failing tests in `WebAuthPolicyTests.cs` and `WebSocketGatewayTests.cs`**

1. In `tests/Shiro.Tests/WebAuthPolicyTests.cs`, add 2 tests (verify file stays `<= 550` lines):
   - `RecordAuthResult_AllLockedOutClients_StillBoundsTrackedClientMap`: simulate `WebAuthPolicy.MaxTrackedClientFailures + 50` distinct IPs each failing `WebAuthPolicy.MaxFailedAttempts` times at the same timestamp `t0`, then assert `WebAuthPolicy.TrackedClientFailureCount <= WebAuthPolicy.MaxTrackedClientFailures` (expose `internal const int MaxTrackedClientFailures = 512;` and `internal static int TrackedClientFailureCount => ClientFailures.Count;` on `WebAuthPolicy`).
   - `RedactSensitiveQuery_RedactsProxyParameter`: assert `WebAuthPolicy.RedactSensitiveQuery("/SetProxy?Account=u&Proxy=http://user:pass@1.2.3.4:8080&Password=abc")` returns `"/SetProxy?Account=u&Proxy=[REDACTED]&Password=[REDACTED]"`.

2. In `tests/Shiro.Tests/WebSocketGatewayTests.cs`, add 3 tests (file is 252 lines, will be ~380 lines `<= 550`):
   - `WebSocketEndpoint_ExceedingMaxConnections_Returns503`: start `KestrelWebServer` with a test override/limit (`internal const int MaxActiveWebSockets = 32;` plus `internal static int? TestMaxActiveWebSocketsOverride`) = 2 clients on `/ws`, verify 3rd upgrade returns HTTP `503 ServiceUnavailable`.
   - `WebSocketEndpoint_OversizedIncomingMessage_ClosesWithMessageTooBig`: connect a `ClientWebSocket`, send a `20_000`-byte text message (`> 16384`), read next frame and assert `result.MessageType == WebSocketMessageType.Close` and `result.CloseStatus == WebSocketCloseStatus.MessageTooBig`.
   - `KestrelWebServer_TwoInstances_DoNotDuplicateStaticEventBroadcasts`: start 2 `KestrelWebServer` instances on distinct ephemeral ports, connect 1 WebSocket client, trigger `AntiAfkService` / `LaunchCoordinator` static event, verify client receives **1** event message (not 2), then stop one instance and verify the remaining instance still receives **1** event message.

- [ ] **Step 2: Run new tests to verify failure**

Run:
```powershell
dotnet test "tests/Shiro.Tests/Shiro.Tests.csproj" --no-incremental --filter "FullyQualifiedName~WebAuthPolicyTests|FullyQualifiedName~WebSocketGatewayTests"
```
Expected: FAIL on the 5 new tests (compile fail until internal test hooks exist, then assertion fail).

- [ ] **Step 3: Fix `WebAuthPolicy.cs`**

1. Make `MaxTrackedClientFailures` `internal const int` and expose `internal static int TrackedClientFailureCount => ClientFailures.Count;`.
2. In `EvictClientFailuresIfNeeded(string incomingKey, DateTimeOffset now)` (after removing `oldestNonLocked` at line 103), add a second eviction pass when `ClientFailures.Count > targetMax` (which happens when all entries are currently locked):
```csharp
if (ClientFailures.Count > targetMax)
{
    int stillExcess = ClientFailures.Count - targetMax;
    var soonestExpiringLocked = ClientFailures
        .Where(kvp => !string.Equals(kvp.Key, incomingKey, StringComparison.OrdinalIgnoreCase))
        .OrderBy(kvp => kvp.Value.LockedUntilUtc)
        .ThenBy(kvp => kvp.Value.LastFailureUtc)
        .Take(stillExcess)
        .Select(kvp => kvp.Key)
        .ToList();

    foreach (string key in soonestExpiringLocked)
    {
        ClientFailures.TryRemove(key, out _);
    }
}
```
3. In `RedactSensitiveQuery` (line 161), extend the regex alternation to include `Proxy`:
```csharp
@"([?&](?:Password|token|Cookie|Proxy)=)[^&\s#]*"
```

- [ ] **Step 4: Harden `KestrelWebServer.cs` and `KestrelWebServer.WebSockets.cs`**

1. In `src/Shiro/Services/Network/KestrelWebServer.cs`:
   - Add static ref-counted event subscription management:
     ```csharp
     private static readonly System.Threading.Lock _eventHookLock = new();
     private static int _eventHookRefCount;
     private bool _hasEventHook;
     ```
   - Add private helpers `AcquireDomainEventHooks()` and `ReleaseDomainEventHooks()`:
     ```csharp
     private void AcquireDomainEventHooks()
     {
         lock (_eventHookLock)
         {
             if (_hasEventHook) return;
             _hasEventHook = true;
             if (++_eventHookRefCount == 1)
             {
                 AntiAfkService.StatusChanged += OnAntiAfkStatusChanged;
                 RobloxSingletonService.StatusChanged += OnMultiRobloxStatusChanged;
                 LaunchCoordinator.LaunchProgressChanged += OnLaunchProgressChanged;
             }
         }
     }

     private void ReleaseDomainEventHooks()
     {
         lock (_eventHookLock)
         {
             if (!_hasEventHook) return;
             _hasEventHook = false;
             if (_eventHookRefCount > 0 && --_eventHookRefCount == 0)
             {
                 AntiAfkService.StatusChanged -= OnAntiAfkStatusChanged;
                 RobloxSingletonService.StatusChanged -= OnMultiRobloxStatusChanged;
                 LaunchCoordinator.LaunchProgressChanged -= OnLaunchProgressChanged;
             }
         }
     }
     ```
   - Call `AcquireDomainEventHooks()` in `StartAsync()` (replacing lines 145–147) and `ReleaseDomainEventHooks()` in `StopAsync()` (replacing lines 181–183).

2. In `src/Shiro/Services/Network/KestrelWebServer.WebSockets.cs`:
   - Add constants/test seam:
     ```csharp
     internal const int MaxActiveWebSockets = 32;
     internal const int MaxIncomingWebSocketMessageBytes = 16384;
     private static readonly AsyncLocal<int?> _testMaxActiveWebSocketsOverride = new();
     internal static int? TestMaxActiveWebSocketsOverride
     {
         get => _testMaxActiveWebSocketsOverride.Value;
         set => _testMaxActiveWebSocketsOverride.Value = value;
     }
     private static int _reservedWebSocketSlots;
     ```
   - In `HandleWebSocketEndpointAsync`, **after** auth succeeds and **before** `AcceptWebSocketAsync()`, atomically reserve a slot using an `Interlocked` compare-exchange loop against `int maxSockets = Math.Max(1, TestMaxActiveWebSocketsOverride ?? MaxActiveWebSockets);`. If full, return HTTP `503 ServiceUnavailable`:
     ```csharp
     bool slotReserved = TryReserveWebSocketSlot(Math.Max(1, TestMaxActiveWebSocketsOverride ?? MaxActiveWebSockets));
     if (!slotReserved)
     {
         await WriteWebSocketErrorAsync(
             context,
             StatusCodes.Status503ServiceUnavailable,
             "Too many active WebSocket connections.",
             isV2).ConfigureAwait(false);
         return;
     }
     ```
     Release the reserved slot (`Interlocked.Decrement(ref _reservedWebSocketSlots)`) in the `finally` block (guaranteed even if `AcceptWebSocketAsync` throws!).
   - In the receive loop (lines 129–132), reject oversized frames instead of silently dropping bytes:
     ```csharp
     if (frameStream.Length + result.Count > MaxIncomingWebSocketMessageBytes)
     {
         await CloseSafeAsync(
             socket,
             gate,
             WebSocketCloseStatus.MessageTooBig,
             "Message exceeds 16 KB limit").ConfigureAwait(false);
         break;
     }
     if (result.Count > 0)
         frameStream.Write(buffer, 0, result.Count);
     if (!result.EndOfMessage) continue;
     ```

- [ ] **Step 5: Run tests + ArchitectureTests**

Run:
```powershell
dotnet test "tests/Shiro.Tests/Shiro.Tests.csproj" --no-incremental --filter "FullyQualifiedName~WebAuthPolicyTests|FullyQualifiedName~WebSocketGatewayTests|FullyQualifiedName~WebSocketBroadcastOptimizationTests|FullyQualifiedName~ArchitectureTests"
```
Expected: PASS (0 failures).

- [ ] **Step 6: Commit**

```powershell
git add src/Shiro/Services/Auth/WebAuthPolicy.cs src/Shiro/Services/Network/KestrelWebServer.cs src/Shiro/Services/Network/KestrelWebServer.WebSockets.cs tests/Shiro.Tests/WebAuthPolicyTests.cs tests/Shiro.Tests/WebSocketGatewayTests.cs
git commit -m "fix(security): bound auth lockout map, redact Proxy query, and harden WebSocket cap/frame/lifecycle"
```

---

### Task 4: Eliminate Plaintext Secrets in Static Caches & Browser Window Globals

**Files:**
- Modify: `src/Shiro/Services/Auth/RobloxHbaService.cs:33-60`, `345-371`
- Modify: `src/Shiro/Services/Network/ProxyBindingService.cs:1-35`, `146-187`, `413-426`
- Modify: `src/Shiro/Services/Launch/RobloxDirectLauncher.CookieGuard.cs:16`, `87`, `236`, `400-405`
- Modify: `src/Shiro/Services/Auth/HbaBrowserScripts.cs:15-160`
- Modify: `src/Shiro/Services/Auth/FastLoginScripts.cs:385-415`
- Create: `tests/Shiro.Tests/Phase1SecretHygieneTests.cs`

- [ ] **Step 1: Write failing secret-hygiene tests in `tests/Shiro.Tests/Phase1SecretHygieneTests.cs`**

Create `tests/Shiro.Tests/Phase1SecretHygieneTests.cs` with concrete assertions for all 4 components (Task 5 and Task 6 will also add focused tests here; keep total file `<= 350` lines):
1. `RobloxHbaService_KeyCache_DoesNotStorePlaintextPrivateKeyAsDictionaryKey`: generate a fresh ECDSA P-256 PKCS#8 base64 key, call `TryBuildBoundAuthToken(...)`, inspect `RobloxHbaService.GetCachedKeyIdsForTesting()`, assert count == 1, assert the stored key is a 64-char hex SHA-256 digest and does **NOT** equal/contain the plaintext PKCS#8 string, and verify `ClearCachedKey(pkcs8)` returns `true` and empties the cache.
2. `ProxyBindingService_ClientCache_DoesNotStorePlaintextProxyCredentialsAsKey`: call `ProxyBindingService.GetClient("http://secretuser:secretpass@127.0.0.1:9999")` and `GetNoRedirectClient`, inspect `ProxyBindingService.GetCacheKeysForTesting()`, assert keys do **NOT** contain `"secretuser"` or `"secretpass"`, assert `PruneClients(new[] { "http://secretuser:secretpass@127.0.0.1:9999" })` retains them, and `PruneClients(Array.Empty<string>())` removes them.
3. `RobloxDirectLauncher_CookieGuard_DoesNotHoldPlaintextStringFieldForLastInjectedToken`: reflect on `typeof(RobloxDirectLauncher)` non-public static fields — assert `_lastInjectedToken` no longer exists and `_protectedLastInjectedToken` has type `byte[]`.
4. `HbaBrowserScripts_BuildCollectionScript_DoesNotAssignPrivateKeyOntoWindowGlobal`: inspect `HbaBrowserScripts.BuildCollectionScript()` — assert it contains `window.__shiroHbaState` (preserving contract) and does **NOT** assign `privateKeyPkcs8` onto `window.__shiroHbaState` or declare `privateKeyPkcs8:` inside the `window.__shiroHbaState = { ... }` object literal; also check `FastLoginScripts.BuildHbaReadScript()` still reads via `__shiroSyncHbaFromIndexedDb()`.

- [ ] **Step 2: Run tests to verify failure**

Run:
```powershell
dotnet test "tests/Shiro.Tests/Shiro.Tests.csproj" --no-incremental --filter "FullyQualifiedName~Phase1SecretHygieneTests"
```
Expected: FAIL.

- [ ] **Step 3: Hash `RobloxHbaService.CachedEcParameters` keys with SHA-256**

In `src/Shiro/Services/Auth/RobloxHbaService.cs`:
- Add helper:
```csharp
private static string ComputeKeyCacheId(string trimmedKey) =>
    Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(trimmedKey)));

internal static IReadOnlyCollection<string> GetCachedKeyIdsForTesting()
{
    lock (KeyCacheLock)
        return new List<string>(CachedEcParameters.Keys);
}
```
- Update `ClearCachedKey(string? hbaPrivateKey)` (line 55): look up/remove `ComputeKeyCacheId(hbaPrivateKey.Trim())`.
- Update `AcquireCachedEcdsa(string trimmedKey)` (lines 349 & 366): compute `string cacheId = ComputeKeyCacheId(trimmedKey);` and key `CachedEcParameters` by `cacheId` instead of `trimmedKey`.

- [ ] **Step 4: Hash `ProxyBindingService._proxyClients` keys with SHA-256**

In `src/Shiro/Services/Network/ProxyBindingService.cs` (add `using System.Security.Cryptography;` and `using System.Text;`, watch 485-line budget — currently 465 lines!):
- Replace `NoRedirectKeyPrefix` usage with a concise helper (~6 lines):
```csharp
private static string ComputeProxyCacheKey(string normalizedProxy, bool allowAutoRedirect)
{
    string canonical = normalizedProxy.Trim();
    string hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(canonical)));
    return allowAutoRedirect ? hash : $"{NoRedirectKeyPrefix}{hash}";
}

internal static IReadOnlyCollection<string> GetCacheKeysForTesting() => _proxyClients.Keys.ToArray();
```
- Update `GetNoRedirectClient` (L153): `ComputeProxyCacheKey(effectiveProxy, allowAutoRedirect: false)`.
- Update `GetClient(string)` (L167): `ComputeProxyCacheKey(normalized, allowAutoRedirect: true)`.
- Update `PruneClients(IEnumerable<string> activeProxies)` (L415–425):
```csharp
var activeKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
foreach (string proxy in NormalizeDistinctProxies(activeProxies))
{
    activeKeys.Add(ComputeProxyCacheKey(proxy, allowAutoRedirect: true));
    activeKeys.Add(ComputeProxyCacheKey(proxy, allowAutoRedirect: false));
}
foreach (var entry in _proxyClients)
{
    if (!activeKeys.Contains(entry.Key) && _proxyClients.TryRemove(entry.Key, out var removed))
        RetireClient(removed);
}
```
Verify `ProxyBindingService.cs` stays `<= 478` lines.

- [ ] **Step 5: Protect `_lastInjectedToken` in `RobloxDirectLauncher.CookieGuard.cs`**

In `src/Shiro/Services/Launch/RobloxDirectLauncher.CookieGuard.cs` (currently **479 / 485 lines**, net diff must be `<= +2` lines!):
- Line 16: replace `private static string? _lastInjectedToken;` with `private static byte[]? _protectedLastInjectedToken;`
- Line 87 (`ResetCookieGuardStateForTest`): replace `_lastInjectedToken = null;` with `InMemorySecretProtector.ZeroAndClear(ref _protectedLastInjectedToken);`
- Line 236 (`InjectAccountCookie`): replace `_lastInjectedToken = token;` with:
  ```csharp
  InMemorySecretProtector.ZeroAndClear(ref _protectedLastInjectedToken);
  _protectedLastInjectedToken = InMemorySecretProtector.Protect(token);
  ```
- Lines 400–404 (`SyncRotatedClientCookieIfApplicable`):
  ```csharp
  injectedToken = _protectedLastInjectedToken != null ? InMemorySecretProtector.Unprotect(_protectedLastInjectedToken) : null;
  expectedTrackerId = _lastInjectedTrackerId;
  _lastInjectedUsername = null;
  InMemorySecretProtector.ZeroAndClear(ref _protectedLastInjectedToken);
  ```
Net line change: **+1 line** (480 / 485 lines).

- [ ] **Step 6: Keep `privateKeyPkcs8` closure-only in `HbaBrowserScripts.cs` and update `FastLoginScripts.cs`**

1. In `src/Shiro/Services/Auth/HbaBrowserScripts.cs` (`BuildCollectionScript()`):
   - Remove `privateKeyPkcs8: ''` from `window.__shiroHbaState = { ... }`; store `let closurePrivateKeyPkcs8 = '';` strictly inside the IIFE closure.
   - Replace every write `window.__shiroHbaState.privateKeyPkcs8 = ...` with `closurePrivateKeyPkcs8 = ...` (and only include `privateKeyPkcs8: closurePrivateKeyPkcs8` in the `window.chrome.webview.postMessage(...)` payload sent directly to the C# host, NEVER on `window.__shiroHbaState`).
   - Update `window.__shiroSyncHbaFromIndexedDb` so it reads from IndexedDB and posts via `postMessage` without writing `privateKeyPkcs8` onto `window.__shiroHbaState`.
   - Keep `window.__shiroHbaState` holding only non-secret state (`publicKeySpki`, `tokenTemplate`, `userAgent`, `authoritativeKey`, `saiBound`) so `RobloxHbaServiceBehaviorTests.cs:117` and `BuildRestoreModerationScript` continue working identically.
2. In `src/Shiro/Services/Auth/FastLoginScripts.cs` (L390–410, `BuildHbaReadScript`):
   - Call `window.__shiroSyncHbaFromIndexedDb()` (which posts the full capture including `privateKeyPkcs8` via `postMessage` to C#) and return only the non-secret `window.__shiroHbaState` metadata JSON (do NOT read `s.privateKeyPkcs8` from `window.__shiroHbaState`).

- [ ] **Step 7: Run tests + existing HBA / Proxy / CookieGuard / Architecture tests**

Run:
```powershell
dotnet test "tests/Shiro.Tests/Shiro.Tests.csproj" --no-incremental --filter "FullyQualifiedName~Phase1SecretHygieneTests|FullyQualifiedName~RobloxHbaService|FullyQualifiedName~Proxy|FullyQualifiedName~CookieGuard|FullyQualifiedName~FastLogin|FullyQualifiedName~ArchitectureTests"
```
Expected: PASS (0 failures, all source files `<= 485` lines).

- [ ] **Step 8: Commit**

```powershell
git add src/Shiro/Services/Auth/RobloxHbaService.cs src/Shiro/Services/Network/ProxyBindingService.cs src/Shiro/Services/Launch/RobloxDirectLauncher.CookieGuard.cs src/Shiro/Services/Auth/HbaBrowserScripts.cs src/Shiro/Services/Auth/FastLoginScripts.cs tests/Shiro.Tests/Phase1SecretHygieneTests.cs
git commit -m "fix(security): remove plaintext secrets from static caches, CookieGuard field, and browser globals"
```

---

### Task 5: Close All 5 `LogRedactor` Secret Gaps

**Files:**
- Modify: `src/Shiro/Services/Storage/LogRedactor.cs`
- Modify: `tests/Shiro.Tests/Phase1SecretHygieneTests.cs`

- [ ] **Step 1: Write failing `LogRedactor` unit tests covering all 5 secret leaks**

Append a `[Theory]` + `[Fact]` in `tests/Shiro.Tests/Phase1SecretHygieneTests.cs` asserting `LogRedactor.Redact(input)` scrubs all secret values while preserving safe context:
1. Roblox launch URI `gameinfo:eyJhbGci...secretTicket123+launchtime:12345` -> does NOT contain `secretTicket123`, retains `gameinfo:[REDACTED]`.
2. Custom gateway auth headers `X-Shiro-Auth: MySuperSecret99` and `X-RAM-Auth: LegacyPass42` -> neither secret remains.
3. Proxy header `Proxy-Authorization: Basic dXNlcjpwYXNz` -> `dXNlcjpwYXNz` removed.
4. Query/URL-encoded/4-part proxy credentials:
   - `https://localhost:7963/SetProxy?Account=u&Proxy=http%3A%2F%2Fbob%3Apw123%401.2.3.4%3A8080` -> neither `bob` nor `pw123` remains.
   - `proxy 1.2.3.4:8080:bob:pw123` -> neither `bob` nor `pw123` remains.
5. JSON private key fields `"HbaPrivateKeyPkcs8":"MIGHAgEAMBMGByqGSM49..."` and `"privateKeyPkcs8":"MIGHAgEAMBMGByqGSM49..."` -> neither key value remains.

- [ ] **Step 2: Run tests to verify failure**

Run:
```powershell
dotnet test "tests/Shiro.Tests/Shiro.Tests.csproj" --no-incremental --filter "FullyQualifiedName~Phase1SecretHygieneTests"
```
Expected: FAIL on the 5 new redaction cases.

- [ ] **Step 3: Add `[GeneratedRegex]` patterns to `src/Shiro/Services/Storage/LogRedactor.cs`**

In `src/Shiro/Services/Storage/LogRedactor.cs` (currently 189 lines — plenty of room under 485):
1. Add compiled `[GeneratedRegex]` patterns (with `RegexOptions.IgnoreCase | RegexOptions.CultureInvariant`, timeout 250ms matching existing conventions in `LogRedactor.cs`):
   - `GameInfoTicketRegex`: `\bgameinfo:[^\s+;&"'<>]+` -> `gameinfo:[REDACTED]`
   - `AuthHeaderRegex`: `\b(X-Shiro-Auth|X-RAM-Auth|Proxy-Authorization)\s*[:=]\s*(?:Basic\s+|Bearer\s+)?[^\s,;"'\r\n]+` -> `$1: [REDACTED]`
   - `QueryProxyParamRegex`: `([?&]Proxy=)[^&\s#"'<>]+` -> `$1[REDACTED]`
   - `EncodedProxyCredsRegex`: `((?:https?|socks[45]h?)%3A%2F%2F)[^%\s@&]+%3A[^%\s@&]+%40` -> `$1[REDACTED]%40`
   - `FourPartProxyRegex`: `(?<![\w.])(\d{1,3}(?:\.\d{1,3}){3}:\d{2,5}):([^\s:@"'<>]{1,128}):([^\s:@"'<>]{1,128})(?=$|[\s,;"'<>])` -> `$1:[REDACTED]:[REDACTED]`
   - Extend existing private-key JSON regex (or add `HbaPrivateKeyJsonRegex`): `("(?:HbaPrivateKey|HbaPrivateKeyPkcs8|privateKeyPkcs8|privateKey)"\s*:\s*")[^"]*(")` -> `$1[REDACTED]$2`
2. Apply all new patterns inside `LogRedactor.Redact(string? input)` in order (keep existing patterns working and zero false positives on timestamps like `12:34:56` because `FourPartProxyRegex` strictly requires an IPv4 dotted quad before `:port:user:pass`).

- [ ] **Step 4: Run tests + existing SecurityHardening / LogRedactor / Architecture tests**

Run:
```powershell
dotnet test "tests/Shiro.Tests/Shiro.Tests.csproj" --no-incremental --filter "FullyQualifiedName~Phase1SecretHygieneTests|FullyQualifiedName~SecurityHardeningTests|FullyQualifiedName~LogRedactor|FullyQualifiedName~ArchitectureTests"
```
Expected: PASS (0 failures).

- [ ] **Step 5: Commit**

```powershell
git add src/Shiro/Services/Storage/LogRedactor.cs tests/Shiro.Tests/Phase1SecretHygieneTests.cs
git commit -m "fix(security): redact gameinfo tickets, auth headers, proxy credentials, and HbaPrivateKeyPkcs8 in logs"
```

---

### Task 6: Block GeoIP SSRF Redirect Bypass

**Files:**
- Modify: `src/Shiro/Services/Network/SharedHttpClient.cs` (add `GetStringNoRedirectAsync`)
- Modify: `src/Shiro/Services/Network/RobloxServerQueryService.cs:368` and `392` (**0 net lines added!** File is 484 / 485 lines!)
- Modify: `tests/Shiro.Tests/Phase1SecretHygieneTests.cs`

- [ ] **Step 1: Write failing GeoIP redirect-rejection tests**

Add 2 tests to `tests/Shiro.Tests/Phase1SecretHygieneTests.cs`:
1. `SharedHttpClient_GetStringNoRedirectAsync_Rejects3xxRedirectResponse`: inject a `FakeHttpHandler` returning `302 Found` with `Location: http://169.254.169.254/latest/meta-data/` into `SharedHttpClient.GetStringNoRedirectCoreAsync(client, url, CancellationToken.None)` and assert it throws `HttpRequestException` (and never follows the redirect).
2. `SharedHttpClient_GetStringNoRedirectAsync_ReturnsBodyOn200Ok`: assert `200 OK` returns the response string cleanly.

- [ ] **Step 2: Run tests to verify failure**

Run:
```powershell
dotnet test "tests/Shiro.Tests/Shiro.Tests.csproj" --no-incremental --filter "FullyQualifiedName~SharedHttpClient_GetStringNoRedirectAsync"
```
Expected: FAIL (method does not exist yet).

- [ ] **Step 3: Implement `SharedHttpClient.GetStringNoRedirectAsync` and wire `RobloxServerQueryService.cs`**

1. In `src/Shiro/Services/Network/SharedHttpClient.cs` (currently 396 lines), add:
```csharp
public static Task<string> GetStringNoRedirectAsync(
    string url,
    CancellationToken cancellationToken = default) =>
    GetStringNoRedirectCoreAsync(GetNoRedirect(), url, cancellationToken);

internal static async Task<string> GetStringNoRedirectCoreAsync(
    HttpClient client,
    string url,
    CancellationToken cancellationToken = default)
{
    using var response = await client.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
    int statusCode = (int)response.StatusCode;
    if (statusCode >= 300 && statusCode < 400)
    {
        throw new HttpRequestException($"Blocked HTTP {statusCode} redirect while querying external endpoint.");
    }
    response.EnsureSuccessStatusCode();
    return await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
}
```
2. In `src/Shiro/Services/Network/RobloxServerQueryService.cs` (MUST stay at **484 lines**, 0 net lines!):
   - Line 368: replace:
     `await SharedHttpClient.GetStringAsync(safePrimaryUrl, retryTransient: false, cancellationToken: cancellationToken).ConfigureAwait(false);`
     with:
     `await SharedHttpClient.GetStringNoRedirectAsync(safePrimaryUrl, cancellationToken).ConfigureAwait(false);`
   - Line 392: replace:
     `await SharedHttpClient.GetStringAsync(safeFbUrl, retryTransient: false, cancellationToken: cancellationToken).ConfigureAwait(false);`
     with:
     `await SharedHttpClient.GetStringNoRedirectAsync(safeFbUrl, cancellationToken).ConfigureAwait(false);`

- [ ] **Step 4: Run GeoIP + ServerQuery + Architecture tests**

Run:
```powershell
dotnet test "tests/Shiro.Tests/Shiro.Tests.csproj" --no-incremental --filter "FullyQualifiedName~Phase1SecretHygieneTests|FullyQualifiedName~RobloxServerQueryService|FullyQualifiedName~GeoIp|FullyQualifiedName~ArchitectureTests"
```
Expected: PASS (0 failures, `RobloxServerQueryService.cs` remains `<= 485` lines).

- [ ] **Step 5: Commit**

```powershell
git add src/Shiro/Services/Network/SharedHttpClient.cs src/Shiro/Services/Network/RobloxServerQueryService.cs tests/Shiro.Tests/Phase1SecretHygieneTests.cs
git commit -m "fix(security): block HTTP redirects on GeoIP lookups to prevent SSRF redirect bypass"
```

---

### Task 7: Eliminate Moderation False Positives & Preserve Session Validity on HTTP 407/408

**Files:**
- Modify: `src/Shiro/Services/Network/RobloxModerationService.cs:199-215`, `317-353`
- Modify: `src/Shiro/Services/Auth/RobloxAccountSessionClient.Validation.cs:125-145`
- Modify: `tests/Shiro.Tests/Network/RobloxModerationServiceBehaviorTests.cs`
- Modify: `tests/Shiro.Tests/ValidateSessionTests.cs`

- [ ] **Step 1: Write failing tests in `RobloxModerationServiceBehaviorTests.cs` and `ValidateSessionTests.cs`**

1. In `tests/Shiro.Tests/Network/RobloxModerationServiceBehaviorTests.cs`, add 3 tests:
   - `ParseModerationText_BenignFooterOrCookieBannerHtml_DoesNotFalsePositive`: assert `IsBanned == false` for:
     - `"<footer>Read our Community Standards and Terms of Use</footer>"`
     - `"<div>Click I Agree to accept cookies</div>"`
     - `"<div>Reason: scheduled maintenance window</div>"`
     - `"<div>Roblox home page - check out Community Standards</div>"`
   - `ParseModerationText_MultiDigitBanDays_DoesNotMisclassifyAsShortBan`: assert:
     - `"Reviewed: today. Ban 21 days"` -> `LockedOrBannedBadge` (NOT `BanOneDayShort`)
     - `"Reviewed: today. Ban 17 days"` -> `LockedOrBannedBadge` (NOT `BanSevenDaysShort`)
     - `"Reviewed: today. Ban 13 days"` -> `LockedOrBannedBadge` (NOT `BanThreeDaysShort`)
     - `"Reviewed: today. Ban 14 days"` -> `BanFourteenDaysShort` (NOT `BanOneDayShort` or `BanThreeDaysShort`)
   - `CheckModerationStatusAsync_Home200WithNullRequestMessageAndFooterHtml_ReturnsNotBanned`: enqueue `200 "{}"` for `v1/not-approved` and `200 "<html><footer>Community Standards - I Agree - Reason: cookies</footer></html>"` with `response.RequestMessage = null` for `/home`, assert `IsBanned == false`.

2. In `tests/Shiro.Tests/ValidateSessionTests.cs`, add `[Theory]` for `HttpStatusCode.ProxyAuthenticationRequired` (407) and `HttpStatusCode.RequestTimeout` (408):
   - Given an account starting with `Valid = true`, when `users.roblox.com/v1/users/authenticated` returns `407` or `408`, `ValidateSessionAsync` preserves `account.Valid == true` and returns `true` (does NOT mark the session dead on proxy auth / request timeout).

- [ ] **Step 2: Run tests to verify failure**

Run:
```powershell
dotnet test "tests/Shiro.Tests/Shiro.Tests.csproj" --no-incremental --filter "FullyQualifiedName~RobloxModerationServiceBehaviorTests|FullyQualifiedName~ValidateSessionTests"
```
Expected: FAIL on the new tests.

- [ ] **Step 3: Tighten `RobloxModerationService.cs`**

1. At lines 199–204 of `src/Shiro/Services/Network/RobloxModerationService.cs`, fail safe when `finalUrl` is empty OR not a moderation URL:
```csharp
string finalUrl = response.RequestMessage?.RequestUri?.AbsoluteUri ?? string.Empty;
if (string.IsNullOrEmpty(finalUrl) || !RobloxModerationHelper.IsModerationOrLockedUrl(finalUrl))
{
    return (false, null, null);
}
```
2. At lines 317–342 (`ParseModerationText`):
   - Remove the 3 overly broad markers (`"community standards"`, `"reason:"`, `"i agree"`) from `hasModerationMarker`:
     ```csharp
     bool hasModerationMarker =
         text.Contains("reviewed:", StringComparison.Ordinal) ||
         text.Contains("moderator note", StringComparison.Ordinal) ||
         text.Contains("terms of use violation", StringComparison.Ordinal) ||
         text.Contains("your account has been", StringComparison.Ordinal) ||
         text.Contains("punishment:", StringComparison.Ordinal) ||
         text.Contains("reactivate my account", StringComparison.Ordinal) ||
         text.Contains("account locked", StringComparison.Ordinal);
     ```
   - Add a small boundary-safe helper `ContainsExactBanDays(string text, int days)` and check `14`, `7`, `3`, `1` in descending order:
     ```csharp
     private static bool ContainsExactBanDays(string text, int days)
     {
         string tokenSpace = $"{days} day";
         string tokenHyphen = $"{days}-day";
         return ContainsStandaloneToken(text, tokenSpace) || ContainsStandaloneToken(text, tokenHyphen);
     }

     private static bool ContainsStandaloneToken(string text, string token)
     {
         int idx = 0;
         while ((idx = text.IndexOf(token, idx, StringComparison.Ordinal)) >= 0)
         {
             bool leftOk = idx == 0 || !char.IsAsciiDigit(text[idx - 1]);
             if (leftOk)
                 return true;
             idx += token.Length;
         }
         return false;
     }
     ```
   - Use `ContainsExactBanDays(text, 14)` -> `BanFourteenDaysShort`, `7` -> `BanSevenDaysShort`, `3` -> `BanThreeDaysShort`, `1` -> `BanOneDayShort`.

- [ ] **Step 4: Preserve `account.Valid` on HTTP 407 & 408 in `RobloxAccountSessionClient.Validation.cs`**

Open `src/Shiro/Services/Auth/RobloxAccountSessionClient.Validation.cs` lines 125–145 (where `status >= 500 || res.StatusCode == HttpStatusCode.TooManyRequests` preserves `account.Valid`) and extend the transient condition to include `HttpStatusCode.ProxyAuthenticationRequired` (407) and `HttpStatusCode.RequestTimeout` (408):
```csharp
if (status >= 500 ||
    res.StatusCode == HttpStatusCode.TooManyRequests ||
    res.StatusCode == HttpStatusCode.ProxyAuthenticationRequired ||
    res.StatusCode == HttpStatusCode.RequestTimeout)
{
    return account.Valid;
}
```
(Preserve the exact surrounding logging/return structure in lines 125–145.)

- [ ] **Step 5: Run Moderation + Validation + AuditSessionAuditor + Architecture tests**

Run:
```powershell
dotnet test "tests/Shiro.Tests/Shiro.Tests.csproj" --no-incremental --filter "FullyQualifiedName~RobloxModerationServiceBehaviorTests|FullyQualifiedName~ValidateSessionTests|FullyQualifiedName~AuditSessionAuditorRegressionTests|FullyQualifiedName~ArchitectureTests"
```
Expected: PASS (0 failures).

- [ ] **Step 6: Commit**

```powershell
git add src/Shiro/Services/Network/RobloxModerationService.cs src/Shiro/Services/Auth/RobloxAccountSessionClient.Validation.cs tests/Shiro.Tests/Network/RobloxModerationServiceBehaviorTests.cs tests/Shiro.Tests/ValidateSessionTests.cs
git commit -m "fix(auth): eliminate moderation false positives and preserve session validity on HTTP 407/408"
```

---

### Task 8: Zero-Decryption Cached Metadata on UI & Background Maintenance Hot Paths

**Files:**
- Modify: `src/Shiro/Services/Storage/InMemorySecretProtector.cs` (add `Interlocked.Increment` counter `UnprotectCallCount` for deterministic verification)
- Modify: `src/Shiro/Domain/Account.cs:66-72` (add `private bool _hasHbaPrivateKey;`)
- Modify: `src/Shiro/Domain/Account.SessionState.CookieRotation.cs:69-102` (expose `internal bool HasSecurityToken`, `internal int SecurityTokenPayloadLength`; reset `_hasHbaPrivateKey = false;` in `ClearSensitiveMemory()`)
- Modify: `src/Shiro/Domain/Account.HbaBinding.cs:100-159` (maintain `_hasHbaPrivateKey` in `HbaPrivateKey` setter and `MigrateProtectedFieldsFromDictionary()`, update `HasHbaBinding` to read `_hasHbaPrivateKey` with zero `Unprotect` calls)
- Modify: `src/Shiro/ViewModels/AccountViewModel.cs:197`
- Modify: `src/Shiro/ViewModels/AccountViewModel.Styling.cs:18`
- Modify: `src/Shiro/Services/Auth/AccountSessionMaintenanceService.cs:40-52`
- Create: `tests/Shiro.Tests/Phase1HotPathMetadataTests.cs`

- [ ] **Step 1: Write failing zero-decryption tests in `tests/Shiro.Tests/Phase1HotPathMetadataTests.cs`**

Create `tests/Shiro.Tests/Phase1HotPathMetadataTests.cs` verifying:
1. `AccountViewModel_HotPathProperties_PerformZeroSecretDecryptions`: create an `Account` with a real `.ROBLOSECURITY` token and a real PKCS#8 `HbaPrivateKey`, wrap in `new AccountViewModel(acc)`, capture `long before = InMemorySecretProtector.UnprotectCallCount;`, read `vm.Valid`, `vm.SessionHealth`, `vm.SessionStatusText`, `vm.SessionTooltip`, `acc.HasHbaBinding`, `acc.HasSecurityToken`, `acc.SecurityTokenPayloadLength` 50 times in a loop, and assert `InMemorySecretProtector.UnprotectCallCount == before` (exact delta `0`), plus concrete assertions on their values (`Assert.True(vm.Valid)`, `Assert.True(acc.HasHbaBinding)`, `Assert.True(acc.HasSecurityToken)`).
2. `Account_HasHbaBinding_MigratesLegacyFieldOnceThenZeroDecryptions`: create an `Account` whose `Fields["HbaPrivateKey"] = "legacy-key-value"`, verify first `HasHbaBinding` returns `true` and removes `Fields["HbaPrivateKey"]`, then capture `before`, read `HasHbaBinding` 20 times, assert delta == `0`, then call `ClearSensitiveMemory()` and assert `HasHbaBinding == false`.
3. `RefreshStaleCookiesAsync_FreshAccounts_PerformZeroSecretDecryptions`: populate `AccountRepository` with 20 accounts having `SecurityToken` set and `LastAttemptedRefresh = DateTime.Now` (within 24h), capture `before`, run `AccountSessionMaintenanceService.Default.RefreshStaleCookiesAsync(gate, CancellationToken.None)`, assert `InMemorySecretProtector.UnprotectCallCount == before` (exact delta `0`).

- [ ] **Step 2: Run tests to verify failure**

Run:
```powershell
dotnet test "tests/Shiro.Tests/Shiro.Tests.csproj" --no-incremental --filter "FullyQualifiedName~Phase1HotPathMetadataTests"
```
Expected: FAIL (until `UnprotectCallCount` exists and hot paths stop calling `SecurityToken`/`HbaPrivateKey` getters).

- [ ] **Step 3: Implement cached metadata + hot-path updates**

1. In `src/Shiro/Services/Storage/InMemorySecretProtector.cs`:
   - Add `private static long _unprotectCallCount;` and `internal static long UnprotectCallCount => Volatile.Read(ref _unprotectCallCount);`.
   - Inside `Unprotect(ReadOnlySpan<byte> protectedBlob)`, right after `if (protectedBlob.IsEmpty) return string.Empty;`, add `Interlocked.Increment(ref _unprotectCallCount);`.

2. In `src/Shiro/Domain/Account.cs` (line 70):
   - Add `private bool _hasHbaPrivateKey;` next to `private bool _hasSecurityToken;`.

3. In `src/Shiro/Domain/Account.SessionState.CookieRotation.cs`:
   - Below `GetCachedTokenMetadata()` (line 75), add:
     ```csharp
     internal bool HasSecurityToken => GetCachedTokenMetadata().HasToken;
     internal int SecurityTokenPayloadLength => GetCachedTokenMetadata().PayloadLength;
     ```
   - Inside `ClearSensitiveMemory()` (line 97), add `_hasHbaPrivateKey = false;` right after `InMemorySecretProtector.ZeroAndClear(ref _protectedHbaPrivateKey);`.

4. In `src/Shiro/Domain/Account.HbaBinding.cs`:
   - In `HbaPrivateKey` setter (line 111), inside `lock (CookieSyncRoot)`:
     ```csharp
     bool hasKey = !string.IsNullOrEmpty(normalized);
     _protectedHbaPrivateKey = hasKey ? InMemorySecretProtector.Protect(normalized) : null;
     _hasHbaPrivateKey = hasKey;
     ```
   - In `MigrateProtectedFieldsFromDictionary()` (lines 146–148), inside `lock (CookieSyncRoot)`:
     ```csharp
     if (_protectedHbaPrivateKey == null)
         _protectedHbaPrivateKey = InMemorySecretProtector.Protect(legacyPriv);
     _hasHbaPrivateKey = _protectedHbaPrivateKey != null;
     ```
   - Replace line 158 (`HasHbaBinding`):
     ```csharp
     [JsonIgnore]
     public bool HasHbaBinding
     {
         get
         {
             bool hasLegacy;
             lock (FieldsSyncRoot)
                 hasLegacy = Fields != null && Fields.ContainsKey(FieldHbaPrivateKey);
             if (hasLegacy)
                 MigrateProtectedFieldsFromDictionary();
             lock (CookieSyncRoot)
                 return _hasHbaPrivateKey;
         }
     }
     ```

5. In `src/Shiro/ViewModels/AccountViewModel.cs` (line 197):
   - Change `public bool Valid => !string.IsNullOrEmpty(_model.SecurityToken) && _model.Valid;`
   - To: `public bool Valid => _model.HasSecurityToken && _model.Valid;`

6. In `src/Shiro/ViewModels/AccountViewModel.Styling.cs` (line 18):
   - Change `int payloadLen = Account.GetSessionTokenPayloadLength(_model.SecurityToken);`
   - To: `int payloadLen = _model.SecurityTokenPayloadLength;`

7. In `src/Shiro/Services/Auth/AccountSessionMaintenanceService.cs` (lines 40–52):
   - Replace `.Where(a => a != null && !string.IsNullOrWhiteSpace(a.SecurityToken))` with `.Where(a => a != null && a.HasSecurityToken)` and move the `24h` check right after it (before `GetField("NoCookieRefresh")` and network-signature checks):
     ```csharp
     var candidates = snapshot
         .Where(a => a != null && a.HasSecurityToken)
         .Where(a => (DateTime.Now - a.LastAttemptedRefresh).TotalHours >= 24 || a.LastAttemptedRefresh == DateTime.MinValue)
         .Where(a => a.GetField("NoCookieRefresh") != "true")
         .Where(a => !protectCrossNetwork ||
                     string.IsNullOrWhiteSpace(a.LastBoundIpSignature) ||
                     localNetSig is "offline" or "unknown" ||
                     string.Equals(a.LastBoundIpSignature, RobloxHbaService.GetNetworkSignatureForAccount(a, localNetSig),
                         StringComparison.Ordinal))
         .OrderByDescending(a => a.Valid)
         .ThenBy(a => a.LastAttemptedRefresh)
         .Take(4)
         .ToList();
     ```

- [ ] **Step 4: Run hot-path + AccountViewModel + HBA + Architecture tests**

Run:
```powershell
dotnet test "tests/Shiro.Tests/Shiro.Tests.csproj" --no-incremental --filter "FullyQualifiedName~Phase1HotPathMetadataTests|FullyQualifiedName~AccountViewModel|FullyQualifiedName~AccountHba|FullyQualifiedName~AccountSessionState|FullyQualifiedName~ArchitectureTests"
```
Expected: PASS (0 failures).

- [ ] **Step 5: Commit**

```powershell
git add src/Shiro/Services/Storage/InMemorySecretProtector.cs src/Shiro/Domain/Account.cs src/Shiro/Domain/Account.SessionState.CookieRotation.cs src/Shiro/Domain/Account.HbaBinding.cs src/Shiro/ViewModels/AccountViewModel.cs src/Shiro/ViewModels/AccountViewModel.Styling.cs src/Shiro/Services/Auth/AccountSessionMaintenanceService.cs tests/Shiro.Tests/Phase1HotPathMetadataTests.cs
git commit -m "perf(account): eliminate per-read AES-GCM decryptions in AccountViewModel, HasHbaBinding, and cookie maintenance"
```

---

## Final Phase 1 Verification Gate

- [ ] **Step 1: Release clean build with `-warnaserror`**

Run:
```powershell
dotnet build "Shiro.sln" --configuration Release --no-incremental -warnaserror
```
Expected: Build succeeded, 0 warnings, 0 errors.

- [ ] **Step 2: Run full test suite (note: if user's live `Shiro.exe` is still running, the 2 pre-existing OS singleton-mutex tests are expected to fail while all other tests pass)**

Run:
```powershell
dotnet test "tests/Shiro.Tests/Shiro.Tests.csproj" --configuration Release --no-build --logger "console;verbosity=normal"
```
Expected: 100% pass (or if live `Shiro.exe` is open, only the 2 known `ROBLOX_singletonMutex` tests fail while every other test and all `ArchitectureTests` pass).

- [ ] **Step 3: Verify git log & working tree cleanliness**

Run:
```powershell
git status
git log --oneline -n 8
```
Expected: Clean working tree, 8 focused conventional commits matching Tasks 1–8.
