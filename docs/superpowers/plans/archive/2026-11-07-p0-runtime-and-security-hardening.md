# P0 Runtime, Memory Safety & Security Hardening Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Restore a 100% green test suite (`1791+` passing, `0` failing) and close all P0 runtime, data-safety, Win32 P/Invoke memory-safety, WebView2 HBA origin, Kestrel/Nexus API boundary, and Auto-Updater integrity defects identified in the 2026-11-07 project audit.

**Architecture:** Six independent, right-sized TDD tasks executed sequentially without breaking public schemas or blowing file/partial-class line budgets. Dynamic delegation replaces frozen startup snapshots in `AppServices`, mutable buffers replace immutable CLR strings in Win32 process creation, strict origin/field/UserID guards seal WebView2, Kestrel, and Nexus boundaries, and fail-closed SHA-256 + Authenticode checks secure binary updates.

**Tech Stack:** .NET 10 (`win-x64`), C# 13, Avalonia UI 11, WebView2, ASP.NET Core Kestrel, Win32 P/Invoke (`LibraryImport` / `wintrust.dll`), xUnit, NSubstitute.

**Spec:** Comprehensive Project Audit Report (`project_audit_report.md`, Section 3.1–3.5, 2026-11-07). Follow-up tracks (Phase 2: Win32 `MemoryOptimizer` + 395-key English i18n; Phase 3: `NetArchTest` namespace debt + `MainViewModel` disposal + doc sync) are intentionally scoped into separate subsequent plans so urgent safety fixes ship cleanly first.

## Global Constraints

- `dotnet build Shiro.sln -c Release`: `0` errors, `0` warnings (`TreatWarningsAsErrors=true`).
- `dotnet test tests/Shiro.Tests/Shiro.Tests.csproj -c Release`: `0` failed; total test count must strictly increase on every C# task (starting from `1791`).
- Coverage gates unchanged: `./scripts/Check-Coverage.ps1 -CoverageDir "coverage" -MinOverall 75.0 -MinBranch 60.0 -MinCore 92.0` must pass, including all per-file branch floors in `tests/coverage-floors.json`.
- Line budgets enforced by `ArchitectureTests`: every `.cs` file `<= 500` lines; partial classes within budget (specifically do **not** increase total lines of `AccountLaunchService`, currently at `1197 / 1200`, or `FastLoginCoordinator.cs`, currently at `500 / 500`).
- Zero `.GetAwaiter().GetResult()`, zero empty `catch {}` blocks, and UTF-8 encoding preserved on all touched files.
- Conventional Commit prefixes (`fix(di):`, `fix(launch):`, `fix(hba):`, `fix(security):`, `fix(nexus):`, `fix(updater):`).

## Review Focus

1. **Startup before `RAMSettings.ini` is loaded (`AppServices.GeneralSettings` split-brain):** Resolving `AppServices.GeneralSettings` when `AppSettings.ActiveIniFile` is `null` and later loading a new `IniFile` into `AccountManager.IniSettings` / `AppSettings.General` must immediately reflect reads and writes against the live `AppSettings.General` instance, while still honoring `AppServices.Override<ISettingsService>(mock)`. Pinned by `GeneralSettings_DelegatesDynamically_WhenActiveIniFileChangesAfterInit` in Task 1.
2. **Untrusted `jobId` characters in game launch URLs & CLI arguments:** Passing a `jobId` containing quotes, ampersands, spaces, or CLI flags (e.g. `123"&--flag`) into `AccountLaunchService` must be rejected/stripped before constructing `PlaceLauncher.ashx` or `RobloxPlayerBeta.exe` arguments. Pinned by `SanitizeJobId_AllowsValidIds_AndRejectsInjectionCharacters` in Task 2.
3. **Lookalike / non-HTTPS WebView2 origins (`https://roblox.com.evil.com`, `http://www.roblox.com`, `https://client-api.arkoselabs.com`):** Both injected HBA scripts and `FastLoginDialog.OnWebMessageReceived` must reject non-HTTPS schemes, third-party CAPTCHA iframes, and suffix-spoofed domains, accepting only exact `roblox.com` or `*.roblox.com` over `https:`. Pinned by `IsTrustedRobloxOrigin_ValidatesHttpsAndExactDomainBoundary` and `GetPersistedKeyInjectionScript_EnforcesOriginGuardAndNonExtractableImport` in Task 3.
4. **Case-variant internal secret field names on Kestrel API (`hbaprivatekey`, `HBATEMPLATE`, `lastboundipsig`):** Querying `/GetField`, `/SetField`, or `/RemoveField` with any casing of internal session/HBA metadata keys must be blocked even when `AllowAccountEditing=true`, and `/GetAccountsJson` must omit those keys from `Fields`. Pinned by `Dispatch_BlocksProtectedInternalFields_CaseInsensitively_AndScrubsAccountsJson` in Task 4.
5. **Pre-existing local `Update.zip` or tampered download archive:** Starting `UpdaterViewModel` when an unverified `Update.zip` already exists in `_baseDirectory` must delete it rather than extracting it, and any downloaded archive with a missing or mismatched SHA-256 digest must be deleted and surface an error without invoking `UpdateArchiveInstaller.Apply`. Pinned by `Start_DeletesPreExistingArchive_AndAbortsOnMissingOrMismatchedSha256` in Task 6.

---

## File Structure & Responsibility Map

