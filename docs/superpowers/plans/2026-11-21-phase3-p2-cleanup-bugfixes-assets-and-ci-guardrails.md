# Phase 3 — P2 Cleanup, Bugfixes, Build/Asset Optimization & CI Guardrails Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use `superpowers:subagent-driven-development` (recommended) or `superpowers:executing-plans` to implement this plan task-by-task. Steps use checkbox (`- [ ]) syntax for tracking.

**Goal:** Remove verified dead code and redundant build/resource overhead (packing `Flags.zip` to shrink `Shiro.dll` by ~1.1 MB with zero visual flag loss), fix all Phase 3 P2 domain/network/storage/launch/UI bugs, eliminate all 9 hidden `async void` lambdas behind an `ArchitectureTests` ratchet, and expand CI Coverage Gate & hotspot floors across core security and process-monitoring files.

**Architecture:** Keep all 10 tasks file-disjoint along existing `Services/`, `ViewModels/`, `AvaloniaUI/`, and `scripts/` boundaries, honoring every `ArchitectureTests` line budget (`src/Shiro/*.cs <= 485`, `tests/Shiro.Tests/*.cs <= 550`, `partial class` total `<= 1150`), localization symmetry (`en.json`/`vi.json`), and non-breaking contract. Breaking public/interface signature cleanup (`IAssetCommerce.PurchaseAsync(..., long productId)`, `Account.HasExpiredShortLivedCookie()`, and multi-caller `preferChrome`/`Internal`/`skipPinCheck` parameters) plus `Shiro.Tests` reorganization and `xunit.v3`/`Avalonia 12` upgrades are explicitly scoped to Phase 4 so Phase 3 remains strictly non-breaking and independently shippable.

**Tech Stack:** .NET 10 (`net10.0-windows10.0.19041.0`), C# 14, Avalonia UI 11.3.22, MSBuild `<ZipDirectory>`, `System.IO.Compression.ZipArchive`, xUnit 2.9.3, Avalonia.Headless.XUnit, NSubstitute 6.2.0, PowerShell (`Check-Coverage.ps1`, `Publish.ps1`, `start.ps1`), Lua (`RAMAccount.lua`).

**Spec:** Phase 3 P2 cleanup, bugfix, asset optimization, and CI guardrail roadmap from the 2026-11-21 comprehensive codebase audit (Sections 4.1–4.5, 5.1, 5.2, 6.3, and 7.2), audited on `main` at `48682ac`.

---

## Global Constraints

- **Architecture Guardrails (enforced by `ArchitectureTests*.cs`):**
  - Every `.cs` file in `src/Shiro/` MUST stay `<= 485` lines (`NoSourceCodeFiles_ShouldExceed500Lines`).
  - Every `.cs` file in `tests/Shiro.Tests/` MUST stay `<= 550` lines (`NoTestFiles_ShouldExceed550Lines`).
  - Every `partial class` total across files MUST stay `<= 1150` lines (`NoClass_ShouldExceedLineBudget_AcrossPartials`).
  - Zero compiler warnings (`-warnaserror`), zero empty `catch {}` blocks, zero untyped `catch` blocks (except `AppLogger.cs`), zero Vietnamese diacritics in C# comments/strings outside `LocalizationService*`, zero `.GetAwaiter().GetResult()`, zero `[DllImport]`.
  - `.Wait(TimeSpan...)` allowed ONLY in `AccountManager.Lifecycle.cs` and `Services/Storage/AppLogger.cs`; `Thread.Sleep` allowed ONLY in `StoragePaths.VaultKey.cs`; `DispatcherFrame`/`PushFrame` forbidden (`Baseline = 0`).
  - Zero `Program.Logger` and zero `AppSettings.` references in `Services/*`, `ViewModels/*`, `Domain/*`, and `AvaloniaUI/Views/*` (use `AppServices.Logger` and `AppServices.GeneralSettings`).
  - `Shiro.Services` classes may not reference `Shiro.ViewModels`, `Shiro.AccountManager`, or `Shiro.AvaloniaUI.Views` (except `AvaloniaDialogService`); `Domain/` may not reference `ViewModels`/`Avalonia`/`Microsoft.Win32`.
  - `Batch.cs` MUST NOT contain `Task.Run`; `ProxyBindingService.ClearClients` stays `internal static`.
  - `en.json` and `vi.json` MUST remain 100% key-symmetric with matching `{N}` placeholders; every `LocalizationService.Get/Format("...")` / `Loc.Get("...")` key in C# MUST exist in both JSON files.
  - Every `[Fact]`/`[Theory]` MUST assert concrete values (not only `Assert.NotNull`/`Assert.NotEmpty`/`Assert.True(x >= 0)`).
- **Build & Test Notes (.NET 10 SDK):**
  - `--no-incremental` is valid ONLY on `dotnet build`, NEVER on `dotnet test`.
  - Do **NOT** terminate any live `Shiro.exe` or `RobloxPlayerBeta.exe` process on the host machine.
- **Tight File Line Budgets to Watch:**
  - `SettingsViewModel.cs`: **483 / 485 lines** -> Task 8 edits MUST be **0 net lines** (put helper in `SettingsViewModel.Advanced.cs` [211/485] if >0 lines needed).
  - `StoragePaths.cs`: **480 / 485 lines** -> Task 6 edit (`clearArray: true`) is **0 net lines**.
  - `AccountApiDispatcher.Endpoints.cs`: **477 / 485 lines** -> Task 4 edit is **<= +1 net line** (`<= 478`).
  - `AccountWorkflowService.cs`: **475 / 485 lines** -> Task 6 edit MUST stay `<= 482` (`<= +6` net lines).
  - `DiscordRpcService.cs`: **472 / 485 lines** -> Task 5 edit MUST stay `<= 478` (`<= +6` net lines).
  - `FastLoginScripts.cs`: **455 / 485 lines** -> Task 7 edit MUST stay `<= 475`.
  - `FlagIconService.cs`: **446 / 485 lines** -> Task 2 edit MUST stay `<= 480`.
  - `LaunchCoordinator*.cs` partial total: **1127 / 1150 lines** -> Task 1 (`0` net in `.Helpers.cs`) + Task 7 (`<= +8` net in `.Queue.cs`) keeps partial total `<= 1135 / 1150`.
- **Coverage Floors (`tests/coverage-floors.json`):** Maintain per-file branch coverage >= floor on all 24 existing hotspots (including `ServerBrowserTabViewModel.Operations.cs >= 80%`, `RobloxSettingsApiService.cs >= 87%`, `AccountWorkflowService.cs >= 73%`, `AccountPersistenceService.cs >= 67%`, `LaunchCoordinator.Helpers.cs >= 74%`).

## Review Focus

