# Phase 2: Win32 Memory Optimizer, Cryptographic Memory Hygiene & Full English i18n Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Fix Win32 `MemoryOptimizer` working-set trimming and its 30-second idle spin loop, harden vault plaintext memory wiping / fail-closed DPAPI / CSV export / per-account proxy HBA signatures, eliminate JSON reflection fallbacks, and achieve 100% English localization across `en.json`, C# ViewModels, and all `.axaml` views.

**Architecture:** Six self-contained TDD tasks aligned with existing service boundaries and strict line-budget ratchets. Win32 trimming unifies around `AppSettingsKeys.General.AggressiveRamTrimming` with non-destructive `SoftRelaxWorkingSetNative` fallback, `CryptographicOperations.ZeroMemory` and fail-closed DPAPI seal local secret handling, a second `ShiroAppJson` instance configured with `WriteIndented = true` eliminates reflection JSON serialization without duplicating attributes, and automated xUnit ratchets permanently enforce English/AXAML/C# localization parity.

**Tech Stack:** .NET 10 (`win-x64`), C# 13, Avalonia UI 11 (`.axaml` `DynamicResource`), `System.Text.Json` Source Generation (`JsonSerializerContext`), Win32 P/Invoke (`kernel32.dll`, `psapi.dll`), DPAPI (`ProtectedData`), xUnit, NSubstitute.

**Spec:** Comprehensive Project Audit Report (`C:/Users/lenovo/.gemini/antigravity/brain/693b9b61-05d2-4a86-8da9-d28370a36cec/project_audit_report.md`, Sections 4.2, 4.3, and 5 — Stage 2, plus Stage 1 Final Review deferred UI/WebView2 items). Stage 3 (`namespace Shiro;` `NetArchTest` coverage, `MainViewModel` `IDisposable`, `AccountRepository` state ownership, and `v5.0.0` documentation sync) remains scoped to Plan 3.

## Global Constraints

- `dotnet build Shiro.sln -c Release`: `0` errors, `0` warnings (`TreatWarningsAsErrors=true`).
- `dotnet test tests/Shiro.Tests/Shiro.Tests.csproj -c Release`: `0` failed; total test count must strictly increase on every task (starting from `1840` on `main` at `8b25f80`).
- Coverage gates unchanged: `./scripts/Check-Coverage.ps1 -CoverageDir "coverage" -MinOverall 75.0 -MinBranch 60.0 -MinCore 92.0` must pass, including all 19 per-file branch floors in `tests/coverage-floors.json` (notably `LaunchCoordinator.Helpers.cs >= 74%`, `AccountManager.Persistence.cs >= 67%`, `AccountManager.Lifecycle.cs >= 70%`, `AccountManager.Accounts.cs >= 75%`, `AccountLaunchService.cs >= 73%`).
- C# line budgets (`ArchitectureTests`): every `.cs` file in `src/Shiro` `<= 500` lines; partial classes within budget (`AccountLaunchService` is at `1197 / 1200` — add `0` net lines; `AccountManager.Persistence.cs` is at `495 / 500` — keep `<= 499` lines; `MemoryOptimizer.cs` is at `469 / 500` — put new helpers in `MemoryOptimizer.ResourceProfile.cs` at `81 / 500`).
- AXAML line budgets (`ArchitectureTests.NoAxamlFiles_ShouldExceed400Lines`): every `.axaml` file `<= 400` lines (`DashboardView.axaml` is at `387 / 400`) and **every `.axaml` line `<= 200` characters**.
- Ambient static ratchets (`ArchitectureTests`): do not increase `Program.Logger` or `AppSettings.` occurrences in guarded `src/Shiro/Services/*` folders (use `AppServices.Logger` and `AppServices.GeneralSettings`).
- Zero `.GetAwaiter().GetResult()`, zero empty `catch {}` blocks, UTF-8 encoding preserved on all touched `.cs`, `.axaml`, and `.json` files, and Conventional Commit prefixes (`fix(perf):`, `fix(security):`, `fix(hba):`, `fix(json):`, `fix(i18n):`).

## Review Focus

1. **Periodic Auto-Trim 30-second spin when `AggressiveRamTrimming=false`:** When `AggressiveRamTrimming` is `false` (so `EmptyWorkingSet` is intentionally skipped in favor of soft working-set relaxation), a background Roblox client sitting at `500 MB` and already at `BelowNormal` priority must **not** trigger `ShouldTriggerPeriodicTrim` every 30 seconds; early 30s wakeups should only fire for priority drift (`lowerPriorityEnabled && currentPriority != BelowNormal`) or when `aggressiveTrimEnabled` is `true`, leaving periodic soft-relaxation to the configured `_trimIntervalMinutes` cadence. Pinned by `ShouldTriggerPeriodicTrim_SkipsMemoryThresholdSpin_WhenAggressiveTrimDisabled` in Task 1.
2. **CSV Formula Injection with leading whitespace or control characters (`"  =1+1"`, `"\t+cmd"`, `"-2+3"`, `"@SUM(A1)"`):** Spreadsheet apps evaluate leading `=`, `+`, `-`, `@`, `\t`, `\r` formulas even when preceded by spaces; `AccountExportService.EscapeCsv` must prefix a single quote (`'`) and CSV-quote any field whose first non-space character is `=`, `+`, `-`, `@`, or whose first character is `\t`/`\r`, without altering normal alphanumeric strings. Pinned by `ExportAccounts_Csv_NeutralizesFormulaInjectionPayloads_IncludingLeadingWhitespace` in Task 2.
3. **Per-account proxy credentials in HBA network signatures (`http://user:pass@1.2.3.4:8080` vs `1.2.3.4:8080:user:pass2`):** `RobloxHbaService.GetNetworkSignatureForAccount` must differentiate direct accounts from proxied accounts and detect `host:port` proxy changes so `Account.Api.cs` never strips HBA on a `401` across a proxy change, while stripping `user:pass` credentials before hashing and returning the exact `GetLocalNetworkSignature()` value when `account.Proxy` is empty. Pinned by `GetNetworkSignatureForAccount_IncludesProxyHostPort_StripsCredentials_AndPreservesDirectSignature` in Task 3.
4. **Indented JSON serialization without reflection (`FastJson.Serialize(..., indented: true)`):** Calling `FastJson.Serialize` or `FastJson.SerializeToUtf8Bytes` with `indented: true` on any type registered in `ShiroAppJson` (including via `object` or `List<AccountExportJsonItem>`) must resolve `JsonTypeInfo` from a source-generated `ShiroAppJson` instance configured with `WriteIndented = true` rather than falling back to `DefaultJsonTypeInfoResolver`. Pinned by `FastJson_IndentedSerialize_UsesSourceGeneratedContext_WithoutReflectionResolver` in Task 4.
5. **Format-placeholder parity (`{0}`, `{1}`, `{0:N0}`) & bilingual language-selector labels in `en.json`:** Translating 392 Vietnamese entries in `en.json` must keep exact placeholder token sets matching `vi.json` so `LocalizationService.Format` never drops arguments, while allowing only the 3 intentional bilingual selector keys (`Cap_Language`, `Set_Language_EN`, `Set_Language_VN`) to contain `"Tiếng Việt"` / `"Tiếng Anh"`. Pinned by `EnJson_ShouldNotContainUntranslatedVietnameseDiacritics` and `EnJson_And_ViJson_ShouldHaveMatchingFormatPlaceholders` in Task 5.

---

## File Structure & Responsibility Map

