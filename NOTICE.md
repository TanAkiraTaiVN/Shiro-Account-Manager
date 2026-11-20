# Third-Party Notices & GPL-3.0 Attribution

## Upstream Project Attribution (GPL-3.0 Section 5a Notice)

**Shiro Account Manager** (`v5.0.0`, 2026) is a modified and modernized derivative work based on:

- **Original Project**: [Roblox Account Manager (RBX Alt Manager)](https://github.com/ic3w0lf22/Roblox-Account-Manager)
- **Original Author**: `ic3w0lf22` and contributors
- **License**: GNU General Public License v3.0 (GPL-3.0)

### Summary of Major Modifications in Shiro Account Manager (2026)
1. **Runtime & UI Modernization**: Replaced legacy .NET Framework 4.7.2, WinForms, and CefSharp with **.NET 10 (`win-x64`)**, **Avalonia UI 11 (Skia)**, and **Microsoft Edge WebView2**.
2. **Embedded Web & WebSocket Gateway**: Replaced legacy `HttpListener` and `Fleck` with **ASP.NET Core Kestrel** (`KestrelWebServer` and `LocalKestrelWebServer`) supporting REST and WebSocket event streams (`/ws`, `/events`, `/v2/events`) with loopback/origin validation and constant-time authentication.
3. **Authenticated Vault Cryptography**: Replaced legacy encryption with pure .NET 10 **AES-256-GCM + Dual-KDF (PBKDF2-SHA256 600,000 iterations V3 for user passwords + HKDF-SHA256 V4 for 256-bit machine keys)** and **Windows DPAPI (`CurrentUser`)** key wrapping (`SHIRO_SECURE_VAULT_V3` / `SHIRO_SECURE_VAULT_V4` / `SHIRO_DPAPI_KEY_V1`, plus `SHIRO_VAULT_V2` in `VaultExportService`).
4. **Architecture & Testing**: Introduced strongly-typed MVVM ViewModels, atomic file persistence (`StoragePaths`), structured secret-redacting logging (`LogRedactor`), and an automated xUnit + Avalonia Headless + NetArchTest test suite.
