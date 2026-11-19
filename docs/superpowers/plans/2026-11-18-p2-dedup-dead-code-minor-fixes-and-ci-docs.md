# P2 Deduplication, Dead Code Cleanup, Minor Fixes, CI Hardening & Docs Sync Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.
> **Synced against live working tree (`107a1d7` + working edits):** Builds directly on landed P2 helpers (`Win32NativeHelper.cs` 141L, `RobloxHttpHandlerHelper.cs` 46L, `ThumbnailBatchHelper.cs` 140L, `RobloxModerationHelper.cs` 24L, `FlagIconService.SvgPrimitives.cs` 233L, `P2AuditRemediationTests.cs` 285L, `src/Shiro/Resources/Strings/{en,vi}.json` pruned `1,233 -> 922` keys). Already-done items are marked `[x]`; every `[ ]` tracks only verified remaining gaps, concrete tests, CI/coverage gates, and exact `.md` doc discrepancies.

**Goal:** Close remaining P2 deduplication gaps (`ThumbnailBatchHelper` magic headers, `SafeProcessHandle OpenProcess` in `Win32NativeHelper`, `RobloxModerationHelper` adoption, `TrackerRegex`, Roblox install/currency/session/priority helpers), retire remaining verified dead code & Nexus/BloxGen remnants (Policy 2), finish Section 4 fixes (`Webhook` embed dedup, `QuickLogInAsync` UI marshal, `FlagIconService` gradients/clipPath/LRU, `AccentThemeService.ParseTheme`, i18n spacing), harden CI & `ArchitectureTests`/`coverage-floors.json`, and bring every project `.md` file into 100% accuracy.

**Architecture:** Extend existing WT helpers (`Win32NativeHelper.cs`, `RobloxModerationHelper.cs`, `ThumbnailBatchHelper.cs`, `FlagIconService.SvgPrimitives.cs`) rather than introducing parallel utilities. Keep source files `<= 485` (`tests <= 550`) and re-run `ArchitectureTests` after every dead-code removal and XAML/i18n edit.
- **Conflict #5 Ruling (`ThumbnailBatchHelper` & `P2AuditRemediationTests`):** Tighten `IsPlausibleImageBytes` (`ThumbnailBatchHelper.cs:18-41`) to require real PNG (`89 50 4E 47 0D 0A 1A 0A`), JPEG (`FF D8 FF`), GIF (`GIF87a`/`GIF89a`), or WEBP (`RIFF....WEBP`) headers; update `P2AuditRemediationTests.cs:200,202` from 4-byte PNG & `{ 1, 2, 3, 4 }` positive assertion to valid 8-byte PNG/GIF/WEBP headers + `Assert.False` on `{ 1, 2, 3, 4 }`.
- **Policy 2 Ruling (`LogRedactor.BloxApiKeyRegex` & Dead Wrappers):** Delete remaining zero-caller dead classes/commands/settings AND `LogRedactor.BloxApiKeyRegex` (`LogRedactor.cs:49,140`, updating `SecurityHardeningTests.cs:85-94`), while preserving active `GameClass.FavoriteGame`, `Presence.GetPresence`, `RobloxBackgroundThrottlerService`, and `EnableBackgroundThrottling`/`BackgroundTargetFps`.

**Tech Stack:** .NET 10 (`win-x64`), C# 14, GitHub Actions YAML, PowerShell (`Publish.ps1`, `Check-Coverage.ps1`), Markdown docs, xUnit.

**Spec:** System Audit Report (`C:/Users/lenovo/.gemini/antigravity/brain/d3d9db7e-c20c-4d8a-b056-391721b18014/system-audit-report.md`) — Sections 4, 5.1, 5.2, 7.1, & P2 Roadmap.

---

## Global Constraints

- `TreatWarningsAsErrors=true`, `<Nullable>enable</Nullable>`, `[LibraryImport]` only, `src` files `<= 485`, partials `<= 1150` default, `tests` `<= 550`, strict ASCII C# comments/logs.
- Run `ArchitectureTests` and `dotnet build Shiro.sln -c Release -warnaserror` after each task.

---

### Task 1: Finish Remaining Section 5.2 Deduplication Clusters & Magic-Header Check (Conflict #5)

