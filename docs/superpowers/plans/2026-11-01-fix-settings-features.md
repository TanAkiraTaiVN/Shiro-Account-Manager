# Fix & Activate Underperforming Settings Features Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Transform all non-functional, placebo, or lagging configuration features in Settings (Potato Mode, Unlock FPS, Auto Update Check, dynamic WebServer/AutoCookie toggling, and Job CPU Cap UX) into genuine, active, and production-grade features.

**Architecture:** Connect `ClientSettingsPatcher` to write native graphics quality level 1 to `GlobalBasicSettings_13.xml` and safe `DFIntTaskSchedulerTargetFps` to `ClientAppSettings.json`. Add a non-blocking background GitHub release checker in `UpdateCheckService` triggered on `MainWindow` startup when `CheckForUpdates` is enabled. Wire dynamic start/stop lifecycle for Kestrel WebServer and `AutoCookieRefresh` directly inside `SettingsView.OnSaveSettingsClicked`.

**Tech Stack:** .NET 10.0, Avalonia UI, System.Text.Json, C# 13, xUnit.

**Spec:** The audit of user settings identified 4 non-functional / lagging settings:
1. Potato Mode (`EnablePotatoMode`): currently a no-op; needs to invoke `ConfigureGlobalBasicSettings(graphicsQualityLevel: 1)`.
2. Unlock FPS & Target FPS (`UnlockFPS`, `MaxFPSValue`): currently dead code; needs safe generation/removal of `DFIntTaskSchedulerTargetFps` in `ClientAppSettings.json`.
3. Auto Check Updates (`CheckForUpdates`): currently not hooked to any background startup check; needs async GitHub release check with toast notification.
4. WebServer & Auto Cookie Refresh (`EnableWebServer`, `AutoCookieRefresh`): toggles in Settings do not take effect immediately; need dynamic start/stop on save.
5. Job CPU Cap (`NumJobCpuCap`): add UX warning if user configures < 40% when multi-client is enabled.

## Global Constraints
- Maximum file length across all changed files must remain strictly under 500 lines per file (enforced by `ArchitectureTests`).
- Partial class budgets in `ArchitectureTests.cs` must not be exceeded.
- Preserve 100% backward compatibility for all existing tests (510/510 passing).
- Safe FastFlag isolation: Never write `FFlagDebug*` or unsafe experimental flags to `ClientAppSettings.json` — only `DFIntTaskSchedulerTargetFps`.

## Review Focus
1. `ConfigureGlobalBasicSettings` must not corrupt existing XML nodes if `GlobalBasicSettings_13.xml` has unexpected structure or missing tags.
2. `ApplyFpsLimit` must safely merge with existing `ClientAppSettings.json` (e.g. from Bloxstrap) without blowing away user custom flags.
3. `UpdateCheckService` must handle network timeouts, HTTP 403 (GitHub API rate limits), and offline environments without throwing unhandled exceptions or delaying startup.
4. Dynamic WebServer start must handle port collisions (e.g. port already bound) gracefully without crashing the UI.
5. Dynamic WebServer stop must cleanly dispose the active Kestrel instance and release socket bindings.

---

### Task 1: Activate Potato Mode & Safe FPS Limit in `ClientSettingsPatcher`

**Files:**
- Modify: `src/Shiro/Classes/ClientSettingsPatcher.cs`
- Modify: `src/Shiro/AvaloniaUI/Views/SettingsView.axaml.cs:191-196`
- Test: `tests/Shiro.Tests/ClientSettingsPatcherTests.cs`

**Interfaces:**
- Consumes: `AccountManager.General`, `AppSettingsKeys.General`
- Produces: `ClientSettingsPatcher.ApplyFpsLimit(bool unlockFps, int targetFps)`
- Produces: `ClientSettingsPatcher.PatchSettings()`

- [ ] **Step 1: Write the failing tests for `ClientSettingsPatcher`**

