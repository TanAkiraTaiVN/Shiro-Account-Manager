# Shiro Account Manager — Security Threat Model (STRIDE)

This document outlines the security architecture, trust boundaries, threat vectors (STRIDE), and concrete mitigations implemented in **Shiro Account Manager**.

---

## 1. Protected Assets

| Asset | Sensitivity | Storage / Lifecycle |
| :--- | :--- | :--- |
| `.ROBLOSECURITY` Session Cookies | **Critical** | Encrypted at rest in `AccountData.json`; held in memory during active sessions; injected into isolated WebView2 / Roblox sessions. |
| Account Passwords & 2FA Secrets | **Critical** | Encrypted at rest in `AccountData.json` (when `SavePasswords` is enabled); never written to logs. |
| Vault Encryption Key (`VaultKey`) | **Critical** | Protected via Windows DPAPI (`DataProtectionScope.CurrentUser`) using Dual-KDF: **HKDF-SHA256 (V4)** for 256-bit machine keys and **PBKDF2-SHA256 (600,000 iterations, V3)** for user passwords with AES-256-GCM encryption. |
| HBA (Hardware-Backed Auth) ECDSA P-256 Private Keys | **High** | Bound per-account to sign `x-bound-auth-token` headers for Roblox API requests; protected by WebView2 `https://*.roblox.com` origin lock and Kestrel `Hba*` field access block. |
| Local Kestrel HTTP & WebSocket Gateway | **High** | Local Kestrel HTTP/WebSocket gateway (`127.0.0.1:7963`) providing REST & real-time WS events (`/ws`, `/events`, `/v2/events`). |
| Discord Webhook URLs & Telegram Bot Tokens | **High** | Encrypted at rest in `RAMSettings.json` via `CryptoHelper.ProtectSecretString` (DPAPI on standard installs, vault-key AES-256-GCM in portable mode). |
| Proxy Lists | **High** | Encrypted at rest in `proxies.txt` via `ProxyManagerService` using `CryptoHelper.ProtectSecretString` (DPAPI on standard installs, vault-key AES-256-GCM in portable mode). |

---

## 2. Trust Boundaries

1. **Process & User Boundary (Windows Session)**:
   - Shiro runs as a standard desktop process under the current Windows user. DPAPI (`CurrentUser`) protects vault secrets from other Windows accounts and offline disk inspection.
2. **Local Loopback Network Boundary (`127.0.0.1`)**:
   - Any website open in the user's browser can attempt cross-origin HTTP or WebSocket requests to `localhost`. Strict `Origin`, `Host`, and authentication checks defend against Cross-Site Request Forgery (CSRF) and Cross-Site WebSocket Hijacking (CSWSH).
3. **Embedded Browser Boundary (WebView2)**:
   - Each interactive `FastLoginDialog` session uses an isolated WebView2 user data directory inside `StoragePaths.BrowserSessionsDirectory` with password manager and sync disabled, and WebView2 HBA/autofill bridges locked to `https://*.roblox.com`.
4. **External Network Boundary (Roblox APIs, GitHub Releases & Proxies)**:
   - Outbound HTTPS traffic to `*.roblox.com`, read-only update checks to GitHub Releases (`UpdateCheckService`), and user-configured HTTP/SOCKS5 proxies.

---

## 3. STRIDE Threat Analysis & Mitigations

### S — Spoofing
- **Threat**: A malicious webpage in the user's browser attempts to connect to the local Kestrel HTTP or WebSocket gateway (`/ws`, `/events`, `/v2/events`) via CSWSH / DNS rebinding, or an untrusted frame attempts to invoke the WebView2 HBA bridge.
- **Mitigation** ([`WebAuthPolicy.cs`](../src/Shiro/Services/Auth/WebAuthPolicy.cs), [`KestrelWebServer.cs`](../src/Shiro/Services/Network/KestrelWebServer.cs)):
  - External listening is disabled by default (`127.0.0.1` loopback binding only).
  - `WebAuthPolicy.IsLoopbackHostName` rejects non-loopback `Host` headers (defeating DNS rebinding).
  - `WebAuthPolicy.IsTrustedLocalOrigin` and `WebAuthPolicy.IsOriginAllowed` reject untrusted browser `Origin` headers.
  - API and external requests require a configured password (`>= 6` chars on loopback, `>= 12` chars for external listening, enforced by `MinExternalPasswordLength = 12`) verified in constant time via `CryptographicOperations.FixedTimeEquals`, with 30-second lockouts after 5 failed attempts.
  - WebSocket handshakes (`/ws?token=`, `/events`, `/v2/events`) validate the gateway token/header password and `Origin` before upgrading connections.
  - WebView2 HBA token signing (`x-bound-auth-token`) and credential autofill scripts enforce a strict `https://*.roblox.com` origin lock.