**Files (Done in WT `[x]`):**
- [x] `Win32NativeHelper.cs` (141L), `RobloxHttpHandlerHelper.cs` (46L), `RobloxCsrfRetryHelper.cs` (63L), `RobloxWindowService`+Arranger grid/tile sharing, `ThumbnailBatchHelper.cs` (140L), & `RobloxModerationHelper.cs` (24L) created/adopted.

**Files to Modify:**
- Modify: `src/Shiro/Services/Network/ThumbnailBatchHelper.cs:18-41`
- Update Conflict #5: `tests/Shiro.Tests/P2AuditRemediationTests.cs:198-204`
- Modify: `src/Shiro/Services/Windows/Win32NativeHelper.cs` & `ProcessExtensions.cs:21,74` (shared `[LibraryImport] SafeProcessHandle OpenProcess`)
- Modify: `src/Shiro/Services/Network/RobloxModerationService.cs:129-134,192-197,272-274` (adopt `RobloxModerationHelper.IsModerationOrLockedUrl`)
- Modify: `src/Shiro/Services/Launch/AccountLaunchService.Diagnostics.cs:21,58-62` (`>= 77%`), `RobloxProcessService.Lifecycle.cs:13` (`>= 67%`), `RobloxWatcher.FileWatcher.cs:13`, `RobloxWindowArrangerService.cs:33` (`TrackerRegex` dedup), `ClientSettingsPatcher.cs:294-303` (`FindRobloxVersionFolders` dedup)
- Modify: `src/Shiro/Services/Network/RobloxEconomyService.cs:37-45` & `SessionAuditorService.Helpers.cs:75`; `Account.SessionState.cs` & `SessionAuditorService*`; `MemoryOptimizer.ResourceProfile.cs` & `RobloxProcessOptimizer.cs`

**Interfaces:**
- **Modified behavior (`ThumbnailBatchHelper.cs:18-41`):** `IsPlausibleImageBytes(byte[]? bytes, string? contentType = null)` validates PNG (8-byte), JPEG (`FF D8 FF`), GIF (`GIF87a`/`GIF89a`), or WEBP (`RIFF....WEBP`) headers instead of accepting arbitrary non-ASCII `{ 1, 2, 3, 4 }`.
- **Added helper seam (`Win32NativeHelper.cs`):** `internal static SafeProcessHandle OpenSafeProcess(uint desiredAccess, int processId)` shared by `ProcessExtensions.cs` & `RobloxProcessService`.

- [ ] **Step 1: Update Conflict #5 in `P2AuditRemediationTests.cs:198-204` & add failing dedup/magic-byte tests (TDD Red)**
  ```csharp
  [Fact]
  public void ThumbnailBatchHelper_IsPlausibleImageBytes_RequiresRealImageMagicHeaders()
  {
      Assert.True(ThumbnailBatchHelper.IsPlausibleImageBytes(
          new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A }, "image/png"));
      Assert.True(ThumbnailBatchHelper.IsPlausibleImageBytes(new byte[] { 0xFF, 0xD8, 0xFF, 0xE0 }, "image/jpeg"));
      Assert.True(ThumbnailBatchHelper.IsPlausibleImageBytes(Encoding.ASCII.GetBytes("GIF89a\x01\x00"), "image/gif"));
      Assert.True(ThumbnailBatchHelper.IsPlausibleImageBytes(Encoding.ASCII.GetBytes("RIFF\x00\x00\x00\x00WEBP"), "image/webp"));
      Assert.False(ThumbnailBatchHelper.IsPlausibleImageBytes(new byte[] { 1, 2, 3, 4 }, "application/octet-stream"));
  }
  ```
- [ ] **Step 2: Run targeted Task 1 tests expecting Red**
  `dotnet test Shiro.sln -c Release --nologo --filter "FullyQualifiedName~P2AuditRemediationTests"`
- [ ] **Step 3: Implement remaining helper consolidations cleanly**
  - Enforce magic headers in `ThumbnailBatchHelper.IsPlausibleImageBytes`.
  - Share `SafeProcessHandle OpenSafeProcess` in `Win32NativeHelper`, `RobloxModerationHelper.IsModerationOrLockedUrl` in `RobloxModerationService`, single shared `TrackerRegex`, shared `RobloxVersion` install folder scanner, shared Economy currency parser, shared secondary session probe, and shared `MemoryOptimizer.ComputeTargetPriorityClass`.
