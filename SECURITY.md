# Security Policy

## Supported Versions

We actively provide security patches and hardening updates for the following versions:

| Version | Supported          |
| ------- | ------------------ |
| 5.0.x   | :white_check_mark: |
| 4.2.x   | :x:                |
| < 4.2   | :x:                |

---

## Security Architecture & Threat Model

Shiro Account Manager handles sensitive user credentials, including Roblox security cookies and authentication tokens. The following principles govern the project's security posture:

1. **Client-Side Only**:
   - Shiro never transmits user credentials, keys, or telemetry to third-party or remote servers. All communications occur directly between the local computer and official Roblox endpoints (`*.roblox.com`).
2. **Zero-Drop Native Cryptography & Dual-KDF Architecture**:
   - AES-256-GCM authenticated encryption is executed via pure .NET `System.Security.Cryptography`, requiring zero untrusted unmanaged DLL drops.
   - Human-chosen master passwords use **PBKDF2-SHA256 (600,000 iterations, V3)** per OWASP recommendations (`SHIRO_SECURE_VAULT_V3` / `SHIRO_VAULT_V3`).
   - High-entropy 256-bit machine keys (`VaultKey`) protected by Windows Data Protection API (DPAPI `CurrentUser`) use fast **HKDF-SHA256 (V4)** key derivation (`SHIRO_SECURE_VAULT_V4`).
   - Sensitive configuration settings (including Discord monitoring webhook URLs and third-party API keys) are encrypted at rest via DPAPI (`ProtectedData`).
3. **Memory Hardening**:
   - Cryptographic keys and decrypted account backup buffers in memory are scrubbed with `CryptographicOperations.ZeroMemory` after use and upon application shutdown.
4. **Log Redaction**:
   - The application logger automatically sanitizes sensitive fields, cookies, and tokens using high-performance Source-Generated Regular Expressions.
5. **WebView2 & HBA Origin Lock**:
   - Embedded WebView2 HBA (`x-bound-auth-token`) fetch bridges and credential autofill scripts enforce strict `https://*.roblox.com` origin validation before executing or returning sensitive tokens.
6. **Local Web Server & HTTP/WebSocket Gateway Hardening**:
   - Loopback verification, DNS rebinding protection, and Origin checking prevent Cross-Site Scripting (XSS) and Cross-Site WebSocket Hijacking (CSWSH) from malicious browser tabs.
   - Constant-time comparison (`CryptographicOperations.FixedTimeEquals`) is enforced for all password verifications, with a 12-character minimum password requirement for external listening.
   - Kestrel `SetField`/`GetField` APIs block internal `Hba*` security fields, and request bodies are strictly capped at 256 KB to prevent memory exhaustion denial of service.
7. **Supply-Chain & Prerequisite Integrity**:
   - Update checks are performed read-only over HTTPS against GitHub Releases (`UpdateCheckService`) without background binary staging or self-modifying PowerShell scripts.
   - Prerequisite installers (`vc_redist.x64.exe`) are verified via Windows **Authenticode** (`WinVerifyTrust` full-chain revocation check + Microsoft signer validation in `PrerequisiteService`) prior to execution.

---

## Reporting a Vulnerability

If you discover a security vulnerability within Shiro Account Manager, please do **NOT** open a public issue.

Please report the vulnerability responsibly by opening a Private Vulnerability Advisory on GitHub or contacting the maintainers directly.

When reporting, please provide:
- A clear description of the vulnerability.
- Steps or a proof-of-concept to reproduce the issue.
- Potential impact and threat assessment.

We take all security reports seriously and will acknowledge receipt within 48 hours and work diligently on an expedited resolution.
