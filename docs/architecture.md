# Shiro Account Manager - Architecture Documentation

## 1. System Overview

**Shiro Account Manager** is a high-performance, enterprise-grade account manager and multi-instance orchestration platform built specifically for Roblox players, developers, and power users on Windows.

### Core Architecture Principles:
- **Framework & Runtime**: Pure modern .NET 10 (win-x64), C# 13, Avalonia UI 11.
- **Zero Native DLL Drop**: Direct usage of `System.Security.Cryptography` (AES-256-GCM, PBKDF2-SHA256, DPAPI).
- **Clean Architecture & Strict MVVM**: ViewModels never touch Windows API or UI Window instances directly. Verified automatically by `NetArchTest`.
- **High-Performance Memory & IO**: `ArrayPool<byte>`, `FrozenDictionary<string, string>` for O(1) localized string lookups, and Channels for non-blocking logging.
- **Micro-File Modularization**: 100% of source files strictly budgeted under 500 lines of code.

---

## 2. Architectural Layers

```mermaid
graph TD
    subgraph UI_Layer ["Presentation Layer (Avalonia UI 11)"]
        MainWindow["MainWindow.axaml"]
        Dashboard["DashboardView.axaml"]
        Dialogs["Dialog Windows & Controls"]
        Converters["IValueConverter & Visual Styles"]
    end

    subgraph VM_Layer ["ViewModel Layer (MVVM Foundation)"]
        MainVM["MainViewModel"]
        AccountVM["AccountViewModel"]
        ServerListVM["ServerListViewModel"]
    end

    subgraph Core_Services ["Domain & Service Layer"]
        LaunchCoord["LaunchCoordinator"]
        DirectLaunch["RobloxDirectLauncher"]
        ProcService["RobloxProcessService"]
        AudioService["RobloxAudioService (WASAPI)"]
        AntiAfk["AntiAfkService"]
        ProxyMgr["ProxyManagerService"]
        KestrelSvc["KestrelWebServer & NexusWebSocket"]
        LocSvc["LocalizationService (FrozenDictionary)"]
    end

    subgraph Storage_Security ["Infrastructure & Security Layer"]
        Storage["StoragePaths (Atomic I/O)"]
        Persistence["AccountPersistenceService & AccountRegistrationService"]
        Crypto["CryptoHelper (AES-256-GCM)"]
        Recovery["VaultKeyRecoveryService (NTFS Scanner)"]
        Logger["AppLogger & LogRedactor"]
    end

    UI_Layer --> VM_Layer
    VM_Layer --> Core_Services
    Core_Services --> Storage_Security
```

---

## 3. Account Launch & Orchestration Flow

```mermaid
sequenceDiagram
    autonumber
    actor User as User / Automation
    participant VM as MainViewModel / AccountVM
    participant Coord as LaunchCoordinator
    participant Direct as RobloxDirectLauncher
    participant Proc as RobloxProcessService
    participant Client as Roblox Client (Windows)

    User->>VM: Request Launch Account(s)
    VM->>Coord: Enqueue(LaunchRequest)
    Coord->>Coord: Throttle / Rate-Limit Gate (ProtonVPN / Local IP)
    
    alt Direct DPAPI Injection (Preferred)
        Coord->>Direct: Inject LocalStorage auth token
        Direct->>Proc: Start RobloxPlayerBeta.exe
    else Web Authentication Ticket Flow
        Coord->>Coord: Request Auth Ticket from Roblox API
        Coord->>Proc: Launch via roblox-player protocol
    end

    Proc->>Client: Spawn Process & Assign to JobObject
    Proc-->>Coord: Process ID & Handle
    Coord->>VM: Update Presence & UI State
    Coord->>User: Launch Confirmed (Audio & Focus managed)
```

---

## 4. Security Hardening Specifications

1. **Zero-Memory Key Clearing (`ZeroMemory`)**:
   - Master encryption key references in `StoragePaths.VaultKey` are explicitly wiped using `CryptographicOperations.ZeroMemory` upon shutdown or credential erasure.
2. **Log Redaction (`LogRedactor`)**:
   - `AppLogger` intercepts and redacts `.ROBLOSECURITY` tokens, cookie values, passwords, query credentials, and bearer tokens before file and console write.
3. **Web Server & WebSocket Protection**:
   - Web Server requests enforce a maximum payload limit of 256 KB.
   - External connections are disabled by default.
   - Loopback verification (`IPAddress.IsLoopback`) and `Origin` header validation prevent Cross-Site WebSocket Hijacking (CSWSH).
   - Constant-time password verification (`FixedTimeEquals`) prevents timing side-channel attacks.
4. **Data Isolation**:
   - Path-traversal checks (`StoragePaths.IsWithinDirectory`) ensure browser profile sessions and temporary workspaces are securely isolated.

---

## 5. Architectural Quality Enforcement