- [ ] **Step 4: Run Release `-warnaserror` + targeted tests + `ArchitectureTests` + `Check-Coverage.ps1`**
  ```powershell
  dotnet build Shiro.sln -c Release --nologo -warnaserror
  dotnet test Shiro.sln -c Release --no-build --nologo --filter "FullyQualifiedName~P2|FullyQualifiedName~Moderation|FullyQualifiedName~ArchitectureTests"
  ```
- [ ] **Step 5: Stage & commit Task 1**
  `git add src/Shiro/Services/ tests/Shiro.Tests/P2AuditRemediationTests.cs`
  `git commit -m "refactor(dedup): enforce image magic headers and finish remaining P2 helper consolidations"`

---

### Task 2: Delete Remaining Verified Dead Code, Legacy Wrappers & Orphan Keys (Policy 2)

**Files (Done in WT `[x]`):**
- [x] Deleted `AssemblyInfo.cs`, `RBX_Alt_Manager`, `Debug Updater`, `KEYS.json`/`UNUSED.json`, dead `Account.Api`/HBA/Proxy/Command items; pruned `src/Shiro/Resources/Strings/{en,vi}.json` (`1,233 -> 922` keys).

**Files to Delete/Modify (`[ ]`):**
- Delete: `src/Shiro/AvaloniaUI/Views/AboutDialog.axaml*`, `Services/Launch/{IRobloxLauncherService.cs,RobloxLauncherService.cs}`, `Services/Auth/{IRobloxSessionService.cs,RobloxSessionService.cs}`, `DashboardView.ContextMenu.cs`.
- Modify: `IDialogService.cs`, `AvaloniaDialogService.cs:215-229`, `MainViewModel.AccountCommands.cs:72,266-270`, `AppServices.cs:53-54,213-214,246`, `App.axaml:14-18`, `AccountViewModel.Styling.cs:28-37`, `DashboardView.axaml.cs:200`, `Account.cs:26` (`SessionHealth.Unknown`), `RobloxApiClient` (unused `Develop`), `Presence.cs:361` (`GetPresenceSingular`), `ProxyBindingService.cs:402` (`ClearClients`), remaining 19 unused `MainViewModel` commands + 41 unreferenced `On*Requested` delegates, 11 dead `AppSettingsKeys` (`SavedFollowUser` `:44`, `UseEmbeddedBrowser` `:49`, `FastFlagProfile` `:61`, `DynamicHardwareThrottling` `:67`, `SettingsPreset` `:70`, 6× `FastFlag_*` `:83-88` — keep `EnableBackgroundThrottling` & `BackgroundTargetFps`), `AppSettingsKeys.AccountControl` (`:17,123`), `AppSettings.AccountControl` (`AppSettings.cs:49`), **`LogRedactor.cs:49,140` (`BloxApiKeyRegex`)**, `scripts/Publish.ps1:86,115-119`, `src/Shiro/Resources/Strings/{en,vi}.json` remaining test-only orphans.
- Rename test files dropping `Nexus`: `LogWatcherNexusAndWebAuthTests.cs`, `PresenceAndNexusBehaviorTests.cs`.
- Update legacy existence/ratchet tests: `MvvmFoundationTests.cs`, `RefactoredServicesTests.cs`, `AppSettingsKeysTests.cs`, `RobloxAudioServiceTests.cs`, `DialogSmokeTests*.cs`, `AccountWorkflowServiceBehaviorTests.cs`, `AccountViewModelStylingBehaviorTests.cs:45-90`, **`SecurityHardeningTests.cs:85-94`** (rename `LogRedactor_RedactsProxyCredentials`, removing `"BLOX-"` expectation), `ArchitectureTests.Ratchets.cs:38`, `PresenceAndNexusBehaviorTests.cs:189`.

**Interfaces:**
- **Removed from `LogRedactor.cs` (Policy 2):** `BloxApiKeyRegex` (`line 49, 140`); `SecurityHardeningTests.cs:85-94` keeps proxy credential assertion and removes stale `"BLOX-"` check.
- **Removed from `IDialogService`:** `OpenAboutDialog(...)`.

