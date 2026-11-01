# Advanced Features & Ecosystem Upgrades Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Implement the 5 strategic architectural and feature upgrades across Shiro Account Manager: end-to-end HBA (Hardware-Backed Authentication) session binding, background dynamic FPS and memory throttling, remote webhook notifications (Discord & Telegram), cross-device encrypted vault export/import (AES-256-GCM + PBKDF2), and automated session health and Robux balance auditing.

**Architecture:**
1. Complete HBA ECDSA P-256 session binding (`RobloxHbaService.Binding.cs`) across auth ticket requests, cookie rotation, and FastLogin capture.
2. Build `RobloxBackgroundThrottlerService` listening to `ForegroundWindowService` focus transitions to dynamically throttle unfocused Roblox clients down to 5–10 FPS and trim standby memory via Win32 `EmptyWorkingSet`.
3. Build `WebhookNotificationService` to asynchronously dispatch non-blocking Discord embeds and Telegram Markdown alerts on account disconnects, 429 rate limits, and moderation flags.
4. Build `VaultExportService` to produce portable `.shiro-vault` files encrypted with PBKDF2-SHA256 (300,000 iterations) and AES-256-GCM, allowing seamless migration across different PCs without DPAPI dependencies.
5. Build `SessionAuditorService` using throttled concurrent HTTP workers (`SemaphoreSlim(3, 3)`) to proactively audit cookie validity, moderation flags, and aggregate Robux/Pending Robux balances.

**Tech Stack:** .NET 10.0 (win-x64), C# 13, Avalonia UI 11, `System.Security.Cryptography` (ECDSA P-256, AES-256-GCM, PBKDF2-SHA256), Win32 API (`EmptyWorkingSet`, `SetPriorityClass`), `System.Text.Json` Source Generation (`ShiroAppJson`), xUnit.

**Spec:** The comprehensive upgrade audit identified 5 strategic enhancements:
1. HBA Session Binding: Roblox enforces `x-bound-auth-token` headers on modern endpoints; Shiro needs full cryptographic signing during ticket acquisition and session recovery.
2. Resource Throttler: Multi-client farming (10–30 accounts) strains GPU/RAM; background windows should automatically drop to 5–10 FPS and purge unneeded memory.
3. Webhook Remote Alerts: Long-running AFK farming requires immediate notifications to Discord / Telegram when clients crash, disconnect (Errors 277/268), or hit rate limits (429).
4. Cross-Device Vault Migration: DPAPI is tied to a single Windows user profile; users need password-protected encrypted vault export/import.
5. Session Health & Balance Auditor: Proactive detection of expired cookies and live Robux/Pending Robux balance aggregation across all stored accounts.

## Global Constraints
- Maximum file length across all created and modified files must remain strictly under 500 lines per file (enforced by `ArchitectureTests.NoSourceCodeFiles_ShouldExceed500Lines`).
- Partial class budgets in `ArchitectureTests.NoClass_ShouldExceedLineBudget_AcrossPartials` must not be exceeded (`MainViewModel`: 3500, `Account`: 2900, `AccountManager`: 3400).
- Preserve 100% backward compatibility for all existing unit tests (529 passing).
- Zero reflection-based JSON serialization: all DTOs and payloads must be registered with `ShiroAppJson` to maintain Native AOT compatibility.
- MVVM separation: ViewModels and Services must never directly reference Avalonia Views or Windows.

## Review Focus
1. `RobloxHbaService.SignPayload` must produce valid DER/IEEE P1363 ECDSA signatures compatible with Roblox's public verification keys and survive clock skews.
2. `RobloxBackgroundThrottlerService` must immediately restore normal FPS and priority when a Roblox window regains foreground focus, with zero perceptible UI lag.
3. `WebhookNotificationService` must rate-limit outbound webhook calls (maximum 1 request per second) to prevent rate-limiting by Discord/Telegram APIs.
4. `VaultExportService` must fail safely with explicit `CryptographicException` on incorrect passphrases or tampered vault files without leaking partial account data.
5. `SessionAuditorService` must strictly limit concurrent requests (`SemaphoreSlim`) and route requests through assigned account proxies to avoid triggering IP-wide rate limits.