- `src/Shiro/Services/Windows/MemoryOptimizer.ResourceProfile.cs` & `MemoryOptimizer.cs` — Unify `IsAggressiveWorkingSetTrimEnabled()`, apply `SoftRelaxWorkingSetNative` when aggressive trim is off, allow explicit `TrimRobloxClients` (`forceWorkingSetTrim: true`) to trim non-foreground working sets, and stop `OnPeriodicCheck` from spinning every 30s when `aggressiveTrimEnabled == false`.
- `src/Shiro/Services/Windows/RobloxBackgroundThrottlerService.cs` — Replace ghost `"AllowEmptyWorkingSetTrimming"` check on line 200 with `MemoryOptimizer.IsAggressiveWorkingSetTrimEnabled()`.
- `src/Shiro/Services/Launch/LaunchCoordinator.Helpers.cs` — Remove the dead empty `lock (RobloxWatcher.InstanceRegistryLock)` loop (`lines 185-205`) in `TryFindProcessPlayerLogPath`.
- `src/Shiro/Services/Storage/AccountFileDecoder.cs`, `src/Shiro/AccountManager.Persistence.cs`, `src/Shiro/AccountManager.Lifecycle.cs` — Wipe intermediate decrypted vault `byte[]` buffers via `CryptographicOperations.ZeroMemory` in `finally` blocks and call `RobloxHbaService.ClearKeyCache()` on shutdown.
- `src/Shiro/Services/Storage/CryptoHelper.cs`, `src/Shiro/Services/Network/WebhookNotificationService.cs`, `src/Shiro/Services/Storage/AccountExportService.cs` — Make `ProtectSecretString` fail-closed with `ZeroMemory`, protect `DiscordWebhookUrl` & `TelegramBotToken` via DPAPI at rest, and neutralize CSV Formula Injection in `EscapeCsv`.
- `src/Shiro/Services/Network/NetworkSignatureHelper.cs`, `src/Shiro/Services/Auth/RobloxHbaService.Network.cs`, `src/Shiro/Classes/Account*.cs`, `src/Shiro/Services/Launch/AccountLaunchService*.cs`, `src/Shiro/AccountManager.Accounts.cs` — Add credential-scrubbed per-account proxy awareness via `RobloxHbaService.GetNetworkSignatureForAccount(Account?)`.
- `src/Shiro/AvaloniaUI/Views/FastLoginDialog.Automation.cs` & `src/Shiro/AvaloniaUI/Views/AccountFieldsDialog.axaml.cs` — Restrict WebView2 BAT `WebResourceRequested` filter/handler to `https://*.roblox.com` and hide/block `Account.IsProtectedInternalField` keys in the desktop Account Fields dialog.
- `src/Shiro/Services/Storage/JsonExtensions.cs` & `src/Shiro/Services/Ui/LocalizationService.Resources.cs` — Add `ShiroAppJson` indented source-gen context instance (`IndentedContext`) for `FastJson.Serialize(..., indented: true)` and pass `ShiroAppJson.Default.DictionaryStringString` when loading embedded `en.json` / `vi.json`.
- `src/Shiro/Resources/Strings/en.json`, `src/Shiro/Resources/Strings/vi.json`, 14 `.axaml` views, 4 Dialog ViewModels, `tests/Shiro.Tests/ArchitectureTests.cs`, `tests/Shiro.Tests/LocalizationIntegrityTests.cs` — Complete English translations, replace all hardcoded Vietnamese UI strings in `.axaml` and ViewModels, fix the C# Vietnamese-literal regex bug in `ArchitectureTests.cs:611`, and add permanent `en.json` + `.axaml` i18n ratchets.

---

### Task 1: Fix `MemoryOptimizer` Working-Set Trimming, Stop 30s Idle Spin Loop & Remove Dead Log-Lock Loop

**Files:**
- Modify: `src/Shiro/Services/Windows/MemoryOptimizer.ResourceProfile.cs:68-80`
- Modify: `src/Shiro/Services/Windows/MemoryOptimizer.cs:129-145,178-228,241-252,301-309`
- Modify: `src/Shiro/Services/Windows/RobloxBackgroundThrottlerService.cs:200`
- Modify: `src/Shiro/Services/Launch/LaunchCoordinator.Helpers.cs:180-206`
- Test: `tests/Shiro.Tests/MemoryOptimizerBehaviorTests.cs`

**Interfaces:**
- Produces: `internal static bool IsAggressiveWorkingSetTrimEnabled()` and `internal static void ResetStatsForTests()` on `MemoryOptimizer` (`MemoryOptimizer.ResourceProfile.cs`).
- Produces: `internal static bool ShouldTriggerPeriodicTrim(long workingSetBytes, ProcessPriorityClass currentPriority, bool isForeground, bool lowerPriorityEnabled, long backgroundThresholdBytes = 350L * 1024 * 1024, bool aggressiveTrimEnabled = true)` on `MemoryOptimizer`.
- Produces: `public static (long bytesSaved, int clientsCount, int priorityChanged) OptimizeRobloxClients(bool lowerPriority = true, bool forceWorkingSetTrim = false)` on `MemoryOptimizer` (`TrimRobloxClients` calls `OptimizeRobloxClients(lowerPriority, forceWorkingSetTrim: true)`).

- [ ] **Step 1: Write failing tests in `tests/Shiro.Tests/MemoryOptimizerBehaviorTests.cs`**

```csharp
[Fact]
public void ShouldTriggerPeriodicTrim_SkipsMemoryThresholdSpin_WhenAggressiveTrimDisabled()
{
    long maxFg = MemoryOptimizer.ForegroundMaxMemoryThresholdBytes;
    long bgLimit = 350L * 1024 * 1024;

    // When aggressiveTrimEnabled == false, background client > 350MB already at BelowNormal must NOT spin every 30s:
    Assert.False(MemoryOptimizer.ShouldTriggerPeriodicTrim(
        bgLimit + (100L * 1024 * 1024),
        ProcessPriorityClass.BelowNormal,
        isForeground: false,
        lowerPriorityEnabled: true,
        bgLimit,
        aggressiveTrimEnabled: false));

    // Priority drift (Normal instead of BelowNormal) still triggers immediate optimization even when aggressiveTrimEnabled == false:
    Assert.True(MemoryOptimizer.ShouldTriggerPeriodicTrim(
        bgLimit + (100L * 1024 * 1024),
        ProcessPriorityClass.Normal,
        isForeground: false,
        lowerPriorityEnabled: true,
        bgLimit,
        aggressiveTrimEnabled: false));

    // Foreground extreme RAM also skips 30s EmptyWorkingSet spin when aggressiveTrimEnabled == false:
    Assert.False(MemoryOptimizer.ShouldTriggerPeriodicTrim(
        maxFg + 1024,
        ProcessPriorityClass.Normal,
        isForeground: true,
        lowerPriorityEnabled: true,
        bgLimit,
        aggressiveTrimEnabled: false));
}

[Fact]
public void IsAggressiveWorkingSetTrimEnabled_ReadsAggressiveRamTrimming_WithLegacyFallback()
{
    var mock = Substitute.For<ISettingsService>();
    using (AppServices.Override<ISettingsService>(mock))
    {
        mock.Exists(AppSettingsKeys.General.AggressiveRamTrimming).Returns(true);
        mock.Get<bool>(AppSettingsKeys.General.AggressiveRamTrimming, false).Returns(true);
        Assert.True(MemoryOptimizer.IsAggressiveWorkingSetTrimEnabled());

        mock.Get<bool>(AppSettingsKeys.General.AggressiveRamTrimming, false).Returns(false);
        Assert.False(MemoryOptimizer.IsAggressiveWorkingSetTrimEnabled());

        mock.Exists(AppSettingsKeys.General.AggressiveRamTrimming).Returns(false);
        mock.Exists("AllowEmptyWorkingSetTrimming").Returns(true);
        mock.Get<bool>("AllowEmptyWorkingSetTrimming", false).Returns(true);
        Assert.True(MemoryOptimizer.IsAggressiveWorkingSetTrimEnabled());
    }
}
```