- [ ] **Step 1: Update legacy existence/ratchet tests (`SecurityHardeningTests:85-94`, `Ratchets:38`, `Presence:189`, `MvvmFoundationTests`, `RefactoredServicesTests`, `AppSettingsKeysTests`) expecting Red until dead code removed**
  ```csharp
  [Fact]
  public void LogRedactor_RedactsProxyCredentials()
  {
      string redacted = LogRedactor.Redact("Proxy error on http://admin:superSecret123@192.168.1.50:8080");
      Assert.DoesNotContain("superSecret123", redacted);
      Assert.Contains("[REDACTED_PROXY_AUTH]", redacted);
  }
  ```
- [ ] **Step 2: Run targeted cleanup tests expecting Red**
  `dotnet test Shiro.sln -c Release --nologo --filter "FullyQualifiedName~SecurityHardeningTests|FullyQualifiedName~AppSettingsKeysTests|FullyQualifiedName~ArchitectureTests|FullyQualifiedName~RefactoredServicesTests"`
- [ ] **Step 3: Delete all listed dead files, wrappers, orphan keys, BloxGen regex & `Nexus*` test/script references**
- [ ] **Step 4: Run Release `-warnaserror` + full test suite + `ArchitectureTests` expecting 0 warnings, 100% PASS**
  `dotnet build Shiro.sln -c Release --nologo -warnaserror && dotnet test Shiro.sln -c Release --no-build --nologo`
- [ ] **Step 5: Stage & commit Task 2**
  `git add -u src/ tests/ scripts/Publish.ps1`
  `git add tests/Shiro.Tests/LogWatcherAndWebAuthTests.cs tests/Shiro.Tests/PresenceBehaviorTests.cs`
  `git commit -m "chore(cleanup): remove dead wrappers, unused commands, orphan settings keys, and legacy Nexus/BloxGen remnants"`

---

### Task 3: Finish Remaining Section 4 Medium/Low Fixes

**Files (Done in WT `[x]`):**
- [x] `AccountSearchHelper`, `GameInfoService`, `WebhookNotificationService` 429 backoff/truncation, `UpdateCheckService`, `ProxyManagerService` + `ProxyBindingService:340` 403 fail-closed, `SendFriendRequestAsync` `{}`, `LocalizationService:141-159`, `vi.json:186`, `MainWindow.axaml:135-137`, `UniformRowPanel`, `ToastService`, `StoragePaths.cs:77`, `FlagIconService` `<defs>`/`<use>`/`<symbol>`.

**Files to Modify (`[ ]`):**
- Modify: `src/Shiro/Services/Network/WebhookNotificationService.cs:193-203`
- Modify: `src/Shiro/Services/Network/RobloxSocialApiService.cs:291` (`QuickLogInAsync`)
- Modify: `src/Shiro/Services/Ui/FlagIconService.cs:19,208` & `.SvgPrimitives.cs:88`
- Modify: `src/Shiro/Services/Ui/AccentThemeService.cs:82` (`ParseTheme`)
- Modify: `src/Shiro/Resources/Strings/{en,vi}.json:158,395` & `src/Shiro/AvaloniaUI/Views/DashboardView.axaml:180-181`
- Test: `tests/Shiro.Tests/P2AuditRemediationTests.cs`, `AccentThemeServiceTests.cs`, `FlagIconServiceTests.cs`, `LocalizationServiceTests.cs`

**Interfaces:**
- **Modified (`FlagIconService.cs`):** Bounded LRU cache (`MaxFlagCacheEntries = 64`) replacing unbounded `ConcurrentDictionary`; SVG parser resolves `clipPath`, `linearGradient`, `radialGradient`, and `url(#id)` fill references.
- **Modified (`AccentThemeService.ParseTheme`):** Validates `Enum.TryParse<AccentTheme>(...) && Enum.IsDefined(parsed)`.

- [ ] **Step 1: Write failing tests in `P2AuditRemediationTests.cs`, `AccentThemeServiceTests.cs`, `FlagIconServiceTests.cs` & `LocalizationServiceTests.cs` (TDD Red)**
  ```csharp
  [Fact]
  public void WebhookNotificationService_FormatDiscordPayload_DoesNotDuplicateDetailsFieldAndDescription()
  {
      string json = WebhookNotificationService.FormatDiscordPayload(new WebhookAlert
      {
          EventType = WebhookEventType.RateLimited,
          Description = "HTTP 429 hit"
      });
      Assert.Single(System.Text.RegularExpressions.Regex.Matches(json, "HTTP 429 hit"));
  }

  [Fact]
  public void AccentThemeService_ParseTheme_RejectsUndefinedNumericStrings()
  {
      Assert.Equal(AccentTheme.Monochrome, AccentThemeService.ParseTheme("99"));
      Assert.Equal(AccentTheme.Cyan, AccentThemeService.ParseTheme("Cyan"));
  }

  [Fact]
  public void Localization_LaunchToastFooter_HasNoLeadingSpaceInsideQuotes()
  {
      string text = LocalizationService.Get("Launch_Toast_CheckExpiredFooter");
      Assert.DoesNotContain("' ", text);
  }
  ```
  Plus failing tests verifying `RobloxSocialApiService.QuickLogInAsync` dispatches prompt via `AppDispatcher.InvokeAsync` and `FlagIconService` renders `url(#grad)` + evicts LRU beyond `MaxFlagCacheEntries`.