- `src/Shiro/Services/AppServices.cs` — Replace snapshot `AddSingleton<ISettingsService>(AppSettings.General)` with a private `DelegatingSettingsService` forwarding live calls to `AppSettings.General`, and dispose transient `ServiceProvider` instances inside `OverrideScope.Dispose()`.
- `src/Shiro/Services/Launch/RobloxDirectLauncher.cs` — Add `SanitizeJobId(string? jobId)` whitelist validator (`[A-Za-z0-9\-_]`, max 64 chars) and harden `BuildGameLaunchArguments`.
- `src/Shiro/Services/Launch/AccountLaunchService.cs` — Escape validated `jobId` in `PlaceLauncher.ashx` URL on line 323 with zero net line growth.
- `src/Shiro/Services/Launch/RobloxDirectLauncher.DeElevation.cs` — Change `CreateProcessWithTokenW` P/Invoke `lpCommandLine` from immutable `string?` to mutable `Span<char>` and pass explicit `lpApplicationName: playerPath`.
- `scripts/Publish.ps1` — Preserve emergency pre-publish user-data backups by removing `'archive'` from `$legacyFolders`.
- `src/Shiro/Services/Auth/WebAuthPolicy.cs` — Add `IsTrustedRobloxOrigin(string? sourceUri)` (`https` + `roblox.com` / `*.roblox.com`).
- `src/Shiro/Services/Auth/RobloxHbaService.Binding.cs` & `RobloxHbaService.BrowserHooks.cs` — Add top-of-IIFE HTTPS `*.roblox.com` hostname guard; remove `window.__shiroHbaState.privateKeyPkcs8` exposure and set `extractable = false` in `GetPersistedKeyInjectionScript`.
- `src/Shiro/AvaloniaUI/Views/FastLoginDialog.Automation.cs` — Guard `OnWebMessageReceived` with `WebAuthPolicy.IsTrustedRobloxOrigin(e.Source)`.
- `src/Shiro/Classes/Account.HbaBinding.cs` & `src/Shiro/Services/Network/AccountApiDispatcher.Endpoints.cs` — Expose `Account.IsProtectedInternalField(string? key)` and filter protected keys in `/GetField`, `/SetField`, `/RemoveField`, and `/GetAccountsJson`.
- `src/Shiro/Nexus/NexusWebSocketServer.cs`, `src/Shiro/Nexus/ControlledAccount.cs`, `src/Shiro/Nexus/Nexus.lua`, `src/Shiro/ViewModels/Dialogs/NexusViewModel.cs` — Enforce strict `UserID` match when `LinkedAccount.UserID > 0`, block `Echo` from broadcasting `execute` commands laterally, and unify `execute <script>` command syntax.
- `.github/workflows/release.yml`, `src/Shiro/Services/Network/ReleaseInfoParser.cs`, `src/Shiro/Services/Storage/UpdateArchiveInstaller.cs`, `src/Shiro/ViewModels/Dialogs/UpdaterViewModel.cs`, `src/Shiro/Services/Launch/PrerequisiteService.cs` — Emit `Shiro-win-x64.zip` + `SHA256SUMS.txt` in CI, enforce fail-closed SHA-256 verification before extracting updates, and verify Microsoft Authenticode (`WinVerifyTrust`) before running `vc_redist.x86.exe`.

---

### Task 1: Fix `AppServices.GeneralSettings` Split-Brain DI State & `OverrideScope` Disposal

**Files:**
- Modify: `src/Shiro/Services/AppServices.cs:37-41,97-126,148-150`
- Test: `tests/Shiro.Tests/AppServicesOverrideTests.cs`
- Verify: `tests/Shiro.Tests/PresenceAndNexusBehaviorTests.cs`

**Interfaces:**
- Produces: `AppServices.GeneralSettings` dynamically delegates all `ISettingsService` members to the current `AppSettings.General` when no `AppServices.Override<ISettingsService>` scope is active, and resolves the overridden `ISettingsService` when an override scope is active.
- Produces: `OverrideScope` disposes the `ServiceProvider` built for that scope when popped.

- [ ] **Step 1: Write failing regression tests in `tests/Shiro.Tests/AppServicesOverrideTests.cs`**

```csharp
[Fact]
public void GeneralSettings_DelegatesDynamically_WhenActiveIniFileChangesAfterInit()
{
    var prevIni = AccountManager.IniSettings;
    var prevGeneral = AccountManager.General;
    try
    {
        _ = AppServices.ServiceProvider; // Force initialization before swapping IniFile

        var iniA = new IniFile();
        iniA.Section("General").Set("ShowPresence", "false");
        AccountManager.IniSettings = iniA;
        AccountManager.General = iniA.Section("General");
        Assert.False(AppServices.GeneralSettings.Get<bool>("ShowPresence", true));

        var iniB = new IniFile();
        iniB.Section("General").Set("ShowPresence", "true");
        AccountManager.IniSettings = iniB;
        AccountManager.General = iniB.Section("General");
        Assert.True(AppServices.GeneralSettings.Get<bool>("ShowPresence", false));

        AppServices.GeneralSettings.Set("CustomFlag", "enabled");
        Assert.Equal("enabled", iniB.Section("General").Get("CustomFlag"));
    }
    finally
    {
        AccountManager.IniSettings = prevIni;
        AccountManager.General = prevGeneral;
    }
}

[Fact]
public void Override_DisposesScopedServiceProvider_OnScopeExit()
{
    var mockSettings = Substitute.For<ISettingsService>();
    mockSettings.Get<bool>("ShowPresence", Arg.Any<bool>()).Returns(false);

    using (AppServices.Override<ISettingsService>(mockSettings))
    {
        Assert.Same(mockSettings, AppServices.GeneralSettings);
        Assert.False(AppServices.GeneralSettings.Get<bool>("ShowPresence", true));
    }

    Assert.NotSame(mockSettings, AppServices.GeneralSettings);
}
```

- [ ] **Step 2: Run targeted tests to verify failure**

Run: `dotnet test "tests/Shiro.Tests/Shiro.Tests.csproj" -c Release --filter "FullyQualifiedName~GeneralSettings_DelegatesDynamically_WhenActiveIniFileChangesAfterInit"`
Expected: FAIL (`Assert.True` or `Assert.False` fails because `AppServices` froze an earlier `IniSection` snapshot).

- [ ] **Step 3: Implement `DelegatingSettingsService` and `ServiceProvider` cleanup in `src/Shiro/Services/AppServices.cs`**