1. **Embedded `Flags.zip` & zero duplicate `Resources/` copies:** Replacing `<AvaloniaResource Include="Resources\Flags\*.svg" />` and `<None Update="Resources\**\*" ... />` with build-time lossless `Shiro.Resources.Flags.zip` must keep all 257 flags renderable (including `<clipPath>`, gradients, and `eu` `<defs>/<use>`) even without loose disk files, shrink `Shiro.dll`, and cache `ServerItemViewModel.FlagImage` so `HasFlagImage` evaluates once — pinned by Task 2 (`tests/Shiro.Tests/Phase3BuildAssetsAndFlagTests.cs`).
2. **Versioned Discord webhooks, Telegram MarkdownV2 4096-char cap, Lua IPv6 & reseller sync:** `IsAllowedDiscordWebhookUrl` must accept `/api/v10/webhooks/...`; truncating Telegram messages to `<= 4096` chars must never leave an odd trailing `\` escape; `RAMAccount.lua` must bracket unbracketed IPv6 hosts (`[::1]`); cheapest reseller selection must sync `expectedPrice` and `sellerType`; and REST `/SetAvatar` must never pop UI dialogs — pinned by Task 4 (`tests/Shiro.Tests/Phase3NetworkAndIntegrationBugfixTests.cs`).
3. **Cross-`await` save suppression, exception rollback & Windows reserved device filenames:** `Account.BeginSuppressDeferredSaveScope()` must flow across `await` continuations (`AsyncLocal<int>`); `ReLoginWithCookieAsync` throwing during validation must roll back `SecurityToken`; `ParseUserPassCombo("alice:p@ss|word")` must split on `:` without breaking `"alice|sec:ret:123"`; `MakeSafeFileName("CON.txt")` must neutralize reserved Windows device names; and `AtomicWriteText`/`VaultKeyRecoveryService` must clear rented/key buffers — pinned by Task 6 (`tests/Shiro.Tests/Phase3DomainAndStorageHardeningTests.cs`).
4. **Non-Roblox WebView2 pages, proxy-scoped 429 backoff & per-account avatar/toast isolation:** `FastLoginScripts` must guard on `roblox.com` origin and clear its `setInterval`; 429 on Proxy A must not stall Proxy B in `LaunchCoordinator.Queue`; `InvalidateAvatar()` on Account A must leave Account B's decoded avatar intact; coalesced toasts must bind the latest `onAction`; and `SettingsViewModel` must clamp `WebServerPort` to `1..65535` — pinned by Tasks 7 & 8 (`tests/Shiro.Tests/Phase3LaunchAndFastLoginHardeningTests.cs`, `tests/Shiro.Tests/Phase3UiAndViewModelBugfixTests.cs`).
5. **Zero hidden `async void` lambdas, modal dialog cancellation, password zeroing & >= 92% Core coverage:** Canceling `SignInFastLoginAsync` must close any open `FastLoginDialog`; `MainWindow` must allow retry attempts and zero password/hash bytes in `finally`; `HiddenAsyncVoidLambdas_ShouldBeZero` must enforce `0` hidden `async void` lambdas repo-wide; and removing `RobloxProcess.cs`/`RobloxWatcher.cs` exclusions while adding security/persistence files to Core Coverage must pass `Check-Coverage.ps1` (`Overall >= 75%`, `Branch >= 60%`, `Core >= 92%`) — pinned by Tasks 9 & 10 (`tests/Shiro.Tests/Phase3AsyncSafetyAndDialogTests.cs`, `tests/Shiro.Tests/Phase3CoreCoverageHardeningTests.cs`).

---

### Task 1: Verified Dead Code Removal & Duplicate P/Invoke / Fallback Cleanup

*(Note: Dead-code removals inside `AccountViewModel.Avatar.cs` [`AvatarUrl`] and `ToastService.cs` [`IconKind`/`Glyph`] are folded into Task 8 alongside their fixes/tests so each file is modified cleanly once. Breaking multi-caller signature cleanups [`IAssetCommerce.PurchaseAsync` `productId`, `Account.HasExpiredShortLivedCookie()`, `preferChrome`, `Internal`, `skipPinCheck`, `ShouldAssignToJobObject(pid, cpuCapPercent)`] remain deferred to Phase 4.)*

**Files:**
- Modify: `src/Shiro/AccountManager.cs:86-90` (`rgSaveLock`)
- Modify: `src/Shiro/Services/Windows/Win32TrayHelper.cs:25-31` (`FindWindowEx`)
- Modify: `src/Shiro/Services/Network/RobloxAccountApiService.cs:183-222` (`GetRateLimitRecoverySeconds`)
- Modify: `src/Shiro/Services/Launch/BrowserService.cs:10-60` (`SuppressedPasswordPrefsUtf8`, `SuppressPasswordManager`)
- Modify: `src/Shiro/Services/Network/RobloxModerationService.cs:195-218` (unreachable `response.StatusCode == OK` branch)
- Modify: `src/Shiro/Services/Windows/MemoryOptimizer.cs:365-370` (`PROCESS_QUERY_INFORMATION`)
- Modify: `src/Shiro/Services/Windows/RobloxProcessOptimizer.cs:15-31, 52-71` (`PROCESS_QUERY_INFORMATION`, `SetProcessAffinityMask`, `SetProcessAffinity`)
- Modify: `src/Shiro/Services/Windows/RobloxWindowService.cs:52-58, 130-145` (duplicate `GetWindowLongPtr` -> `Win32NativeHelper.GetWindowLongPtrW`)
- Modify: `src/Shiro/Services/Ui/Utilities.cs:44-52` (`InvokeIfRequired`)
- Modify: `src/Shiro/Services/Launch/LaunchCoordinator.Helpers.cs:420-446` (redundant `Loc.Get(..., LocalizationService.Get(...))` fallback)
- Modify: `src/Shiro/AvaloniaUI/App.axaml.cs:25-31` (redundant `SetLanguage` call immediately after `Initialize`)
- Modify: `src/Shiro/Services/Storage/IniFile.cs:45-77, 220-232, 257-262` (`IniProperty.Comment`, `IniSection.Comment`, `WriteSpacingBetweenNameAndValue`, `CommentChar`)
- Modify affected tests:
  - `tests/Shiro.Tests/AccountSessionStateBehaviorTests.cs:238-280`
  - `tests/Shiro.Tests/AccountLauncherAndHelperBehaviorTests.cs:150-170`
  - `tests/Shiro.Tests/RobloxDirectLauncherAndOptimizerTests.cs:35-42`
  - `tests/Shiro.Tests/RobloxWindowServicesBehaviorTests.cs:137-153`
  - `tests/Shiro.Tests/ClientSettingsAndUtilitiesBehaviorTests.cs:118-130`

**Interfaces:**
- Produces: cleaner internal/public surfaces with zero dead helpers; preserves all active `Account`, `IAssetCommerce`, `RobloxProcessOptimizer.ShouldAssignToJobObject`, and `RobloxSettingsApiService` public contracts.

- [ ] **Step 1: Verify zero remaining callers before removal & update dead-code test references**

Inspect each symbol and update the 5 directly affected tests first:
1. Remove `GetRateLimitRecoverySeconds_ParsesHeadersCorrectly` from `tests/Shiro.Tests/AccountSessionStateBehaviorTests.cs:238-280` (testing a dead `private static` method with 0 callers in `src/`).
2. Remove `BrowserService_SuppressPasswordManager_WritesPreferencesFile` from `tests/Shiro.Tests/AccountLauncherAndHelperBehaviorTests.cs:150-170` (keep `BrowserService_NoPasswordManagerSwitches_ContainsExpectedSwitches`).
3. Remove the `SetProcessAffinity(-1, ...)` lines from `tests/Shiro.Tests/RobloxDirectLauncherAndOptimizerTests.cs:38` and `tests/Shiro.Tests/RobloxWindowServicesBehaviorTests.cs:142` (keep the `SetProcessPriority`, `LowerPriorityForPids`, `RestorePriorityForPids`, and `OptimizeLaunchedProcess` assertions).
4. In `tests/Shiro.Tests/ClientSettingsAndUtilitiesBehaviorTests.cs:122`: remove `sec.Comment = "General settings";` (keep all functional INI section assertions).

- [ ] **Step 2: Run affected tests after adding a compile-check assertion if helpful, or proceed straight to removing the dead production symbols**

Note: since removing unused methods/fields makes dead tests fail to compile in Step 3, verify via grep that 0 `src/Shiro` callers exist before deleting:
- `AccountManager.cs`: delete `rgSaveLock` (`L88`).
- `Win32TrayHelper.cs`: delete unused `FindWindowEx` (`L27-28`).
- `RobloxAccountApiService.cs`: delete `GetRateLimitRecoverySeconds` (`L185-220`).
- `BrowserService.cs`: delete `SuppressedPasswordPrefsUtf8` (`L13-29`) and `SuppressPasswordManager` (`L42-59`).
- `RobloxModerationService.cs`: collapse unreachable `L199-216` (`if (response.StatusCode == OK)` after `L103-113` already returned unconditionally) to `return (false, null, null);`.
- `MemoryOptimizer.cs` (`L367`) & `RobloxProcessOptimizer.cs` (`L18, 27-29, 54-69`): remove `PROCESS_QUERY_INFORMATION`, `SetProcessAffinityMask`, and `SetProcessAffinity`.
- `RobloxWindowService.cs`: delete `GetWindowLongPtr` P/Invoke (`L54-55`) and replace calls at `L134, L139` with `Win32NativeHelper.GetWindowLongPtrW`.
- `Utilities.cs`: delete `InvokeIfRequired` (`L46-50`).
- `LaunchCoordinator.Helpers.cs`: replace `Loc.Get("Launch_CaptchaClosedError", Shiro.Services.LocalizationService.Get("Launch_CaptchaClosedError"))` (`L423`) and `Launch_CaptchaRequiredError` (`L443`) with direct `LocalizationService.Get(...)` calls (preserves `>= 74%` branch floor).
- `App.axaml.cs`: remove redundant `LocalizationService.SetLanguage(LocalizationService.CurrentLanguage, false);` (`L28`) right after `LocalizationService.Initialize(this);`.
- `IniFile.cs`: remove `IniProperty.Comment` (`L47`, clone `L74`, assignments `L222,228,230`), `IniSection.Comment` (`L64`), `WriteSpacingBetweenNameAndValue` (`L259`), and `CommentChar` (`L260`); keep optional `string? comment = null` parameter on `IniSection.Set` so `AccountManager.cs:160` compiles unchanged.

- [ ] **Step 3: Build & verify all unit + architecture + hotspot coverage tests**

Run:
```powershell
dotnet build "Shiro.sln" --no-incremental -warnaserror
dotnet test "tests/Shiro.Tests/Shiro.Tests.csproj" --no-build --filter "FullyQualifiedName~AccountSessionStateBehaviorTests|FullyQualifiedName~AccountLauncherAndHelperBehaviorTests|FullyQualifiedName~RobloxDirectLauncherAndOptimizerTests|FullyQualifiedName~RobloxWindowServicesBehaviorTests|FullyQualifiedName~ClientSettingsAndUtilitiesBehaviorTests|FullyQualifiedName~LaunchCoordinator|FullyQualifiedName~RobloxModerationServiceBehaviorTests|FullyQualifiedName~Win32TrayHelperBehaviorTests|FullyQualifiedName~Shiro.Tests.ArchitectureTests"
dotnet test "tests/Shiro.Tests/Shiro.Tests.csproj" --no-build --filter "FullyQualifiedName~P3CompositionAndArchitectureTests"
```
Expected: `0 Warning(s), 0 Error(s)` and 100% PASS (`Failed: 0`).

- [ ] **Step 4: Commit**

```powershell
git add src/Shiro/AccountManager.cs src/Shiro/Services/Windows/Win32TrayHelper.cs src/Shiro/Services/Network/RobloxAccountApiService.cs src/Shiro/Services/Launch/BrowserService.cs src/Shiro/Services/Network/RobloxModerationService.cs src/Shiro/Services/Windows/MemoryOptimizer.cs src/Shiro/Services/Windows/RobloxProcessOptimizer.cs src/Shiro/Services/Windows/RobloxWindowService.cs src/Shiro/Services/Ui/Utilities.cs src/Shiro/Services/Launch/LaunchCoordinator.Helpers.cs src/Shiro/AvaloniaUI/App.axaml.cs src/Shiro/Services/Storage/IniFile.cs tests/Shiro.Tests/AccountSessionStateBehaviorTests.cs tests/Shiro.Tests/AccountLauncherAndHelperBehaviorTests.cs tests/Shiro.Tests/RobloxDirectLauncherAndOptimizerTests.cs tests/Shiro.Tests/RobloxWindowServicesBehaviorTests.cs tests/Shiro.Tests/ClientSettingsAndUtilitiesBehaviorTests.cs
git commit -m "chore(cleanup): remove verified dead code, duplicate P/Invokes, and redundant fallbacks"
```

---

### Task 2: Build Optimization, Script Hardening, Lossless Embedded `Flags.zip` & `ServerItemViewModel` Flag Caching

**Files:**
- Modify: `src/Shiro/Shiro.csproj:29, 73-92`
- Modify: `src/Shiro/Services/Ui/FlagIconService.cs:157-185` (`<= 485` lines; currently `446`)
- Modify: `src/Shiro/ViewModels/ServerItemViewModel.cs:70-158` (`<= 485` lines; currently `213`)
- Modify: `scripts/Publish.ps1:93-101`
- Modify: `start.ps1:31-42`
- Test: `tests/Shiro.Tests/Phase3BuildAssetsAndFlagTests.cs`

**Interfaces:**
- Produces:
  - `FlagIconService.TryOpenFromEmbeddedArchive(string code)` (`internal static Stream?`) — opens `<code.ToLowerInvariant()>.svg` lazily from embedded manifest resource `Shiro.Resources.Flags.zip` (cached `byte[]` + lock-free per-call `ZipArchive` over `MemoryStream` wrapper, so thread-safe).
  - `ServerItemViewModel.HasFlagImage` evaluates without re-calling `FlagIconService.GetFlagImage` on every read when country/region hasn't changed (`_cachedFlagImage` / `_cachedFlagLookupKey`).

- [ ] **Step 1: Write failing tests in `tests/Shiro.Tests/Phase3BuildAssetsAndFlagTests.cs`**

Create `tests/Shiro.Tests/Phase3BuildAssetsAndFlagTests.cs` (`[Collection("SerialGlobalState")]`) with 5 tests:
1. `ShiroAssembly_EmbedsCompressedFlagsZip_AndExcludesDuplicateFlagAvaloniaResources`: asserts `typeof(FlagIconService).Assembly.GetManifestResourceNames()` contains `"Shiro.Resources.Flags.zip"` and checks `Shiro.csproj` uses `<Optimize Condition="'$(Configuration)' == 'Release'">true</Optimize>` and no `<None Update="Resources\**\*">`.
2. `FlagIconService_TryOpenFromEmbeddedArchive_LoadsAllDatacenterAndSampleFlags`: opens `"us"`, `"vn"`, `"sg"`, `"eu"`, `"rs"`, `"bo"`, `"gt"`, `"bz"`, `"ni"` directly via `FlagIconService.TryOpenFromEmbeddedArchive(code)`, asserting valid `<svg` XML stream content without needing any `Resources/Flags` files on disk (`!Directory.Exists(Path.Combine(AppContext.BaseDirectory, "Resources"))`).
3. `ServerItemViewModel_FlagImage_CachesAcrossRepeatedHasFlagImageAndFlagImageReads`: clears `FlagIconService` cache or observes `ServerItemViewModel { CountryCode = "US", Region = "Virginia" }` reading `HasFlagImage` and `FlagImage` multiple times, asserting non-null image, `HasFlagImage == true`, and changing `CountryCode = "VN"` updates cached flag cleanly; setting `CountryCode = ""` sets `HasFlagImage == false` and `FlagImage == null`.
4. `PublishScript_PreservesIntermediateObjFolder_WhileCleaningReleaseOutputs`: parses `scripts/Publish.ps1` and verifies `$legacyFolders` does NOT remove `'obj'` or `'artifacts'` (which hold `build/artifacts/obj` incremental Debug state).
5. `StartScript_QuotesArgumentsWithSpacesProperly`: verifies `start.ps1` escapes/quotes arguments safely (uses `ArgumentList` or quoted joining so args with spaces are preserved).

- [ ] **Step 2: Run build/tests to verify RED**

Run:
```powershell
dotnet build "Shiro.sln" --no-incremental -warnaserror
```
Expected: FAIL to compile because `FlagIconService.TryOpenFromEmbeddedArchive` does not exist yet.

- [ ] **Step 3: Implement build-time lossless `Flags.zip`, `FlagIconService`, `ServerItemViewModel`, `Publish.ps1`, and `start.ps1`**

1. `src/Shiro/Shiro.csproj`:
   - Change line 29 `<Optimize>true</Optimize>` -> `<Optimize Condition="'$(Configuration)' == 'Release'">true</Optimize>` and `<Optimize Condition="'$(Configuration)' != 'Release'">false</Optimize>`.
   - Replace `<AvaloniaResource Include="Resources\Flags\*.svg" />` (`L77`) with `<ItemGroup><FlagSvgFile Include="Resources\Flags\*.svg" /></ItemGroup>` and add a MSBuild target BEFORE `AssignTargetPaths`:
     ```xml
     <Target Name="PackFlagsArchive" BeforeTargets="AssignTargetPaths" Inputs="@(FlagSvgFile)" Outputs="$(IntermediateOutputPath)Flags.zip">
       <MakeDir Directories="$(IntermediateOutputPath)" />
       <ZipDirectory SourceDirectory="Resources\Flags" DestinationFile="$(IntermediateOutputPath)Flags.zip" Overwrite="true" />
       <ItemGroup>
         <EmbeddedResource Include="$(IntermediateOutputPath)Flags.zip" LogicalName="Shiro.Resources.Flags.zip" />
       </ItemGroup>
     </Target>
     ```
     *(Crucial: adding `<EmbeddedResource>` inside `PackFlagsArchive` `BeforeTargets="AssignTargetPaths"` runs AFTER line 91 `<EmbeddedResource Remove="bin\**\*;obj\**\*;build\**\*" />` static item evaluation, so `$(IntermediateOutputPath)Flags.zip` inside `build/artifacts/obj/` is NOT stripped before embedding!)*
   - Remove `<None Update="Resources\**\*" CopyToOutputDirectory="PreserveNewest" CopyToPublishDirectory="Never" />` (`L81`) and `<None Include="Resources\shiro.ico">...</None>` (`L82-85`) so output dirs never copy redundant disk duplicates (except `shiro.ico` if `ApplicationIcon` embeds it directly into Win32 PE resource — verify `ApplicationIcon` builds cleanly with 0 warnings!).
2. `src/Shiro/Services/Ui/FlagIconService.cs`:
   - Add `private static readonly Lazy<byte[]?> EmbeddedFlagsArchiveBytes = new(LoadEmbeddedFlagsArchiveBytes);` reading `Shiro.Resources.Flags.zip` from `typeof(FlagIconService).Assembly`.
   - Implement `internal static Stream? TryOpenFromEmbeddedArchive(string code)` opening a new `MemoryStream(archiveBytes, writable: false)` + `ZipArchive(ms, ZipArchiveMode.Read)` and copying the matching `{code}.svg` (case-insensitive) entry into a returned `MemoryStream` (position 0), keeping `OpenFlagSvgStream(string code)` checking `TryOpenFromEmbeddedArchive(code)` -> `AssetLoader` -> disk fallback cleanly (`<= 480` lines total).
3. `src/Shiro/ViewModels/ServerItemViewModel.cs`:
   - Remove empty `set { }` blocks on `PlayerDisplay` (`L70`), `PlayerPercentage` (`L76`), `PingDisplay` (`L95`), `PingColor` (`L107`).
   - Cache `(string? Key, IImage? Image)` inside `ServerItemViewModel` so `HasFlagImage => FlagImage != null;` hits the local cached field instead of calling `InferCountryCode` + `FlagIconService.GetFlagImage` twice; invalidate cached tuple when `_countryCode` or `_region` changes.
4. `scripts/Publish.ps1`:
   - In `$legacyFolders` (`L93`): keep only `@('Release-Latest')` (stop deleting `build\artifacts\obj` or `build\artifacts`), preventing Debug incremental `CS2001` `.editorconfig` loss.
5. `start.ps1`:
   - When `$UseShellExecute = $true` cannot use `ArgumentList` cleanly (actually in .NET ProcessStartInfo on Windows/PowerShell 7 & 5.1, `$psi.UseShellExecute = $false` OR building escaped arguments via `$launchArgs | ForEach-Object { if ($_ -match '[\s"]') { '"' + ($_ -replace '(\\*)"', '$1$1\"') + '"' } else { $_ } }` works with BOTH `$UseShellExecute = $true` and `$false`!). Switch to `$psi.UseShellExecute = $false` with `$psi.ArgumentList.Add($arg)` (or quoted escaping if PS5 compatibility needed — check `$psi.ArgumentList` works cleanly in .NET / PS 5.1+ -> wait: `$psi.ArgumentList` was added in .NET Core 2.1 / PS 7+, NOT .NET Framework 4.8 PowerShell 5.1! So quoting each argument explicitly with regex escaping works in BOTH Windows PowerShell 5.1 AND PowerShell 7!). Use explicit Windows command-line quoting in `start.ps1`.

- [ ] **Step 4: Run tests and inspect build outputs (`bin/Debug`, `bin/Release`)**

Run:
```powershell
dotnet build "Shiro.sln" --no-incremental -warnaserror
dotnet test "tests/Shiro.Tests/Shiro.Tests.csproj" --no-build --filter "FullyQualifiedName~Phase3BuildAssetsAndFlagTests|FullyQualifiedName~FlagIconServiceTests|FullyQualifiedName~ServerItemViewModelTests|FullyQualifiedName~LruCacheAndRpcOptimizationTests|FullyQualifiedName~P2AuditRemediationTests|FullyQualifiedName~Shiro.Tests.ArchitectureTests"
dotnet test "tests/Shiro.Tests/Shiro.Tests.csproj" --no-build --filter "FullyQualifiedName~P3CompositionAndArchitectureTests"
```
Verify:
- `0 Warning(s), 0 Error(s)` and 100% PASS.
- Clean rebuild (`dotnet clean` or remove `build/artifacts/Debug/Resources` once) and re-build confirms `build/artifacts/Debug/net10.0-windows10.0.19041.0/win-x64/Resources` is NOT re-created on build, and `Shiro.dll` size shrunk by ~1 MB.

- [ ] **Step 5: Commit**

```powershell
git add src/Shiro/Shiro.csproj src/Shiro/Services/Ui/FlagIconService.cs src/Shiro/ViewModels/ServerItemViewModel.cs scripts/Publish.ps1 start.ps1 tests/Shiro.Tests/Phase3BuildAssetsAndFlagTests.cs
git commit -m "perf(assets): pack 257 flag SVGs into lossless Flags.zip, remove duplicate output copies, and cache flag lookup"
```

---

### Task 3: `ServerBrowserTabViewModel` Localization Keys & Bounded Throttled `PlayerSearch`

**Files:**
- Modify: `src/Shiro/Resources/Strings/en.json:696-700`
- Modify: `src/Shiro/Resources/Strings/vi.json:696-700`
- Modify: `src/Shiro/ViewModels/ServerList/ServerBrowserTabViewModel.Operations.cs:100-142` (`<= 485`; currently `203`)
- Modify: `src/Shiro/ViewModels/ServerList/ServerBrowserTabViewModel.PlayerSearch.cs:115-182` (`<= 485`; currently `282`)
- Modify: `tests/Shiro.Tests/ServerListViewModelOperationsBehaviorTests.cs:160-200`
- Test: `tests/Shiro.Tests/Phase3ServerBrowserAndSearchTests.cs`

**Interfaces:**
- Produces:
  - `ServerBrowserTabViewModel.MaxPlayerSearchPages` (`internal const int = 15`)
  - `ServerBrowserTabViewModel.PlayerSearchStatusThrottleMs` (`internal const int = 100`)
  - Localization keys `"ServerQuery.LoginRequired"` (`"Login required"` / `"Cần đăng nhập"`), `"ServerQuery.RegionLoadError"` (`"Load error"` / `"Lỗi tải khu vực"`), and `"ServerList.PlayerSearchPageLimitReached"` (`"Player '{0}' was not found within {1} servers."` / `"Không tìm thấy người chơi '{0}' trong {1} máy chủ đầu tiên."`).
  - Keep existing `"ServerQuery.FetchingRegion"` key in `en.json`/`vi.json` untouched ONLY if still used by `RobloxServerQueryService.cs:157` (so `RobloxServerQueryServiceBehaviorTests.cs:62` passes without churn!).

- [ ] **Step 1: Write failing tests in `tests/Shiro.Tests/Phase3ServerBrowserAndSearchTests.cs` & update `ServerListViewModelOperationsBehaviorTests.cs`**

1. Update `tests/Shiro.Tests/ServerListViewModelOperationsBehaviorTests.cs:165, 197`:
   - Line 165 (`QueryServerRegionAndPingAsync` throws): assert `LocalizationService.Get("ServerQuery.RegionLoadError")` instead of `"MissingAssets.PriceLoadError"`.
   - Line 197 (`acc == null`): assert `LocalizationService.Get("ServerQuery.LoginRequired")` instead of `"ServerQuery.FetchingRegion"`.
2. Create `tests/Shiro.Tests/Phase3ServerBrowserAndSearchTests.cs` (`[Collection("SerialGlobalState")]`) with 3 tests:
   - `LoadRegionCoreAsync_UsesProperServerQueryLocalizationKeys_NotMissingAssetsKey`: verifies `acc == null` sets `ServerQuery.LoginRequired` and exception sets `ServerQuery.RegionLoadError` (never `MissingAssets.PriceLoadError`), both non-empty and localized in `en` and `vi`.
   - `SearchPlayerAsync_EnforcesMaxPlayerSearchPagesCap_AndStopsScanning`: serves 25 endless mock pages (`nextPageCursor: "page_{n+1}"`) each with 10 servers, invokes `SearchPlayerAsync`, and asserts HTTP public-server requests stop at `<= ServerBrowserTabViewModel.MaxPlayerSearchPages` (`15`), reporting `ServerList.PlayerSearchPageLimitReached` / not found cleanly without looping endlessly.
   - `SearchPlayerAsync_ThrottlesUiStatusPostsPerBatch`: serves 1 page containing 100 servers with player tokens and counts `AppDispatcher.CustomDispatcher` status invocations during scan, asserting UI status posts are throttled (`<= 5` per 100 servers rather than 100+ per page).

- [ ] **Step 2: Run tests to verify RED**

Run:
```powershell
dotnet build "Shiro.sln" --no-incremental -warnaserror
```
Expected: FAIL to compile (`MaxPlayerSearchPages` missing, `LocalizationIntegrityTests` would also flag missing keys until Step 3).

- [ ] **Step 3: Implement localization fixes, page cap, and throttled `AppDispatcher.Post` in `PlayerSearch`**

1. `src/Shiro/Resources/Strings/en.json` & `vi.json`: add identical keys `"ServerQuery.LoginRequired"`, `"ServerQuery.RegionLoadError"`, `"ServerList.PlayerSearchPageLimitReached"` (exact matching `{0}`/`{1}` placeholders).
2. `src/Shiro/ViewModels/ServerList/ServerBrowserTabViewModel.Operations.cs`:
   - Line 104: replace `"ServerQuery.FetchingRegion"` with `"ServerQuery.LoginRequired"`.
   - Line 136: replace `"MissingAssets.PriceLoadError"` with `"ServerQuery.RegionLoadError"`.
   - Preserve branch coverage >= `80%` on `Operations.cs`.
3. `src/Shiro/ViewModels/ServerList/ServerBrowserTabViewModel.PlayerSearch.cs`:
   - Add `internal const int MaxPlayerSearchPages = 15;` and `internal const int PlayerSearchStatusThrottleMs = 100;`.
   - Track `int pagesFetched = 0;` and `bool hitPageLimit = false;` in `ProduceServerBatchesAsync`; stop `while (...)` once `pagesFetched >= MaxPlayerSearchPages` (`hitPageLimit = !string.IsNullOrEmpty(cursor) && Volatile.Read(ref matchedServer) == null && pagesFetched >= MaxPlayerSearchPages`).
   - Throttle `PostPlayerSearchStatus` inside `foreach (var s in sInfo.data)` using `Environment.TickCount64` (`long lastStatusPostTicks`) so UI status updates fire at most once every `PlayerSearchStatusThrottleMs` (plus final batch summary if not found / hit limit).

- [ ] **Step 4: Run tests to verify GREEN**

Run:
```powershell
dotnet build "Shiro.sln" --no-incremental -warnaserror
dotnet test "tests/Shiro.Tests/Shiro.Tests.csproj" --no-build --filter "FullyQualifiedName~Phase3ServerBrowserAndSearchTests|FullyQualifiedName~ServerListViewModelOperationsBehaviorTests|FullyQualifiedName~ServerListPlayerSearchBehaviorTests|FullyQualifiedName~LocalizationIntegrityTests|FullyQualifiedName~Shiro.Tests.ArchitectureTests"
```
Expected: `0 Warning(s), 0 Error(s)` and 100% PASS.

- [ ] **Step 5: Commit**

```powershell
git add src/Shiro/Resources/Strings/en.json src/Shiro/Resources/Strings/vi.json src/Shiro/ViewModels/ServerList/ServerBrowserTabViewModel.Operations.cs src/Shiro/ViewModels/ServerList/ServerBrowserTabViewModel.PlayerSearch.cs tests/Shiro.Tests/ServerListViewModelOperationsBehaviorTests.cs tests/Shiro.Tests/Phase3ServerBrowserAndSearchTests.cs
git commit -m "fix(server-list): fix ServerQuery localization keys, bound PlayerSearch pages, and throttle UI status posts"
```

---

### Task 4: `RAMAccount.lua` IPv6 Formatting, `WebhookNotificationService` v10/4096-Char Truncation, `AssetCommerceService` Reseller Sync & Headless `/SetAvatar` Guard

**Files:**
- Modify: `RAMAccount.lua:22-53, 185-210`
- Modify: `src/Shiro/Services/Network/WebhookNotificationService.cs:195-225, 319-335` (`<= 485`; currently `437`)
- Modify: `src/Shiro/Services/Network/AssetCommerceService.cs:117-156` (`<= 485`; currently `234`)
- Modify: `src/Shiro/Services/Network/RobloxSettingsApiService.cs:320-360` (`<= 485`; currently `416`)
- Modify: `src/Shiro/Domain/Account.SettingsApi.cs:29-31` (`<= 485`; currently `32`)
- Modify: `src/Shiro/Services/Network/AccountApiDispatcher.Endpoints.cs:462-472` (`<= 485`; currently `477`)
- Test: `tests/Shiro.Tests/Phase3NetworkAndIntegrationBugfixTests.cs`

**Interfaces:**
- Produces:
  - `Account.SetAvatarAsync(string avatarJsonData, CancellationToken cancellationToken = default)` (unchanged signature) + overload `internal Task<bool> SetAvatarAsync(string avatarJsonData, bool suppressMissingAssetsDialog, CancellationToken cancellationToken = default)` (same pattern in `RobloxSettingsApiService`: `bool suppressMissingAssetsDialog = false` optional parameter at end so ALL existing tests in `RobloxSettingsApiServiceTests.cs` pass unchanged while `AccountApiDispatcher.Endpoints.cs:468` passes `suppressMissingAssetsDialog: true`!).
  - `WebhookNotificationService.FormatTelegramMessage` (`<= 4096` chars, never ends on an unescaped odd trailing `\`).

- [ ] **Step 1: Write failing tests in `tests/Shiro.Tests/Phase3NetworkAndIntegrationBugfixTests.cs`**

Create `tests/Shiro.Tests/Phase3NetworkAndIntegrationBugfixTests.cs` (`[Collection("SerialGlobalState")]`) with 5 tests:
1. `WebhookNotificationService_IsAllowedDiscordWebhookUrl_AcceptsVersionedDiscordPaths`: asserts `true` for `"https://discord.com/api/v10/webhooks/123456/tok-en"` and `"https://canary.discordapp.com/api/v9/webhooks/123/abc"`, and asserts `false` for `"https://discord.com/api/v10/users/1"`, `"http://discord.com/api/v10/webhooks/123/abc"`, `"https://evil.com/api/v10/webhooks/123/abc"`.
2. `WebhookNotificationService_FormatTelegramMessage_TruncatesTo4096WithoutTrailingOddBackslash`: passes `AccountUsername = "User"` and `Description` containing `5000` escaped special characters (`"a_b*c[d]..."`), asserts returned MarkdownV2 string `.Length <= 4096`, does not end with an odd number of trailing `\`, and still preserves `*Event:*` / `"..."` ellipsis cleanly.
3. `AssetCommerceService_PurchaseAsync_CheapestReseller_SyncsExpectedPriceAndSellerType`: mocks `marketplace-items/v1/items/details` returning `hasResellers: true`, `lowestAvailableResaleProductId: "resale-cp-9"`, `lowestAvailableResaleSellerId: 999`, `lowestPrice: 450` (and `lowestAvailableResaleSellerType: "Group"` / fallback), calls `PurchaseAsync(..., expectedPrice: 100, sellerId: 111, collectibleItemId: "col-9", collectibleProductId: null, sellerType: "User")`, asserts purchase request JSON body carries `"expectedPrice":450`, `"expectedSellerId":999`, `"expectedSellerType":"Group"`, and `"collectibleProductId":"resale-cp-9"`.
4. `AccountApiDispatcher_SetAvatar_DoesNotOpenMissingAssetsDialog`: invokes `/SetAvatar` via `AccountApiDispatcher` against a mocked `IRobloxApiClient.Avatar` returning HTTP 400 `errors` with invalid asset ID `[98765]` and mocks `IDialogService`; asserts `OpenMissingAssetsDialog` is **NOT** called (`DidNotReceiveWithAnyArgs()`). (Also tests direct `RobloxSettingsApiService.Instance.SetAvatarAsync(acc, json, suppressMissingAssetsDialog: false)` still calls `OpenMissingAssetsDialog`.)
5. `RamAccountLua_BracketsBareIpv6HostInHttpAndWebSocketUrls`: inspects `RAMAccount.lua` verifying `FormatHostForUrl` (or bracket helper) wraps bare IPv6 (`string.find(host, ':')` and not already `[...]`) as `'[' .. host .. ']'` in BOTH `BuildUrl` (`http://[::1]:7963/...`) and `ConnectEvents` (`ws://[::1]:7963/ws`), and checks `IsLoopbackHost(" ::1 ")` / `IsLoopbackHost("[::1]")`.