- [ ] **Step 2: Run targeted tests expecting Red**
  `dotnet test Shiro.sln -c Release --nologo --filter "FullyQualifiedName~P2AuditRemediationTests|FullyQualifiedName~AccentThemeService|FullyQualifiedName~FlagIconService|FullyQualifiedName~LocalizationService"`
- [ ] **Step 3: Implement all 5 Section 4 fixes cleanly**
  - Omit redundant `DiscordWebhookField("Details", ...)` in `WebhookNotificationService.cs:193-196` when `embed.Description` holds `alert.Description`; wrap `Utilities.YesNoPrompt` in `await AppDispatcher.InvokeAsync(...)` in `RobloxSocialApiService.cs:291`; strip `' Expired'`/`' Hết hạn'` space in `src/Shiro/Resources/Strings/{en,vi}.json:395` & format `Bar_BatchActions` without split `")"` XAML text in `DashboardView.axaml:180-181`.
- [ ] **Step 4: Run Release `-warnaserror` + targeted tests + `ArchitectureTests`**
- [ ] **Step 5: Stage & commit Task 3**
  `git add src/Shiro/Services/Network/WebhookNotificationService.cs src/Shiro/Services/Network/RobloxSocialApiService.cs src/Shiro/Services/Ui/FlagIconService*.cs src/Shiro/Services/Ui/AccentThemeService.cs src/Shiro/Resources/Strings/ src/Shiro/AvaloniaUI/Views/DashboardView.axaml tests/Shiro.Tests/`
  `git commit -m "fix(polish): dedup webhook embed description, marshal QuickLogIn prompt, support SVG gradients, and validate theme enums"`

---

### Task 4: Harden CI Workflows, `ArchitectureTests`, Coverage Floors & Synchronize All Docs

**Files:**
- Modify: `.github/workflows/build.yml:27,51-58` & `.github/workflows/release.yml:30,41-48` (add `/p:SelfContained=true` on `restore --locked-mode`, pass `--no-restore` on `publish`, run `dotnet build -warnaserror` in `release.yml`, upload `coverage/` artifact on failure)
- Generate: `tests/Shiro.Tests/packages.lock.json`
- Modify: `src/Shiro/Shiro.csproj:34,48-53` (exclude non-Windows WebView2/Skia native RID folders on `win-x64` if offline locked restore & publish remain green)
- Modify: `tests/Shiro.Tests/ArchitectureTests*.cs` (add `Services !-> ViewModels` ratchet, ban `DispatcherFrame.PushFrame`/`.Wait(TimeSpan)`/`Thread.Sleep`, fix dotted `{DynamicResource ([A-Za-z0-9_.]+)}` regex)
- Modify: `tests/coverage-floors.json` (add `WebAuthPolicy.cs`, `KestrelWebServer.cs`, `AccountRuntimeStateStore.cs`, `SessionAuditorService.cs`) & `src/Shiro/Services/Windows/RobloxProcess.cs:174-230` (extract pure log helpers into `RobloxProcess.LogPatterns.cs` so coverage tracks them; align `docs/coverage-exclusions.md`)
- Update ALL verified inaccurate `.md` files using exact Step 2 counts & live P1–P2 architecture:
  - `README.md:20,72` (remove unverified `~17 MB`; clarify parameterized test cases vs test methods)
  - `SECURITY.md:23,25,34`, `docs/threat-model.md:11,16,26,36,41,42,76`, `docs/perf-baseline.md:7,21,22,24-26,30` (purge `Nexus`, `Chromium`, `Updaters`, `Classes/Cryptography.cs`, `AccountManager.BackupRecovery`, `RobloxLauncherService`, fake `SHIRO_VAULT_V3`, "third-party API keys"; document 12-char external min)
  - `docs/baseline.md:30,36,39,57-58` (sync test count, `~66.6 MB` binary size, and actual `/v2/*` + OpenAPI routes)
  - `CONTRIBUTING.md:41-48,52,60,70` (fix `485` / `1,150` caps, remove illegal `AppSettings` advice in `Services`, document split `ArchitectureTests*` & `{DynamicResource}` rules, match `Publish.ps1`)
  - `docs/architecture.md:12,94,109,111,114,125,145-159` (`485` cap, replace `FastLoginDialogViewModel` -> `CookieImportViewModel`, drop removed `IRobloxSessionService`/`IRobloxLauncherService`, list top-level `src/Shiro/*.cs`)
  - `docs/coverage-exclusions.md:9,35-36` (`17` vs `19` listed deleted files, reference `ArchitectureTests.Ratchets.cs`)
  - `CHANGELOG.md:10-37`, `NOTICE.md:13`, `docs/VIETNAM_NETWORK_ARCHITECTURE.md:20,28,31` (fix `[Unreleased]` self-contradictions & add `f562eb5..107a1d7` notes, purge `NexusWebSocketServer`, update `GetNetworkSignatureForAccount` / `PreferDirectLaunch` descriptions).