In `AppServices.cs`:
1. Add `private sealed class DelegatingSettingsService : ISettingsService` whose 8 interface methods (`Get`, `Get<T>`, `GetBool`, `GetInt`, `GetDecimal`, `Exists`, `Set`, `RemoveProperty`) forward directly to `(AppSettings.General ?? new IniSection("General"))`.
2. In `EnsureInitialized`, register `services.AddSingleton<ISettingsService>(new DelegatingSettingsService());` instead of `services.AddSingleton<ISettingsService>(AppSettings.General);`.
3. Store `ServiceProvider _ownedProvider` on `OverrideScope` (passed from `newServices.BuildServiceProvider()` in `Override<T>`) and call `_ownedProvider.Dispose()` inside `OverrideScope.Dispose()` after restoring `_serviceProvider = _previousProvider`.

- [ ] **Step 4: Run `AppServicesOverrideTests`, `PresenceAndNexusBehaviorTests`, and full test suite**

Run: `dotnet test "tests/Shiro.Tests/Shiro.Tests.csproj" -c Release --nologo --verbosity minimal`
Expected: PASS (`Failed: 0, Passed: 1793`).

- [ ] **Step 5: Commit**

```powershell
git add src/Shiro/Services/AppServices.cs tests/Shiro.Tests/AppServicesOverrideTests.cs
git commit -m "fix(di): delegate GeneralSettings dynamically to live AppSettings.General"
```

---

### Task 2: Fix `CreateProcessWithTokenW` P/Invoke Buffer Safety, `jobId` Sanitization & `Publish.ps1` Backup Retention

**Files:**
- Modify: `src/Shiro/Services/Launch/RobloxDirectLauncher.DeElevation.cs:60-71,203-215`
- Modify: `src/Shiro/Services/Launch/RobloxDirectLauncher.cs:126-135`
- Modify: `src/Shiro/Services/Launch/AccountLaunchService.cs:323`
- Modify: `scripts/Publish.ps1:93-101`
- Test: `tests/Shiro.Tests/RobloxDirectLauncherBehaviorTests.cs`

**Interfaces:**
- Produces: `public static string SanitizeJobId(string? jobId)` on `RobloxDirectLauncher` — returns trimmed `jobId` if and only if length is `1..64` and every character is ASCII letter, digit, `'-'`, or `'_'`; otherwise returns `string.Empty`.
- Produces: `CreateProcessWithToken` private P/Invoke taking `string? lpApplicationName, Span<char> lpCommandLine`, invoked with `lpApplicationName: playerPath` and a null-terminated `char[]` buffer (`cmdChars`).

- [ ] **Step 1: Write failing tests in `tests/Shiro.Tests/RobloxDirectLauncherBehaviorTests.cs`**

```csharp
[Theory]
[InlineData("550e8400-e29b-41d4-a716-446655440000", "550e8400-e29b-41d4-a716-446655440000")]
[InlineData("  job_123-ABC  ", "job_123-ABC")]
[InlineData("123\" --evil", "")]
[InlineData("123&request=RequestPrivateGame", "")]
[InlineData("", "")]
[InlineData(null, "")]
public void SanitizeJobId_AllowsValidIds_AndRejectsInjectionCharacters(string? raw, string expected)
{
    Assert.Equal(expected, RobloxDirectLauncher.SanitizeJobId(raw));
}

[Fact]
public void BuildGameLaunchArguments_StripsQuotesFromTicketJoinUrlAndTracker()
{
    string args = RobloxDirectLauncher.BuildGameLaunchArguments("tok\"1", "https://assetgame.roblox.com/game/PlaceLauncher.ashx?a=1\"2", "123\"45");
    Assert.DoesNotContain("tok\"1", args);
    Assert.DoesNotContain("1\"2", args);
    Assert.DoesNotContain("123\"45", args);
}

[Fact]
public void PublishScript_DoesNotDeleteArchiveBackupFolder()
{
    var dir = new DirectoryInfo(AppContext.BaseDirectory);
    while (dir != null && !File.Exists(Path.Combine(dir.FullName, "Shiro.sln")))
        dir = dir.Parent;

    Assert.NotNull(dir);
    string publishPs1 = File.ReadAllText(Path.Combine(dir!.FullName, "scripts", "Publish.ps1"));
    Assert.DoesNotContain("'archive'", publishPs1);
}
```

- [ ] **Step 2: Run targeted tests to verify failure**

Run: `dotnet test "tests/Shiro.Tests/Shiro.Tests.csproj" -c Release --filter "FullyQualifiedName~RobloxDirectLauncherBehaviorTests"`
Expected: Compilation/assertion FAIL (`SanitizeJobId` missing and `'archive'` present in `Publish.ps1`).

- [ ] **Step 3: Implement P/Invoke mutable buffer, `SanitizeJobId`, URL escaping, and `Publish.ps1` fix**

1. In `RobloxDirectLauncher.cs`: implement `SanitizeJobId(string? jobId)` (`1..64` ASCII `[A-Za-z0-9\-_]`); in `BuildGameLaunchArguments` and `BuildHomeLaunchArguments`, strip `"`/`\r`/`\n` from `ticket` and `joinUrl`, and restrict `browserTrackerId` to ASCII digits.
2. In `AccountLaunchService.cs:323` (keep exact line count): replace `string.IsNullOrEmpty(jobId)` usage on that single line by normalizing `jobId = RobloxDirectLauncher.SanitizeJobId(jobId);` inline or via `Uri.EscapeDataString` so `AccountLaunchService` adds `0` net lines across partials.
3. In `RobloxDirectLauncher.DeElevation.cs:60-71,203-215`: change `string? lpCommandLine` to `Span<char> lpCommandLine`; allocate `char[] cmdBuffer = ($"\"{playerPath}\" {args}\0").ToCharArray();` and pass `lpApplicationName: playerPath, lpCommandLine: cmdBuffer`.
4. In `scripts/Publish.ps1:93`: change `$legacyFolders = @('Release-Latest', 'obj', 'artifacts', 'archive')` to `$legacyFolders = @('Release-Latest', 'obj', 'artifacts')`.

- [ ] **Step 4: Run tests (including `ArchitectureTests` partial-class line budget check)**

Run: `dotnet test "tests/Shiro.Tests/Shiro.Tests.csproj" -c Release --filter "FullyQualifiedName~RobloxDirectLauncherBehaviorTests|FullyQualifiedName~ArchitectureTests"`
Expected: PASS (`0` failed).

