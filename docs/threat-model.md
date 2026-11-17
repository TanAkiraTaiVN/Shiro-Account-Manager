# Shiro Account Manager — Security Threat Model (STRIDE)

This document outlines the security architecture, trust boundaries, threat vectors (STRIDE), and concrete mitigations implemented in **Shiro Account Manager**.

---

## 1. Protected Assets

| Asset | Sensitivity | Storage / Lifecycle |
| :--- | :--- | :--- |
| `.ROBLOSECURITY` Session Cookies | **Critical** | Encrypted at rest in `AccountData.json`; held in memory during active sessions; injected into isolated Roblox / Chromium profiles. |
| Account Passwords & 2FA Secrets | **Critical** | Encrypted at rest in `AccountData.json` (when `SavePasswords` is enabled); never written to logs. |
| Vault Encryption Key (`VaultKey`) | **Critical** | Protected via Windows DPAPI (`DataProtectionScope.CurrentUser`) using Dual-KDF: **HKDF-SHA256 (V4)** for 256-bit machine keys and **PBKDF2-SHA256 (600,000 iterations, V3)** for user passwords with AES-256-GCM encryption. |
| HBA (Hardware-Backed Auth) ECDSA P-256 Private Keys | **High** | Bound per-account to sign `x-bound-auth-token` headers for Roblox API requests; protected by WebView2 `https://*.roblox.com` origin lock and Kestrel `Hba*` field access block. |
| Local Kestrel HTTP & WebSocket Gateway | **High** | Local Kestrel HTTP/WebSocket gateway (`127.0.0.1:7963`) providing REST & real-time WS events (`/ws`, `/events`, `/v2/events`). |
| Discord Webhook URLs & Third-Party API Keys | **High** | Encrypted at rest in `Settings.ini` via Windows DPAPI (`CryptoHelper.ProtectSecretString`). |

---

## 2. Trust Boundaries

1. **Process & User Boundary (Windows Session)**:
   - Shiro runs as a standard desktop process under the current Windows user. DPAPI (`CurrentUser`) protects vault secrets from other Windows accounts and offline disk inspection.
2. **Local Loopback Network Boundary (`127.0.0.1`)**:
   - Any website open in the user's browser can attempt cross-origin HTTP or WebSocket requests to `localhost`. Strict `Origin`, `Host`, and authentication checks defend against Cross-Site Request Forgery (CSRF) and Cross-Site WebSocket Hijacking (CSWSH).
3. **Embedded Browser Boundary (WebView2 & Chromium CDP)**:
   - Each interactive or automated browser session uses an isolated profile directory inside `StoragePaths.BrowserSessionsDirectory` with password manager and sync disabled, and WebView2 HBA/autofill bridges locked to `https://*.roblox.com`.
4. **External Network Boundary (Roblox APIs, Updaters & Proxies)**:
   - Outbound HTTPS traffic to `*.roblox.com`, GitHub Releases, and user-configured HTTP/SOCKS5 proxies.

---

## 3. STRIDE Threat Analysis & Mitigations

### S — Spoofing
- **Threat**: A malicious webpage in the user's browser attempts to connect to the local Kestrel WebServer or Nexus WebSocket server (CSWSH / DNS rebinding), or an untrusted frame attempts to invoke the WebView2 HBA bridge.
- **Mitigation** ([`WebAuthPolicy.cs`](../src/Shiro/Services/Auth/WebAuthPolicy.cs), [`KestrelWebServer.cs`](../src/Shiro/Services/Network/KestrelWebServer.cs)):
  - External listening is disabled by default (`127.0.0.1` loopback binding only).
  - `AccountApiDispatcher.IsLoopbackHostName` rejects non-loopback `Host` headers (defeating DNS rebinding).
  - `AccountApiDispatcher.IsTrustedLocalOrigin` rejects untrusted browser `Origin` headers.
  - API and external requests require a configured password (`>= 6` chars on loopback, `>= 12` chars for external listening) verified in constant time via `CryptographicOperations.FixedTimeEquals`.
  - Nexus WebSocket client registration verifies `UserID` ownership against the authenticated account record.
  - WebView2 HBA token signing (`x-bound-auth-token`) and credential autofill scripts enforce a strict `https://*.roblox.com` origin lock.

