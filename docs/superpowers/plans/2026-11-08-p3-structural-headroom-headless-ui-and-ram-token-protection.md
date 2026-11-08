# Phase 4 (P3): Structural Headroom, Headless UI Verification, Watcher Coverage & In-Memory Secret Protection Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Eliminate the remaining structural, testing, and in-memory credential weaknesses in Shiro Account Manager `v5.0.0` by decompressing near-ceiling source files and partial classes (ratcheting `ArchitectureTests` line budgets down), expanding `[AvaloniaFact]` Headless UI smoke tests across all remaining Views/Dialogs in both `"vi"` and `"en"`, extracting and unit-testing the `RobloxProcess` log state machine and `RobloxWatcher` metadata classifiers under Cobertura coverage, and replacing plaintext `_SecurityToken` and `_Password` fields on `Account` with AES-GCM encrypted in-memory buffers (`InMemorySecretProtector`).

**Architecture:** Four independent, self-contained tasks executed in TDD order: (1) extract cohesive static helpers from the 6 files at `491–500` lines and the 3 partial classes near their ceiling (`MainViewModel`, `ServerListViewModel`, `AccountLaunchService`) and tighten `ArchitectureTests` line ratchets (`<= 485` lines per `.cs` file and lower partial class budgets); (2) add `[AvaloniaFact]` Headless UI binding-error verification for `AboutDialog`, `AboutView`, `AccountCardView`, `AccountFieldsDialog`, `DashboardView`, `InputDialog`, `MessageBoxDialog`, `NexusWindow`, and `ServerListView` across both `"vi"` and `"en"` locales; (3) extract the `RobloxProcess` log state-machine step (`EvaluateLogLineTransition`) into `RobloxProcess.LogPatterns.cs` and `RobloxWatcher` command-line/window-rect classifiers into `RobloxWatcher.FileWatcher.cs` with unit tests; (4) introduce `InMemorySecretProtector` (per-process ephemeral 256-bit AES-GCM with `CryptographicOperations.ZeroMemory` scrubbing) so `Account` never stores plaintext `SecurityToken` or `Password` `string` fields on the managed heap.

**Tech Stack:** .NET 10.0 (`net10.0-windows10.0.19041.0`, `win-x64`), C# 13, Avalonia UI 11.2.5 (`Avalonia.Headless.XUnit`), `System.Security.Cryptography.AesGcm`, xUnit, NetArchTest.eNhancedEdition.

**Spec:** Conversation audit report (`project_audit_report.md`) — Phase 4 (P3) Structural & Deep Hardening items 1–4.

## Global Constraints

- Target framework and runtime: `net10.0-windows10.0.19041.0` (`win-x64`), zero build warnings (`0 Warning(s)`, `0 Error(s)`).
- File line count guardrail (`ArchitectureTests.NoSourceCodeFiles_ShouldExceed500Lines`): Ratcheted in Task 1 from `500` down to `485` lines maximum for every `.cs` file in `src/Shiro`; `.axaml` files must not exceed `400` lines (`<= 200` chars per line).
- Partial class line budgets (`ArchitectureTests.NoClass_ShouldExceedLineBudget_AcrossPartials`): Ratcheted in Task 1 to `Account <= 2400`, `MainViewModel <= 3300`, `AccountManager <= 2850`, `ServerListViewModel <= 1650`, and all other classes `<= 1150`.
- Coverage gates (`scripts/Check-Coverage.ps1` with `tests/coverage.runsettings`): Overall Line `>= 75.0%`, Overall Branch `>= 60.0%`, Core Security/Persistence `>= 92.0%`, plus all 19 per-file branch floors in `tests/coverage-floors.json`.
- Test assertion guardrail (`ArchitectureTests.TestMethods_ShouldAssertOnValues_NotOnlyNullness`): Every `[Fact]` / `[Theory]` / `[AvaloniaFact]` method must contain concrete value assertions (`Assert.Equal`, `Assert.True`, `Assert.Empty`, etc.) and must not place inline regex literals containing `{` or `}` inside test method bodies.

## Review Focus