- [ ] **Step 5: Commit**

```powershell
git add src/Shiro/Services/Launch/RobloxDirectLauncher.cs src/Shiro/Services/Launch/RobloxDirectLauncher.DeElevation.cs src/Shiro/Services/Launch/AccountLaunchService.cs scripts/Publish.ps1 tests/Shiro.Tests/RobloxDirectLauncherBehaviorTests.cs
git commit -m "fix(launch): use mutable buffer in CreateProcessWithTokenW, sanitize jobId, and keep publish backups"
```

---

### Task 3: Lock WebView2 HBA Scripts & `OnWebMessageReceived` to `https://*.roblox.com` (`extractable: false`)

**Files:**
- Modify: `src/Shiro/Services/Auth/WebAuthPolicy.cs:95-105`
- Modify: `src/Shiro/Services/Auth/RobloxHbaService.Binding.cs:229-265`
- Modify: `src/Shiro/Services/Auth/RobloxHbaService.BrowserHooks.cs:13-28`
- Modify: `src/Shiro/AvaloniaUI/Views/FastLoginDialog.Automation.cs:105-115`
- Test: `tests/Shiro.Tests/RobloxHbaAndIpPersistenceTests.cs`

**Interfaces:**
- Produces: `public static bool IsTrustedRobloxOrigin(string? sourceUri)` on `WebAuthPolicy` — returns `true` iff `Uri.TryCreate(sourceUri, UriKind.Absolute, out var uri)` succeeds with `uri.Scheme == Uri.UriSchemeHttps` and `uri.Host` equal to `"roblox.com"` or ending with `".roblox.com"` (OrdinalIgnoreCase).

- [ ] **Step 1: Write failing security tests in `tests/Shiro.Tests/RobloxHbaAndIpPersistenceTests.cs`**

```csharp
[Theory]
[InlineData("https://www.roblox.com/login", true)]
[InlineData("https://roblox.com/", true)]
[InlineData("https://auth.roblox.com/v2/login", true)]
[InlineData("http://www.roblox.com/login", false)]
[InlineData("https://roblox.com.evil.example/login", false)]
[InlineData("https://notroblox.com/login", false)]
[InlineData("https://client-api.arkoselabs.com/fc/gc/", false)]
[InlineData("about:blank", false)]
[InlineData("", false)]
[InlineData(null, false)]
public void IsTrustedRobloxOrigin_ValidatesHttpsAndExactDomainBoundary(string? url, bool expected)
{
    Assert.Equal(expected, WebAuthPolicy.IsTrustedRobloxOrigin(url));
}

[Fact]
public void GetPersistedKeyInjectionScript_EnforcesOriginGuardAndNonExtractableImport()
{
    var (privB64, pubB64) = GenerateP256KeyPair();
    string script = RobloxHbaService.GetPersistedKeyInjectionScript(privB64, pubB64, RobloxHbaService.DefaultTemplate);

    Assert.Contains("location.protocol !== 'https:'", script);
    Assert.Contains(".endsWith('.roblox.com')", script);
    Assert.DoesNotContain("privateKeyPkcs8 =", script);
    Assert.Contains("importKey('pkcs8', fromB64(privB64), alg, false, ['sign'])", script);

    string captureScript = RobloxHbaService.GetBrowserCaptureScript();
    Assert.Contains("location.protocol !== 'https:'", captureScript);
    Assert.Contains(".endsWith('.roblox.com')", captureScript);
}
```

- [ ] **Step 2: Run targeted tests to verify failure**

Run: `dotnet test "tests/Shiro.Tests/Shiro.Tests.csproj" -c Release --filter "FullyQualifiedName~IsTrustedRobloxOrigin_ValidatesHttpsAndExactDomainBoundary|FullyQualifiedName~GetPersistedKeyInjectionScript_EnforcesOriginGuardAndNonExtractableImport"`
Expected: FAIL.

- [ ] **Step 3: Implement `IsTrustedRobloxOrigin`, JS hostname guards, non-extractable import, and `e.Source` check**

1. Add `WebAuthPolicy.IsTrustedRobloxOrigin(string? sourceUri)` in `src/Shiro/Services/Auth/WebAuthPolicy.cs`.
2. At the top of the outer `try` in both `RobloxHbaService.GetPersistedKeyInjectionScript` (`RobloxHbaService.Binding.cs:231`) and `RobloxHbaService.GetBrowserCaptureScript` (`RobloxHbaService.BrowserHooks.cs:15`), guard with:
   `const h = (window.location && window.location.hostname ? window.location.hostname : '').toLowerCase(); if (!window.location || window.location.protocol !== 'https:' || (h !== 'roblox.com' && !h.endsWith('.roblox.com'))) return;`
3. In `RobloxHbaService.Binding.cs:238-255`: remove `window.__shiroHbaState.privateKeyPkcs8 = privB64;` (keep `privB64` strictly inside the closure scope) and change `subtle.importKey('pkcs8', fromB64(privB64), alg, true, ['sign'])` to `false`.
4. In `FastLoginDialog.Automation.cs:108`: add `if (!WebAuthPolicy.IsTrustedRobloxOrigin(e.Source)) return;` before reading the web message payload.

- [ ] **Step 4: Run HBA and security test suites**

Run: `dotnet test "tests/Shiro.Tests/Shiro.Tests.csproj" -c Release --filter "FullyQualifiedName~RobloxHba|FullyQualifiedName~WebAuthPolicy"`
Expected: PASS (`0` failed).

- [ ] **Step 5: Commit**

```powershell
git add src/Shiro/Services/Auth/WebAuthPolicy.cs src/Shiro/Services/Auth/RobloxHbaService.Binding.cs src/Shiro/Services/Auth/RobloxHbaService.BrowserHooks.cs src/Shiro/AvaloniaUI/Views/FastLoginDialog.Automation.cs tests/Shiro.Tests/RobloxHbaAndIpPersistenceTests.cs
git commit -m "fix(hba): restrict WebView2 HBA hooks and messages to https roblox origins"
```