- [ ] **Step 2: Run build/tests to verify RED**

Run:
```powershell
dotnet build "Shiro.sln" --no-incremental -warnaserror
dotnet test "tests/Shiro.Tests/Shiro.Tests.csproj" --no-build --filter "FullyQualifiedName~Phase3NetworkAndIntegrationBugfixTests"
```
Expected: Compile error on `SetAvatarAsync(..., suppressMissingAssetsDialog: false)` or assertions fail (webhook v10 rejected, Telegram length > 4096, reseller `expectedPrice` stays `100`, `/SetAvatar` calls `OpenMissingAssetsDialog`, `RAMAccount.lua` lacks IPv6 bracketing).

- [ ] **Step 3: Implement fixes across the 6 files**

1. `src/Shiro/Services/Network/WebhookNotificationService.cs`:
   - In `IsAllowedDiscordWebhookUrl`: accept `Regex` `^/api/(?:v\d+/)?webhooks/` (case-insensitive) via `[GeneratedRegex]` (verify `webhooks/{id}` path segment present).
   - In `FormatTelegramMessage`: truncate resulting MarkdownV2 string (or description portion + whole message safety clamp) to `<= 4096` characters using a small helper `TruncateTelegramMarkdownSafe(string text, int maxLength = 4096)`: if `text.Length > maxLength`, cut to `maxLength - 3` (leaving room for `...`), walk backward while the character at the cut boundary is preceded by an odd run of `'\\'` characters (so no `\x` MarkdownV2 escape sequence is severed), and append `"..."`.