### T — Tampering
- **Threat**: Modification or corruption of encrypted vault files (`AccountData.json`), tampering with internal `Hba*` fields via local REST APIs, path traversal during profile cleanup, or untrusted update/prerequisite binaries.
- **Mitigation** ([`CryptoHelper.cs`](../src/Shiro/Services/Storage/CryptoHelper.cs), [`StoragePaths.cs`](../src/Shiro/Services/Storage/StoragePaths.cs), [`UpdateCheckService.cs`](../src/Shiro/Services/Network/UpdateCheckService.cs), [`PrerequisiteService.cs`](../src/Shiro/Services/Launch/PrerequisiteService.cs)):
  - Vault payloads use authenticated encryption (**AES-256-GCM**) with a 12-byte random nonce and 16-byte authentication tag (`SHIRO_SECURE_VAULT_V3` / `SHIRO_SECURE_VAULT_V4`), detecting any ciphertext tampering.
  - Atomic file writes (`.tmp` write + flush + atomic rename + `.bak` rotation) prevent partial writes.
  - Kestrel `SetField`/`GetField` endpoints explicitly block access to internal `Hba*` security fields.
  - `StoragePaths.IsWithinDirectory` enforces strict canonical path-boundary checks before any directory deletion.
  - `PrerequisiteService` verifies Microsoft Authenticode signatures on VC++ Redistributable binaries prior to execution, and `UpdateCheckService` queries GitHub Releases over TLS.

### R — Repudiation & Information Disclosure in Logs
- **Threat**: Sensitive tokens (`.ROBLOSECURITY`, passwords, API keys, bearer tokens) accidentally written to `logs/` or diagnostic output.
- **Mitigation** ([`LogRedactor.cs`](../src/Shiro/Services/Storage/LogRedactor.cs)):
  - Every log message passes through `LogRedactor.Redact` before reaching disk or console.
  - Automatically masks `_\|WARNING:-DO-NOT-SHARE-THIS...` cookies, `password=`, `secret=`, `Authorization: Bearer`, and JSON credential fields.

### I — Information Disclosure (Memory & Storage)
- **Threat**: Plaintext key material or decrypted backup payloads lingering in process memory or leaked via configuration files / exports.
- **Mitigation** ([`VaultKeyRecoveryService.cs`](../src/Shiro/Services/Storage/VaultKeyRecoveryService.cs), [`AccountPersistenceService.Recovery.cs`](../src/Shiro/Services/Storage/AccountPersistenceService.Recovery.cs)):
  - Master key buffers and temporary decrypted backup buffers are wiped via `CryptographicOperations.ZeroMemory` when unloaded, rotated, or after recovery parsing.
  - Discord monitoring webhook URLs are encrypted with DPAPI via `CryptoHelper.ProtectSecretString` before saving to `Settings.ini`.

### D — Denial of Service
- **Threat**: Resource exhaustion via oversized local HTTP requests, runaway Roblox child processes, or Roblox API rate-limiting (`429 Too Many Requests`).
- **Mitigation**:
  - Local Kestrel server enforces a 256 KB request body limit.
  - `RobloxRateLimitCoordinator` enforces per-category token-bucket pacing and exponential backoff across concurrent session checks and launches.
  - `KestrelWebServer` uses bounded `ArrayPool<byte>` buffers and per-socket `SemaphoreSlim` write gates.

### E — Elevation of Privilege
- **Threat**: Malicious game server responses or crafted launch URLs executing arbitrary commands.
- **Mitigation**:
  - `RobloxLauncherService` and `RobloxProcess` validate PlaceId/JobId formats and invoke `RobloxPlayerBeta.exe` with `UseShellExecute = false` and explicit argument lists rather than shell interpolation.