---

### Task 4: Block Internal `Hba*` / Session Metadata Fields on Kestrel API Endpoints

**Files:**
- Modify: `src/Shiro/Classes/Account.HbaBinding.cs:20-26`
- Modify: `src/Shiro/Services/Network/AccountApiDispatcher.Endpoints.cs:78-95,267-290`
- Test: `tests/Shiro.Tests/AccountApiDispatcherEndpointsBehaviorTests.cs`

**Interfaces:**
- Produces: `internal static bool IsProtectedInternalField(string? fieldName)` and `internal Dictionary<string, string> GetPublicFieldsSnapshot()` on `Account` (`Account.HbaBinding.cs`), blocking case-insensitive matches for `"HbaPrivateKey"`, `"HbaPublicKey"`, `"HbaTemplate"`, `"SessionUserAgent"`, `"LastBoundIpSig"`, and any key starting with `"Hba"` (`StringComparison.OrdinalIgnoreCase`).

- [ ] **Step 1: Write failing test in `tests/Shiro.Tests/AccountApiDispatcherEndpointsBehaviorTests.cs`**

```csharp
[Fact]
public async Task Dispatch_BlocksProtectedInternalFields_CaseInsensitively_AndScrubsAccountsJson()
{
    var mgr = AccountManager.Instance ?? new AccountManager();
    AccountManager.WSPassword = AdminPassword;
    AccountManager.WebServer.Set("AllowGetAccounts", "true");
    AccountManager.WebServer.Set("AllowAccountEditing", "true");
    AccountManager.WebServer.Set("AllowGetCookie", "false");

    var acc = new Account { Username = "HbaGuardUser", UserID = 777, Valid = true };
    acc.HbaPrivateKey = "MIGHAgEAMBMGByqGSM49AgEGCCqGSM49AwEHBG0wawIBAQQgSecretKeyMaterial";
    acc.SetField("LastBoundIpSig", "sig-secret-123");
    acc.SetField("Note", "SafeVal");
    AccountManager.ResetAccountsForTests(new[] { acc });

    int statusGet = 200;
    var reqGet = CreateRequest("/GetField", query: new(StringComparer.OrdinalIgnoreCase) { ["Account"] = "HbaGuardUser", ["Field"] = "hbaprivatekey" });
    reqGet.SetStatusCode = s => statusGet = s;
    string getBody = await mgr.ProcessApiRequestAsync(reqGet);
    Assert.Equal(403, statusGet);
    Assert.DoesNotContain("SecretKeyMaterial", getBody, StringComparison.Ordinal);

    int statusSet = 200;
    var reqSet = CreateRequest("/SetField", query: new(StringComparer.OrdinalIgnoreCase) { ["Account"] = "HbaGuardUser", ["Field"] = "HbaPrivateKey", ["Value"] = "overwritten" });
    reqSet.SetStatusCode = s => statusSet = s;
    _ = await mgr.ProcessApiRequestAsync(reqSet);
    Assert.Equal(403, statusSet);
    Assert.Contains("SecretKeyMaterial", acc.HbaPrivateKey, StringComparison.Ordinal);

    int statusRem = 200;
    var reqRem = CreateRequest("/RemoveField", query: new(StringComparer.OrdinalIgnoreCase) { ["Account"] = "HbaGuardUser", ["Field"] = "lastboundipsig" });
    reqRem.SetStatusCode = s => statusRem = s;
    _ = await mgr.ProcessApiRequestAsync(reqRem);
    Assert.Equal(403, statusRem);
    Assert.Equal("sig-secret-123", acc.GetField("LastBoundIpSig"));

    string safeField = await mgr.ProcessApiRequestAsync(CreateRequest("/GetField", query: new(StringComparer.OrdinalIgnoreCase) { ["Account"] = "HbaGuardUser", ["Field"] = "Note" }));
    Assert.Contains("SafeVal", safeField, StringComparison.Ordinal);

    string listJson = await mgr.ProcessApiRequestAsync(CreateRequest("/GetAccountsJson"));
    Assert.Contains("SafeVal", listJson, StringComparison.Ordinal);
    Assert.DoesNotContain("SecretKeyMaterial", listJson, StringComparison.Ordinal);
    Assert.DoesNotContain("HbaPrivateKey", listJson, StringComparison.OrdinalIgnoreCase);
    Assert.DoesNotContain("sig-secret-123", listJson, StringComparison.Ordinal);
}
```

- [ ] **Step 2: Run targeted test to verify failure**

Run: `dotnet test "tests/Shiro.Tests/Shiro.Tests.csproj" -c Release --filter "FullyQualifiedName~Dispatch_BlocksProtectedInternalFields_CaseInsensitively_AndScrubsAccountsJson"`
Expected: FAIL (`/GetField` returns 200 with raw `HbaPrivateKey` and `/GetAccountsJson` includes `HbaPrivateKey`).

- [ ] **Step 3: Implement field denylist in `Account.HbaBinding.cs` and `AccountApiDispatcher.Endpoints.cs`**

1. In `Account.HbaBinding.cs`: add `internal static bool IsProtectedInternalField(string? fieldName)` returning `true` for `FieldHbaPrivateKey`, `FieldHbaPublicKey`, `FieldHbaTemplate`, `FieldSessionUserAgent`, `FieldLastBoundIpSig`, or `fieldName.StartsWith("Hba", StringComparison.OrdinalIgnoreCase)`; add `internal Dictionary<string, string> GetPublicFieldsSnapshot()` returning a copy of `Fields` without protected keys.
2. In `AccountApiDispatcher.Endpoints.cs`:
   - Line 91 (`GetAccountsJson`): pass `acc.GetPublicFieldsSnapshot()` instead of `acc.Fields`.
   - Lines 267–289 (`GetField`, `SetField`, `RemoveField`): if `Account.IsProtectedInternalField(fieldName)`, immediately `return reply("Protected internal field", false, 403, "Forbidden field");` (keep total lines of `AccountApiDispatcher.Endpoints.cs` `< 485`).

- [ ] **Step 4: Run Kestrel API dispatcher tests**

