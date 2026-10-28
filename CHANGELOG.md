# Changelog

All notable changes to the Shiro Account Manager project will be documented in this file.

The format is based on [Keep a Changelog](https://keepachangelog.com/en/1.0.0/),
and this project adheres to [Semantic Versioning](https://semver.org/spec/v2.0.0.html).

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
