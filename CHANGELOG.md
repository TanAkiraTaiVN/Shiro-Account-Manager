# Changelog

All notable changes to the Shiro Account Manager project will be documented in this file.

The format is based on [Keep a Changelog](https://keepachangelog.com/en/1.0.0/),
and this project adheres to [Semantic Versioning](https://semver.org/spec/v2.0.0.html).

---

## [Unreleased]

### Added
- **Launch Pacing, Telemetry & Server Operation Helpers**: Extracted `LaunchTimingService`, `LaunchQueuePacingHelper`, `LaunchCaptchaHelper`, `LaunchCoordinator.Queue`, and `ServerListOperationHelper`, backed by dedicated behavioral unit tests (`LaunchCoordinatorCaptchaAndAppendTests`, `LaunchSummaryAndSettingsCaptchaTests`).
- **Phase 6 - Architecture & Hygiene Invariants**:
  - `NoTestFiles_ShouldExceed550Lines`: Enforced a strict maximum budget of `<= 550` lines per test file across `tests/Shiro.Tests/` and `<= 485` lines per `ArchitectureTests*.cs` file.
  - `DomainFolder_ShouldNotDependOn_ViewModelsOrAvaloniaOrWin32Registry`: Guarded the `Domain/` layer against UI, ViewModel, and platform-specific Win32 dependencies.
  - `FastJson_ShouldHaveSingleDefinition`: Verified single canonical JSON serializer implementation.

### Changed
- **Legacy Module Consolidation & Removal**: Removed legacy `PuppeteerSharp` / Chromium browser automation (`Services/Browser/`, `ChromeLaunchHelper`), `Nexus` WebSocket controller (`src/Shiro/Nexus/`, `NexusWindow`, `NexusViewModel`), and archive self-installer (`UpdaterWindow`, `UpdaterViewModel`, `UpdateArchiveInstaller`, `ReleaseInfoParser`), consolidating interactive authentication on WebView2 `FastLoginDialog` and local automation on `KestrelWebServer`.
- **Phase 6 - Zero Ambient Statics, Domain Purity & Test Hygiene**:
  - **Zero `async void` in `AvaloniaUI/`**: Refactored native WebView2 navigation event handlers in `FastLoginDialog.Automation.cs` into task-based methods with comprehensive error catching and logging, ensuring 0 `async void` across the entire application.
  - **Pure `Domain/` Layer & Unified `FastJson`**: Relocated UI prompt utilities from `Domain/Utilities.cs` to `Services/Ui/Utilities.cs`, purified `Domain/` model dependencies, and eliminated redundant JSON serialization definitions.
  - **Zero Ambient Static Logging & Settings Across All 10 Folders**: Replaced ambient `Program.Logger` and `AppSettings.*` references across `Domain/`, `ViewModels/`, `AvaloniaUI/Views/`, `Services/Ui/`, `Services/Browser/`, `Services/Windows/`, and `Services/Launch/` with injected `IAppLogger` and `ISettingsService` through `AppServices`, locking all 10 project folders to `(0, 0)` in `AmbientStaticAccess_ShouldNotExceedPerFolderBaseline`.
  - **AccountManager Budget Compliance**: Extracted `AccountSessionMaintenanceService` and `AccountLaunchBatchService`, reducing `AccountManager` aggregate partial line count to <= 1150 lines without artificial compression.
  - **AccountListFilterController Extraction**: Decoupled account list search, filtering, and tag management into `AccountListFilterController`, keeping `MainViewModel` well within its class budget.
  - **Test Suite Hygiene & Modularization**: Split 7 oversized test files (`ArchitectureTests.cs`, `DialogSmokeTests.cs`, `RobloxHbaAndIpPersistenceTests.cs`, `AccountLaunchServiceBranchTests.cs`, `AccountManagerLifecycleAndAccountsBehaviorTests.cs`, `WebServerEndpointsAndWorkflowTests.cs`, and `IpSwitchSessionPersistenceTests.cs`) into modular partial classes (`ArchitectureTests.Layering.cs`, `ArchitectureTests.Budgets.cs`, `DialogSmokeTests.Extended.cs`, `RobloxHbaAndIpPersistenceTests.Extended.cs`, `AccountLaunchServiceBranchTests.Fallback.cs`, `AccountManagerAccountsBehaviorTests.cs`, `WebServerWorkflowBehaviorTests.cs`, `IpSwitchSessionPersistenceTests.Extended.cs`), preserving all 1,915 tests while enforcing file length limits.
- **Task 1 - WebAuthPolicy, Nexus & API Layer Decoupling**: Decoupled `WebAuthPolicy` from `AccountApiDispatcher`, eliminated `AccountManager` dependencies in `Services/` and `Nexus/`, and sanitized HTTP 500 error responses across local API endpoints to return a generic `"Internal server error"` body instead of raw exception messages (the only runtime behavior change in this release).
- **Task 2 - Untyped Catch Blocks Elimination (Part 1)**: Replaced broad untyped `catch` and variable-less `catch (Exception)` blocks across networking, launcher, and view models with specific typed exception handlers and diagnostic logging.
- **Task 3 - Untyped Catch Blocks Elimination (Part 2)**: Eliminated all remaining untyped catch blocks across core storage, security, and utility services, bringing repository untyped catch count to 0 (enforced by ratchet test).
- **Task 4 - AccountManager Static State Facade Elimination**: Removed static state facade accessors on `AccountManager`, directing all state access through `IAccountRepository`, `ISettingsService`, and registered service abstractions.
- **Task 5 - Account Persistence Extraction**: Extracted `AccountPersistenceService` from `AccountManager` to manage atomic JSON/binary serialization, recovery, and backup logic in `Services/Storage/`.
- **Task 6 - Account Registration Extraction & Circular Dependency Break**: Extracted `AccountRegistrationService` for account credential onboarding and lifecycle registration, breaking circular references between `AccountRepository` and `AccountManager`.
- **Task 7 - State Service Singleton Access Enforcement**: Enforced single-point resolution for state service `.Default` accessors, restricting direct `.Default` references to the composition root (`AppServices`).
- **Task 8 - Legacy Classes Folder Dissolution**: Reorganized all remaining types in legacy `src/Shiro/Classes/` into appropriate domain namespaces (`Shiro.Services.Launch`, `Shiro.Services.Storage`, etc.) and removed `Classes/`.
- **Task 9 - Repository Hygiene & Platform Scope Documentation**: Cleaned up historical superpowers plans and legacy upgrade notes into `docs/archive/`, untracked agent artifacts, enforced English comments via architecture test, and documented Windows x64 exclusive platform scope and accepted Roblox ToS product risks.

---

## [5.0.0] - 2026-11-04 (Milestone 10/10 Architecture Overhaul)

### Added
- **Dialog MVVM & Headless Decoupling**:
  - Implemented `DialogViewModelBase` across all dialog ViewModels with standardized lifecycle, busy status management (`StatusKind`), and `RequestClose` handling.
  - Introduced `IUserPrompt` abstraction, decoupling UI alert/confirmation prompts from Avalonia window contexts and enabling deterministic headless mocking in unit tests.
  - Extracted `FastLoginCoordinator` and `FastLoginQueue` to isolate WebView2 web automation logic from UI presentation.
- **Nexus Controller Decoupling**:
  - Introduced `INexusHost` interface and thread-safe registry `NexusHost.Current`.
  - Added comprehensive headless integration test coverage for Nexus WebSocket payloads, anti-AFK pulses, and remote Lua executions.
- **Ratchet Test Coverage Gates (75 / 60 / 92)**:
  - Added 150+ behavioral unit tests covering launch services, window tiling, avatar downloads, cookie rotation, de-elevation, rate-limiting, and network services, expanding test suite to 1,555 tests (100% passing).
  - Raised global quality gates in CI/CD (`build.yml`, `release.yml`, `scripts/Check-Coverage.ps1`, and `ArchitectureTests.cs`):
    - Overall Line Coverage: raised to **75.47%** (Gate: >= 75.0%)
    - Overall Branch Coverage: raised to **63.93%** (Gate: >= 60.0%)
    - Core Security / Persistence Coverage: raised to **92.83%** (Gate: >= 92.0%)
  - Configured per-file branch coverage floors in `tests/coverage-floors.json`.
- **Architectural Quality Enforcement**:
  - Added strict assertion quality validation (`TestMethods_ShouldAssertOnValues_NotOnlyNullness`) preventing mock-less or trivial assertions.
  - Eliminated all unhandled `async void` methods; strictly documented and scoped the remaining 5 native WebView2 event handlers in `docs/coverage-exclusions.md`.
  - Verified 0 empty catch blocks, 0 sync-over-async blocking calls, and 100% adherence to the <= 500 lines per file budget across all source files.
- **P0 Security Hardening**:
  - Implemented Dual-KDF Vault Encryption: PBKDF2-SHA256 (`600,000` iterations, `SHIRO_SECURE_VAULT_V3` / `SHIRO_VAULT_V3`) for user-supplied passwords and fast HKDF-SHA256 (`SHIRO_SECURE_VAULT_V4`) for high-entropy 256-bit DPAPI machine keys.
  - Locked WebView2 HBA (`x-bound-auth-token`) fetch bridge and credential autofill scripts to verified `https://*.roblox.com` origins.
  - Blocked internal `Hba*` security fields on Kestrel `SetField`/`GetField` endpoints, enforced `UserID` ownership verification on Nexus WebSocket registration, and encrypted Discord monitoring webhook URLs via Windows DPAPI.
  - Enforced fail-closed SHA-256 verification and PowerShell single-quote path escaping in `AutoUpdaterService`, plus Windows Authenticode signature verification in `VcRedistInstaller`.
- **P1 Concurrency, Handle & Resource Hardening**:
  - Added `CancellationToken` propagation, bounded shutdown timeouts, and deterministic native/managed handle disposal across `AccountBrowser`, `LaunchCoordinator`, `NexusWebSocketServer`, `DiscordRpcService`, and `ToastService`.
  - Fixed `AsyncRelayCommand` overlap synchronization, UI thread dispatching for `ServerListViewModel.Servers`, and single-instance `EventWaitHandle` lifecycle.
- **P2 Architecture, State Ownership & Documentation Sync**:
  - Decoupled `AccountManager` from `Shiro.AvaloniaUI.Views` via `IDialogService.ShowInputPromptAsync` and `IHostUiBridgeService`.
  - Implemented `IDisposable` on `MainViewModel` and `ServerListViewModel` to unsubscribe static events and dispose timers/cancellation tokens.
  - Consolidated `AccountsList` and `PlaceHistory` ownership in `AccountRepository`, removed synchronous HTTP from `Account` constructors, zeroed decrypted backup buffers in `AccountManager.BackupRecovery`, and expanded the test suite to **1,868+ unit tests**.

## [4.2.0] - 2026-10-30 (Project Real Architecture Transfer & Hardening)

### Added
- **Offline Roblox Datacenter Engine & Flag Support**:
  - Integrated complete global Roblox datacenter database (`datacenters.json`) covering 22 regions worldwide with internal `dataCenterIds` and subnet mapping.
  - Added `RobloxDatacenterService` for high-performance offline resolution of game server locations, eliminating external HTTP GeoIP latency and rate-limiting.
  - Embedded 257 SVG country flags and automatic Unicode flag emoji generation (`🇸🇬`, `🇺🇸`, `🇻🇳`, `🇯🇵`, `🇩🇪`) on `ServerItemViewModel`.
- **Token De-Elevation (Administrator Mitigation)**:
  - Implemented `RobloxDirectLauncher.DeElevation.cs` using Windows Token Duplication (`OpenProcessToken`, `DuplicateTokenEx`, `CreateProcessWithTokenW`).
  - When Shiro executes with Administrator privileges, spawned Roblox clients are automatically de-elevated to Medium Integrity (Standard User) via `explorer.exe` shell token, eliminating UIPI restrictions and Hyperion elevation flags.
- **Privacy & Streamer Mode (`SetWindowDisplayAffinity`)**:
  - Added `StreamerModeService` utilizing Win32 `WDA_EXCLUDEFROMCAPTURE` (0x11) to make Shiro windows completely invisible on OBS, Discord, and Twitch screen captures.
  - Automatic background detection of streaming software (`obs64`, `Discord`, `Streamlabs OBS`).
  - Dynamic identity masking for usernames (`R**********r`) and Robux balances (`*** R$`) in `AccountViewModel`.
- **AltGen (Automated BloxGen API Integration)**:
  - Added `BloxGenService` supporting automated account generation by region (`AS`, `NA`, `EU`, `OC`, `SA`, `RANDOM`) and type (`unverified`, `verified`).
  - Added `AltGenDialog` for 1-click alt generation, automatic Vault import into group `AltGen`, and historical audit logging to `altgen_history.csv`.
- **Native Discord Rich Presence with "Join Game" Button**:
  - Implemented zero-dependency `DiscordRpcService` via asynchronous named pipe IPC (`\\.\pipe\discord-ipc-0..9`).
  - Features real-time multi-account status and a clickable "Tham gia Server" button for Discord friends.
- **Audio Feedback & Launcher Branding**:
  - Added native Win32 `mciSendString` MP3 audio notifications on `ToastService` using `Notify_sound.mp3`.
  - Added third-party launcher branding assets (Bloxstrap, Fishstrap, Voidstrap).

---

## [4.1.0] - 2026-10-28 (Milestone 10/10 Architecture & Hardening)

### Added
- **Architecture Quality Enforcement**:
  - Integrated `NetArchTest.eNhancedEdition` to enforce MVVM separation and layer isolation.
  - Added automated rule guaranteeing zero source files exceed 500 lines of code.
- **Security Hardening**:
  - Added `LogRedactor` service with Source-Generated Regular Expressions to automatically mask `.ROBLOSECURITY` cookies, passwords, and bearer tokens in logs.
  - Implemented `ZeroMemory` memory scrubbing for master vault keys on shutdown.
  - Added 256 KB request size limits and Origin / Loopback validation for Kestrel WebServer and Nexus WebSocket Server against CSWSH.
  - Added ToS & responsibility risk disclaimer on initial startup with safe defaults.
- **Embedded Resource Localization**:
  - Replaced massive C# string dictionaries with compiled JSON streams and `FrozenDictionary<string, string>` for O(1) allocation-free lookups.
  - Added Vietnamese and English localization catalogs.
- **CI/CD Automation**:
  - Added automated GitHub Actions release workflow with SHA-256 checksum generation.
  - Unified multi-target build using `Shiro.sln`.

### Changed
- **Decomposition**:
  - Refactored and decomposed 100% of monolithic files (> 500 lines) into focused, single-responsibility partial classes.
  - Completely decoupled UI views from background business logic and Windows native interop.
- **Testing**:
  - Expanded comprehensive test suite from 394 to 419 unit tests passing 100% with zero warnings and zero errors under `TreatWarningsAsErrors=true`.

---

## [4.0.0] - 2026-10-27 (Modernization Baseline)

### Added
- Pure Avalonia UI 11 modernization on .NET 10 (win-x64).
- Native AES-256-GCM + DPAPI encryption eliminating legacy crypto DLL drops.
- LaunchCoordinator with adaptive throttling and rate-limit mitigation.
- NTFS MFT record recovery and automated vault key recovery CLI.