Run: `dotnet test "tests/Shiro.Tests/Shiro.Tests.csproj" -c Release --filter "FullyQualifiedName~AccountApiDispatcher|FullyQualifiedName~WebServerEndpoints"`
Expected: PASS (`0` failed).

- [ ] **Step 5: Commit**

```powershell
git add src/Shiro/Classes/Account.HbaBinding.cs src/Shiro/Services/Network/AccountApiDispatcher.Endpoints.cs tests/Shiro.Tests/AccountApiDispatcherEndpointsBehaviorTests.cs
git commit -m "fix(security): block HBA and internal session fields on Kestrel API endpoints"
```

---

### Task 5: Harden Nexus WebSocket `UserID` Verification, Block Lateral `Echo -> execute` & Fix UI `execute` Syntax

**Files:**
- Modify: `src/Shiro/Nexus/NexusWebSocketServer.cs:142-150`
- Modify: `src/Shiro/Nexus/ControlledAccount.cs:254-258`
- Modify: `src/Shiro/ViewModels/Dialogs/NexusViewModel.cs:361`
- Modify: `src/Shiro/Nexus/Nexus.lua:311-324`
- Modify: `tests/Shiro.Tests/Dialogs/NexusViewModelTests.cs:122`
- Test: `tests/Shiro.Tests/Integration/NexusEndToEndTests.cs`

**Interfaces:**
- Produces: `NexusWebSocketServer` rejects connections with `403 Forbidden` whenever `account.LinkedAccount is { UserID: > 0 }` and `(!long.TryParse(id, out long parsedUserId) || parsedUserId != account.LinkedAccount.UserID)`.
- Produces: `ControlledAccount.HandleMessage` drops `Echo` payloads whose `TrimStart()` begins with `"execute "` / `"execute\t"` / `"execute:"` (OrdinalIgnoreCase) or equals `"execute"`.
- Produces: `NexusViewModel.ExecuteScript` emits `$"execute {ScriptInput}"` (matching `ControlledAccount.Connect` and `Nexus.lua`), while `Nexus.lua` also normalizes `"execute:<script>"` to `"execute <script>"` for backward compatibility.

- [ ] **Step 1: Write failing tests in `tests/Shiro.Tests/Integration/NexusEndToEndTests.cs` and update `NexusViewModelTests.cs`**

In `NexusEndToEndTests.cs`:
```csharp
[Theory]
[InlineData("0")]
[InlineData("-1")]
[InlineData("not-a-number")]
[InlineData("9999")]
public async Task Client_InvalidOrZeroUserId_ForLinkedAccount_Rejected403(string suppliedId)
{
    var (server, _, port) = await SetupServerAsync(); // LinkedAccount "alice" has UserID = 42
    try
    {
        using var ws = new ClientWebSocket();
        var uri = new Uri($"ws://127.0.0.1:{port}/Nexus?name=alice&id={suppliedId}&jobId=J1");
        var ex = await Assert.ThrowsAsync<WebSocketException>(() => ws.ConnectAsync(uri, CancellationToken.None));
        Assert.Contains("403", ex.Message, StringComparison.Ordinal);
    }
    finally
    {
        await server.StopAsync();
        NexusHost.Current = null;
    }
}

[Fact]
public void ControlledAccount_EchoExecutePayload_IsBlockedFromBroadcast()
{
    var host = Substitute.For<INexusHost>();
    NexusHost.Current = host;
    try
    {
        var controlled = new ControlledAccount(new Account { Username = "alice", UserID = 42 });
        controlled.HandleMessage("""{"Name":"Echo","Payload":{"Content":"  execute print('pwn')"}}""");
        controlled.HandleMessage("""{"Name":"Echo","Payload":{"Content":"Execute:print('pwn')"}}""");
        host.DidNotReceiveWithAnyArgs().EmitMessage(default!, default);

        controlled.HandleMessage("""{"Name":"Echo","Payload":{"Content":"chat hello"}}""");
        host.Received(1).EmitMessage("chat hello", true);
    }
    finally
    {
        NexusHost.Current = null;
    }
}
```
And in `tests/Shiro.Tests/Dialogs/NexusViewModelTests.cs:122`, update expected tuple to `("a", "execute print(1)")`.

- [ ] **Step 2: Run targeted Nexus tests to verify failure**

Run: `dotnet test "tests/Shiro.Tests/Shiro.Tests.csproj" -c Release --filter "FullyQualifiedName~Nexus"`
Expected: FAIL (`id=0`, `-1`, `not-a-number` connect with 200/101 instead of 403; `Echo` broadcasts `execute`; `NexusViewModel` sends `Execute:print(1)`).

- [ ] **Step 3: Implement `UserID` check, `Echo` filter, and `execute ` command normalization**

1. In `NexusWebSocketServer.cs:142-149`: replace the `if` condition with:
   ```csharp
   if (account.LinkedAccount != null && account.LinkedAccount.UserID > 0 &&
       (!long.TryParse(id, out long parsedUserId) || parsedUserId != account.LinkedAccount.UserID))
   ```
2. In `ControlledAccount.cs:254-257`: trim leading whitespace on `echoContent` and reject (log warning + return without `EmitMessage`) if it equals `"execute"` or starts with `"execute "`, `"execute\t"`, or `"execute:"` (`StringComparison.OrdinalIgnoreCase`).
3. In `NexusViewModel.cs:361`: change `EmitMessage($"Execute:{ScriptInput}", toAll);` to `EmitMessage($"execute {ScriptInput}", toAll);`.
4. In `src/Shiro/Nexus/Nexus.lua:312`: if `Message:sub(1, 8):lower() == 'execute:'` rewrite `Message = 'execute ' .. Message:sub(9)` before splitting on `' '`.

- [ ] **Step 4: Run all Nexus unit & end-to-end tests**

Run: `dotnet test "tests/Shiro.Tests/Shiro.Tests.csproj" -c Release --filter "FullyQualifiedName~Nexus"`
Expected: PASS (`0` failed).

- [ ] **Step 5: Commit**