Architecture rules are permanently verified on every build via `NetArchTest.eNhancedEdition` in `tests/Shiro.Tests/ArchitectureTests.cs`:
- **MVVM Separation**: `Types.InAssembly(CoreAssembly).That().ResideInNamespace("Shiro.ViewModels").ShouldNot().HaveDependencyOnAny("Shiro.AvaloniaUI.Views")`.
- **Service Decoupling & DI (`AppServices`)**: Services in `Shiro.Services` resolve dependencies via `AppServices` (`IAccountRepository`, `IRobloxApiClient`, `IRobloxSessionService`, `IRobloxLauncherService`, `IDialogService`, `AppSettings`) rather than static `AccountManager.*` state.
- **Zero Sync-over-Async**: Automated checks verify zero `.GetAwaiter().GetResult()` blocking calls across `src/Shiro`.
- **Encoding & Localization Integrity**: Automated checks enforce zero `U+FFFD` replacement characters across `.cs`, `.axaml`, and `.json` files, and verify 100% of AXAML `{DynamicResource}` localization keys exist in `LocalizationService`.
- **Micro-File & Partial Class Budgets**: Automated tests enforce <= 500 lines per `.cs` file and strict line budgets across `partial class` definitions.
- **Naming Conventions**: Strict adherence to domain suffixes across the entire `Shiro.Services` namespace.

---

## 6. Dialog MVVM Architecture & Headless Decoupling

To ensure 100% testability and prevent UI thread deadlocks, all dialogs in Shiro Account Manager are architected with a decoupled MVVM foundation:
- **`DialogViewModelBase`**: Unified base class for dialog ViewModels, managing title, status text, reactive `StatusKind` (Idle, Busy, Success, Error), and standard confirmation/cancellation request events (`RequestClose`).
- **`IUserPrompt` Abstraction**: Replaces intrusive synchronous or asynchronous `MessageBox` calls with an injectable interface. In headless unit tests, mock implementations (`Substitute.For<IUserPrompt>()`) supply deterministic user responses (Accept, Decline, Retry) without showing UI dialogs.
- **`FastLoginCoordinator` & `FastLoginQueue`**: Separates WebView2 DOM injection, URL inspection, and batch queue state from the Avalonia code-behind.
- **Headless Test Coverage**: All dialog ViewModels (`NexusViewModel`, `FastLoginDialogViewModel`, `CookieImportViewModel`, `AltGenViewModel`, `SettingsViewModel`) are exercised by headless xUnit suites without Avalonia windowing dependencies.

---

## 7. Nexus Controller Architecture (`INexusHost`)

The Nexus multi-client controller provides real-time bi-directional telemetry and execution orchestration between Shiro and running Roblox client sessions:
- **`INexusHost` Interface**: Abstracts the hosting environment for Nexus WebSocket clients, anti-AFK heartbeat dispatching, and remote Lua script execution (`RAMAccount.lua`).
- **`NexusHost.Current`**: Thread-safe active host registry allowing seamless switching between production Kestrel WebSocket services and isolated test harnesses.
- **Security & Concurrency**:
  - Validates client session tokens and origin integrity.
  - Enforces loopback restriction for unauthenticated endpoints.
  - Constant-time password comparison protects against side-channel analysis.

---

## 8. Source Directory Structure

```
src/Shiro/
├── AvaloniaUI/          # Presentation layer: Views, Controls, Converters, Assets
├── Domain/              # Core domain models: Account, Avatar, GameClass, Server, NaturalStringComparer
├── Http/                # HTTP client infrastructure (RestClient)
├── Nexus/               # Multi-client controller, WebSocket protocols, ControlledAccount
├── Services/            # Business logic, orchestration, and infrastructure services
│   ├── Auth/            # Authentication, session auditing, HBA crypto, BloxGen
│   ├── Browser/         # Web browser automation (ChromiumHomeBrowser, RobloxBrowserLogin/Signup, AccountBrowser)
│   ├── Launch/          # Roblox launch orchestration, batch operations, client patchers
│   ├── Network/         # External API dispatching, RPC, presence monitoring
│   ├── Storage/         # Atomic file storage, account persistence (AccountPersistenceService, AccountRegistrationService), serializers, vault encryption
│   ├── Ui/              # Theme, dialogs, localization, toasts
│   └── Windows/         # Windows process monitoring (RobloxProcess, RobloxWatcher), window arrangement
├── ViewModels/          # Strict MVVM ViewModels, dialog ViewModels, command palette
├── Program.cs           # Application entry point, single-instance mutex, crash handler
└── Shiro.csproj         # .NET 10 project definition
```


---

## 9. Platform Scope & Accepted Risks

### 1. Windows x64 Exclusive Scope
Shiro targets `net10.0-windows` (`win-x64`) exclusively due to deep Win32 integrations:
- **DPAPI Secret Protection**: Windows Data Protection API for protecting sensitive credentials at rest.
- **WebView2 Embedded Chromium**: Windows-specific embedded browser control for Roblox web sessions and authentication flows.
- **Win32 Job Objects**: Process management and lifecycle grouping for spawned Roblox client instances.
- **WASAPI Audio Muting**: Low-level Windows Core Audio APIs for individual client audio management.
- **Window Arrangement & Tiling**: Native Win32 API calls (`SetWindowPos`, `GetWindowRect`, window enumeration) for desktop multi-window tiling.
- **System Tray**: Windows shell tray notification icons and background execution.

All native Win32 interop and process monitoring implementations are strictly isolated under `Services/Windows/` and `Services/Launch/`. Cross-platform execution (Linux/macOS) is explicitly out of scope.

### 2. Roblox Terms of Service (Accepted Product Risk)
Certain capabilities provided by Shiro—including multi-instance mutex management (`ROBLOX_singletonMutex`), `.ROBLOSECURITY` session handling, and anti-AFK automation—carry inherent Roblox Terms of Service (ToS) risk.
This is an **accepted product risk** explicitly disclosed to users in `README.md`. No architectural bypass or obfuscation layers are introduced; users assume responsibility for usage compliance.