Also update `OptimizeRobloxClients_NoClients_And_WithTargetProcess_CoversExecution` in `MemoryOptimizerBehaviorTests.cs` to capture `var selfProc = Process.GetCurrentProcess(); var origPrio = selfProc.PriorityClass;` before calling `OptimizeRobloxClients` and restore `selfProc.PriorityClass = origPrio; MemoryOptimizer.DisableEcoQoS(selfProc.Handle); MemoryOptimizer.ResetStatsForTests();` inside `finally`.

- [ ] **Step 2: Run targeted tests to verify failure**

Run: `dotnet test "tests/Shiro.Tests/Shiro.Tests.csproj" -c Release --filter "FullyQualifiedName~MemoryOptimizerBehaviorTests"`
Expected: Compilation FAIL (`aggressiveTrimEnabled`, `IsAggressiveWorkingSetTrimEnabled`, `ResetStatsForTests` do not exist).

- [ ] **Step 3: Implement `MemoryOptimizer`, `RobloxBackgroundThrottlerService`, and `LaunchCoordinator.Helpers` fixes**

1. In `MemoryOptimizer.ResourceProfile.cs`:
   - Add `internal static bool IsAggressiveWorkingSetTrimEnabled()`: if `AppServices.GeneralSettings.Exists(AppSettingsKeys.General.AggressiveRamTrimming)` return `AppServices.GeneralSettings.Get<bool>(AppSettingsKeys.General.AggressiveRamTrimming, false)`; if `AppServices.GeneralSettings.Exists("AllowEmptyWorkingSetTrimming")` return `AppServices.GeneralSettings.Get<bool>("AllowEmptyWorkingSetTrimming", false)`; otherwise return `false`.
   - Add `internal static void ResetStatsForTests()` setting `Interlocked.Exchange(ref _totalBytesSaved, 0)` and `_lastFullTrim = DateTime.MinValue`.
2. In `MemoryOptimizer.cs` (keep total lines `<= 475`, zero new `AppSettings.` or `Program.Logger` calls):
   - Extend `ShouldTriggerPeriodicTrim` with `bool aggressiveTrimEnabled = true`: if `isForeground`, return `aggressiveTrimEnabled && workingSetBytes > ForegroundMaxMemoryThresholdBytes`; otherwise return `(aggressiveTrimEnabled && workingSetBytes > backgroundThresholdBytes) || (lowerPriorityEnabled && currentPriority != ProcessPriorityClass.BelowNormal)`.
   - In `OnPeriodicCheck`: compute `bool aggressiveTrim = IsAggressiveWorkingSetTrimEnabled();` once and pass `aggressiveTrim` to `ShouldTriggerPeriodicTrim`.
   - In `TrimRobloxClients`: call `OptimizeRobloxClients(lowerPriority, forceWorkingSetTrim: true)`.
   - In `OptimizeRobloxClients(bool lowerPriority = true, bool forceWorkingSetTrim = false)`: compute `bool allowEmptyWorkingSet = forceWorkingSetTrim || IsAggressiveWorkingSetTrimEnabled();`. When `allowEmptyWorkingSet && (!isForeground || before > ForegroundMaxMemoryThresholdBytes)`, call `EmptyWorkingSet(hProcess); count++;`; else if `!isForeground`, call `SetProcessMemoryPriorityNative(hProcess, isForeground: false); if (SoftRelaxWorkingSetNative(hProcess)) count++;`.
3. In `RobloxBackgroundThrottlerService.cs:200`: replace `AppServices.GeneralSettings.Get<bool>("AllowEmptyWorkingSetTrimming", false)` with `MemoryOptimizer.IsAggressiveWorkingSetTrimEnabled()`.
4. In `LaunchCoordinator.Helpers.cs:185-205`: delete the dead no-op `lock (RobloxWatcher.InstanceRegistryLock)` loop in `TryFindProcessPlayerLogPath` that performed `File.Exists` inside an empty `if` body.

- [ ] **Step 4: Run optimizer, throttler, launch-coordinator, and architecture tests**

Run: `dotnet test "tests/Shiro.Tests/Shiro.Tests.csproj" -c Release --filter "FullyQualifiedName~MemoryOptimizer|FullyQualifiedName~UnifiedResourceOptimizer|FullyQualifiedName~LaunchCoordinatorHelpers|FullyQualifiedName~ArchitectureTests"`
Expected: PASS (`0` failed).

- [ ] **Step 5: Commit**

```powershell
git add src/Shiro/Services/Windows/MemoryOptimizer.ResourceProfile.cs src/Shiro/Services/Windows/MemoryOptimizer.cs src/Shiro/Services/Windows/RobloxBackgroundThrottlerService.cs src/Shiro/Services/Launch/LaunchCoordinator.Helpers.cs tests/Shiro.Tests/MemoryOptimizerBehaviorTests.cs
git commit -m "fix(perf): unify AggressiveRamTrimming in MemoryOptimizer, stop 30s spin loop, and drop dead log lock"
```

---

### Task 2: Vault Plaintext `ZeroMemory` Hygiene, Shutdown `ClearKeyCache`, Fail-Closed DPAPI, Webhook Secret Encryption & CSV Formula Neutralization

**Files:**
- Modify: `src/Shiro/Services/Storage/AccountFileDecoder.cs:46-58,122-153`
- Modify: `src/Shiro/AccountManager.Persistence.cs:52-55`
- Modify: `src/Shiro/AccountManager.Lifecycle.cs:279-281`
- Modify: `src/Shiro/Services/Storage/CryptoHelper.cs:60-99`
- Modify: `src/Shiro/Services/Network/WebhookNotificationService.cs:64-86`
- Modify: `src/Shiro/Services/Storage/AccountExportService.cs:108-120`
- Test: `tests/Shiro.Tests/CryptographyAndHbaOptimizationTests.cs`, `tests/Shiro.Tests/AutoReconnectAndProxyTests.cs`, `tests/Shiro.Tests/NetworkServicesBehaviorTests.cs`

**Interfaces:**
- Produces: `CryptoHelper.ProtectSecretString(string plainText)` — throws `CryptographicException` on DPAPI failure instead of returning `plainText`, zeroes `plainBytes` in `finally`, and exposes `internal static Func<byte[], byte[]>? TestProtectBytesOverride` for unit tests.
- Produces: `CryptoHelper.UnprotectSecretString(string cipherText)` — zeroes decrypted `byte[]` in `finally`; returns `string.Empty` (never `"dpapi:..."`) when a `"dpapi:"`-prefixed payload fails Base64/DPAPI decoding, while continuing to return legacy unprefixed plaintext unchanged.
- Produces: `AccountExportService.EscapeCsv(string? val)` — prefixes `'` and CSV-quotes any value whose first character is `\t`/`\r` or whose `.TrimStart(' ')` starts with `'='`, `'+'`, `'-'`, or `'@'`.

- [ ] **Step 1: Write failing security & hygiene tests**