### T — Tampering
- **Threat**: Modification or corruption of encrypted vault files (`AccountData.json`), tampering with internal `Hba*` fields via local REST APIs, path traversal during profile cleanup, or untrusted prerequisite installers.
- **Mitigation** ([`Cryptography.cs`](../src/Shiro/Services/Storage/Cryptography.cs), [`CryptoHelper.cs`](../src/Shiro/Services/Storage/CryptoHelper.cs), [`StoragePaths.cs`](../src/Shiro/Services/Storage/StoragePaths.cs), [`UpdateCheckService.cs`](../src/Shiro/Services/Network/UpdateCheckService.cs), [`PrerequisiteService.cs`](../src/Shiro/Services/Launch/PrerequisiteService.cs)):
  - Vault payloads use authenticated encryption (**AES-256-GCM**) with a 12-byte random nonce and 16-byte authentication tag (`SHIRO_SECURE_VAULT_V3` / `SHIRO_SECURE_VAULT_V4`, plus `SHIRO_VAULT_V2` in `VaultExportService`), detecting any ciphertext tampering.
  - Atomic file writes (`.tmp` write + flush + atomic rename + `.bak` rotation) prevent partial writes.
  - Kestrel `SetField`/`GetField` endpoints explicitly block access to internal `Hba*` security fields.
  - `StoragePaths.IsWithinDirectory` enforces strict canonical path-boundary checks before any directory deletion.
  - `PrerequisiteService` verifies Microsoft Authenticode signatures on VC++ Redistributable binaries prior to execution, and `UpdateCheckService` queries GitHub Releases over TLS.

### R — Repudiation & Information Disclosure in Logs
- **Threat**: Sensitive tokens (`.ROBLOSECURITY`, passwords, webhook URLs, Telegram bot tokens, bearer tokens) accidentally written to `logs/` or diagnostic output.
- **Mitigation** ([`LogRedactor.cs`](../src/Shiro/Services/Storage/LogRedactor.cs)):
  - Every log message passes through `LogRedactor.Redact` before reaching disk or console.
  - Automatically masks `_\|WARNING:-DO-NOT-SHARE-THIS...` cookies, `password=`, `secret=`, Telegram bot tokens, `Authorization: Bearer`, and JSON credential fields.

### I — Information Disclosure (Memory & Storage)
- **Threat**: Plaintext key material or decrypted backup payloads lingering in process memory or leaked via configuration files / exports.
- **Mitigation** ([`VaultKeyRecoveryService.cs`](../src/Shiro/Services/Storage/VaultKeyRecoveryService.cs), [`AccountPersistenceService.Recovery.cs`](../src/Shiro/Services/Storage/AccountPersistenceService.Recovery.cs), [`ProxyManagerService.cs`](../src/Shiro/Services/Network/ProxyManagerService.cs)):
  - Master key buffers and temporary decrypted backup buffers are wiped via `CryptographicOperations.ZeroMemory` when unloaded, rotated, or after recovery parsing.
  - Discord monitoring webhook URLs and Telegram bot tokens are encrypted via `CryptoHelper.ProtectSecretString` before saving to `RAMSettings.json`.
  - Encrypted proxy lists are persisted separately to `proxies.txt` via `ProxyManagerService` using `CryptoHelper.ProtectSecretString`.

### D — Denial of Service
- **Threat**: Resource exhaustion via oversized local HTTP requests, runaway Roblox child processes, or Roblox API rate-limiting (`429 Too Many Requests`).
- **Mitigation**:
  - Local Kestrel server enforces a 256 KB request body limit.
  - `RobloxRateLimitCoordinator` enforces per-category token-bucket pacing and exponential backoff across concurrent session checks and launches.
  - `KestrelWebServer` uses bounded `ArrayPool<byte>` buffers and per-socket `SemaphoreSlim` write gates.

### E — Elevation of Privilege
- **Threat**: Malicious game server responses or crafted launch URLs executing arbitrary commands.
- **Mitigation**:
  - `LaunchCoordinator`, `RobloxDirectLauncher` (`SanitizeJobId`, `SanitizeCliArgumentValue`, `SanitizeBrowserTrackerId`), and `RobloxProcess` validate PlaceId/JobId/tracker inputs and invoke `RobloxPlayerBeta.exe` directly via `ProcessStartInfo(playerPath, args)` or `CreateProcessWithTokenW` rather than shell command interpolation.