2. `src/Shiro/Services/Network/AssetCommerceService.cs`:
   - Inside `if (hasResellers && lowestAvailableResaleProductId ...)`: update `expectedPrice` from `lowestAvailableResalePrice` / `lowestResalePrice` / `lowestPrice` (`TryGetInt64`, `>= 0` when present; wait: check property names commonly returned by Roblox `marketplace-items/v1/items/details`: check `"lowestAvailableResalePrice"`, `"lowestResalePrice"`, `"lowestPrice"` in priority order) AND update `sellerType` from `"lowestAvailableResaleSellerType"` / `"resellerSellerType"` (falling back cleanly to existing `sellerType` if absent). Also check when `!usingResale` if `creatorType` exists when `sellerId` is resolved from `creatorId`!
3. `src/Shiro/Services/Network/RobloxSettingsApiService.cs`, `src/Shiro/Domain/Account.SettingsApi.cs`, `src/Shiro/Services/Network/AccountApiDispatcher.Endpoints.cs`:
   - Update `RobloxSettingsApiService.SetAvatarAsync`:
     `public Task<bool> SetAvatarAsync(Account account, string avatarJsonData, CancellationToken cancellationToken = default) => SetAvatarAsync(account, avatarJsonData, suppressMissingAssetsDialog: false, cancellationToken);`
     `internal async Task<bool> SetAvatarAsync(Account account, string avatarJsonData, bool suppressMissingAssetsDialog, CancellationToken cancellationToken = default)` (or `bool suppressMissingAssetsDialog = false` carefully avoiding ambiguous overload! Using a separate explicit `internal` overload without default conflict, or 1 method `public async Task<bool> SetAvatarAsync(Account account, string avatarJsonData, CancellationToken cancellationToken = default, bool suppressMissingAssetsDialog = false)` ensures zero ambiguity for `(acc, json)` and `(acc, json, ct)`!). Inside it: `if (!suppressMissingAssetsDialog && missingIds.Length > 0) AppServices.Dialogs.OpenMissingAssetsDialog(account, missingIds);`.
   - `Account.SettingsApi.cs`: add `internal Task<bool> SetAvatarAsync(string avatarJsonData, bool suppressMissingAssetsDialog, CancellationToken cancellationToken = default)` (+2 lines, total 34).
   - `AccountApiDispatcher.Endpoints.cs:468`: pass `suppressMissingAssetsDialog: true` (**0 net lines!** Stays `477 / 485`).
4. `RAMAccount.lua`:
   - Normalize `IsLoopbackHost(host)` (trim whitespace, check `'127.0.0.1'`, `'localhost'`, `'::1'`, `'[::1]'`, plus `^127\.`).
   - Add `local function FormatHostForUrl(host)` wrapping bare IPv6 (`string.find(host, ':')` without leading `[` or trailing `]`) in `'[' .. host .. ']'` and use it in BOTH `BuildUrl` (`L45`) and `ConnectEvents` (`L198`).

- [ ] **Step 4: Run tests to verify GREEN & check branch floors (`>= 87%` on `RobloxSettingsApiService.cs`, `>= 77%` on `AccountApiDispatcher.Endpoints.cs`)**

Run:
```powershell
dotnet build "Shiro.sln" --no-incremental -warnaserror
dotnet test "tests/Shiro.Tests/Shiro.Tests.csproj" --no-build --filter "FullyQualifiedName~Phase3NetworkAndIntegrationBugfixTests|FullyQualifiedName~WebhookNotificationTests|FullyQualifiedName~AssetCommerceServiceTests|FullyQualifiedName~RobloxSettingsApiServiceTests|FullyQualifiedName~GatewayApiAndOpenApiTests|FullyQualifiedName~P1NetworkRegressionTests|FullyQualifiedName~Shiro.Tests.ArchitectureTests"
```
Expected: `0 Warning(s), 0 Error(s)` and 100% PASS.

- [ ] **Step 5: Commit**

```powershell
git add RAMAccount.lua src/Shiro/Services/Network/WebhookNotificationService.cs src/Shiro/Services/Network/AssetCommerceService.cs src/Shiro/Services/Network/RobloxSettingsApiService.cs src/Shiro/Domain/Account.SettingsApi.cs src/Shiro/Services/Network/AccountApiDispatcher.Endpoints.cs tests/Shiro.Tests/Phase3NetworkAndIntegrationBugfixTests.cs
git commit -m "fix(network): bracket IPv6 in RAMAccount.lua, guard Telegram 4096/Discord v10, sync reseller price, and suppress headless SetAvatar popup"
```

---

### Task 5: `RestClient` Pool/`Method.Delete`/Form-Signing, `AccountApiDispatcher` `RequestAborted`/Dynamic Port & `DiscordRpcService` Empty-ClientId Idle Guard

**Files:**
- Modify: `src/Shiro/Http/RestClient.cs:28-50, 130-140, 160-206` (`<= 485`; currently `359`)
- Modify: `src/Shiro/Services/Network/AccountApiDispatcher.cs:116-142, 308-321` (`<= 485`; currently `443`)
- Modify: `src/Shiro/Services/Network/DiscordRpcService.cs:97-108` (`<= 485`; currently `472`)
- Test: `tests/Shiro.Tests/Phase3HttpGatewayAndRpcTests.cs`

**Interfaces:**
- Consumes: `ApiDocumentationService.GetOpenApiJson(int port)` and `GetDocsHtml(int port)` (already exist!).
- Preserves: `DiscordRpcService.IsEnabled` (`DiscordRpcServiceBehaviorTests.cs:69`) and `VersionMetadata_ShouldBeSynchronizedAcrossProjectAndDocs` (`ArchitectureTests.cs:368-373` calling `GetOpenApiJson()` / `GetDocsHtml()` with default port).

- [ ] **Step 1: Write failing tests in `tests/Shiro.Tests/Phase3HttpGatewayAndRpcTests.cs`**