- **Decompressed helper delegation in `FastLoginCoordinator`, `RobloxDirectLauncher`, and `ServerListViewModel`:** Extracting static helper classes must preserve all existing public/internal method signatures on the original classes so existing callers and reflection-based tests (`AllCommandPaletteBuiltinCommands_ShouldMapToValidLucideIcons`) continue to work unchanged.
- **Headless UI lifecycle & static state cleanup (`MainWindow.Instance`, `NexusWindow.Instance`, `Logger.Sink`):** Every `[AvaloniaFact]` test must restore `Avalonia.Logging.Logger.Sink`, `LocalizationService.CurrentLanguage`, and close/hide any opened window in a `finally` block so subsequent tests do not inherit a stale `BindingErrorSink` or language.
- **`RobloxProcess` log state-machine parity on challenge + cookie rotation kills:** When `HasGameJoinChallenge` is true and `!IsConnected`, or when `(HasGameJoinChallenge || CurrentDataModel > 0) && HasSecurityCookieChanged`, encountering a return-to-LuaApp line must immediately return `RobloxLogLineAction.KillChallengedOrHijackedClient` and reset `CurrentDataModel = -1`.
- **`Account` `SessionHealthStatus` and `IsShortLivedSession` without plaintext token allocation:** Checking `account.SessionHealthStatus`, `account.IsShortLivedSession`, or `account.HasExpiredShortLivedCookie()` must use cached `_hasSecurityToken` and `_securityTokenPayloadLength` metadata under `CookieSyncRoot` rather than decrypting `_protectedSecurityToken` on every UI refresh tick.
- **Zeroing superseded ciphertext buffers on `Account.SecurityToken` / `Account.Password` mutation:** Overwriting `SecurityToken` or `Password` (or calling `ClearSensitiveMemory()`) must call `CryptographicOperations.ZeroMemory` on the old `byte[]` buffer before replacing the reference.

---

### Task 1: Decompress Near-Ceiling Source Files & Partial Classes and Ratchet `ArchitectureTests` Line Budgets

**Files:**
- Create: `src/Shiro/Services/Auth/FastLoginScripts.cs`
- Create: `src/Shiro/Services/Launch/RobloxCookieFileHelper.cs`
- Create: `src/Shiro/ViewModels/ServerListFilterHelper.cs`
- Create: `src/Shiro/ViewModels/CommandPaletteCatalog.cs`
- Modify: `src/Shiro/Services/Auth/FastLoginCoordinator.cs:182-326`
- Modify: `src/Shiro/Services/Launch/AutoReconnectService.cs:240-296`
- Modify: `src/Shiro/Services/Launch/AutoReconnectService.Models.cs:80-122`
- Modify: `src/Shiro/Services/Launch/RobloxDirectLauncher.CookieGuard.cs:255-355`
- Modify: `src/Shiro/AvaloniaUI/Views/FastLoginDialog.SessionCapture.cs:355-406`
- Modify: `src/Shiro/AvaloniaUI/Views/FastLoginDialog.Automation.cs:140-167`
- Modify: `src/Shiro/Services/Windows/RobloxProcessService.Lifecycle.cs:442-491`
- Modify: `src/Shiro/Services/Windows/RobloxProcessService.cs:420-450`
- Modify: `src/Shiro/AccountManager.Persistence.cs:465-491`
- Modify: `src/Shiro/AccountManager.BackupRecovery.cs:1-120`
- Modify: `src/Shiro/Services/Launch/AccountLaunchService.Window.cs:125-243`
- Modify: `src/Shiro/ViewModels/ServerListViewModel.Sorting.cs:198-267`
- Modify: `src/Shiro/ViewModels/MainViewModel.CommandPalette.cs:191-274`
- Test: `tests/Shiro.Tests/ArchitectureTests.cs`

