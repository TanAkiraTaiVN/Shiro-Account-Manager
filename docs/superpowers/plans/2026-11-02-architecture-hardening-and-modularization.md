# Architecture Hardening & Modularization Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Eliminate upward UI dependencies from `Services`/`Classes`, extract testable session-capture logic from `FastLoginDialog`, organize `Services/` into domain folders, and route service-level settings/account queries through `AppSettings.General` and `AppServices.AccountRepository`.

**Architecture:**
1. Decouple `Shiro.Services` and `Shiro.Classes` from `Shiro.AvaloniaUI.Views` by routing dialogs through `AppServices.Dialogs` (`IDialogService`), window handle lookup through `IClassicDesktopStyleApplicationLifetime`, and dashboard notifications through `AccountManager`. Lock this boundary with a NetArchTest rule.
2. Extract DOM moderation classification, script builders, and post-capture validation from `FastLoginDialog` into `FastLoginCoordinator` (`Shiro.Services`), reducing `FastLoginDialog` below 1,200 lines and enabling unit tests for session capture edge cases.
3. Organize `src/Shiro/Services/` into domain subfolders (`Auth`, `Launch`, `Storage`, `Windows`, `Network`, `Ui`) while preserving `namespace Shiro.Services`.
4. Replace direct `AccountManager.General` and `AccountManager.GetAccountsSnapshot()` calls across `src/Shiro/Services/` with `AppSettings.General` and `AppServices.AccountRepository.GetAll()`, guarded by an architecture test.

**Tech Stack:** .NET 10 (`net10.0-windows10.0.19041.0`), Avalonia UI 11, WebView2, xUnit, NetArchTest.Rules.

---

### Task 1: Eliminate `Services` & `Classes` -> `Views` Dependencies and Lock with `ArchitectureTests`

**Files:**
- Modify: `src/Shiro/Services/IDialogService.cs`
- Modify: `src/Shiro/Services/AvaloniaDialogService.cs`
- Modify: `src/Shiro/Services/BloxGenService.cs`
- Modify: `src/Shiro/Services/RobloxWindowService.cs`
- Modify: `src/Shiro/Classes/Presence.cs`
- Modify: `src/Shiro/Classes/Utilities.cs`
- Modify: `src/Shiro/Classes/RobloxBrowserLogin.cs`
- Modify: `src/Shiro/Classes/RobloxBrowserLogin.PageAutomation.cs`
- Test: `tests/Shiro.Tests/ArchitectureTests.cs`

- [x] **Step 1: Add `bool? ShowYesNoCancel(string message, string title = "Shiro")` to `IDialogService` and `AvaloniaDialogService`**
- [x] **Step 2: Replace `MainWindow.Instance` and `MessageBoxDialog` usages in `BloxGenService`, `RobloxWindowService`, `Presence`, `Utilities`, and `RobloxBrowserLogin*`**
- [x] **Step 3: Add `Services_And_Classes_ShouldNotDependOn_Views` in `ArchitectureTests.cs` and verify it passes**

---

### Task 2: Extract `FastLoginCoordinator` from `FastLoginDialog` and Add Unit Tests

**Files:**
- Create: `src/Shiro/Services/Auth/FastLoginCoordinator.cs`
- Modify: `src/Shiro/AvaloniaUI/Views/FastLoginDialog.Automation.cs`
- Modify: `src/Shiro/AvaloniaUI/Views/FastLoginDialog.SessionCapture.cs`
- Modify: `src/Shiro/AvaloniaUI/Views/FastLoginDialog.axaml.cs`
- Modify: `tests/Shiro.Tests/ArchitectureTests.cs`
- Create: `tests/Shiro.Tests/FastLoginCoordinatorTests.cs`

- [x] **Step 1: Create `FastLoginCoordinator` with `IsModerationOrLockedUrl`, `ClassifyDomIssueText`, `MapDomIssueCode`, script generators, and `ProcessCapturedSessionAsync`**
- [x] **Step 2: Refactor `FastLoginDialog` partials to delegate to `FastLoginCoordinator` and lower `FastLoginDialog` line budget in `ArchitectureTests.cs` to 1200**
- [x] **Step 3: Add unit tests in `FastLoginCoordinatorTests.cs` covering DOM classification, 9002 unauthenticated moderation, username mismatch, and invalid session rejection**

---

### Task 3: Organize `src/Shiro/Services/` into Domain Subfolders

**Files:**
- Move files within `src/Shiro/Services/` into:
  - `src/Shiro/Services/Auth/`
  - `src/Shiro/Services/Launch/`
  - `src/Shiro/Services/Storage/`
  - `src/Shiro/Services/Windows/`
  - `src/Shiro/Services/Network/`
  - `src/Shiro/Services/Ui/`

- [x] **Step 1: Move service files into their respective domain subdirectories using `git mv` / file move while keeping `namespace Shiro.Services`**
- [x] **Step 2: Verify compilation and `ArchitectureTests`**

---

### Task 4: Route Service Settings and Account Queries Through `AppSettings.General` and `AppServices.AccountRepository`

**Files:**
- Modify: `src/Shiro/Services/ISettingsService.cs`
- Modify: `src/Shiro/Services/*/AccentThemeService.cs`, `AntiAfkService.cs`, `AutoReconnectService.cs`, `BloxGenService.cs`, `ChromeLaunchHelper.Proxy.cs`, `DiscordRpcService.cs`, `LocalizationService.cs`, `MemoryOptimizer.cs`, `RobloxDirectLauncher.CookieGuard.cs`, `RobloxDirectLauncher.DeElevation.cs`, `RobloxPlaceHelper.cs`, `RobloxProcessOptimizer.cs`, `RobloxServerQueryService.cs`, `RobloxWindowService.cs`, `StreamerModeService.cs`, `ToastService.cs`, `WebhookNotificationService.cs`, `AccountWorkflowService.cs`
- Modify: `tests/Shiro.Tests/SettingsServiceTests.cs`
- Modify: `tests/Shiro.Tests/ArchitectureTests.cs`

- [x] **Step 1: Add `bool RemoveProperty(string name)` to `ISettingsService` and `FakeSettingsService`**
- [x] **Step 2: Replace `AccountManager.General` with `AppSettings.General` and `AccountManager.GetAccountsSnapshot()` with `AppServices.AccountRepository.GetAll()` across `src/Shiro/Services/`**
- [x] **Step 3: Add `Services_ShouldNotAccess_AccountManagerGeneral_Directly` in `ArchitectureTests.cs` and run full test suite**