In `tests/Shiro.Tests/CryptographyAndHbaOptimizationTests.cs`:
```csharp
[Fact]
public void CryptoHelper_ProtectSecretString_FailsClosedOnDpapiError_AndUnprotectRejectsCorruptDpapiPrefix()
{
    string protectedVal = CryptoHelper.ProtectSecretString("super-secret-token");
    Assert.StartsWith("dpapi:", protectedVal, StringComparison.Ordinal);
    Assert.DoesNotContain("super-secret-token", protectedVal, StringComparison.Ordinal);
    Assert.Equal("super-secret-token", CryptoHelper.UnprotectSecretString(protectedVal));

    // Corrupt dpapi: payload must fail closed to empty string, never leaking raw "dpapi:..." into headers/URLs
    Assert.Equal(string.Empty, CryptoHelper.UnprotectSecretString("dpapi:not-valid-base64!!!"));

    // Legacy plaintext without prefix remains readable for backward compatibility
    Assert.Equal("legacy-plain", CryptoHelper.UnprotectSecretString("legacy-plain"));

    var prev = CryptoHelper.TestProtectBytesOverride;
    try
    {
        CryptoHelper.TestProtectBytesOverride = _ => throw new System.Security.Cryptography.CryptographicException("DPAPI unavailable");
        Assert.Throws<System.Security.Cryptography.CryptographicException>(() => CryptoHelper.ProtectSecretString("must-not-leak-plaintext"));
    }
    finally
    {
        CryptoHelper.TestProtectBytesOverride = prev;
    }
}

[Fact]
public void AccountFileDecoder_WipesUnprotectedDpapiBuffer_AfterDeserialization()
{
    var sample = new List<Account> { new Account { Username = "ZeroMemUser", UserID = 101, SecurityToken = "_|WARNING:-DO-NOT-SHARE-THIS.--SecretCookiePayload12345" } };
    byte[] serialized = AccountDataSerializer.Serialize(sample, includePasswords: true);
    byte[]? capturedPlainBuffer = null;

    var keys = new AccountDecodeKeys(
        GetVaultKeyCopy: () => Array.Empty<byte>(),
        TryRecoverVaultKey: null,
        UnprotectDpapi: _ =>
        {
            capturedPlainBuffer = (byte[])serialized.Clone();
            return (capturedPlainBuffer, false);
        },
        PlaintextOptIn: false);

    var result = AccountFileDecoder.Decode(new byte[] { 0x01, 0x02, 0x03 }, null, keys);
    Assert.Equal(AccountDecodeStatus.Loaded, result.Status);
    Assert.Single(result.Accounts);
    Assert.NotNull(capturedPlainBuffer);
    Assert.All(capturedPlainBuffer!, b => Assert.Equal(0, b));
}
```

In `tests/Shiro.Tests/NetworkServicesBehaviorTests.cs`, assert that after `WebhookNotificationService.DiscordWebhookUrl = "https://discord.com/api/webhooks/test"`, `AppServices.GeneralSettings.Get(AppSettingsKeys.General.DiscordWebhookUrl)` starts with `"dpapi:"` while `WebhookNotificationService.DiscordWebhookUrl` returns `"https://discord.com/api/webhooks/test"` (and same for `TelegramBotToken`), and setting `null` still returns `null`.

In `tests/Shiro.Tests/AutoReconnectAndProxyTests.cs`:
```csharp
[Theory]
[InlineData("=CMD|' /C calc'!A0")]
[InlineData("  =HYPERLINK(\"http://evil\")")]
[InlineData("+SUM(1,1)")]
[InlineData("-2+3")]
[InlineData("@import")]
[InlineData("\t=1+1")]
public void ExportAccounts_Csv_NeutralizesFormulaInjectionPayloads_IncludingLeadingWhitespace(string maliciousAlias)
{
    var acc = new Account { Username = "CsvSafeUser", UserID = 42, Alias = maliciousAlias };
    string csv = AccountExportService.ExportAccounts(new[] { acc }, AccountExportFormat.Csv);
    string dataLine = csv.Split('\n', StringSplitOptions.RemoveEmptyEntries)[1].TrimEnd('\r');
    string[] cols = dataLine.Split(',');
    Assert.StartsWith("\"'", cols[2], StringComparison.Ordinal);
}
```

- [ ] **Step 2: Run targeted tests to verify failure**

Run: `dotnet test "tests/Shiro.Tests/Shiro.Tests.csproj" -c Release --filter "FullyQualifiedName~CryptoHelper_ProtectSecretString|FullyQualifiedName~AccountFileDecoder_WipesUnprotectedDpapiBuffer|FullyQualifiedName~ExportAccounts_Csv_NeutralizesFormulaInjectionPayloads|FullyQualifiedName~NetworkServicesBehaviorTests"`
Expected: FAIL.

- [ ] **Step 3: Implement `ZeroMemory` wiping, `ClearKeyCache` shutdown hook, fail-closed `CryptoHelper`, DPAPI webhook properties, and CSV formula guard**