Create `tests/Shiro.Tests/Phase3HttpGatewayAndRpcTests.cs` (`[Collection("SerialGlobalState")]`) with 5 tests:
1. `RestClient_VaryingUserAgents_ShareDefaultPool_AndDeleteSupportsBody`: compares pooled `HttpClient` backing field (or `SharedHttpClient.PoolCount` before/after constructing `RestClient("https://example.com", "Agent-A")` and `RestClient("https://example.com", "Agent-B")`), asserting NO per-UserAgent pool leak (`ua_*` pools not created) while per-request `User-Agent` header still carries `"Agent-B"`, and asserts `RestClient.IsBodyMethod(Method.Delete) == true` and `DELETE` with JSON body preserves content.
2. `RestClient_SerializeRequestBodyForSigning_SupportsContentTypePropertyAndFormParams`: checks `AddStringBody("{\"a\":1}", "application/x-www-form-urlencoded")` / `AddParameter("k", "v")` produces deterministic HBA body string aligned with `CreateHttpRequestMessage`.
3. `AccountApiDispatcher_SwaggerAndDocs_UseConfiguredPortOrRequestHostPort`: calls `ProcessApiRequestAsync` for `/openapi.json` and `/docs` when `Host = "localhost:8123"` (or `AppServices.GeneralSettings` configured `WebServerPort = 8123`), asserting returned spec & HTML reference `http://localhost:8123` and `ws://localhost:8123/ws` rather than hardcoded `7963`.
4. `AccountApiDispatcher_BodyRead_RespectsContextRequestAbortedToken`: tests Kestrel body reader with a canceling stream + aborted `HttpContext.RequestAborted` token, verifying prompt `OperationCanceledException` cancellation instead of hanging (and `ArrayPool` buffer cleared).
5. `DiscordRpcService_Start_WhenClientIdEmpty_DoesNotSpawnReconnectWorkerLoop`: with `DiscordRpcEnabled = true` and `DiscordRpcClientId = ""`, calls `service.Start()` and `service.UpdateCurrentPresence()`, asserting `service.WorkerGenerationForTest == 0`, `!service.IsConnected`, and `IsEnabled == true` (zero infinite pipe-polling background task spawned when ClientId is blank).

- [ ] **Step 2: Run build/tests to verify RED**

Run:
```powershell
dotnet build "Shiro.sln" --no-incremental -warnaserror
dotnet test "tests/Shiro.Tests/Shiro.Tests.csproj" --no-build --filter "FullyQualifiedName~Phase3HttpGatewayAndRpcTests"
```
Expected: FAIL (`RestClient` creates `ua_*` pools & returns `IsBodyMethod(Delete) == false`; `/openapi.json` returns `7963`; `DiscordRpcService.Start()` increments `WorkerGenerationForTest > 0`).

- [ ] **Step 3: Implement fixes in `RestClient.cs`, `AccountApiDispatcher.cs`, and `DiscordRpcService.cs`**

1. `src/Shiro/Http/RestClient.cs`:
   - Line 34-35: use `_httpClient = Services.SharedHttpClient.Get();` (already sends per-request `User-Agent` header at `L120-124` and `L134` — check if `L134` skips per-request header when `UserAgent` set: allow per-request override if `request.Headers` contains `"User-Agent"`, else `UserAgent`).
   - Line 49: add `Method.Delete` -> `method is Method.Post or Method.Put or Method.Patch or Method.Delete;`.
   - Line 164 & 194-205 (`SerializeRequestBodyForSigning`): honor BOTH `request.ContentType` and `request.Headers["Content-Type"]`; if `isForm && request.Parameters.Count == 0 && request.StringBody != null` send/sign `StringBody` consistently (`new StringContent(request.StringBody, Encoding.UTF8, "application/x-www-form-urlencoded")` vs `FormUrlEncodedContent` when `Parameters.Count > 0`); return `string.Join("&", request.Parameters.Select(...))` only for `IsBodyMethod(request.Method)` (or if explicitly form-encoded).
2. `src/Shiro/Services/Network/AccountApiDispatcher.cs`:
   - Line 125: pass `context.RequestAborted` to `await req.Body.ReadAsync(buffer.AsMemory(), context.RequestAborted).ConfigureAwait(false)` and `Array.Clear(buffer, 0, Math.Min(totalRead, buffer.Length))` before returning to `ArrayPool`.
   - Lines 309-319: resolve gateway port from `req.Host` port if present (`Uri.TryCreate($"http://{req.Host}", ...)` / colon parse for `[::1]:port` or `host:port`) else `AppServices.GeneralSettings?.GetInt(AppSettingsKeys.WebServer.WebServerPort, 7963) ?? 7963` (clamped `1..65535`) and pass it to `ApiDocumentationService.GetOpenApiJson(docsPort)` & `GetDocsHtml(docsPort)`.
3. `src/Shiro/Services/Network/DiscordRpcService.cs`:
   - In `Start()` (`L101`): check `string.IsNullOrWhiteSpace(GetConfiguredClientId())` (`if (_isDisposed || !IsEnabled || string.IsNullOrWhiteSpace(GetConfiguredClientId()) || _isConnected || _cts != null) return;`). Keep `IsEnabled` property behavior intact (`<= 475` lines).

- [ ] **Step 4: Run tests to verify GREEN**

Run:
```powershell
dotnet build "Shiro.sln" --no-incremental -warnaserror
dotnet test "tests/Shiro.Tests/Shiro.Tests.csproj" --no-build --filter "FullyQualifiedName~Phase3HttpGatewayAndRpcTests|FullyQualifiedName~HttpClientModernizationTests|FullyQualifiedName~GatewayApiAndOpenApiTests|FullyQualifiedName~DiscordRpcServiceBehaviorTests|FullyQualifiedName~Shiro.Tests.ArchitectureTests"
```
Expected: `0 Warning(s), 0 Error(s)` and 100% PASS.

- [ ] **Step 5: Commit**

```powershell
git add src/Shiro/Http/RestClient.cs src/Shiro/Services/Network/AccountApiDispatcher.cs src/Shiro/Services/Network/DiscordRpcService.cs tests/Shiro.Tests/Phase3HttpGatewayAndRpcTests.cs
git commit -m "fix(http): share RestClient HTTP pool, pass RequestAborted and dynamic port in Gateway, and skip empty-ClientId Discord worker"
```

---

### Task 6: Domain & Storage P2 Hardening

**Files:**
- Modify: `src/Shiro/Domain/Account.cs:27-39` (`AsyncLocal<int>` suppression depth)
- Modify: `src/Shiro/Domain/Account.SessionState.CookieRotation.cs:78-92` (verify `EndSuppressDeferredSave` clamps >= 0 on `AsyncLocal<int>`)
- Modify: `src/Shiro/Services/Storage/AccountWorkflowService.cs:141-190` (`<= 485`; currently `475` — exception rollback in `ReLoginWithCookieAsync`, keeping `<= 481`)
- Modify: `src/Shiro/Services/Storage/AccountPersistenceService.VaultSecurity.cs:50-54` (observed exception handling on sync wrapper `ResetEncryption`)
- Modify: `src/Shiro/Services/Auth/AccountSessionMaintenanceService.cs:227-268` (`ParseUserPassCombo` & Windows reserved device names in `MakeSafeFileName`)
- Modify: `src/Shiro/Services/Storage/VaultKeyRecoveryService.cs:24-90` (`ZeroMemory` candidate buffers & shared DPAPI header constants if accessible cleanly)
- Modify: `src/Shiro/Services/Storage/StoragePaths.cs:450-455` (`ArrayPool.Return(rented, clearArray: true)`, `0` net lines)
- Test: `tests/Shiro.Tests/Phase3DomainAndStorageHardeningTests.cs`

**Interfaces:**
- Produces:
  - Cross-`await` thread-safe `Account.BeginSuppressDeferredSaveScope()`.
  - Exception-safe `AccountWorkflowService.ReLoginWithCookieAsync(...)` rolling back `SecurityToken`, `UserID`, `Username`, `AccountStatusIssue`, `CSRFToken`, `TokenSet`, and `Valid` when `ValidateSessionAsync()` throws.
  - Hardened `AccountSessionMaintenanceService.MakeSafeFileName(string value, string fallback)` neutralizing Windows reserved device names (`CON`, `PRN`, `AUX`, `NUL`, `COM1`–`COM9`, `LPT1`–`LPT9`, even with extensions like `CON.txt` or trailing spaces/dots).

- [ ] **Step 1: Write failing tests in `tests/Shiro.Tests/Phase3DomainAndStorageHardeningTests.cs`**

Create `tests/Shiro.Tests/Phase3DomainAndStorageHardeningTests.cs` (`[Collection("SerialGlobalState")]`) with 6 tests:
1. `Account_BeginSuppressDeferredSaveScope_FlowsAcrossAwaitThreadHops`: enters `using var scope = Account.BeginSuppressDeferredSaveScope()`, awaits `Task.Run(async () => { await Task.Yield(); ... })` (`ConfigureAwait(false)` hopping ThreadPool threads), checks `Account.IsDeferredSaveSuppressed` inside continuation AND after `scope.Dispose()`, asserting `true` inside scope and `false` after dispose on BOTH threads (verify non-corrupted depth).
2. `AccountWorkflowService_ReLoginWithCookieAsync_RestoresPreviousTokenWhenValidateThrows`: creates an `Account` with valid `previousToken`, stubs `ValidateSessionAsync` (via mocked HTTP throwing `HttpRequestException`), invokes `ReLoginWithCookieAsync`, asserts exception is handled/logged cleanly OR propagated while `account.SecurityToken == previousToken` and `account.Valid == wasValid`.
3. `AccountPersistenceService_ResetEncryptionSync_ObservesTaskExceptionsSafely`: invokes `ResetEncryption(true)` with `TestPasswordPromptAsync` throwing `InvalidOperationException`, awaits/waits briefly, asserts exception is observed and logged (`Warn`/`Error`) instead of surfacing on `TaskScheduler.UnobservedTaskException`.
4. `ParseUserPassCombo_PrefersColonWhenPasswordContainsPipe_WhileSupportingPipeCombos`: tests `"alice:p@ss|word"` -> `("alice", "p@ss|word")` AND `"alice|sec:ret:123"` -> `("alice", "sec:ret:123")` AND `"user\tpass|123"` -> `("user", "pass|123")` (disambiguated cleanly without breaking `P1AuthAndDomainRegressionTests.cs:195`!). *Algorithm rule:* scan index of first `':'`, `'|'`, `'\t'`; if the string uses `user:pass` format cleanly where `':'` comes **before** `'|'` or `'\t'` (i.e. earliest separator wins!), `"alice:p@ss|word"` splits at `':'` (pos 5) and `"alice|sec:ret:123"` splits at `'|'` (pos 5)!
5. `MakeSafeFileName_RejectsWindowsReservedDeviceNames_WithOrWithoutExtensions`: checks `"CON"`, `"prn"`, `"AUX.txt"`, `"nul.json"`, `"COM1.log"`, `"LPT9"`, `"foo . ."` — asserting none equals a bare Windows reserved device name (prefixes `_` or returns safe prefix like `_CON.txt`) and `"Normal_User.1"` passes untouched.
6. `VaultKeyRecoveryService_TryRecoverInProcess_ZerosCandidateBuffersAndDetectsKey`: verifies recovery succeeds on valid DPAPI/raw candidate and leaves zeroed/clean state without leaving corrupt candidate key active.

- [ ] **Step 2: Run build/tests to verify RED**

Run:
```powershell
dotnet build "Shiro.sln" --no-incremental -warnaserror
dotnet test "tests/Shiro.Tests/Shiro.Tests.csproj" --no-build --filter "FullyQualifiedName~Phase3DomainAndStorageHardeningTests"
```
Expected: Failures on cross-thread suppression (`AsyncLocal`), `ReLoginWithCookieAsync` leaving new token on exception, `ParseUserPassCombo("alice:p@ss|word")`, and `MakeSafeFileName("CON.txt")`.

- [ ] **Step 3: Implement fixes across the 7 files (respecting tight line budgets)**