```csharp
[Fact]
public void ApplyFpsLimit_WhenUnlocked_GeneratesValidJsonWithTargetFps()
{
    string tempDir = Path.Combine(Path.GetTempPath(), "Shiro_Patcher_Test_" + Guid.NewGuid());
    Directory.CreateDirectory(tempDir);
    try
    {
        ClientSettingsPatcher.ApplyFpsLimitToDirectory(tempDir, unlockFps: true, targetFps: 144);
        string jsonPath = Path.Combine(tempDir, "ClientSettings", "ClientAppSettings.json");
        Assert.True(File.Exists(jsonPath));
        string content = File.ReadAllText(jsonPath);
        Assert.Contains("\"DFIntTaskSchedulerTargetFps\": 144", content);
    }
    finally
    {
        Directory.Delete(tempDir, recursive: true);
    }
}

[Fact]
public void ApplyFpsLimit_WhenLocked_RemovesFpsKeyPreservingOtherKeys()
{
    string tempDir = Path.Combine(Path.GetTempPath(), "Shiro_Patcher_Test_" + Guid.NewGuid());
    string settingsDir = Path.Combine(tempDir, "ClientSettings");
    Directory.CreateDirectory(settingsDir);
    try
    {
        File.WriteAllText(Path.Combine(settingsDir, "ClientAppSettings.json"), "{\"DFIntTaskSchedulerTargetFps\": 144, \"FStringCustom\": \"Test\"}");
        ClientSettingsPatcher.ApplyFpsLimitToDirectory(tempDir, unlockFps: false, targetFps: 60);
        string content = File.ReadAllText(Path.Combine(settingsDir, "ClientAppSettings.json"));
        Assert.DoesNotContain("DFIntTaskSchedulerTargetFps", content);
        Assert.Contains("FStringCustom", content);
    }
    finally
    {
        Directory.Delete(tempDir, recursive: true);
    }
}
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test tests/Shiro.Tests/Shiro.Tests.csproj --filter "FullyQualifiedName~ClientSettingsPatcherTests"`
Expected: FAIL (method `ApplyFpsLimitToDirectory` not defined)

- [ ] **Step 3: Implement `ApplyFpsLimit` and update `PatchSettings` in `ClientSettingsPatcher.cs`**

Implement `ApplyFpsLimitToDirectory(string versionFolder, bool unlockFps, int targetFps)` to safely read, parse JSON, inject or delete `DFIntTaskSchedulerTargetFps`, and write atomically.
In `PatchSettings()`:
If `EnablePotatoMode` is true, invoke `ConfigureGlobalBasicSettings(graphicsQualityLevel: 1)`.
Invoke `ApplyFpsLimit(unlockFps, targetFps)` across all discovered Roblox version folders.

- [ ] **Step 4: Run test to verify it passes**

Run: `dotnet test tests/Shiro.Tests/Shiro.Tests.csproj --filter "FullyQualifiedName~ClientSettingsPatcherTests"`
Expected: PASS

- [ ] **Step 5: Commit**

```bash
git add src/Shiro/Classes/ClientSettingsPatcher.cs src/Shiro/AvaloniaUI/Views/SettingsView.axaml.cs tests/Shiro.Tests/ClientSettingsPatcherTests.cs
git commit -m "feat(settings): activate Potato Mode and safe FPS unlock via ClientSettingsPatcher"
```

---

### Task 2: Background GitHub Update Checker on Startup

**Files:**
- Create: `src/Shiro/Services/UpdateCheckService.cs`
- Modify: `src/Shiro/AvaloniaUI/App.axaml.cs:33-35`
- Test: `tests/Shiro.Tests/UpdateCheckServiceTests.cs`

**Interfaces:**
- Consumes: `AppSettings.Current.CheckForUpdates`, `ToastService`
- Produces: `UpdateCheckService.CheckForUpdatesInBackgroundAsync(CancellationToken ct)`
- Produces: `UpdateCheckService.CompareVersions(string current, string remote)`

- [ ] **Step 1: Write the failing tests for `UpdateCheckService`**