---

### Task 1: Complete HBA Cryptographic Binding for Launch & Cookie Rotation

**Files:**
- Create: `src/Shiro/Services/RobloxHbaService.Binding.cs`
- Modify: `src/Shiro/Classes/Account.Launcher.cs:35-70`
- Modify: `src/Shiro/Classes/Account.SessionState.CookieRotation.cs:40-80`
- Test: `tests/Shiro.Tests/RobloxHbaAndIpPersistenceTests.cs`

**Interfaces:**
- Consumes: `RobloxHbaService`, `Account.HbaBinding`
- Produces: `RobloxHbaService.TryAttachHbaHeader(HttpRequestMessage request, Account account)`
- Produces: `RobloxHbaService.GenerateBoundAuthToken(Account account, string httpMethod, string requestUrl, string? timestamp = null)`

- [x] **Step 1: Write the failing tests for HBA header attachment and signature generation**

```csharp
[Fact]
public void GenerateBoundAuthToken_WithValidHbaKey_ReturnsSignedHeaderValue()
{
    var account = new Account("TestHbaUser", "pass", "alias");
    using var ecdsa = System.Security.Cryptography.ECDsa.Create(System.Security.Cryptography.ECCurve.NamedCurves.nistP256);
    account.HbaPrivateKeyPkcs8 = Convert.ToBase64String(ecdsa.ExportPkcs8PrivateKey());
    account.HbaPublicKeySpki = Convert.ToBase64String(ecdsa.ExportSubjectPublicKeyInfo());

    string? token = RobloxHbaService.GenerateBoundAuthToken(account, "POST", "https://auth.roblox.com/v1/authentication-ticket");

    Assert.NotNull(token);
    Assert.StartsWith("v1|", token);
    Assert.Contains("|", token);
}

[Fact]
public void TryAttachHbaHeader_WhenAccountHasNoHbaKey_DoesNotAttachHeader()
{
    var account = new Account("NoHbaUser", "pass", "alias");
    using var request = new HttpRequestMessage(HttpMethod.Post, "https://auth.roblox.com/v1/authentication-ticket");

    bool attached = RobloxHbaService.TryAttachHbaHeader(request, account);

    Assert.False(attached);
    Assert.False(request.Headers.Contains(RobloxHbaService.BoundAuthHeaderName));
}
```

- [x] **Step 2: Run test to verify it fails**

Run: `dotnet test tests/Shiro.Tests --filter "FullyQualifiedName~GenerateBoundAuthToken"`
Expected: FAIL with compilation error or `MissingMethodException`.

- [x] **Step 3: Implement `RobloxHbaService.Binding.cs` and hook into `Account.Launcher.cs`**

Create `src/Shiro/Services/RobloxHbaService.Binding.cs` under 500 lines implementing `GenerateBoundAuthToken` and `TryAttachHbaHeader` using existing ECDSA P-256 signing logic. Update `Account.Launcher.cs` to invoke `TryAttachHbaHeader` on ticket request headers.

- [x] **Step 4: Run test to verify it passes**

Run: `dotnet test tests/Shiro.Tests --filter "FullyQualifiedName~GenerateBoundAuthToken"`
Expected: PASS.

- [x] **Step 5: Commit**

```bash
git add src/Shiro/Services/RobloxHbaService.Binding.cs src/Shiro/Classes/Account.Launcher.cs src/Shiro/Classes/Account.SessionState.CookieRotation.cs tests/Shiro.Tests/RobloxHbaAndIpPersistenceTests.cs
git commit -m "feat(hba): complete end-to-end HBA signature binding for launch and cookie rotation"
```

---

### Task 2: Background Dynamic FPS & Memory Limiter (Resource Throttler)

**Files:**
- Create: `src/Shiro/Services/RobloxBackgroundThrottlerService.cs`
- Modify: `src/Shiro/Services/ForegroundWindowService.cs:40-90`
- Modify: `src/Shiro/Services/AppSettingsKeys.cs:30-55`
- Test: `tests/Shiro.Tests/RobloxBackgroundThrottlerTests.cs`