1. `src/Shiro/Domain/Account.cs` & `Account.SessionState.CookieRotation.cs`: change `[ThreadStatic] private static int _suppressDeferredSaveDepth;` -> `private static readonly AsyncLocal<int> _suppressDeferredSaveDepth = new();`, expose safe increment/decrement clamping `Math.Max(0, _suppressDeferredSaveDepth.Value - 1)`.
2. `src/Shiro/Services/Storage/AccountWorkflowService.cs` (`475 -> <= 482`!): wrap `ReLoginWithCookieAsync`'s `ValidateSessionAsync()` block (`L147-188`) cleanly so if `ValidateSessionAsync()` throws, `try { ... } catch (Exception ex)` logs `_logger.Warn(...)` / restores `previousToken`, `previousUserId`, `previousUsername`, `previousIssue`, and `wasValid` in one shared `RestorePreviousSession()` local function (zero duplication, `<= +6` net lines, keeping branch floor >= `73%`!).
3. `src/Shiro/Services/Storage/AccountPersistenceService.VaultSecurity.cs`: replace `_ = ResetEncryptionAsync(manualReset);` with an observed helper (`ObserveResetEncryptionAsync`) wrapping `await ResetEncryptionAsync(manualReset).ConfigureAwait(false)` in `try/catch (Exception ex) { _logger.Error(...); }` + dialog notification if UI available.
4. `src/Shiro/Services/Auth/AccountSessionMaintenanceService.cs`:
   - `ParseUserPassCombo`: pick the **earliest** positive index among `'|'`, `'\t'`, `':'` (rather than always preferring `'|'` anywhere inside a password after `':'`!).
   - `MakeSafeFileName`: trim trailing `' '` as well as `'.'`, and check base stem (before first `'.'`) against `ReservedWindowsDeviceNames` (`CON`, `PRN`, `AUX`, `NUL`, `COM1`..`COM9`, `LPT1`..`LPT9`, ordinal ignore-case); prefix `"_"` if reserved.
5. `src/Shiro/Services/Storage/VaultKeyRecoveryService.cs`: `ZeroMemory` `raw`, `unwrapped`, and intermediate key `blob` in `finally`; reuse `StoragePaths` DPAPI header/entropy internal constants (expose `internal` constants on `StoragePaths.VaultKey.cs` if under budget `[412/485]`) instead of duplicating bytes; clone 32-byte raw array (`raw.AsSpan().ToArray()`) instead of aliasing.
6. `src/Shiro/Services/Storage/StoragePaths.cs:452`: change `ArrayPool<byte>.Shared.Return(rented);` -> `ArrayPool<byte>.Shared.Return(rented, clearArray: true);` (`0` net lines, stays `480 / 485`).

- [ ] **Step 4: Run tests to verify GREEN**

Run:
```powershell
dotnet build "Shiro.sln" --no-incremental -warnaserror
dotnet test "tests/Shiro.Tests/Shiro.Tests.csproj" --no-build --filter "FullyQualifiedName~Phase3DomainAndStorageHardeningTests|FullyQualifiedName~P1AuthAndDomainRegressionTests|FullyQualifiedName~AccountWorkflowServiceBehaviorTests|FullyQualifiedName~AccountManagerLifecycleAndAccountsBehaviorTests|FullyQualifiedName~VaultKeyRecoveryServiceTests|FullyQualifiedName~CoreCoverageExpansionTests|FullyQualifiedName~Shiro.Tests.ArchitectureTests"
```
Expected: `0 Warning(s), 0 Error(s)` and 100% PASS.

- [ ] **Step 5: Commit**

```powershell
git add src/Shiro/Domain/Account.cs src/Shiro/Domain/Account.SessionState.CookieRotation.cs src/Shiro/Services/Storage/AccountWorkflowService.cs src/Shiro/Services/Storage/AccountPersistenceService.VaultSecurity.cs src/Shiro/Services/Auth/AccountSessionMaintenanceService.cs src/Shiro/Services/Storage/VaultKeyRecoveryService.cs src/Shiro/Services/Storage/StoragePaths.cs src/Shiro/Services/Storage/StoragePaths.VaultKey.cs tests/Shiro.Tests/Phase3DomainAndStorageHardeningTests.cs
git commit -m "fix(storage): make suppress-save AsyncLocal, rollback failed cookie relogin, sanitize reserved device filenames, and zero buffers"
```

---

### Task 7: Launch & FastLogin P2 Hardening

**Files:**
- Modify: `src/Shiro/Services/Launch/LaunchCoordinator.Queue.cs:20-135` (`<= 485`; currently `292`; partial total `LaunchCoordinator*` currently `1127 / 1150` -> keep `<= +8` net lines!)
- Modify: `src/Shiro/Services/Auth/FastLoginScripts.cs:15-50` (`<= 485`; currently `455`)
- Test: `tests/Shiro.Tests/Phase3LaunchAndFastLoginHardeningTests.cs`

**Interfaces:**
- Consumes: `RobloxRateLimitCoordinator.ResolveProxyScope(Account)` and `IsBackingOffCategory` / `GetRemainingBackoff` / `WaitForBackoffAsync` / `RecordRateLimitHit` / `RecordSuccess` overloads taking `proxyScope`.

- [ ] **Step 1: Write failing tests in `tests/Shiro.Tests/Phase3LaunchAndFastLoginHardeningTests.cs`**

Create `tests/Shiro.Tests/Phase3LaunchAndFastLoginHardeningTests.cs` (`[Collection("SerialGlobalState")]`) with 3 tests:
1. `LaunchCoordinator_ProxyAccount429_IsolatesBackoffToAccountProxyScope`: sets `UseProxies = true`, records a 429 (`RateLimited`) launch outcome for Account A on `"http://10.0.0.1:8080"`, then launches Account B on `"http://10.0.0.2:8080"` and Account C direct (no proxy), asserting `RobloxRateLimitCoordinator.Instance.IsBackingOffCategory(RateLimitCategory.Authentication, proxyScopeA) == true` while proxy B and `"direct"` remain `false` and launch immediately without waiting for proxy A's backoff.
2. `FastLoginScripts_GetLoginPageEnhancementScript_GuardsOriginToRobloxDomains`: inspects generated JS script verifying `location.hostname` check restricts injection strictly to `roblox.com` / `.roblox.com` (`endsWith('.roblox.com')`) so third-party CAPTCHA/OAuth origins return early without injecting CSS or hooking inputs.
3. `FastLoginScripts_GetLoginPageEnhancementScript_BoundsAndClearsSetInterval`: verifies `setInterval` handle is stored, bounded by attempt count or cleared when hooked AND cleaned up on `pagehide`/`beforeunload` (`clearInterval`), never running unbounded forever.

- [ ] **Step 2: Run build/tests to verify RED**

Run:
```powershell
dotnet build "Shiro.sln" --no-incremental -warnaserror
dotnet test "tests/Shiro.Tests/Shiro.Tests.csproj" --no-build --filter "FullyQualifiedName~Phase3LaunchAndFastLoginHardeningTests"
```
Expected: Tests FAIL (`LaunchCoordinator.Queue` hits `"direct"` backoff globally; `FastLoginScripts` lacks origin guard and `clearInterval`).

- [ ] **Step 3: Implement proxy-scoped backoff & origin-guarded interval cleanup**

1. `src/Shiro/Services/Launch/LaunchCoordinator.Queue.cs`:
   - Resolve `string proxyScope = RobloxRateLimitCoordinator.ResolveProxyScope(request.Account);` once at top of `ExecuteAttemptWithRetriesAsync` (`+1` line).
   - Pass `proxyScope` into all 7 `RobloxRateLimitCoordinator.Instance` calls (`IsBackingOffCategory`, both `GetRemainingBackoff`, both `WaitForBackoffAsync`, `RecordRateLimitHit`, `RecordSuccess`) — `0` extra lines (`1128 / 1150` partial total!).
2. `src/Shiro/Services/Auth/FastLoginScripts.cs`:
   - Wrap `GetLoginPageEnhancementScript()` body with `const h = (location.hostname || '').toLowerCase(); if (h !== 'roblox.com' && !h.endsWith('.roblox.com')) return;` (wrapped inside `(() => { ... })()` so `return;` is valid JS in `AddScriptToExecuteOnDocumentCreated`!).
   - Track `let attempts = 0; const timerId = setInterval(() => { hookPass(); if (hooked || ++attempts >= 50) clearInterval(timerId); }, 400);` and `window.addEventListener('pagehide', () => clearInterval(timerId), { once: true });` (`<= +6` lines -> `<= 462 / 485`).

- [ ] **Step 4: Run tests to verify GREEN**

Run:
```powershell
dotnet build "Shiro.sln" --no-incremental -warnaserror
dotnet test "tests/Shiro.Tests/Shiro.Tests.csproj" --no-build --filter "FullyQualifiedName~Phase3LaunchAndFastLoginHardeningTests|FullyQualifiedName~LaunchCoordinator|FullyQualifiedName~P0CookieGuardAndLaunchCoordinatorTests|FullyQualifiedName~P1LaunchRegressionTests|FullyQualifiedName~P1AuthAndDomainRegressionTests|FullyQualifiedName~Shiro.Tests.ArchitectureTests"
```
Expected: `0 Warning(s), 0 Error(s)` and 100% PASS.

- [ ] **Step 5: Commit**

```powershell
git add src/Shiro/Services/Launch/LaunchCoordinator.Queue.cs src/Shiro/Services/Auth/FastLoginScripts.cs tests/Shiro.Tests/Phase3LaunchAndFastLoginHardeningTests.cs
git commit -m "fix(launch): propagate per-account proxyScope backoff in LaunchCoordinator and guard FastLoginScripts origin/timer"
```

---

### Task 8: UI & ViewModel P2 Bugfixes (+ Removal of Dead `AvatarUrl`, `ToastItem.IconKind`, `ToastItem.Glyph`)

**Files:**
- Modify: `src/Shiro/ViewModels/AccountViewModel.Avatar.cs:20-347` (`<= 485`; currently `347`)
- Modify: `src/Shiro/Services/Ui/ToastService.cs:100-120, 190-245` (`<= 485`; currently `374`)
- Modify: `src/Shiro/ViewModels/Dialogs/SettingsViewModel.cs:255-265, 355-366` (**CRITICAL: `483 / 485` — MUST be `<= 485`, 0 net lines!**)
- Modify: `src/Shiro/ViewModels/Dialogs/BulkImportViewModel.cs:165, 240-245` (`<= 485`; currently `292`)
- Modify: `src/Shiro/Services/Windows/ClipboardHelper.cs:18-38` (`<= 485`; currently `77`)
- Modify affected tests:
  - `tests/Shiro.Tests/P3FinalFixTests.cs:94-135` (remove obsolete `AvatarUrl` assertions; add/keep `AvatarImage` assertions)
  - `tests/Shiro.Tests/UiUxUpgradeTests.cs:45-60` (replace removed `Glyph`/`IconKind` assertions with `IconGeometry` & `ActionCommand` assertions)
- Test: `tests/Shiro.Tests/Phase3UiAndViewModelBugfixTests.cs`

**Interfaces:**
- Produces:
  - Per-instance `AccountViewModel._avatarGeneration` (instead of global `static`) plus global `InvalidateAllAvatars()` only when clearing the whole disk avatar cache.
  - Coalesced toast updates `ActionCommand` when newer `onAction` is supplied.
  - `SettingsViewModel` clamps `WebServerPort` (`1..65535`) on both `Load()` and `Save()`.
  - `BulkImportViewModel` deduplicates `queuedForFastLogin` (`HashSet<Account>`) cleanly preserving insertion order.
  - `ClipboardHelper.SetClipboardTextAsync` dispatches onto `Dispatcher.UIThread` when `!CheckAccess()`.

- [ ] **Step 1: Write failing tests in `tests/Shiro.Tests/Phase3UiAndViewModelBugfixTests.cs` & update `P3FinalFixTests.cs` / `UiUxUpgradeTests.cs`**

