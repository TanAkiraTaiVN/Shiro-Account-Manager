# Changelog

All notable changes to the Shiro Account Manager project will be documented in this file.

The format is based on [Keep a Changelog](https://keepachangelog.com/en/1.0.0/),
and this project adheres to [Semantic Versioning](https://semver.org/spec/v2.0.0.html).

---

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
  - Added third-party launcher branding assets (Bloxstrap, Fishstrap, Froststrap, Voidstrap).

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