```powershell
git add src/Shiro/Nexus/NexusWebSocketServer.cs src/Shiro/Nexus/ControlledAccount.cs src/Shiro/Nexus/Nexus.lua src/Shiro/ViewModels/Dialogs/NexusViewModel.cs tests/Shiro.Tests/Dialogs/NexusViewModelTests.cs tests/Shiro.Tests/Integration/NexusEndToEndTests.cs
git commit -m "fix(nexus): enforce strict UserID match, block lateral Echo execute, and align script prefix"
```

---

### Task 6: Fail-Closed SHA-256 Verification in Auto-Updater, Release ZIP Packaging & Authenticode Check for VCRedist

**Files:**
- Modify: `.github/workflows/release.yml:50-72`
- Modify: `src/Shiro/Services/Network/ReleaseInfoParser.cs:7-63`
- Modify: `src/Shiro/Services/Storage/UpdateArchiveInstaller.cs:30-42`
- Modify: `src/Shiro/ViewModels/Dialogs/UpdaterViewModel.cs:93-101,145-175`
- Modify: `src/Shiro/Services/Launch/PrerequisiteService.cs:29-55`
- Test: `tests/Shiro.Tests/ReleaseInfoParserTests.cs`, `tests/Shiro.Tests/Dialogs/UpdaterViewModelTests.cs`

**Interfaces:**
- Produces: `public sealed record ReleaseInfo(string Tag, Version Version, string ZipUrl, long Size, string? ExpectedSha256 = null, string? ChecksumsUrl = null, string AssetName = "Shiro-win-x64.zip");` (default parameters preserve existing 4-arg callers).
- Produces: `UpdateArchiveInstaller.VerifyFileSha256(string filePath, string? expectedHexSha256)` (`CryptographicOperations.FixedTimeEquals` over decoded 32-byte SHA-256 digests) and `UpdateArchiveInstaller.TryParseSha256Sums(string? sumsText, string assetFileName)`.
- Produces: `PrerequisiteService.VerifyMicrosoftAuthenticode(string filePath)` using Win32 `WinVerifyTrust` (`wintrust.dll`, `WINTRUST_ACTION_GENERIC_VERIFY_V2`, `WTD_UI_NONE`, `WTD_REVOKE_WHOLECHAIN`) plus X.509 signer check containing `"Microsoft"`.

- [ ] **Step 1: Write failing tests in `ReleaseInfoParserTests.cs` and `UpdaterViewModelTests.cs`**

In `ReleaseInfoParserTests.cs`:
```csharp
[Fact]
public void TryGetNewer_ExtractsGitHubDigestAndSha256SumsUrl()
{
    const string hash = "0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef";
    string json = $$"""
    {
      "tag_name": "v5.1.0",
      "assets": [
        { "name": "SHA256SUMS.txt", "browser_download_url": "https://ex.com/SHA256SUMS.txt", "size": 96 },
        { "name": "Shiro-win-x64.zip", "browser_download_url": "https://ex.com/Shiro-win-x64.zip", "size": 2048, "digest": "sha256:{{hash}}" }
      ]
    }
    """;
    ReleaseInfo? info = ReleaseInfoParser.TryGetNewer(json, new Version(5, 0, 0));
    Assert.NotNull(info);
    Assert.Equal(hash, info!.ExpectedSha256);
    Assert.Equal("https://ex.com/SHA256SUMS.txt", info.ChecksumsUrl);
    Assert.Equal("Shiro-win-x64.zip", info.AssetName);
}

[Fact]
public void VerifyFileSha256_And_TryParseSha256Sums_ValidateCorrectly()
{
    string tmp = Path.Combine(Path.GetTempPath(), $"shiro-sha-{Guid.NewGuid():N}.bin");
    try
    {
        byte[] data = "shiro-release-payload"u8.ToArray();
        File.WriteAllBytes(tmp, data);
        string hex = Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(data));

        Assert.True(UpdateArchiveInstaller.VerifyFileSha256(tmp, hex));
        Assert.False(UpdateArchiveInstaller.VerifyFileSha256(tmp, new string('0', 64)));
        Assert.False(UpdateArchiveInstaller.VerifyFileSha256(tmp, "not-hex"));

        string sums = $"{hex} *Shiro-win-x64.zip\n{new string('f', 64)} *Shiro.exe\n";
        Assert.Equal(hex, UpdateArchiveInstaller.TryParseSha256Sums(sums, "Shiro-win-x64.zip"));
    }
    finally
    {
        if (File.Exists(tmp)) File.Delete(tmp);
    }
}
```