1. Update `tests/Shiro.Tests/P3FinalFixTests.cs:96-132`: remove the 3 obsolete `AvatarUrl` asserts (`L97, 127, 130`), keep `AvatarImage` asserts (verify non-null after fetch, `null` + `"AvatarImage"` raised on `ClearAvatarCache`).
2. Update `tests/Shiro.Tests/UiUxUpgradeTests.cs:47-48, 56-57`: replace obsolete `warnToast.Glyph` / `warnToast.IconKind` / `errToast.Glyph` / `errToast.IconKind` assertions with `Assert.NotNull(warnToast.IconGeometry)` / `Assert.NotNull(errToast.IconGeometry)`.
3. Create `tests/Shiro.Tests/Phase3UiAndViewModelBugfixTests.cs` (`[Collection("SerialGlobalState")]`) with 5 tests:
   - `AccountViewModel_InvalidateAvatar_OnOneAccount_DoesNotInvalidateOtherAccountAvatar`: loads/caches valid PNG `AvatarImage` on two separate `AccountViewModel` instances (`acc1`, `acc2`), calls `acc1.InvalidateAvatar()`, asserts `acc1.AvatarImage == null` while `acc2.AvatarImage != null` remains cached (and `ClearAvatarCache()` still invalidates all accounts!).
   - `ToastService_CoalescedToast_UpdatesActionCommandToLatestOnAction`: shows two identical actionable toasts (`"Title"`, `"Msg"`, `"Retry"`) with callback #1 then callback #2, executes `toast.ActionCommand.Execute(null)`, asserts callback #2 ran (`1`) and callback #1 did NOT (`0`).
   - `SettingsViewModel_LoadAndSave_ClampsOutOfRangeWebServerPort`: tests `WebServerPort = 0` and `99999`, asserts `Load()` and `Save()` clamp `TargetPort` / saved `_settings.WebServerPort` to `1` and `65535` (keeping existing valid ports untouched).
   - `BulkImportViewModel_DuplicateUserPassLines_QueuesEachAccountOnceForFastLogin`: feeds duplicate `"dupuser:pass1\ndupuser:pass2"` lines to `BulkImportViewModel`, asserts mocked `IDialogService` receives `OpenFastLoginDialog(account)` once (`QueuedCount == 1`) rather than `OpenFastLoginDialogBatch` with 2 duplicate entries.
   - `ClipboardHelper_SetClipboardTextAsync_MarshalsOffUiThreadCleanly`: invokes `SetClipboardTextAsync("hello")` from a background `Task.Run` when desktop lifetime is null / headless, verifies clean non-throwing async completion and `HasUiThreadAccess` / `CheckAccess` path in source.

- [ ] **Step 2: Run build/tests to verify RED**