**Interfaces:**
- Consumes: Existing `FastLoginCoordinator`, `RobloxDirectLauncher`, `ServerListViewModel`, `MainViewModel`, `AccountLaunchService` APIs.
- Produces:
  - `internal static class FastLoginScripts` in `namespace Shiro.Services` (`GetLoginPageEnhancementScript()`, `BuildCredentialFillScript(string username, string? password, bool forceClick)`)
  - `internal static class RobloxCookieFileHelper` in `namespace Shiro.Services` (`TryExtractCookieFromCookiesDatFile(string filePath)`, `TryExtractTrackerMetadataFromCookiesDatFile(string filePath, out long userId, out string? browserTrackerId)`, `CanSyncRotatedCookieForTarget(Account target, long trackerUserId, string? trackerBrowserId, string? expectedTrackerId)`)
  - `internal static class ServerListFilterHelper` in `namespace Shiro.ViewModels` (`FilterAndSort(IReadOnlyList<ServerItemViewModel> allServers, bool hideFullServers, bool hideVipServers, ServerSortMode sortMode)`)
  - `internal static class CommandPaletteCatalog` in `namespace Shiro.ViewModels` (`internal sealed record BuiltinCommandDef(...)`, `internal static List<BuiltinCommandDef> GetBuiltinCommands()`)
  - Tightened ratchets in `ArchitectureTests.NoSourceCodeFiles_ShouldExceed500Lines` (`maxLines = 485`) and `ArchitectureTests.NoClass_ShouldExceedLineBudget_AcrossPartials` (`DefaultBudget = 1150`, `Account = 2400`, `MainViewModel = 3300`, `AccountManager = 2850`, `ServerListViewModel = 1650`).

- [ ] **Step 1: Write the failing ratchet assertions in `tests/Shiro.Tests/ArchitectureTests.cs`**

Update `NoSourceCodeFiles_ShouldExceed500Lines` and `NoClass_ShouldExceedLineBudget_AcrossPartials` in `tests/Shiro.Tests/ArchitectureTests.cs`:

```csharp
// In NoSourceCodeFiles_ShouldExceed500Lines:
if (lineCount > 485)
{
    oversizedFiles.Add($"{Path.GetRelativePath(appDir, file)} ({lineCount} lines)");
}
```

```csharp
// In NoClass_ShouldExceedLineBudget_AcrossPartials:
const int DefaultBudget = 1150;
var baselines = new System.Collections.Generic.Dictionary<string, int>(StringComparer.Ordinal)
{
    ["Account"] = 2400,
    ["MainViewModel"] = 3300,
    ["AccountManager"] = 2850,
    ["ServerListViewModel"] = 1650
};
```

- [ ] **Step 2: Run `ArchitectureTests` to verify the new ratchets fail on the current oversized files and partial classes**

Run: `dotnet test tests/Shiro.Tests/Shiro.Tests.csproj -c Release --filter "FullyQualifiedName~NoSourceCodeFiles_ShouldExceed500Lines|FullyQualifiedName~NoClass_ShouldExceedLineBudget_AcrossPartials"`
Expected: FAIL listing `FastLoginCoordinator.cs (500 lines)`, `AutoReconnectService.cs (499 lines)`, `RobloxDirectLauncher.CookieGuard.cs (496 lines)`, `FastLoginDialog.SessionCapture.cs (496 lines)`, `RobloxProcessService.Lifecycle.cs (492 lines)`, `AccountManager.Persistence.cs (491 lines)`, `RobloxProcess.cs (488 lines)`, and partial budgets `MainViewModel (3424 > 3300)`, `ServerListViewModel (1781 > 1650)`, `AccountLaunchService (1197 > 1150)`.

- [ ] **Step 3: Extract cohesive helpers to bring all `.cs` files `<= 485` lines and all partial classes below the ratcheted budgets**

