# Security Policy

## Supported Versions

We actively provide security patches and hardening updates for the following versions:

| Version | Supported          |
| ------- | ------------------ |
| 4.1.x   | :white_check_mark: |
| 4.0.x   | :x:                |
| < 4.0   | :x:                |

---

## Security Architecture & Threat Model

Shiro Account Manager handles sensitive user credentials, including Roblox security cookies and authentication tokens. The following principles govern the project's security posture:

1. **Client-Side Only**:
   - Shiro never transmits user credentials, keys, or telemetry to third-party or remote servers. All communications occur directly between the local computer and official Roblox endpoints (`*.roblox.com`).
2. **Zero-Drop Native Cryptography**:
   - AES-256-GCM authenticated encryption and PBKDF2-SHA256 password derivation are executed via pure .NET `System.Security.Cryptography`, requiring zero untrusted unmanaged DLL drops.
   - Master vault keys are protected using Windows Data Protection API (DPAPI) bound to the current Windows user profile.
3. **Memory Hardening**:
   - Cryptographic keys in memory are scrubbed with `CryptographicOperations.ZeroMemory` upon application shutdown.
4. **Log Redaction**:
   - The application logger automatically sanitizes sensitive fields, cookies, and tokens using high-performance Source-Generated Regular Expressions.
5. **Local Web Server Hardening**:
   - Loopback verification and Origin checking prevent Cross-Site Scripting (XSS) and Cross-Site WebSocket Hijacking (CSWSH) from malicious browser tabs.
   - Constant-time comparison is enforced for all password verifications.
   - Request bodies are strictly capped at 256 KB to prevent memory exhaustion denial of service.

---

## Reporting a Vulnerability

If you discover a security vulnerability within Shiro Account Manager, please do **NOT** open a public issue.

Please report the vulnerability responsibly by opening a Private Vulnerability Advisory on GitHub or contacting the maintainers directly.

When reporting, please provide:
- A clear description of the vulnerability.
- Steps or a proof-of-concept to reproduce the issue.
- Potential impact and threat assessment.

We take all security reports seriously and will acknowledge receipt within 48 hours and work diligently on an expedited resolution.