Run:
```powershell
dotnet build "Shiro.sln" --no-incremental -warnaserror
dotnet test "tests/Shiro.Tests/Shiro.Tests.csproj" --no-build --filter "FullyQualifiedName~Phase3UiAndViewModelBugfixTests"
```
Expected: Tests FAIL (`acc2.AvatarImage` cleared, callback #1 ran instead of #2, `WebServerPort = 99999` saved un-clamped, `BulkImportViewModel` queued `2` items).

- [ ] **Step 3: Implement UI & ViewModel fixes (watching `SettingsViewModel.cs` `483/485` limit!)**

1. `src/Shiro/ViewModels/AccountViewModel.Avatar.cs`:
   - Remove dead `_avatarUrl` and `public string AvatarUrl` property (`L25, L106-135`) and remove `"AvatarUrl"` `RaisePropertyChanged` calls.
   - Replace `_avatarCacheGeneration` usage in instance `InvalidateAvatar()` with per-instance `private int _instanceAvatarGeneration;` (`Interlocked.Increment(ref _instanceAvatarGeneration)`), keeping `static int _globalAvatarCacheGeneration` ONLY for static `InvalidateAllCachedAvatars()` (called by `SettingsSideEffects.ClearAvatarCache`), and checking BOTH `_instanceAvatarGeneration` and `_globalAvatarCacheGeneration` in async avatar load continuations!
2. `src/Shiro/Services/Ui/ToastService.cs`:
   - Remove dead `IconKind` (`L104-110`) and `Glyph` (`L112-118`) from `ToastItem`.
   - Extract helper `BuildActionCommand(ToastItem toast, string cleanAction, Action? onAction)` and invoke it in BOTH new-toast creation (`L213`) AND the `else { toast = coalescedToast; ... }` branch (`L240-243`) when `onAction != null` (or clearing `ActionCommand = null` if `string.IsNullOrWhiteSpace(cleanAction)`). Also update `ActionCommand` setter notification cleanly (`ObservableProperty` or `private ActionCommand?` with `PropertyChanged` if bound).
3. `src/Shiro/ViewModels/Dialogs/SettingsViewModel.cs` (**0 net lines -> `483 / 485`**):
   - Line 260: `WebServerPort = Math.Clamp(_settings.WebServerPort, 1, 65535);` (`0` net lines!).
   - Line 359: `int targetPort = WebServerPort.HasValue ? Math.Clamp((int)WebServerPort.Value, 1, 65535) : prevPort;` (`0` net lines!).
4. `src/Shiro/ViewModels/Dialogs/BulkImportViewModel.cs`:
   - Alongside `var queuedForFastLogin = new List<Account>();`, add `var queuedSet = new HashSet<Account>();` and gate `L243` with `if (queuedSet.Add(targetAcc)) queuedForFastLogin.Add(targetAcc);`.
5. `src/Shiro/Services/Windows/ClipboardHelper.cs`:
   - Inside `SetClipboardTextAsync`: check `Avalonia.Threading.Dispatcher.UIThread.CheckAccess()`; if false, marshal `desktop.MainWindow.Clipboard.SetTextAsync(text)` through `Avalonia.Threading.Dispatcher.UIThread.InvokeAsync(...)` instead of calling UI-affine `Clipboard` directly on a background thread; fix doc comment accurately.

- [ ] **Step 4: Run tests to verify GREEN**

Run:
```powershell
dotnet build "Shiro.sln" --no-incremental -warnaserror
dotnet test "tests/Shiro.Tests/Shiro.Tests.csproj" --no-build --filter "FullyQualifiedName~Phase3UiAndViewModelBugfixTests|FullyQualifiedName~P3FinalFixTests|FullyQualifiedName~UiUxUpgradeTests|FullyQualifiedName~SettingsViewModelTests|FullyQualifiedName~BulkImportViewModelTests|FullyQualifiedName~SettingsAndWorkflowTask3Tests|FullyQualifiedName~Shiro.Tests.ArchitectureTests"
```
Expected: `0 Warning(s), 0 Error(s)`, `SettingsViewModel.cs <= 485`, and 100% PASS.

- [ ] **Step 5: Commit**

```powershell
git add src/Shiro/ViewModels/AccountViewModel.Avatar.cs src/Shiro/Services/Ui/ToastService.cs src/Shiro/ViewModels/Dialogs/SettingsViewModel.cs src/Shiro/ViewModels/Dialogs/BulkImportViewModel.cs src/Shiro/Services/Windows/ClipboardHelper.cs tests/Shiro.Tests/P3FinalFixTests.cs tests/Shiro.Tests/UiUxUpgradeTests.cs tests/Shiro.Tests/Phase3UiAndViewModelBugfixTests.cs
git commit -m "fix(ui): scope avatar invalidation per account, update coalesced toast action, clamp WebServerPort, dedup BulkImport queue, and marshal clipboard"
```

---

### Task 9: Eliminate All 9 Hidden `async void` Lambdas, Fix `AvaloniaDialogService` & `MainWindow` Master Password UX, and Add `ArchitectureTests` Ratchet

**Files:**
- Modify: `src/Shiro/Services/Ui/AppDispatcher.cs:61-100` (`<= 485`; currently `301` — add `PostTask(Func<Task>, Action<Exception>? onException = null)`)
- Modify: `src/Shiro/Services/Ui/AvaloniaDialogService.cs:26-52, 120-260` (`<= 485`; currently `283`)
- Modify: `src/Shiro/AvaloniaUI/Views/MainWindow.axaml.cs:81-183` (`<= 485`; currently `376`; partial total currently `734 / 1150`)
- Modify: `tests/Shiro.Tests/ArchitectureTests.Ratchets.cs:13-27, 94-113` (`<= 550`; currently `213`)
- Test: `tests/Shiro.Tests/Phase3AsyncSafetyAndDialogTests.cs`

**Interfaces:**
- Produces:
  - `AppDispatcher.PostTask(Func<Task> asyncAction, string? contextLabel = null)` (`public static void`) — invokes `PostWithPriority(() => _ = ExecuteObservedAsync(asyncAction, contextLabel), DispatcherPriority.Normal)` so **`CustomDispatcher` in `AvaloniaDialogServiceBehaviorTests.cs:53-73` still receives all 10 posted actions cleanly**, with ZERO `async void` lambdas!
  - `ArchitectureTests.HiddenAsyncVoidLambdas_ShouldBeZero()` & `AsyncVoidMethods_AcrossSourceFiles_ShouldOnlyExistInRelayCommand()`.

- [ ] **Step 1: Write failing tests in `tests/Shiro.Tests/Phase3AsyncSafetyAndDialogTests.cs` & add failing ratchet in `ArchitectureTests.Ratchets.cs`**

1. In `tests/Shiro.Tests/ArchitectureTests.Ratchets.cs`:
   - Add `[Fact] public void HiddenAsyncVoidLambdas_ShouldBeZero()`: scans all `SourceScan.AppSourceFiles(RepoRoot)` (after `SourceScan.StripCommentsAndStringLiterals`) for:
     - `@"\+=\s*async\b"`
     - `@"\b(?:AppDispatcher|Dispatcher\.UIThread)\.Post(?:Background)?\s*\(\s*async\b"`
     - `@"\bnew\s+Action(?:<[^>]+>)?\s*\(\s*async\b"`
     - `@"\.Register\s*\(\s*async\b"`
     asserting empty offenders across the entire `src/Shiro/` codebase!
   - Add `[Fact] public void ExplicitAsyncVoid_AcrossAllSourceFiles_ShouldOnlyExistInRelayCommand()`: allows `async void` ONLY in `ViewModels/RelayCommand.cs` (count `== 2`, for `ICommand.Execute`).
2. Create `tests/Shiro.Tests/Phase3AsyncSafetyAndDialogTests.cs` (`[Collection("SerialGlobalState")]`) with 4 tests:
   - `AppDispatcher_PostTask_RunsAsyncBodyCleanly_AndObservesFaultsWithoutAsyncVoid`: tests both `CustomDispatcher` compatibility and fault observation (exceptions thrown inside `asyncAction` are logged to `AppServices.Logger`, never becoming unhandled synchronization-context exceptions).
   - `AvaloniaDialogService_ConfirmAndAlertAsync_CheckIsAvaloniaUiReadyBeforeDispatcher`: verifies `ConfirmAsync` / `AlertAsync` check `MessageBoxDialog.IsAvaloniaUiReady()` and execute cleanly without touching `Dispatcher.UIThread` when Avalonia lifetime is uninitialized.
   - `AvaloniaDialogService_SignInFastLoginAsync_CancellationClosesOpenDialog` (`[AvaloniaFact]`): starts `SignInFastLoginAsync` on headless Avalonia UI with an already-canceled or mid-flight `cts.Cancel()`, pumps `Dispatcher.UIThread.RunJobs()`, asserts task cancels cleanly AND no orphaned `FastLoginDialog` remains open.
   - `MainWindow_UnlockMasterPassword_AllowsRetryBeforeRecovery_AndZerosPasswordBytes` (`[AvaloniaFact]` / helper test): extracts `internal static int TryPromptAndUnlockMasterPasswordAsync(...)` helper (or tests up to 3 wrong attempts allowing retry on attempt 1/2 before falling back to backup prompt after 3 failures or cancel, and checks `CryptographicOperations.ZeroMemory` clears both UTF-8 password bytes and SHA-512 hash bytes in `finally` — note check existing localization keys so no unlocalized strings slip in!).

- [ ] **Step 2: Run build/tests to verify RED**

Run:
```powershell
dotnet build "Shiro.sln" --no-incremental -warnaserror
dotnet test "tests/Shiro.Tests/Shiro.Tests.csproj" --no-build --filter "FullyQualifiedName~HiddenAsyncVoidLambdas_ShouldBeZero|FullyQualifiedName~Phase3AsyncSafetyAndDialogTests"
```
Expected: `HiddenAsyncVoidLambdas_ShouldBeZero` FAILS reporting `AvaloniaUI/Views/MainWindow.axaml.cs` and `Services/Ui/AvaloniaDialogService.cs` (the 9 hidden `async void` lambdas!), and new tests fail before fixes.

- [ ] **Step 3: Eliminate all 9 hidden `async void` lambdas and fix `AvaloniaDialogService` + `MainWindow`**

1. `src/Shiro/Services/Ui/AppDispatcher.cs`:
   - Add `public static void PostTask(Func<Task> asyncAction, string? contextLabel = null)` routing through `PostWithPriority(() => _ = RunObservedPostTaskAsync(asyncAction, contextLabel), DispatcherPriority.Normal)` where `RunObservedPostTaskAsync` runs `await asyncAction().ConfigureAwait(false)` inside `try/catch (Exception ex) { AppServices.Logger?.Error($"[{contextLabel ?? nameof(PostTask)}] {ex}"); }` (preserves `AvaloniaDialogServiceBehaviorTests.cs` `CustomDispatcher` count == `10`!).
2. `src/Shiro/Services/Ui/AvaloniaDialogService.cs`:
   - Replace all 8 `AppDispatcher.Post(async () => ...)` calls (`L130, 150, 166, 182, 198, 214, 231, 248`) with shared helper `OpenModalDialogAsync<T>(Func<T> factory, string label) where T : Avalonia.Controls.Window` passed to `AppDispatcher.PostTask(() => OpenModalDialogAsync(...), ...)` — eliminates boilerplate duplication AND all 8 hidden `async void` lambdas!
   - Guard `ConfirmAsync` (`L26`) and `AlertAsync` (`L37`) with `if (!MessageBoxDialog.IsAvaloniaUiReady()) return ...` matching `ShowMessageBoxAsync` (`L87`).
   - Fix `SignInFastLoginAsync` (`L120-146`): check `if (cancellationToken.IsCancellationRequested) return Task.FromCanceled<Account?>(cancellationToken);`, hold `FastLoginDialog? activeDialog = null;`, register cancellation callback that sets `tcs.TrySetCanceled(cancellationToken)` AND dispatches `if (activeDialog != null) AppDispatcher.Post(() => activeDialog.Close(false));`, check `cancellationToken.IsCancellationRequested` right after creating `dialog` on UI thread before showing, and dispose `reg` in `tcs.Task.ContinueWith`.
3. `src/Shiro/AvaloniaUI/Views/MainWindow.axaml.cs`:
   - Replace `Opened += async (s, e) => { ... }` (`L81`) with `Opened += OnMainWindowOpened;` where `private void OnMainWindowOpened(object? sender, EventArgs e) => _ = RunObservedStartupPromptsAsync();` (`async Task RunObservedStartupPromptsAsync()` with full `try/catch`).
   - Inside unlock flow (`L134-166`): allow up to `3` attempts (`const int MaxMasterPasswordAttempts = 3`) in a loop when wrong non-empty password entered (`pass` fails `LoadFromStore(hash)` -> show `MainWindow.WrongPasswordTitle`/`IncorrectMasterPassword` toast and re-prompt; exit loop immediately if empty or `"/UC"` or `LoadFromStore(hash)` succeeds); allocate `byte[]? passBytes = null; byte[]? hash = null;` cleared unconditionally via `CryptographicOperations.ZeroMemory` inside `try/finally` on every attempt!
   - Remove redundant fallback block at `L90-95` (`string title = LocalizationService.Get("tos_disclaimer_title"); string msg = LocalizationService.Get("tos_disclaimer_message");`).

- [ ] **Step 4: Run tests to verify GREEN**

Run:
```powershell
dotnet build "Shiro.sln" --no-incremental -warnaserror
dotnet test "tests/Shiro.Tests/Shiro.Tests.csproj" --no-build --filter "FullyQualifiedName~Phase3AsyncSafetyAndDialogTests|FullyQualifiedName~AvaloniaDialogServiceBehaviorTests|FullyQualifiedName~P3CompositionAndArchitectureTests|FullyQualifiedName~LocalizationIntegrityTests|FullyQualifiedName~Shiro.Tests.ArchitectureTests"
```
Expected: `0 Warning(s), 0 Error(s)` and 100% PASS (`HiddenAsyncVoidLambdas_ShouldBeZero` and `OpenDialogMethods_PostExpectedActionsToDispatcher` both pass!).

- [ ] **Step 5: Commit**

```powershell
git add src/Shiro/Services/Ui/AppDispatcher.cs src/Shiro/Services/Ui/AvaloniaDialogService.cs src/Shiro/AvaloniaUI/Views/MainWindow.axaml.cs tests/Shiro.Tests/ArchitectureTests.Ratchets.cs tests/Shiro.Tests/Phase3AsyncSafetyAndDialogTests.cs
git commit -m "fix(async): eliminate all 9 hidden async-void lambdas, enforce ArchitectureTests ratchet, fix dialog cancellation, and add password retry/zeroing"
```

---

### Task 10: CI Coverage Gate Hardening (`Check-Coverage.ps1`, `coverage.runsettings`, `coverage-floors.json` & Unit Tests)

**Files:**
- Modify: `scripts/Check-Coverage.ps1:28-32`
- Modify: `tests/coverage.runsettings:10`
- Modify: `tests/coverage-floors.json` (add measured branch floors for newly included non-UI files `Services/Windows/RobloxProcess.cs` & `Services/Windows/RobloxWatcher.cs`, or update any hotspot touched in Tasks 1–9 cleanly upward never downward)
- Modify: `tests/Shiro.Tests/ArchitectureTests.cs:336-350` (`ApprovedCoverageExclusions`)
- Modify: `tests/Shiro.Tests/ArchitectureTests.Ratchets.cs:50-92` (include new hotspot entries in `requiredHotspots` check if added to `coverage-floors.json`)
- Create: `tests/Shiro.Tests/Phase3CoreCoverageHardeningTests.cs` (`<= 550` lines)

**Interfaces:**
- Produces:
  - Expanded Core Security/Persistence/Lifecycle regex in `scripts/Check-Coverage.ps1` covering: `Cryptography.cs`, `VaultKey.cs`, `AccountDataSerializer.cs`, `AccountStore.cs`, `StoragePaths.cs`, `WebAuthPolicy.cs`, `LogRedactor.cs`, `AccountFileDecoder.cs`, `CryptoHelper.cs`, `VaultExportService.cs`, `VaultKeyRecoveryService.cs`, `InMemorySecretProtector.cs`, `RobloxHbaService.cs`, `RobloxHbaService.Binding.cs`, `RobloxHbaService.Network.cs`, `RobloxHbaService.BrowserHooks.cs`, `RobloxDirectLauncher.CookieGuard.cs`, `RobloxDirectLauncher.DeElevation.cs`, `ShutdownCoordinator.cs`.
  - Un-excluded non-UI runtime monitors `**/RobloxProcess.cs` and `**/RobloxWatcher.cs` in `tests/coverage.runsettings` & `ArchitectureTests.ApprovedCoverageExclusions`.

- [ ] **Step 1: Measure baseline coverage of candidate Core files + un-excluded `RobloxProcess.cs`/`RobloxWatcher.cs`, then write missing unit tests in `tests/Shiro.Tests/Phase3CoreCoverageHardeningTests.cs`**

1. Inspect uncovered branches in `RobloxProcess.cs`, `RobloxWatcher.cs`, `RobloxHbaService*.cs`, `RobloxDirectLauncher.CookieGuard.cs`, `RobloxDirectLauncher.DeElevation.cs`, `VaultExportService.cs`, `VaultKeyRecoveryService.cs`, `CryptoHelper.cs`, `InMemorySecretProtector.cs`, `ShutdownCoordinator.cs` using an ephemeral Cobertura run (`coverage/` deleted immediately afterward).
2. Create `tests/Shiro.Tests/Phase3CoreCoverageHardeningTests.cs` (`[Collection("SerialGlobalState")]`, `<= 550` lines) covering remaining uncovered real branches (zero dummy/no-op tests!):
   - `RobloxWatcher` testable pure/internal seams (`PruneExitedMetadataUnderLock`, `ClassifyProcessCommandLine` / `ShouldCloseForMemoryThreshold`, `ProcessMemoryCheck` edge branches).
   - `RobloxProcess` testable log-polling/event-raising & disposal edge paths (`ReadLogFile`, `StopMonitoring` idempotency, negative/invalid PID handling).
   - `CryptoHelper`, `VaultExportService`, `RobloxHbaService*`, and `RobloxDirectLauncher.DeElevation` / `CookieGuard` edge branches (corrupt envelopes, null/empty tokens, DPAPI fallback boundaries) so the expanded 19-file Core Security/Persistence metric stays strictly **`>= 92.0%`**.

- [ ] **Step 2: Run Coverage Gate with proposed `Check-Coverage.ps1` / `coverage.runsettings` changes to verify RED/metrics**

Update `Check-Coverage.ps1`, `coverage.runsettings`, and `ArchitectureTests.cs:ApprovedCoverageExclusions`:
- Remove `**/RobloxProcess.cs`, `**/RobloxWatcher.cs` from `tests/coverage.runsettings:10` and `tests/Shiro.Tests/ArchitectureTests.cs:339`.
- Update `scripts/Check-Coverage.ps1:30` regex to match all 19 Core Security/Persistence/Lifecycle files listed above.
- Run Release build + Coverage collection + `./scripts/Check-Coverage.ps1`: if any file pulls Core below `92.0%` or misses branch/line thresholds, strengthen real unit tests in `Phase3CoreCoverageHardeningTests.cs` (without lowering any gate threshold!).
- Optionally record realistic measured non-regressing floors for `Services/Windows/RobloxProcess.cs` and `Services/Windows/RobloxWatcher.cs` in `tests/coverage-floors.json` & `ArchitectureTests.Ratchets.cs` (note: do NOT exceed 550 lines in any test file!).

- [ ] **Step 3: Run full Coverage Gate & Architecture Tests to verify GREEN**

Run:
```powershell
dotnet build "Shiro.sln" --configuration Release --no-incremental -warnaserror
if (Test-Path "coverage") { Remove-Item -Recurse -Force "coverage" }
dotnet test "tests/Shiro.Tests/Shiro.Tests.csproj" --configuration Release --no-build --collect:"XPlat Code Coverage" --settings "tests/coverage.runsettings" --results-directory "coverage" --logger "console;verbosity=minimal"
./scripts/Check-Coverage.ps1 -CoverageDir "coverage" -MinOverall 75.0 -MinBranch 60.0 -MinCore 92.0
$covExit = $LASTEXITCODE
Remove-Item -Recurse -Force "coverage"
exit $covExit
```
Expected: `Overall >= 75.0%`, `Branch >= 60.0%`, `Core (19 files) >= 92.0%`, all `coverage-floors.json` entries PASS, and `Check-Coverage.ps1` exits `0`.

- [ ] **Step 4: Commit**

```powershell
git add scripts/Check-Coverage.ps1 tests/coverage.runsettings tests/coverage-floors.json tests/Shiro.Tests/ArchitectureTests.cs tests/Shiro.Tests/ArchitectureTests.Ratchets.cs tests/Shiro.Tests/Phase3CoreCoverageHardeningTests.cs
git commit -m "test(coverage): un-exclude RobloxProcess/RobloxWatcher, expand 19-file Core Security gate, and add edge-branch tests"
```

---

### Final Release Verification Gate (After All 10 Tasks Complete)

- [ ] **Step 1: Full clean Release restore, `-warnaserror` build, output hygiene check, and full Release test suite**

Run inside worktree:
```powershell
dotnet restore "Shiro.sln" --runtime win-x64 /p:SelfContained=true --locked-mode
dotnet build "Shiro.sln" --configuration Release --no-restore --no-incremental -warnaserror
dotnet test "tests/Shiro.Tests/Shiro.Tests.csproj" --configuration Release --no-build --logger "console;verbosity=normal"
```
Expected:
- `Build succeeded. 0 Warning(s), 0 Error(s)`
- **100% of unit, integration, UI, and architecture tests PASS (`Failed: 0, Skipped: 0`)** while live host `Shiro.exe`/`RobloxPlayerBeta.exe` stay untouched.
- Verify no duplicate `Resources/` folder copied into clean `build/artifacts/Release/.../win-x64/` and verify compressed `Shiro.Resources.Flags.zip` size inside `Shiro.dll` (~575 KB vs ~1.62 MB raw SVGs previously).

- [ ] **Step 2: Run Coverage Gate (matching CI release workflow)**

Run:
```powershell
if (Test-Path "coverage") { Remove-Item -Recurse -Force "coverage" }
dotnet test "tests/Shiro.Tests/Shiro.Tests.csproj" --configuration Release --no-build --collect:"XPlat Code Coverage" --settings "tests/coverage.runsettings" --results-directory "coverage" --logger "console;verbosity=minimal"
./scripts/Check-Coverage.ps1 -CoverageDir "coverage" -MinOverall 75.0 -MinBranch 60.0 -MinCore 92.0
$covExit = $LASTEXITCODE
Remove-Item -Recurse -Force "coverage"
exit $covExit
```
Expected: Exit code `0` (`Overall >= 75.0%`, `Branch >= 60.0%`, `Core >= 92.0%`).

- [ ] **Step 3: Review branch diff & commit history**

Run:
```powershell
git status --short
git log --oneline main..HEAD
```
Expected: Clean working tree, 10 focused TDD commits ready for `superpowers:finishing-a-development-branch`.