**Interfaces:**
- Consumes: `ForegroundWindowService`, Win32 API (`EmptyWorkingSet`, `SetPriorityClass`)
- Produces: `RobloxBackgroundThrottlerService.ApplyThrottleState(int processId, bool isForeground, int backgroundFps = 10)`
- Produces: `RobloxBackgroundThrottlerService.TrimWorkingSet(int processId)`
- Produces: `RobloxBackgroundThrottlerService.IsEnabled`

- [x] **Step 1: Write the failing tests for background process throttling and focus transitions**

```csharp
[Fact]
public void Throttler_WhenProcessLosesFocus_MarksAsBackgroundAndAppliesThrottle()
{
    var service = new RobloxBackgroundThrottlerService();
    service.IsEnabled = true;
    service.TargetBackgroundFps = 10;

    int dummyPid = Process.GetCurrentProcess().Id;
    service.OnForegroundWindowChanged(activeProcessId: 999999, runningRobloxPids: new[] { dummyPid });

    Assert.True(service.IsProcessThrottled(dummyPid));
}

[Fact]
public void Throttler_WhenProcessGainsFocus_RestoresActiveState()
{
    var service = new RobloxBackgroundThrottlerService();
    service.IsEnabled = true;

    int dummyPid = Process.GetCurrentProcess().Id;
    service.OnForegroundWindowChanged(activeProcessId: dummyPid, runningRobloxPids: new[] { dummyPid });

    Assert.False(service.IsProcessThrottled(dummyPid));
}
```

- [x] **Step 2: Run test to verify it fails**

Run: `dotnet test tests/Shiro.Tests --filter "FullyQualifiedName~RobloxBackgroundThrottlerTests"`
Expected: FAIL with `RobloxBackgroundThrottlerService` not found.

- [x] **Step 3: Implement `RobloxBackgroundThrottlerService.cs`**

Implement `RobloxBackgroundThrottlerService.cs` (strictly < 500 lines) with Win32 P/Invoke for `EmptyWorkingSet`, priority adjustment (`IDLE_PRIORITY_CLASS` vs `BELOW_NORMAL_PRIORITY_CLASS`), and `ForegroundWindowService` subscription.

- [x] **Step 4: Run test to verify it passes**

Run: `dotnet test tests/Shiro.Tests --filter "FullyQualifiedName~RobloxBackgroundThrottlerTests"`
Expected: PASS.

- [x] **Step 5: Commit**

```bash
git add src/Shiro/Services/RobloxBackgroundThrottlerService.cs src/Shiro/Services/ForegroundWindowService.cs src/Shiro/Services/AppSettingsKeys.cs tests/Shiro.Tests/RobloxBackgroundThrottlerTests.cs
git commit -m "feat(perf): implement dynamic background FPS and memory throttling for multi-instance Roblox"
```

---

### Task 3: Remote Alerts via Webhooks (Discord & Telegram)

**Files:**
- Create: `src/Shiro/Services/WebhookNotificationService.cs`
- Modify: `src/Shiro/Services/AutoReconnectService.cs:75-115`
- Modify: `src/Shiro/Services/AppSettingsKeys.cs:55-80`
- Test: `tests/Shiro.Tests/WebhookNotificationTests.cs`

**Interfaces:**
- Consumes: `SharedHttpClient`, `AutoReconnectService`
- Produces: `WebhookNotificationService.SendAlertAsync(WebhookAlert alert, CancellationToken ct = default)`
- Produces: `WebhookAlert` model (`AccountUsername`, `EventType`, `Description`, `Timestamp`)

- [x] **Step 1: Write the failing tests for Webhook payload formatting and throttling**