```csharp
[Theory]
[InlineData("4.2.0", "v4.3.0", true)]
[InlineData("4.2.0", "4.2.1", true)]
[InlineData("4.2.0", "v4.2.0", false)]
[InlineData("4.3.0", "v4.2.0", false)]
public void CompareVersions_DetectsNewerReleasesAccurately(string currentVer, string releaseTag, bool expectedNewer)
{
    bool isNewer = UpdateCheckService.IsRemoteVersionNewer(currentVer, releaseTag);
    Assert.Equal(expectedNewer, isNewer);
}
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test tests/Shiro.Tests/Shiro.Tests.csproj --filter "FullyQualifiedName~UpdateCheckServiceTests"`
Expected: FAIL (type `UpdateCheckService` not defined)

- [ ] **Step 3: Implement `UpdateCheckService.cs` and trigger in `App.axaml.cs`**

Create `src/Shiro/Services/UpdateCheckService.cs` with:
- `IsRemoteVersionNewer(string currentVer, string remoteTag)` using `System.Version.TryParse`.
- `CheckForUpdatesInBackgroundAsync(CancellationToken ct)` with 5-second timeout, `User-Agent: Shiro-Account-Manager`, checking `https://api.github.com/repos/X-Shiro-X/Shiro-Account-Manager/releases/latest`.
- If newer version is detected: post a Toast via `ToastService.ShowInfo("Bản cập nhật mới", $"Đã có phiên bản {remoteTag}. Bấm để mở trang cập nhật.")`.
Trigger `UpdateCheckService.StartBackgroundCheck()` in `App.axaml.cs` when `!isUpdater` and `AppSettings.Current.CheckForUpdates` is true.

- [ ] **Step 4: Run test to verify it passes**

Run: `dotnet test tests/Shiro.Tests/Shiro.Tests.csproj --filter "FullyQualifiedName~UpdateCheckServiceTests"`
Expected: PASS

- [ ] **Step 5: Commit**

```bash
git add src/Shiro/Services/UpdateCheckService.cs src/Shiro/AvaloniaUI/App.axaml.cs tests/Shiro.Tests/UpdateCheckServiceTests.cs
git commit -m "feat(updates): add background update check on startup when enabled"
```

---

### Task 3: Real-Time Dynamic Lifecycle for WebServer & AutoCookieRefresh

**Files:**
- Modify: `src/Shiro/AvaloniaUI/Views/SettingsView.axaml.cs:220-234`
- Modify: `src/Shiro/AccountManager.Lifecycle.cs`
- Test: `tests/Shiro.Tests/DynamicSettingsLifecycleTests.cs`

**Interfaces:**
- Consumes: `AccountManager.AltManagerWS`, `AccountManager.Instance.AutoCookieRefresh`
- Produces: `AccountManager.RestartWebServerAsync(int port, bool allowExternal)`
- Produces: `AccountManager.StopWebServerAsync()`
- Produces: `AccountManager.SetAutoCookieRefreshEnabled(bool enabled)`

- [ ] **Step 1: Write the failing tests for dynamic lifecycle management**

```csharp
[Fact]
public void SetAutoCookieRefreshEnabled_TogglesTaskStateDynamically()
{
    var prevGeneral = AccountManager.General;
    AccountManager.General = new IniSection("General");
    try
    {
        AccountManager.Instance.SetAutoCookieRefreshEnabled(false);
        Assert.Null(AccountManager.Instance.AutoCookieRefresh);

        AccountManager.Instance.SetAutoCookieRefreshEnabled(true);
        Assert.NotNull(AccountManager.Instance.AutoCookieRefresh);

        AccountManager.Instance.SetAutoCookieRefreshEnabled(false);
        Assert.Null(AccountManager.Instance.AutoCookieRefresh);
    }
    finally
    {
        AccountManager.General = prevGeneral;
    }
}
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test tests/Shiro.Tests/Shiro.Tests.csproj --filter "FullyQualifiedName~DynamicSettingsLifecycleTests"`
Expected: FAIL (method `SetAutoCookieRefreshEnabled` not defined)