1. In `AccountFileDecoder.cs`: wrap `decrypted` (`line 50`), `decryptedVault` (`line 122-133`), and `plain` (`line 144-148`) in `try ... finally { if (buf != null) CryptographicOperations.ZeroMemory(buf); }` (do **not** zero the caller's `data` input array on line 159).
2. In `AccountManager.Persistence.cs:53` (keep file `<= 498` lines): change `try { _ = Cryptography.Decrypt(data, vaultKey); return false; }` to `try { byte[] p = Cryptography.Decrypt(data, vaultKey); CryptographicOperations.ZeroMemory(p); return false; }`.
3. In `AccountManager.Lifecycle.cs:280`: add `SafeExecuteHelper.Try(RobloxHbaService.ClearKeyCache);` right next to `SafeExecuteHelper.Try(StoragePaths.ClearCachedVaultKey);`.
4. In `CryptoHelper.cs`:
   - Add `internal static Func<byte[], byte[]>? TestProtectBytesOverride;`.
   - In `ProtectSecretString`: allocate `byte[] plainBytes = Encoding.UTF8.GetBytes(plainText);`, call `TestProtectBytesOverride != null ? TestProtectBytesOverride(plainBytes) : ProtectedData.Protect(plainBytes, null, DataProtectionScope.CurrentUser)` inside `try ... finally { CryptographicOperations.ZeroMemory(plainBytes); }`, and let `CryptographicException` propagate (remove `catch { return plainText; }`).
   - In `UnprotectSecretString`: decode `enc` and `plain`, return `Encoding.UTF8.GetString(plain)` with `CryptographicOperations.ZeroMemory(plain)` in `finally`, and on `FormatException` / `CryptographicException` log via `AppServices.Logger.Debug(...)` and return `string.Empty`.
5. In `WebhookNotificationService.cs:64-86`:
   - `DiscordWebhookUrl`: `get` reads `string? raw = _settings.Get(AppSettingsKeys.General.DiscordWebhookUrl); if (string.IsNullOrWhiteSpace(raw)) return null; string plain = CryptoHelper.UnprotectSecretString(raw); return string.IsNullOrEmpty(plain) ? null : plain;`; `set` writes `_settings.Set(AppSettingsKeys.General.DiscordWebhookUrl, CryptoHelper.ProtectSecretString(value));` when non-whitespace.
   - Apply the exact same pattern to `TelegramBotToken`.
6. In `AccountExportService.cs:111-119`: in `EscapeCsv(string? val)`, detect formula-triggering values (`val[0] is '\t' or '\r'` or `val.AsSpan().TrimStart(' ')` starting with `'='`, `'+'`, `'-'`, or `'@'`); when matched, prepend `'` (`string safe = "'" + val;`) and return `$"\"{safe.Replace("\"", "\"\"")}\""`.

- [ ] **Step 4: Run storage, crypto, webhook, export, and lifecycle tests**

Run: `dotnet test "tests/Shiro.Tests/Shiro.Tests.csproj" -c Release --filter "FullyQualifiedName~CryptographyAndHbaOptimizationTests|FullyQualifiedName~AccountFileDecoder|FullyQualifiedName~AutoReconnectAndProxyTests|FullyQualifiedName~NetworkServicesBehaviorTests|FullyQualifiedName~AccountManagerLifecycle|FullyQualifiedName~ArchitectureTests"`
Expected: PASS (`0` failed).

- [ ] **Step 5: Commit**

```powershell
git add src/Shiro/Services/Storage/AccountFileDecoder.cs src/Shiro/AccountManager.Persistence.cs src/Shiro/AccountManager.Lifecycle.cs src/Shiro/Services/Storage/CryptoHelper.cs src/Shiro/Services/Network/WebhookNotificationService.cs src/Shiro/Services/Storage/AccountExportService.cs tests/Shiro.Tests/CryptographyAndHbaOptimizationTests.cs tests/Shiro.Tests/AutoReconnectAndProxyTests.cs tests/Shiro.Tests/NetworkServicesBehaviorTests.cs
git commit -m "fix(security): zero decrypted vault buffers, fail-close DPAPI secrets, and block CSV formula injection"
```

---

### Task 3: Per-Account Proxy HBA Network Signatures, HTTPS-Only WebView2 BAT Filter & `AccountFieldsDialog` Protected-Key Scrubbing

**Files:**
- Modify: `src/Shiro/Services/Network/NetworkSignatureHelper.cs:162-220`
- Modify: `src/Shiro/Services/Auth/RobloxHbaService.Network.cs:24-31`
- Modify: `src/Shiro/Classes/Account.Api.cs:128`
- Modify: `src/Shiro/Classes/Account.HbaBinding.cs:201`
- Modify: `src/Shiro/Classes/Account.HbaBinding.SessionSync.cs:26,104`
- Modify: `src/Shiro/Classes/Account.SessionState.CookieRotation.cs:134`
- Modify: `src/Shiro/Services/Launch/AccountLaunchService.cs:226`
- Modify: `src/Shiro/Services/Launch/AccountLaunchService.Home.cs:148`
- Modify: `src/Shiro/AccountManager.Accounts.cs:258`
- Modify: `src/Shiro/AvaloniaUI/Views/FastLoginDialog.Automation.cs:42,73-76`
- Modify: `src/Shiro/AvaloniaUI/Views/AccountFieldsDialog.axaml.cs:52-105`
- Test: `tests/Shiro.Tests/RobloxHbaAndIpPersistenceTests.cs`

**Interfaces:**
- Produces: `internal static string? NormalizeAccountProxyEndpoint(string? rawProxy)` on `NetworkSignatureHelper` — parses `scheme://[user:pass@]host:port`, `host:port`, or `host:port:user:pass` into lowercase `"{scheme}://{host}:{port}"` (defaulting `scheme` to `"http"`), stripping all credentials; returns `null` if null/whitespace.
- Produces: `public static string GetNetworkSignatureForAccount(Account? account)` on `RobloxHbaService` — calls `GetLocalNetworkSignature()`; if `localSig is "offline" or "unknown"` or `NormalizeAccountProxyEndpoint(account?.Proxy)` is `null`, returns `localSig` unchanged; otherwise returns `Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes($"{localSig}|acctproxy:{normalizedProxy}")).AsSpan(0, 12))`.

- [ ] **Step 1: Write failing tests in `tests/Shiro.Tests/RobloxHbaAndIpPersistenceTests.cs`**

```csharp
[Fact]
public void GetNetworkSignatureForAccount_IncludesProxyHostPort_StripsCredentials_AndPreservesDirectSignature()
{
    string directBase = RobloxHbaService.GetLocalNetworkSignature();
    var directAcc = new Account { Username = "DirectUser", Proxy = "" };
    Assert.Equal(directBase, RobloxHbaService.GetNetworkSignatureForAccount(directAcc));
    Assert.Equal(directBase, RobloxHbaService.GetNetworkSignatureForAccount(null));

    if (directBase is not ("offline" or "unknown"))
    {
        var proxyUriAcc1 = new Account { Username = "P1", Proxy = "http://alice:secret1@203.0.113.10:8080" };
        var proxyUriAcc2 = new Account { Username = "P2", Proxy = "203.0.113.10:8080:bob:secret2" };
        var proxyOtherHost = new Account { Username = "P3", Proxy = "http://203.0.113.99:8080" };

        string sig1 = RobloxHbaService.GetNetworkSignatureForAccount(proxyUriAcc1);
        string sig2 = RobloxHbaService.GetNetworkSignatureForAccount(proxyUriAcc2);
        string sig3 = RobloxHbaService.GetNetworkSignatureForAccount(proxyOtherHost);

        Assert.Equal(24, sig1.Length);
        Assert.NotEqual(directBase, sig1);
        Assert.Equal(sig1, sig2); // Same host:port with different credentials produces identical endpoint signature
        Assert.NotEqual(sig1, sig3);
    }

    Assert.Equal("http://203.0.113.10:8080", NetworkSignatureHelper.NormalizeAccountProxyEndpoint("http://u:p@203.0.113.10:8080"));
    Assert.Equal("http://203.0.113.10:8080", NetworkSignatureHelper.NormalizeAccountProxyEndpoint("203.0.113.10:8080:u:p"));
    Assert.Equal("socks5://203.0.113.10:1080", NetworkSignatureHelper.NormalizeAccountProxyEndpoint("socks5://u:p@203.0.113.10:1080"));
    Assert.Null(NetworkSignatureHelper.NormalizeAccountProxyEndpoint("   "));
}

[Fact]
public void FastLoginAutomation_And_AccountFieldsDialog_EnforceHttpsResourceFilterAndProtectedFieldGuard()
{
    string repoRoot = FindRepoRoot();
    string fastLogin = File.ReadAllText(Path.Combine(repoRoot, "src", "Shiro", "AvaloniaUI", "Views", "FastLoginDialog.Automation.cs"));
    Assert.Contains("AddWebResourceRequestedFilter(\"https://*.roblox.com/*\"", fastLogin, StringComparison.Ordinal);
    Assert.Contains("!WebAuthPolicy.IsTrustedRobloxOrigin(uri)", fastLogin, StringComparison.Ordinal);

    string fieldsDialog = File.ReadAllText(Path.Combine(repoRoot, "src", "Shiro", "AvaloniaUI", "Views", "AccountFieldsDialog.axaml.cs"));
    Assert.Contains("GetPublicFieldsSnapshot()", fieldsDialog, StringComparison.Ordinal);
    Assert.Contains("Account.IsProtectedInternalField", fieldsDialog, StringComparison.Ordinal);
}
```

- [ ] **Step 2: Run targeted tests to verify failure**

Run: `dotnet test "tests/Shiro.Tests/Shiro.Tests.csproj" -c Release --filter "FullyQualifiedName~GetNetworkSignatureForAccount_IncludesProxyHostPort|FullyQualifiedName~FastLoginAutomation_And_AccountFieldsDialog"`
Expected: FAIL.

- [ ] **Step 3: Implement `GetNetworkSignatureForAccount`, HTTPS BAT filter, and `AccountFieldsDialog` guard**

1. Add `NormalizeAccountProxyEndpoint(string? rawProxy)` in `NetworkSignatureHelper.cs` and `GetNetworkSignatureForAccount(Account? account)` in `RobloxHbaService.Network.cs`.
2. Replace `RobloxHbaService.GetLocalNetworkSignature()` with `RobloxHbaService.GetNetworkSignatureForAccount(this)` (or `(account)`) on the exact single lines in:
   - `Account.Api.cs:128`
   - `Account.HbaBinding.cs:201`
   - `Account.HbaBinding.SessionSync.cs:26` and `:104`
   - `Account.SessionState.CookieRotation.cs:134`
   - `AccountLaunchService.cs:226` (**keep 0 net line delta** — `AccountLaunchService` is at `1197 / 1200` lines!)
   - `AccountLaunchService.Home.cs:148` (**keep 0 net line delta**!)
   - In `AccountManager.Accounts.cs:258` (where a batch of accounts is checked before `account` loop), keep `localNetSig = RobloxHbaService.GetLocalNetworkSignature()` for direct accounts or evaluate per-account signature inside the account check using `RobloxHbaService.GetNetworkSignatureForAccount(acc)`.
3. In `FastLoginDialog.Automation.cs`: change line 42 filter from `"*://*.roblox.com/*"` to `"https://*.roblox.com/*"` and add `if (!WebAuthPolicy.IsTrustedRobloxOrigin(uri)) return;` right after reading `string? uri = e.Request?.Uri;` in `OnWebResourceRequested` (`line 75`).
4. In `AccountFieldsDialog.axaml.cs`:
   - Constructor (`lines 52-58`): populate `_fieldItems` from `_account.GetPublicFieldsSnapshot()` instead of raw `_account.Fields`.
   - `BtnAddField_Click`: add `if (Account.IsProtectedInternalField(key)) return;` right after `if (string.IsNullOrEmpty(key)) return;`, and mutate via `_account.SetField(key, val);` instead of `_account.Fields[key] = val;`.
   - `BtnDeleteField_Click`: add `if (Account.IsProtectedInternalField(item.Key)) return;` and call `_account.RemoveField(item.Key);` instead of `_account.Fields.Remove(item.Key);`.

- [ ] **Step 4: Run HBA, launch, and architecture tests**

Run: `dotnet test "tests/Shiro.Tests/Shiro.Tests.csproj" -c Release --filter "FullyQualifiedName~RobloxHba|FullyQualifiedName~AccountLaunchService|FullyQualifiedName~ArchitectureTests"`
Expected: PASS (`0` failed).

- [ ] **Step 5: Commit**

```powershell
git add src/Shiro/Services/Network/NetworkSignatureHelper.cs src/Shiro/Services/Auth/RobloxHbaService.Network.cs src/Shiro/Classes/Account.Api.cs src/Shiro/Classes/Account.HbaBinding.cs src/Shiro/Classes/Account.HbaBinding.SessionSync.cs src/Shiro/Classes/Account.SessionState.CookieRotation.cs src/Shiro/Services/Launch/AccountLaunchService.cs src/Shiro/Services/Launch/AccountLaunchService.Home.cs src/Shiro/AccountManager.Accounts.cs src/Shiro/AvaloniaUI/Views/FastLoginDialog.Automation.cs src/Shiro/AvaloniaUI/Views/AccountFieldsDialog.axaml.cs tests/Shiro.Tests/RobloxHbaAndIpPersistenceTests.cs
git commit -m "fix(hba): bind per-account proxy into network signatures, enforce https BAT filter, and scrub AccountFieldsDialog"
```

---

### Task 4: Eliminate Reflection Fallback in `FastJson` (`indented: true`) and `LocalizationService` for NativeAOT Readiness

**Files:**
- Modify: `src/Shiro/Services/Storage/JsonExtensions.cs:55-133`
- Modify: `src/Shiro/Services/Ui/LocalizationService.Resources.cs:65`
- Test: `tests/Shiro.Tests/LocalizationServiceTests.cs`

**Interfaces:**
- Produces: `internal static readonly ShiroAppJson IndentedContext` on `FastJson` (`new ShiroAppJson(CreateContextOptions(indented: true))`), used by `FastJson.Serialize<T>(T value, bool indented = false)` and `FastJson.SerializeToUtf8Bytes<T>(T value, bool indented = false)` so both `indented: false` (`ShiroAppJson.Default`) and `indented: true` (`IndentedContext`) serialize via source-generated `JsonTypeInfo` without reflection.

- [ ] **Step 1: Write failing test in `tests/Shiro.Tests/LocalizationServiceTests.cs`**

```csharp
[Fact]
public void FastJson_IndentedSerialize_UsesSourceGeneratedContext_WithoutReflectionResolver()
{
    var items = new List<AccountExportJsonItem>
    {
        new("Alice", 12345, "Main", "Farming", "pw", 500, null, new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc), "tok")
    };

    JsonTypeInfo? indentedTypeInfo = FastJson.IndentedContext.GetTypeInfo(items.GetType());
    Assert.NotNull(indentedTypeInfo);
    Assert.True(indentedTypeInfo!.Options.WriteIndented);

    string compact = FastJson.Serialize<object>(items, indented: false);
    string pretty = FastJson.Serialize<object>(items, indented: true);
    byte[] prettyUtf8 = FastJson.SerializeToUtf8Bytes<object>(items, indented: true);

    Assert.DoesNotContain("\n", compact, StringComparison.Ordinal);
    Assert.Contains("\n  ", pretty, StringComparison.Ordinal);
    Assert.Equal(pretty, System.Text.Encoding.UTF8.GetString(prettyUtf8));
}
```

- [ ] **Step 2: Run targeted test to verify failure**

Run: `dotnet test "tests/Shiro.Tests/Shiro.Tests.csproj" -c Release --filter "FullyQualifiedName~FastJson_IndentedSerialize_UsesSourceGeneratedContext_WithoutReflectionResolver"`
Expected: Compilation FAIL (`FastJson.IndentedContext` does not exist).

- [ ] **Step 3: Wire `FastJson.IndentedContext` and `ShiroAppJson.Default.DictionaryStringString`**

1. In `JsonExtensions.cs`:
   - Add `private static JsonSerializerOptions CreateContextOptions(bool indented)` configuring `PropertyNameCaseInsensitive = true`, `ReadCommentHandling = JsonCommentHandling.Skip`, `AllowTrailingCommas = true`, `IncludeFields = true`, `WriteIndented = indented`, and `Converters = { new FlexibleStringConverter() }`.
   - Define `internal static readonly ShiroAppJson IndentedContext = new(CreateContextOptions(indented: true));`.
   - In `FastJson.Serialize<T>` (`line 116`) and `FastJson.SerializeToUtf8Bytes<T>` (`line 125`): select `var ctx = indented ? IndentedContext : ShiroAppJson.Default;` and when `value != null && ctx.GetTypeInfo(value.GetType()) is JsonTypeInfo typeInfo`, call `JsonSerializer.Serialize(value, typeInfo)` / `JsonSerializer.SerializeToUtf8Bytes(value, typeInfo)` directly (handling both `indented: false` and `indented: true` via source-generated metadata!).
2. In `LocalizationService.Resources.cs:65`: change `JsonSerializer.Deserialize<Dictionary<string, string>>(stream)` to `JsonSerializer.Deserialize(stream, ShiroAppJson.Default.DictionaryStringString)`.

- [ ] **Step 4: Run JSON, export, and localization tests**

Run: `dotnet test "tests/Shiro.Tests/Shiro.Tests.csproj" -c Release --filter "FullyQualifiedName~LocalizationServiceTests|FullyQualifiedName~LocalizationIntegrityTests|FullyQualifiedName~VaultExportTests|FullyQualifiedName~ArchitectureTests"`
Expected: PASS (`0` failed).

- [ ] **Step 5: Commit**

```powershell
git add src/Shiro/Services/Storage/JsonExtensions.cs src/Shiro/Services/Ui/LocalizationService.Resources.cs tests/Shiro.Tests/LocalizationServiceTests.cs
git commit -m "fix(json): use source-generated ShiroAppJson for indented serialization and resource loading"
```

---

### Task 5: Translate All 392 Remaining Vietnamese Entries in `en.json` and Enforce Placeholder Parity

**Files:**
- Modify: `src/Shiro/Resources/Strings/en.json`
- Test: `tests/Shiro.Tests/LocalizationIntegrityTests.cs`

**Interfaces:**
- Produces: 100% English values across `src/Shiro/Resources/Strings/en.json` (except the 3 intentional bilingual language-switcher entries: `"Cap_Language"`, `"Set_Language_EN"`, `"Set_Language_VN"`), with identical `{0}`, `{1}`, `{0:N0}` composite format tokens to `vi.json`.

- [ ] **Step 1: Write failing integrity & placeholder-parity tests in `tests/Shiro.Tests/LocalizationIntegrityTests.cs`**

```csharp
private static readonly HashSet<string> AllowedBilingualKeysInEnJson = new(StringComparer.OrdinalIgnoreCase)
{
    "Cap_Language",
    "Set_Language_EN",
    "Set_Language_VN"
};

private static readonly Regex VietnameseDiacriticRegex = new(
    "[àáạảãâầấậẩẫăằắặẳẵèéẹẻẽêềếệểễìíịỉĩòóọỏõôồốộổỗơờớợởỡùúụủũưừứựửữỳýỵỷỹđÀÁẠẢÃÂẦẤẬẨẪĂẰẮẶẲẴÈÉẸẺẼÊỀẾỆỂỄÌÍỊỈĨÒÓỌỎÕÔỒỐỘỔỖƠỜỚỢỞỠÙÚỤỦŨƯỪỨỰỬỮỲÝỴỶỸĐ]",
    RegexOptions.Compiled);

[Fact]
public void EnJson_ShouldNotContainUntranslatedVietnameseDiacritics()
{
    var enDict = JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(EnJsonPath))!;
    var untranslated = enDict
        .Where(kvp => !AllowedBilingualKeysInEnJson.Contains(kvp.Key) && VietnameseDiacriticRegex.IsMatch(kvp.Value))
        .Select(kvp => $"{kvp.Key}: {kvp.Value}")
        .ToList();

    Assert.True(untranslated.Count == 0,
        $"Found {untranslated.Count} untranslated Vietnamese entries in en.json:\n{string.Join("\n", untranslated.Take(30))}");
}

[Fact]
public void EnJson_And_ViJson_ShouldHaveMatchingFormatPlaceholders()
{
    var viDict = JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(ViJsonPath))!;
    var enDict = JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(EnJsonPath))!;
    var placeholderRegex = new Regex(@"\{\d+(?:[:,][^}]+)?\}", RegexOptions.Compiled);

    var mismatches = new List<string>();
    foreach (var (key, viVal) in viDict)
    {
        if (!enDict.TryGetValue(key, out string? enVal)) continue;
        var viTokens = placeholderRegex.Matches(viVal).Select(m => m.Value).OrderBy(x => x).ToArray();
        var enTokens = placeholderRegex.Matches(enVal).Select(m => m.Value).OrderBy(x => x).ToArray();
        if (!viTokens.SequenceEqual(enTokens))
        {
            mismatches.Add($"{key}: vi=[{string.Join(", ", viTokens)}] vs en=[{string.Join(", ", enTokens)}]");
        }
    }

    Assert.True(mismatches.Count == 0,
        $"Format placeholder mismatches between vi.json and en.json:\n{string.Join("\n", mismatches)}");
}
```

- [ ] **Step 2: Run targeted test to verify failure**

Run: `dotnet test "tests/Shiro.Tests/Shiro.Tests.csproj" -c Release --filter "FullyQualifiedName~EnJson_ShouldNotContainUntranslatedVietnameseDiacritics|FullyQualifiedName~EnJson_And_ViJson_ShouldHaveMatchingFormatPlaceholders"`
Expected: FAIL (`Found 392 untranslated Vietnamese entries in en.json`).

- [ ] **Step 3: Translate all 392 Vietnamese entries in `src/Shiro/Resources/Strings/en.json` to accurate, natural English**

Translate every non-allowlisted Vietnamese value in `en.json` while preserving:
- Exact key names and key ordering.
- Exact `{0}`, `{1}`, `{0:N0}`, `{0:F1}` format specifiers and `\n` line breaks matching `vi.json`.
- Domain terminology consistent with the rest of `en.json` (`Vault`, `Master Password`, `Session`, `Re-login`, `Multi-Roblox`, `Nexus`, `HBA`, `Server`, `Place ID`, `Job ID`, `Browser Tracker ID`, `Green` / `Pink` / `Purple` / `White` for `AccentTheme.*`).
- Valid UTF-8 JSON formatting.
*(Check if any existing unit test in `tests/Shiro.Tests` asserts a specific Vietnamese fallback when `AppLanguage.English` is active, and verify all localization tests stay green.)*

- [ ] **Step 4: Run localization integrity & full localization test suite**

Run: `dotnet test "tests/Shiro.Tests/Shiro.Tests.csproj" -c Release --filter "FullyQualifiedName~Localization"`
Expected: PASS (`0` failed).

- [ ] **Step 5: Commit**

```powershell
git add src/Shiro/Resources/Strings/en.json tests/Shiro.Tests/LocalizationIntegrityTests.cs
git commit -m "fix(i18n): translate all 392 remaining Vietnamese entries in en.json and enforce placeholder parity"
```

---

### Task 6: Localize All 14 `.axaml` Views & 4 Dialog ViewModels and Seal C#/AXAML Hardcoded-Vietnamese Ratchets

**Files:**
- Modify: `src/Shiro/AvaloniaUI/Views/AboutDialog.axaml`
- Modify: `src/Shiro/AvaloniaUI/Views/AccountFieldsDialog.axaml`
- Modify: `src/Shiro/AvaloniaUI/Views/AccountUtilsDialog.axaml`
- Modify: `src/Shiro/AvaloniaUI/Views/AltGenDialog.axaml`
- Modify: `src/Shiro/AvaloniaUI/Views/BulkImportDialog.axaml`
- Modify: `src/Shiro/AvaloniaUI/Views/DashboardView.axaml` *(keep `<= 400` lines and every line `<= 200` chars!)*
- Modify: `src/Shiro/AvaloniaUI/Views/FastLoginDialog.axaml`
- Modify: `src/Shiro/AvaloniaUI/Views/InputDialog.axaml`
- Modify: `src/Shiro/AvaloniaUI/Views/MessageBoxDialog.axaml`
- Modify: `src/Shiro/AvaloniaUI/Views/MissingAssetsDialog.axaml`
- Modify: `src/Shiro/AvaloniaUI/Views/NexusWindow.axaml`
- Modify: `src/Shiro/AvaloniaUI/Views/ProxyManagerDialog.axaml`
- Modify: `src/Shiro/AvaloniaUI/Views/SettingsView.axaml`
- Modify: `src/Shiro/AvaloniaUI/Views/UpdaterWindow.axaml`
- Modify: `src/Shiro/ViewModels/Dialogs/AltGenViewModel.cs:44`
- Modify: `src/Shiro/ViewModels/Dialogs/MissingAssetsViewModel.cs:24-26`
- Modify: `src/Shiro/ViewModels/Dialogs/NexusViewModel.cs:36-38,56`
- Modify: `src/Shiro/ViewModels/Dialogs/UpdaterViewModel.cs:43`
- Modify: `src/Shiro/Resources/Strings/vi.json`, `src/Shiro/Resources/Strings/en.json`
- Modify: `tests/Shiro.Tests/ArchitectureTests.cs:610-612`
- Test: `tests/Shiro.Tests/LocalizationIntegrityTests.cs`

**Interfaces:**
- Consumes: `EnJson_ShouldNotContainUntranslatedVietnameseDiacritics` and `EnJson_And_ViJson_ShouldHaveMatchingFormatPlaceholders` from Task 5 (every new key added to `vi.json` and `en.json` in Task 6 must pass both!).
- Produces: Zero hardcoded Vietnamese text across all `.axaml` files (outside XML `<!-- ... -->` comments) and across all `.cs` files (outside `LocalizationService*`), enforced automatically by `AxamlFiles_ShouldNotContain_HardcodedVietnameseText` and the fixed `CSharpSourceFiles_ShouldNotContain_HardcodedVietnameseStringLiterals`.

- [ ] **Step 1: Fix the C# regex bug in `ArchitectureTests.cs:611` and add `AxamlFiles_ShouldNotContain_HardcodedVietnameseText` in `LocalizationIntegrityTests.cs`**

1. In `tests/Shiro.Tests/ArchitectureTests.cs:610-612`, fix the double-escaped `\\r\\n` character class (which previously excluded any literal containing `'r'` or `'n'`!) to:
   ```csharp
   var vnLiteralRegex = new System.Text.RegularExpressions.Regex(
       "\"[^\"\r\n]*[àáảãạăằắẳẵặâầấẩẫậèéẻẽẹêềếểễệìíỉĩịòóỏõọôồốổỗộơờớởỡợùúủũụưừứửữựỳýỷỹỵđÀÁẢÃẠĂẰẮẲẴẶÂẦẤẨẪẬÈÉẺẼẸÊỀẾỂỄỆÌÍỈĨỊÒÓỎÕỌÔỒỐỔỖỘƠỜỚỞỠỢÙÚỦŨỤƯỪỨỬỮỰỲÝỶỸỴĐ][^\"\r\n]*\"");
   ```
2. In `tests/Shiro.Tests/LocalizationIntegrityTests.cs`, add:
   ```csharp
   [Fact]
   public void AxamlFiles_ShouldNotContain_HardcodedVietnameseText()
   {
       var commentRegex = new Regex(@"<!--[\s\S]*?-->", RegexOptions.Compiled);
       var violations = new List<string>();

       foreach (var file in SourceFiles("src/Shiro", "*.axaml"))
       {
           string raw = File.ReadAllText(file);
           string withoutComments = commentRegex.Replace(raw, string.Empty);
           string[] lines = withoutComments.Split('\n');
           for (int i = 0; i < lines.Length; i++)
           {
               if (VietnameseDiacriticRegex.IsMatch(lines[i]))
               {
                   violations.Add($"{Path.GetFileName(file)}:{i + 1} -> {lines[i].Trim()}");
               }
           }
       }

       Assert.True(violations.Count == 0,
           $"Found hardcoded Vietnamese text in AXAML files ({violations.Count}):\n{string.Join("\n", violations.Take(40))}");
   }
   ```

- [ ] **Step 2: Run targeted tests to verify failure**

Run: `dotnet test "tests/Shiro.Tests/Shiro.Tests.csproj" -c Release --filter "FullyQualifiedName~CSharpSourceFiles_ShouldNotContain_HardcodedVietnameseStringLiterals|FullyQualifiedName~AxamlFiles_ShouldNotContain_HardcodedVietnameseText"`
Expected: FAIL (`9` C# violations across 4 ViewModels + `~114` AXAML violations across 14 `.axaml` files).

- [ ] **Step 3: Replace hardcoded Vietnamese strings in the 4 ViewModels and 14 `.axaml` views**

1. In `AltGenViewModel.cs:44`, `MissingAssetsViewModel.cs:24-26`, `NexusViewModel.cs:36-38,56`, and `UpdaterViewModel.cs:43`: replace the 9 Vietnamese default field/property initializers with `LocalizationService.Get(...)` / `LocalizationService.Format(...)` using existing or newly added descriptive semantic keys (never `Auto_*` — `LocalizationKeys_ShouldNotUseAutoGeneratedNames` forbids `Auto_`). Check `UpdaterViewModelTests`, `NexusViewModelTests`, `AltGenViewModelTests`, and `MissingAssetsViewModelTests` if any test asserts initial status text and align cleanly.
2. Across the 14 `.axaml` files, replace hardcoded Vietnamese `Text=`, `Content=`, `Header=`, `Title=`, `Watermark=`, and `ToolTip.Tip=` strings with `{DynamicResource Loc_<Key>}` (remember `LocalizationService.EnsureCoreKeys()` registers both `<Key>` and `Loc_<Key>` in `_enResourceDict` and `_viResourceDict`; in `SettingsView.axaml:49-50`, bind `Content="{DynamicResource Set_Language_VN}"` and `Content="{DynamicResource Set_Language_EN}"`), reusing existing keys where exact matches already exist in `vi.json` and adding clean semantic keys (`[A-Za-z0-9_.]+`, avoiding substrings `"Theme"`/`"Font"` or suffixes `"Brush"`/`"Color"` so `EveryReferencedKey_ExistsInBothDictionaries` validates every key) to both `vi.json` (original Vietnamese text) and `en.json` (accurate English text).
   - **Strict AXAML budget check:** Keep `DashboardView.axaml <= 400` lines (replace attributes in-place without adding extra lines) and keep **every line in every `.axaml` file `<= 200` characters**!

- [ ] **Step 4: Run full build, full test suite, and Cobertura coverage gate**

Run:
```powershell
Remove-Item -LiteralPath "coverage" -Recurse -Force -ErrorAction SilentlyContinue
dotnet build "Shiro.sln" -c Release
dotnet test "tests/Shiro.Tests/Shiro.Tests.csproj" -c Release --collect:"XPlat Code Coverage" --settings "tests/coverage.runsettings" --results-directory "coverage"
powershell -ExecutionPolicy Bypass -File .\scripts\Check-Coverage.ps1 -CoverageDir "coverage" -MinOverall 75.0 -MinBranch 60.0 -MinCore 92.0
Remove-Item -LiteralPath "coverage" -Recurse -Force -ErrorAction SilentlyContinue
```
Expected: `0` warnings, `0` errors, `0` failed tests (`>= 1852` passing), all global coverage gates (`>= 75%` line, `>= 60%` branch, `>= 92%` core) and all 19 per-file branch floors PASS.

- [ ] **Step 5: Commit**

```powershell
git add src/Shiro/AvaloniaUI/Views/*.axaml src/Shiro/ViewModels/Dialogs/AltGenViewModel.cs src/Shiro/ViewModels/Dialogs/MissingAssetsViewModel.cs src/Shiro/ViewModels/Dialogs/NexusViewModel.cs src/Shiro/ViewModels/Dialogs/UpdaterViewModel.cs src/Shiro/Resources/Strings/vi.json src/Shiro/Resources/Strings/en.json tests/Shiro.Tests/ArchitectureTests.cs tests/Shiro.Tests/LocalizationIntegrityTests.cs
git commit -m "fix(i18n): localize all AXAML views and dialog ViewModels and enforce Vietnamese literal ratchets"
```