In `UpdaterViewModelTests.cs`:
```csharp
[Fact]
public async Task Start_DeletesPreExistingArchive_AndAbortsOnMissingOrMismatchedSha256()
{
    string tempDir = Path.Combine(Path.GetTempPath(), $"updater-test-{Guid.NewGuid():N}");
    Directory.CreateDirectory(tempDir);
    try
    {
        string staleZip = Path.Combine(tempDir, "Update.zip");
        File.WriteAllBytes(staleZip, new byte[] { 1, 2, 3 });
        bool restarted = false;

        var notFoundHandler = new FakeHttpHandler().Enqueue(HttpStatusCode.NotFound, "");
        using (var http1 = new HttpClient(notFoundHandler))
        {
            var vm1 = new UpdaterViewModel(http1, () => new Version(4, 2, 0), tempDir, _ => restarted = true);
            await vm1.StartCommand.ExecuteAsync(null);
            Assert.False(File.Exists(staleZip));
            Assert.False(restarted);
        }

        string releaseJson = """{"tag_name":"v9.0.0","assets":[{"name":"Shiro-win-x64.zip","browser_download_url":"https://ex.com/s.zip","size":4,"digest":"sha256:aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa"}]}""";
        int step = 0;
        var mismatchHandler = new FakeHttpHandler(_ =>
            ++step == 1
                ? new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(releaseJson) }
                : new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(new byte[] { 0x50, 0x4B, 0x05, 0x06 }) });
        using var http2 = new HttpClient(mismatchHandler);
        var vm2 = new UpdaterViewModel(http2, () => new Version(4, 2, 0), tempDir, _ => restarted = true);
        await vm2.StartCommand.ExecuteAsync(null);

        Assert.True(vm2.HasError);
        Assert.False(File.Exists(staleZip));
        Assert.False(restarted);
    }
    finally
    {
        if (Directory.Exists(tempDir)) Directory.Delete(tempDir, recursive: true);
    }
}

[Fact]
public async Task Start_ValidSha256Digest_AppliesArchiveAndRestarts()
{
    string tempDir = Path.Combine(Path.GetTempPath(), $"updater-test-{Guid.NewGuid():N}");
    Directory.CreateDirectory(tempDir);
    try
    {
        File.WriteAllText(Path.Combine(tempDir, "Shiro.exe"), "stub");
        using var ms = new MemoryStream();
        using (var zip = new System.IO.Compression.ZipArchive(ms, System.IO.Compression.ZipArchiveMode.Create, leaveOpen: true))
        {
            var entry = zip.CreateEntry("payload.txt");
            await using var writer = new StreamWriter(entry.Open());
            await writer.WriteAsync("verified");
        }
        byte[] zipBytes = ms.ToArray();
        string hex = Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(zipBytes));
        string releaseJson = $$"""{"tag_name":"v9.0.0","assets":[{"name":"Shiro-win-x64.zip","browser_download_url":"https://ex.com/s.zip","size":{{zipBytes.Length}},"digest":"sha256:{{hex}}"}]}""";

        int step = 0;
        var handler = new FakeHttpHandler(_ =>
            ++step == 1
                ? new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(releaseJson) }
                : new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(zipBytes) });

        string? restartedExe = null;
        using var http = new HttpClient(handler);
        var vm = new UpdaterViewModel(http, () => new Version(4, 2, 0), tempDir, exe => restartedExe = exe);
        await vm.StartCommand.ExecuteAsync(null);

        Assert.False(vm.HasError);
        Assert.Equal("verified", File.ReadAllText(Path.Combine(tempDir, "payload.txt")));
        Assert.NotNull(restartedExe);
    }
    finally
    {
        if (Directory.Exists(tempDir)) Directory.Delete(tempDir, recursive: true);
    }
}
```

- [ ] **Step 2: Run targeted updater tests to verify failure**

Run: `dotnet test "tests/Shiro.Tests/Shiro.Tests.csproj" -c Release --filter "FullyQualifiedName~UpdaterViewModelTests|FullyQualifiedName~ReleaseInfoParserTests"`
Expected: FAIL.

- [ ] **Step 3: Implement release ZIP packaging, SHA-256 verification, and `WinVerifyTrust` check**

1. In `.github/workflows/release.yml:50-72`: after `dotnet publish`, create `Shiro-win-x64.zip` from `build/Release/Shiro.exe` (`Compress-Archive -Path $exe.FullName -DestinationPath "Shiro-win-x64.zip" -Force`), write SHA-256 lines for **both** `Shiro.exe` and `Shiro-win-x64.zip` into `SHA256SUMS.txt`, attest both `build/Release/*.exe` and `build/Release/*.zip`, and attach `build/Release/*.zip` to `softprops/action-gh-release`.
2. In `ReleaseInfoParser.cs`: parse `digest` (stripping `"sha256:"` prefix when 64 hex chars), `name` (`AssetName`), and sibling `SHA256SUMS.txt` `browser_download_url` (`ChecksumsUrl`) across the `assets` array.
3. In `UpdateArchiveInstaller.cs`: implement `VerifyFileSha256` and `TryParseSha256Sums`.
4. In `UpdaterViewModel.cs`:
   - Lines 93–98: replace auto-install of pre-existing `archivePath` with `if (File.Exists(archivePath)) SafeExecuteHelper.Try(() => File.Delete(archivePath), "Updater.CleanStaleArchive");`.
   - After downloading `archivePath`: obtain `expectedHash = release.ExpectedSha256`; if empty and `!string.IsNullOrEmpty(release.ChecksumsUrl)`, GET `release.ChecksumsUrl` and call `UpdateArchiveInstaller.TryParseSha256Sums(sumsBody, release.AssetName)`. If `string.IsNullOrEmpty(expectedHash)` or `!UpdateArchiveInstaller.VerifyFileSha256(archivePath, expectedHash)`, delete `archivePath`, throw/call `ShowError("SHA-256 integrity verification failed.")`, and return before `ApplyAndRestartAsync`.
5. In `PrerequisiteService.cs`: add `VerifyMicrosoftAuthenticode(string filePath)` using `[LibraryImport("wintrust.dll")] WinVerifyTrust` + `X509CertificateLoader.LoadCertificateFromFile(filePath)` subject check for `"Microsoft"`, and abort `InstallVCRedistAsync` (returning `false`) before `Process.Start` if verification fails.

- [ ] **Step 4: Run full test suite & coverage gates**

Run:
```powershell
dotnet build "Shiro.sln" -c Release
dotnet test "tests/Shiro.Tests/Shiro.Tests.csproj" -c Release --collect:"XPlat Code Coverage" --settings "tests/coverage.runsettings" --results-directory "coverage"
powershell -ExecutionPolicy Bypass -File .\scripts\Check-Coverage.ps1 -CoverageDir "coverage" -MinOverall 75.0 -MinBranch 60.0 -MinCore 92.0
```
Expected: `0` warnings, `0` errors, `0` failed tests (`>= 1805` passing), all 3 global coverage gates and 19 per-file branch floors PASS.

- [ ] **Step 5: Commit**

```powershell
git add .github/workflows/release.yml src/Shiro/Services/Network/ReleaseInfoParser.cs src/Shiro/Services/Storage/UpdateArchiveInstaller.cs src/Shiro/ViewModels/Dialogs/UpdaterViewModel.cs src/Shiro/Services/Launch/PrerequisiteService.cs tests/Shiro.Tests/ReleaseInfoParserTests.cs tests/Shiro.Tests/Dialogs/UpdaterViewModelTests.cs
git commit -m "fix(updater): enforce fail-closed SHA-256 release verification and Authenticode check for VCRedist"
```