- [ ] **Step 3: Implement dynamic WebServer & AutoCookie methods in `AccountManager.Lifecycle.cs` and invoke in `SettingsView.axaml.cs`**

In `AccountManager.Lifecycle.cs`:
- Add `SetAutoCookieRefreshEnabled(bool enabled)`.
- Add `RestartWebServerAsync(int port, bool allowExternal)` and `StopWebServerAsync()`.
In `SettingsView.axaml.cs`:
- On saving settings, compare previous `settings.EnableWebServer` and `settings.AutoCookieRefresh` with current toggle states.
- If changed, invoke `RestartWebServerAsync` / `StopWebServerAsync` and `SetAutoCookieRefreshEnabled` without requiring app restart.

- [ ] **Step 4: Run test to verify it passes**

Run: `dotnet test tests/Shiro.Tests/Shiro.Tests.csproj --filter "FullyQualifiedName~DynamicSettingsLifecycleTests"`
Expected: PASS

- [ ] **Step 5: Commit**

```bash
git add src/Shiro/AccountManager.Lifecycle.cs src/Shiro/AvaloniaUI/Views/SettingsView.axaml.cs tests/Shiro.Tests/DynamicSettingsLifecycleTests.cs
git commit -m "feat(settings): support real-time dynamic lifecycle for WebServer and AutoCookieRefresh"
```

---

### Task 4: UI/UX Improvements & Job CPU Cap Guidance

**Files:**
- Modify: `src/Shiro/AvaloniaUI/Views/SettingsView.axaml`
- Modify: `src/Shiro/Resources/Strings/vi.json`
- Modify: `src/Shiro/Resources/Strings/en.json`
- Test: `tests/Shiro.Tests/LocalizationServiceTests.cs`

**Interfaces:**
- Consumes: Localization JSON files
- Produces: Updated tips for `Set_JobCpuCapTip`, `Set_PotatoModeTip`, `Set_UnlockFpsTip`

- [ ] **Step 1: Write verification test for localization keys**

```csharp
[Theory]
[InlineData("vi", "Set_JobCpuCapTip")]
[InlineData("en", "Set_JobCpuCapTip")]
public void SettingsStrings_ContainCpuCapGuidance(string lang, string key)
{
    var dict = LocalizationService.LoadStringsForLanguage(lang);
    Assert.True(dict.ContainsKey(key));
    Assert.False(string.IsNullOrWhiteSpace(dict[key]));
}
```

- [ ] **Step 2: Update string tips in `vi.json` and `en.json`**

Update `Set_JobCpuCapTip`: Add note that setting CPU cap below 40% with multiple clients may cause lag/disconnects.
Update `Set_PotatoModeTip`: Note that it sets Roblox graphics quality level to 1 via native settings.
Update `Set_UnlockFpsTip`: Note that it configures `DFIntTaskSchedulerTargetFps` cleanly.

- [ ] **Step 3: Run localization tests to verify all keys pass**

Run: `dotnet test tests/Shiro.Tests/Shiro.Tests.csproj --filter "FullyQualifiedName~LocalizationServiceTests"`
Expected: PASS

- [ ] **Step 4: Commit**

```bash
git add src/Shiro/AvaloniaUI/Views/SettingsView.axaml src/Shiro/Resources/Strings/vi.json src/Shiro/Resources/Strings/en.json tests/Shiro.Tests/LocalizationServiceTests.cs
git commit -m "docs(settings): update UX tips and guidance for CPU Cap and Potato Mode"
```

---

### Task 5: Full Integration & Architecture Verification

**Files:**
- Test: `tests/Shiro.Tests/ArchitectureTests.cs`

- [ ] **Step 1: Run full test suite**

Run: `dotnet test --nologo`
Expected: 510+ tests passing, 0 failed.

- [ ] **Step 2: Verify Architecture & Line Budgets**

Run: `dotnet test tests/Shiro.Tests/Shiro.Tests.csproj --filter "FullyQualifiedName~ArchitectureTests"`
Expected: All line budgets and file size checks PASS.