**Interfaces:**
- **Modified gates (`ArchitectureTests*.cs`):** Add `Services !-> ViewModels` layering rule, ban `DispatcherFrame.PushFrame`/`.Wait(TimeSpan)`/`Thread.Sleep` in `src/`, match dotted `{DynamicResource ([A-Za-z0-9_.]+)}` keys cleanly, and add `WebAuthPolicy.cs`, `KestrelWebServer.cs`, `AccountRuntimeStateStore.cs`, & `SessionAuditorService.cs` floors in `tests/coverage-floors.json`.

- [ ] **Step 1: Add failing `ArchitectureTests` gate checks & harden CI YAML/lockfiles/`Shiro.csproj`/`coverage-floors.json` (TDD Red)**
  ```csharp
  [Theory]
  [InlineData("{DynamicResource Theme.AccentBrush}", "Theme.AccentBrush")]
  public void ArchitectureTests_DynamicResourceRegex_CapturesDottedKeysAndBansDispatcherFrame(string sample, string expectedKey)
  {
      var match = ArchitectureTests.DynamicResourcePattern().Match(sample);
      Assert.True(match.Success);
      Assert.Equal(expectedKey, match.Groups[1].Value);
  }
  ```
- [ ] **Step 2: Run full offline CI-parity restore + Release `-warnaserror` build + coverage + `Check-Coverage.ps1` & record exact counts**
  ```powershell
  dotnet restore Shiro.sln --runtime win-x64 /p:SelfContained=true --locked-mode
  dotnet build Shiro.sln --configuration Release --no-restore --nologo -warnaserror
  dotnet test Shiro.sln --configuration Release --no-build --nologo --collect:"XPlat Code Coverage" --settings tests\coverage.runsettings --results-directory coverage
  powershell.exe -NoProfile -ExecutionPolicy Bypass -File .\scripts\Check-Coverage.ps1
  ```
  Expected: `0 Warning(s)`, `0 Error(s)`, all coverage floors + Core `>= 92.0%` `SUCCESS`.
- [ ] **Step 3: Synchronize all listed `.md` docs using exact observed Step 2 metrics & live file paths**
- [ ] **Step 4: Stage & commit Task 4**
  `git add .github/workflows/ tests/Shiro.Tests/packages.lock.json src/Shiro/Shiro.csproj tests/coverage-floors.json tests/Shiro.Tests/ArchitectureTests*.cs src/Shiro/Services/Windows/RobloxProcess*.cs README.md SECURITY.md CONTRIBUTING.md CHANGELOG.md NOTICE.md docs/*.md`
  `git commit -m "chore(ci,docs): harden locked CI build/coverage gates and synchronize project documentation"`

---

## Execution Handoff

Plan complete. Recommend executing Wave P2 sequentially after Wave P1 using either:
1. **Subagent-driven execution** (`superpowers:subagent-driven-development`) *(recommended)*
2. **Inline execution** (`superpowers:executing-plans`)