```csharp
[Fact]
public void FormatDiscordPayload_CreatesValidEmbedJson()
{
    var alert = new WebhookAlert
    {
        AccountUsername = "Player1",
        EventType = WebhookEventType.Disconnected,
        Description = "Roblox client disconnected with Error 277.",
        Timestamp = DateTimeOffset.UtcNow
    };

    string json = WebhookNotificationService.FormatDiscordPayload(alert);

    Assert.Contains("\"embeds\":", json);
    Assert.Contains("Player1", json);
    Assert.Contains("Error 277", json);
}

[Fact]
public void FormatTelegramMessage_EscapesMarkdownCharactersCorrectly()
{
    var alert = new WebhookAlert
    {
        AccountUsername = "Player_1",
        EventType = WebhookEventType.RateLimited,
        Description = "HTTP 429 received. Waiting 60s.",
        Timestamp = DateTimeOffset.UtcNow
    };

    string message = WebhookNotificationService.FormatTelegramMessage(alert);

    Assert.Contains("Player", message);
    Assert.Contains("429", message);
}
```

- [x] **Step 2: Run test to verify it fails**

Run: `dotnet test tests/Shiro.Tests --filter "FullyQualifiedName~WebhookNotificationTests"`
Expected: FAIL with `WebhookNotificationService` not found.

- [x] **Step 3: Implement `WebhookNotificationService.cs`**

Implement `WebhookNotificationService.cs` with rate-limiting gate (1 request/sec via `SemaphoreSlim`), non-blocking fire-and-forget channel dispatch, and integration with `AutoReconnectService.NotifyDisconnected` and rate-limit events.

- [x] **Step 4: Run test to verify it passes**

Run: `dotnet test tests/Shiro.Tests --filter "FullyQualifiedName~WebhookNotificationTests"`
Expected: PASS.

- [x] **Step 5: Commit**

```bash
git add src/Shiro/Services/WebhookNotificationService.cs src/Shiro/Services/AutoReconnectService.cs src/Shiro/Services/AppSettingsKeys.cs tests/Shiro.Tests/WebhookNotificationTests.cs
git commit -m "feat(webhook): add Discord and Telegram remote alert notification engine"
```

---

### Task 4: Cross-Device Encrypted Vault Transfer (Export/Import)

**Files:**
- Create: `src/Shiro/Services/VaultExportService.cs`
- Modify: `src/Shiro/Services/AccountStore.cs:80-120`
- Test: `tests/Shiro.Tests/VaultExportTests.cs`

**Interfaces:**
- Consumes: `AccountDataSerializer`, `CryptoHelper`
- Produces: `VaultExportService.ExportVault(IReadOnlyList<Account> accounts, string passphrase, Stream destination)`
- Produces: `VaultExportService.ImportVault(Stream source, string passphrase)`

- [x] **Step 1: Write the failing tests for Vault Export and Import with password verification**

```csharp
[Fact]
public void ExportAndImportVault_WithCorrectPassphrase_RestoresAccountsMatchingOriginal()
{
    var originalAccounts = new List<Account>
    {
        new Account("UserAlpha", "Pass1", "AliasA") { SecurityToken = "_|WARNING:1" },
        new Account("UserBeta", "Pass2", "AliasB") { SecurityToken = "_|WARNING:2" }
    };

    using var ms = new MemoryStream();
    VaultExportService.ExportVault(originalAccounts, "SecurePassphrase123!", ms);

    ms.Position = 0;
    var restoredAccounts = VaultExportService.ImportVault(ms, "SecurePassphrase123!");

    Assert.Equal(2, restoredAccounts.Count);
    Assert.Equal("UserAlpha", restoredAccounts[0].Username);
    Assert.Equal("_|WARNING:1", restoredAccounts[0].SecurityToken);
}

[Fact]
public void ImportVault_WithIncorrectPassphrase_ThrowsCryptographicException()
{
    var originalAccounts = new List<Account> { new Account("UserAlpha", "Pass1", "AliasA") };
    using var ms = new MemoryStream();
    VaultExportService.ExportVault(originalAccounts, "CorrectPass123!", ms);

    ms.Position = 0;
    Assert.Throws<System.Security.Cryptography.CryptographicException>(() =>
    {
        VaultExportService.ImportVault(ms, "WrongPass!");
    });
}
```