1. Create `src/Shiro/Services/Auth/FastLoginScripts.cs` containing `internal static class FastLoginScripts` with `GetLoginPageEnhancementScript()` and `BuildCredentialFillScript(string username, string? password, bool forceClick)`. Have `FastLoginCoordinator.GetLoginPageEnhancementScript()` and `FastLoginCoordinator.BuildCredentialFillScript(...)` in `src/Shiro/Services/Auth/FastLoginCoordinator.cs` forward to `FastLoginScripts`.
2. Move `UnregisterSession`, `SuppressReconnect`, `SuppressAll`, `Clear`, `TryGetSession`, and `NotifyMaxRetriesExceededForTesting` (`~55` lines) from `src/Shiro/Services/Launch/AutoReconnectService.cs` into `src/Shiro/Services/Launch/AutoReconnectService.Models.cs`.
3. Create `src/Shiro/Services/Launch/RobloxCookieFileHelper.cs` containing `internal static class RobloxCookieFileHelper` with the pure file-parsing and target-matching methods from `RobloxDirectLauncher.CookieGuard.cs` (`TryExtractCookieFromCookiesDatFile`, `TryExtractTrackerMetadataFromCookiesDatFile`, `CanSyncRotatedCookieForTarget`), and forward the existing `internal static` methods on `RobloxDirectLauncher` to `RobloxCookieFileHelper`.
4. Move the settled-cookie and DOM password extraction helpers (`TryReadSettledRobloxCookieAsync` and `TryCaptureDomPasswordAsync`, lines `356–405`) from `src/Shiro/AvaloniaUI/Views/FastLoginDialog.SessionCapture.cs` into `src/Shiro/AvaloniaUI/Views/FastLoginDialog.Automation.cs`, and move the static `BuildDomIssueDetectionScript()` literal from `FastLoginDialog.SessionCapture.cs` into `FastLoginScripts.cs`.
5. Move `ClearRobloxClientSession(bool killProcesses = true)` (`~45` lines) from `src/Shiro/Services/Windows/RobloxProcessService.Lifecycle.cs` into `src/Shiro/Services/Windows/RobloxProcessService.cs` (or `RobloxCookieFileHelper.ClearLocalSessionFiles()`), keeping `RobloxProcessService.Lifecycle.cs` branch coverage `>= 67%` and both files `<= 485` lines.
6. Move `_deferredSaveTimer`, `QueueDeferredSave()`, and `TryUnprotectAccountData(byte[] data, out bool usedLegacyScope)` (`~26` lines) from `src/Shiro/AccountManager.Persistence.cs` into `src/Shiro/AccountManager.BackupRecovery.cs`.
7. Move `TryGetRobloxLogTimestampUtc` and `IsRobloxRateLimitedLogLine` + log-tail reading from `src/Shiro/Services/Launch/AccountLaunchService.Window.cs` into `src/Shiro/Classes/RobloxProcess.LogPatterns.cs` (reusing `RobloxProcess`'s log parser), reducing `AccountLaunchService` partial total below `1150` lines.
8. Create `src/Shiro/ViewModels/ServerListFilterHelper.cs` with `FilterAndSort`, `ComparePlayersAsc`, `ComparePlayersDesc`, `ComparePingAsc`, and `ComparePingDesc`, and call `ServerListFilterHelper.FilterAndSort(...)` from `ServerListViewModel.Sorting.cs` (reducing `ServerListViewModel` partial total from `1781` to `< 1650` lines).
9. Create `src/Shiro/ViewModels/CommandPaletteCatalog.cs` containing `BuiltinCommandDef` and `GetBuiltinCommands()`, and keep `private static List<CommandPaletteCatalog.BuiltinCommandDef> GetBuiltinCommands() => CommandPaletteCatalog.GetBuiltinCommands();` on `MainViewModel` in `MainViewModel.CommandPalette.cs` so `AllCommandPaletteBuiltinCommands_ShouldMapToValidLucideIcons` continues to work while reducing `MainViewModel` partial total from `3424` to `< 3300` lines.
10. Compact `src/Shiro/Classes/RobloxProcess.cs` (`488` lines -> `<= 480` lines) by removing redundant blank lines/comments ahead of Task 3's full state-machine extraction.

- [ ] **Step 4: Run tests to verify all architecture ratchets and unit tests pass**

Run: `dotnet test tests/Shiro.Tests/Shiro.Tests.csproj -c Release --filter "FullyQualifiedName~ArchitectureTests|FullyQualifiedName~FastLogin|FullyQualifiedName~AutoReconnect|FullyQualifiedName~RobloxProcessService"`
Expected: PASS (`0` failed, `0` warnings).

- [ ] **Step 5: Commit**

```bash
git add src/Shiro tests/Shiro.Tests/ArchitectureTests.cs
git commit -m "refactor(arch): decompress near-ceiling files and partial classes and ratchet line budgets"
```

---

### Task 2: Expand Headless UI Verification (`[AvaloniaFact]`) Across All Remaining Views & Dialogs in Both `"vi"` and `"en"`

**Files:**
- Modify: `tests/Shiro.Tests/Ui/DialogSmokeTests.cs`

**Interfaces:**
- Consumes: `BindingErrorSink` in `tests/Shiro.Tests/Ui/DialogSmokeTests.cs`, `LocalizationService.SetLanguage(string)`.
- Produces: 10 new `[AvaloniaFact]` tests in `tests/Shiro.Tests/Ui/DialogSmokeTests.cs` covering `AboutDialog`, `AboutView`, `AccountCardView`, `AccountFieldsDialog`, `DashboardView`, `InputDialog`, `MessageBoxDialog`, `NexusWindow`, `ServerListView`, and `MainWindow` with zero Avalonia binding errors across both `"vi"` and `"en"` locales.

- [ ] **Step 1: Add the `[AvaloniaFact]` smoke and locale-switching tests to `tests/Shiro.Tests/Ui/DialogSmokeTests.cs`**

Add tests in `tests/Shiro.Tests/Ui/DialogSmokeTests.cs` that attach `BindingErrorSink` to `Avalonia.Logging.Logger.Sink`, instantiate and render each view/dialog inside a headless `Window`, switch `LocalizationService.SetLanguage("en")` and `LocalizationService.SetLanguage("vi")`, and assert `Assert.Empty(sink.Errors)`:
- `AboutDialog_Opens_WithPopulatedRuntimeAndMemoryMetrics_AndZeroBindingErrors`
- `AboutView_Loads_InBothLanguages_WithZeroBindingErrors`
- `AccountCardView_BindsAccountViewModel_WithZeroBindingErrors`
- `AccountFieldsDialog_Opens_FiltersInternalFields_AndHasZeroBindingErrors`
- `InputDialog_And_MessageBoxDialog_Open_WithZeroBindingErrors`
- `ServerListView_Loads_WithServerListViewModel_AndZeroBindingErrors`
- `DashboardView_Loads_SwitchesLanguage_AndDisposesCleanly_WithZeroBindingErrors`
- `NexusWindow_Opens_TogglesVisualState_AndHides_WithZeroBindingErrors`

- [ ] **Step 2: Run `DialogSmokeTests` to verify all headless UI tests pass**

Run: `dotnet test tests/Shiro.Tests/Shiro.Tests.csproj -c Release --filter "FullyQualifiedName~DialogSmokeTests"`
Expected: PASS (`16` passed, `0` failed).

- [ ] **Step 3: Commit**

```bash
git add tests/Shiro.Tests/Ui/DialogSmokeTests.cs
git commit -m "test(ui): expand Avalonia headless smoke and binding verification across all views and dialogs"
```

---

### Task 3: Extract & Unit-Test `RobloxProcess` Log State Machine and `RobloxWatcher` Classifiers Under Cobertura Coverage

**Files:**
- Modify: `src/Shiro/Classes/RobloxProcess.LogPatterns.cs`
- Modify: `src/Shiro/Classes/RobloxProcess.cs:198-318`
- Modify: `src/Shiro/Classes/RobloxWatcher.FileWatcher.cs`
- Modify: `src/Shiro/Classes/RobloxWatcher.cs:233-303,353-386`
- Test: `tests/Shiro.Tests/LogBindingAndWatcherTests.cs`

**Interfaces:**
- Consumes: Existing regexes and helpers in `RobloxProcess.LogPatterns.cs` (`IsJoiningGameLogLine`, `IsChallengeCompletedLogLine`, `IsGameJoinChallengedLogLine`, `IsSecurityCookieChangedLogLine`, `TryParseDisconnectReason`, `TryParseDataModelInit`, `IsReturnToLuaAppLine`).
- Produces:
  - `internal enum RobloxLogLineOutcome { None, Connected, Disconnected, DataModelInitialized, DataModelPaused, DataModelReset, KillChallengedOrHijackedClient, KillBetaHomeMenu }` in `src/Shiro/Classes/RobloxProcess.LogPatterns.cs`
  - `internal struct RobloxLogSessionState { public long CurrentDataModel; public bool IsDMPaused; public bool IsConnected; public bool HasGameJoinChallenge; public bool HasSecurityCookieChanged; public string? LastLine; public int LastDisconnectReason; }` in `src/Shiro/Classes/RobloxProcess.LogPatterns.cs`
  - `internal static RobloxLogLineOutcome AdvanceLogSessionState(string line, ref RobloxLogSessionState state, bool verifyDataModel, bool exitOnBeta)` in `src/Shiro/Classes/RobloxProcess.LogPatterns.cs`
  - `internal static bool TryClassifyClientCommandLine(string? commandLine, out string trackerId)` and `internal static bool TryValidateWindowBoundsChange(RobloxWatcher.RECT rect, RobloxWatcher.RECT lastRect, out int width, out int height)` in `src/Shiro/Classes/RobloxWatcher.FileWatcher.cs`.

- [ ] **Step 1: Write the failing unit tests in `tests/Shiro.Tests/LogBindingAndWatcherTests.cs`**

Add unit tests in `tests/Shiro.Tests/LogBindingAndWatcherTests.cs`:
1. `AdvanceLogSessionState_TracksConnect_DataModelInit_Pause_AndDisconnectReason`
2. `AdvanceLogSessionState_TriggersKillChallengedOrHijackedClient_WhenCookieChangedAndReturningToLuaApp`
3. `AdvanceLogSessionState_TriggersKillBetaHomeMenu_WhenExitOnBetaEnabledAndDataModelStopped`
4. `RobloxWatcher_TryClassifyClientCommandLine_And_TryValidateWindowBoundsChange_HandleEdgeCases`

- [ ] **Step 2: Run `LogBindingAndWatcherTests` to verify compilation fails on the missing methods**

Run: `dotnet test tests/Shiro.Tests/Shiro.Tests.csproj -c Release --filter "FullyQualifiedName~LogBindingAndWatcherTests"`
Expected: FAIL with `CS0117` (`AdvanceLogSessionState`, `TryClassifyClientCommandLine`, `TryValidateWindowBoundsChange` not defined).

- [ ] **Step 3: Implement `AdvanceLogSessionState` in `RobloxProcess.LogPatterns.cs` and `TryClassifyClientCommandLine` / `TryValidateWindowBoundsChange` in `RobloxWatcher.FileWatcher.cs`**

1. In `src/Shiro/Classes/RobloxProcess.LogPatterns.cs`, define `RobloxLogLineOutcome`, `RobloxLogSessionState`, and `AdvanceLogSessionState(string line, ref RobloxLogSessionState state, bool verifyDataModel, bool exitOnBeta)` encapsulating the exact line-by-line state transitions from `RobloxProcess.ReadLogFile`.
2. In `src/Shiro/Classes/RobloxProcess.cs`, replace the inline state fields (`CurrentDataModel`, `IsDMPaused`, `HasGameJoinChallenge`, `HasSecurityCookieChanged`, `LastLine`) or bind them to `_sessionState` and dispatch on `AdvanceLogSessionState(...)` inside `ReadLogFile`, reducing `RobloxProcess.cs` to `~380` lines.
3. In `src/Shiro/Classes/RobloxWatcher.FileWatcher.cs`, implement `TryClassifyClientCommandLine(string? commandLine, out string trackerId)` and `TryValidateWindowBoundsChange(RECT rect, RECT lastRect, out int width, out int height)`, and call them from `RobloxWatcher.CheckProcesses` in `src/Shiro/Classes/RobloxWatcher.cs`.

- [ ] **Step 4: Run `LogBindingAndWatcherTests` and `ArchitectureTests` to verify they pass**

Run: `dotnet test tests/Shiro.Tests/Shiro.Tests.csproj -c Release --filter "FullyQualifiedName~LogBindingAndWatcherTests|FullyQualifiedName~ArchitectureTests"`
Expected: PASS (`0` failed, `0` warnings).

- [ ] **Step 5: Commit**

```bash
git add src/Shiro/Classes/RobloxProcess.cs src/Shiro/Classes/RobloxProcess.LogPatterns.cs src/Shiro/Classes/RobloxWatcher.cs src/Shiro/Classes/RobloxWatcher.FileWatcher.cs tests/Shiro.Tests/LogBindingAndWatcherTests.cs
git commit -m "refactor(watcher): extract RobloxProcess log state machine and RobloxWatcher classifiers under test coverage"
```

---

### Task 4: Protect `Account.SecurityToken` and `Account.Password` in RAM with `InMemorySecretProtector`

**Files:**
- Create: `src/Shiro/Classes/InMemorySecretProtector.cs`
- Modify: `src/Shiro/Classes/Account.cs:95-118,231-249,322-348`
- Modify: `src/Shiro/Classes/Account.SessionState.cs:24-38`
- Modify: `src/Shiro/Classes/Account.SessionState.CookieRotation.cs:114-132`
- Test: `tests/Shiro.Tests/SecurityHardeningTests.cs`

**Interfaces:**
- Consumes: `System.Security.Cryptography.AesGcm`, `System.Security.Cryptography.RandomNumberGenerator`, `System.Security.Cryptography.CryptographicOperations.ZeroMemory`.
- Produces:
  - `internal static class InMemorySecretProtector` in `namespace Shiro.Classes`:
    - `public static byte[]? Protect(string? plaintext)`
    - `public static string Unprotect(ReadOnlySpan<byte> protectedBlob)`
    - `public static void ZeroAndClear(ref byte[]? protectedBlob)`
  - `public void ClearSensitiveMemory()` on `Account`
  - `Account` stores `private byte[]? _protectedSecurityToken;`, `private bool _hasSecurityToken;`, `private int _securityTokenPayloadLength;`, and `private byte[]? _protectedPassword;` instead of plaintext `string _SecurityToken` and `string _Password`.

- [ ] **Step 1: Write the failing unit tests in `tests/Shiro.Tests/SecurityHardeningTests.cs`**

Add two tests to `tests/Shiro.Tests/SecurityHardeningTests.cs`:

```csharp
[Fact]
public void Account_DoesNotHoldPlaintextSecurityTokenOrPassword_InInstanceStringFields()
{
    const string rawCookie = "_|WARNING:-DO-NOT-SHARE-THIS.--Sharing-this-will-allow-someone-to-log-in-as-you-and-to-steal-your-ROBUX-and-items.|_SECRET_COOKIE_PAYLOAD_999888777";
    const string rawPassword = "SuperSecretAccountPassword!2026";

    var account = new Account
    {
        Username = "RamProtectedUser",
        SecurityToken = rawCookie,
        Password = rawPassword
    };

    Assert.Equal(rawCookie, account.SecurityToken);
    Assert.Equal(rawPassword, account.Password);

    var instanceFields = typeof(Account).GetFields(
        System.Reflection.BindingFlags.Instance |
        System.Reflection.BindingFlags.NonPublic |
        System.Reflection.BindingFlags.Public);

    foreach (var field in instanceFields)
    {
        if (field.FieldType == typeof(string))
        {
            string? val = (string?)field.GetValue(account);
            Assert.NotEqual(rawCookie, val);
            Assert.NotEqual(rawPassword, val);
        }
    }

    account.ClearSensitiveMemory();
    Assert.Equal(string.Empty, account.SecurityToken);
    Assert.Equal(string.Empty, account.Password);
    Assert.Equal(SessionHealth.Expired, account.SessionHealthStatus);
}

[Fact]
public void InMemorySecretProtector_RoundTripsUnicode_AndZeroesBufferOnClear()
{
    const string secret = "p@ssw0rd_việt_nam_🔒";
    byte[]? blob = InMemorySecretProtector.Protect(secret);
    Assert.NotNull(blob);
    byte[] snapshotRef = blob!;

    Assert.Equal(secret, InMemorySecretProtector.Unprotect(blob));

    InMemorySecretProtector.ZeroAndClear(ref blob);
    Assert.Null(blob);
    Assert.All(snapshotRef, b => Assert.Equal(0, b));
}
```

- [ ] **Step 2: Run `SecurityHardeningTests` to verify compilation fails on `InMemorySecretProtector` and `ClearSensitiveMemory`**

Run: `dotnet test tests/Shiro.Tests/Shiro.Tests.csproj -c Release --filter "FullyQualifiedName~Account_DoesNotHoldPlaintextSecurityTokenOrPassword_InInstanceStringFields|FullyQualifiedName~InMemorySecretProtector_RoundTripsUnicode_AndZeroesBufferOnClear"`
Expected: FAIL with `CS0103` / `CS1061` (`InMemorySecretProtector` and `ClearSensitiveMemory` do not exist).

- [ ] **Step 3: Implement `InMemorySecretProtector` and update `Account` to store AES-GCM encrypted blobs in RAM**

1. Create `src/Shiro/Classes/InMemorySecretProtector.cs` using an ephemeral per-process 32-byte key (`RandomNumberGenerator.GetBytes(32)`), 12-byte random nonce, and 16-byte AES-GCM authentication tag. In `Protect(string? plaintext)`, rent/allocate the UTF-8 byte buffer, encrypt into `[nonce (12) | tag (16) | ciphertext (N)]`, and call `CryptographicOperations.ZeroMemory(utf8Bytes)` in `finally`. In `Unprotect(ReadOnlySpan<byte> protectedBlob)`, decrypt into a temporary byte buffer, decode UTF-8 `string`, and call `CryptographicOperations.ZeroMemory(utf8Bytes)` in `finally`. In `ZeroAndClear(ref byte[]? protectedBlob)`, call `CryptographicOperations.ZeroMemory` on the existing array and set the reference to `null`.
2. In `src/Shiro/Classes/Account.cs`, `src/Shiro/Classes/Account.SessionState.cs`, and `src/Shiro/Classes/Account.SessionState.CookieRotation.cs`:
   - Replace `private string _SecurityToken = "";` with `private byte[]? _protectedSecurityToken;`, `private bool _hasSecurityToken;`, and `private int _securityTokenPayloadLength;`.
   - Replace `private string _Password = "";` with `private byte[]? _protectedPassword;`.
   - Add `private bool TrySetSecurityTokenUnderLock(string sanitized)` in `Account.SessionState.cs` that compares `GetCurrentSecurityToken()` to `sanitized`, calls `InMemorySecretProtector.ZeroAndClear(ref _protectedSecurityToken)`, sets `_protectedSecurityToken = InMemorySecretProtector.Protect(sanitized)`, `_hasSecurityToken = !string.IsNullOrWhiteSpace(sanitized)`, and `_securityTokenPayloadLength = GetSessionTokenPayloadLength(sanitized)`.
   - Use `_hasSecurityToken` and `_securityTokenPayloadLength` in `HasExpiredShortLivedCookie()`, `IsShortLivedSession`, and `SessionHealthStatus` so checking session health never decrypts `_protectedSecurityToken`.
   - Add `public void ClearSensitiveMemory()` on `Account` to zero and clear both `_protectedSecurityToken` and `_protectedPassword` under `CookieSyncRoot`.

- [ ] **Step 4: Run full test suite and coverage gate verification**

Run:
```powershell
dotnet test tests/Shiro.Tests/Shiro.Tests.csproj -c Release --collect:"XPlat Code Coverage" --settings tests/coverage.runsettings --results-directory "coverage"
& .\scripts\Check-Coverage.ps1 -CoverageDir "coverage" -MinOverall 75.0 -MinBranch 60.0 -MinCore 92.0
Remove-Item -Recurse -Force "coverage" -ErrorAction SilentlyContinue
```
Expected: PASS (`1880+` tests pass, `0` failed, all coverage gates pass).

- [ ] **Step 5: Commit**

```bash
git add src/Shiro/Classes/InMemorySecretProtector.cs src/Shiro/Classes/Account.cs src/Shiro/Classes/Account.SessionState.cs src/Shiro/Classes/Account.SessionState.CookieRotation.cs tests/Shiro.Tests/SecurityHardeningTests.cs
git commit -m "feat(security): protect Account SecurityToken and Password in RAM via InMemorySecretProtector"
```