- [x] **Step 2: Run test to verify it fails**

Run: `dotnet test tests/Shiro.Tests --filter "FullyQualifiedName~VaultExportTests"`
Expected: FAIL with `VaultExportService` not found.

- [x] **Step 3: Implement `VaultExportService.cs`**

Implement `VaultExportService.cs` (strictly < 500 lines) using magic header `SHIRO_VAULT_V1`, PBKDF2-SHA256 (300,000 rounds) key derivation, AES-256-GCM encryption, and atomic stream parsing.

- [x] **Step 4: Run test to verify it passes**

Run: `dotnet test tests/Shiro.Tests --filter "FullyQualifiedName~VaultExportTests"`
Expected: PASS.

- [x] **Step 5: Commit**

```bash
git add src/Shiro/Services/VaultExportService.cs src/Shiro/Services/AccountStore.cs tests/Shiro.Tests/VaultExportTests.cs
git commit -m "feat(security): implement cross-device encrypted vault export and import"
```

---

### Task 5: Session Health & Balance Auditor (Background Scanner & UI)

**Files:**
- Create: `src/Shiro/Services/SessionAuditorService.cs`
- Modify: `src/Shiro/ViewModels/MainViewModel.StatusAndCounts.cs:40-90`
- Test: `tests/Shiro.Tests/SessionAuditorTests.cs`

**Interfaces:**
- Consumes: `ProxyBindingService`, `RobloxApiClient`
- Produces: `SessionAuditorService.AuditAccountAsync(Account account, CancellationToken ct = default)`
- Produces: `SessionAuditorService.AuditAllAccountsAsync(IEnumerable<Account> accounts, IProgress<AuditProgress>? progress = null, CancellationToken ct = default)`
- Produces: `AccountSessionAuditResult` (`Status`, `RobuxBalance`, `PendingRobux`, `AuditedAt`)

- [x] **Step 1: Write the failing tests for Session Auditor status evaluation**

```csharp
[Fact]
public void EvaluateAuditStatus_WhenHttpResponseIs401_ReturnsExpiredCookie()
{
    var status = SessionAuditorService.MapStatusCodeToSessionStatus(System.Net.HttpStatusCode.Unauthorized);
    Assert.Equal(AccountSessionStatus.ExpiredCookie, status);
}

[Fact]
public void EvaluateAuditStatus_WhenHttpResponseIs403_ReturnsTwoStepOrBlocked()
{
    var status = SessionAuditorService.MapStatusCodeToSessionStatus(System.Net.HttpStatusCode.Forbidden);
    Assert.Equal(AccountSessionStatus.VerificationRequired, status);
}

[Fact]
public void EvaluateAuditStatus_WhenHttpResponseIs200_ReturnsValid()
{
    var status = SessionAuditorService.MapStatusCodeToSessionStatus(System.Net.HttpStatusCode.OK);
    Assert.Equal(AccountSessionStatus.Valid, status);
}
```

- [x] **Step 2: Run test to verify it fails**

Run: `dotnet test tests/Shiro.Tests --filter "FullyQualifiedName~SessionAuditorTests"`
Expected: FAIL with `SessionAuditorService` not found.

- [x] **Step 3: Implement `SessionAuditorService.cs`**

Implement `SessionAuditorService.cs` with `SemaphoreSlim(3, 3)` to cap concurrency, proxy-aware routing via `ProxyBindingService`, aggregate Robux and Pending Robux balance summation, and observable progress reporting.

- [x] **Step 4: Run test to verify it passes**

Run: `dotnet test tests/Shiro.Tests --filter "FullyQualifiedName~SessionAuditorTests"`
Expected: PASS.

- [x] **Step 5: Commit**

```bash
git add src/Shiro/Services/SessionAuditorService.cs src/Shiro/ViewModels/MainViewModel.StatusAndCounts.cs tests/Shiro.Tests/SessionAuditorTests.cs
git commit -m "feat(audit): implement session health and Robux balance auditor service"
```
