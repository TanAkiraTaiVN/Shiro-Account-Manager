# God Classes Refactoring Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Refactor and decouple the core God Classes in Shiro-Account-Manager (`AccountManager`, `Account`, `MainViewModel`, and `ServerListViewModel`) into cohesive, single-responsibility services while preserving 100% backward compatibility and passing all 647 unit tests.

**Architecture:**
1. **Phase 1 (`AccountManager` WebServer Separation):** Extract Kestrel HTTP request routing, security guards, and all 15 REST endpoints from `AccountManager.WebServer.cs` and `AccountManager.WebServer.Endpoints.cs` into `AccountApiDispatcher` in `src/Shiro/Services/Network/`. Keep delegation wrappers in `AccountManager` for backward compatibility.
2. **Phase 2 (`Account` Entity Decoupling):** Extract external Roblox HTTP APIs and HBA cryptographic operations out of `Account` entity into dedicated services (`RobloxEconomyService`, `RobloxModerationService`, `RobloxSocialApiService`, `RobloxSettingsApiService` in `src/Shiro/Services/Network/`). `Account` delegates to these services.
3. **Phase 3 (`ServerListViewModel` Service Extraction):** Extract direct HTTP calls and Roblox Games API response parsers from `ServerListViewModel` into `IRobloxServerQueryService` / `RobloxServerQueryService`.
4. **Phase 4 (`MainViewModel` Decomposition):** Extract Command Palette parsing, indexing, and execution from `MainViewModel.CommandPalette.cs` into `CommandPaletteCoordinator` in `src/Shiro/Services/Ui/`.

**Tech Stack:** .NET 10 (`net10.0-windows10.0.19041.0`), C# 13, Avalonia UI 11, Kestrel, xUnit, NetArchTest.

## Global Constraints
- Every file in `src/Shiro/` must adhere to `ArchitectureTests.NoSourceCodeFiles_ShouldExceed500Lines()` (<= 500 lines).
- All new services in `Shiro.Services` must follow the naming convention in `ArchitectureTests.Services_ShouldFollowNamingConvention()`.
- No regression: All 647 existing unit tests must continue to pass after every single task.
- Zero public API breaking changes: Keep method forwarding where external callers or UI bindings expect existing signatures.

## Review Focus
1. Backward compatibility of `AccountManager.ProcessApiRequestAsync` and `AccountManager.SendKestrelResponse`.
2. HBA binding and session fingerprint generation fidelity in `Account` when delegated.
3. Thread safety when accessing `AccountManager.AccountsList` or session state caches.
4. Error handling in HTTP API services (ensure 429 rate limits, network timeouts, and proxy errors are handled consistently).
5. Architecture test compliance: verify namespace rules, view dependency rules, and naming rules.

---

### Task 1: Extract `AccountApiDispatcher` from `AccountManager` WebServer Partial Classes

**Files:**
- Create: `src/Shiro/Services/Network/AccountApiDispatcher.cs`
- Create: `src/Shiro/Services/Network/AccountApiDispatcher.Endpoints.cs`
- Modify: `src/Shiro/AccountManager.WebServer.cs`
- Modify: `src/Shiro/AccountManager.WebServer.Endpoints.cs`
- Test: `tests/Shiro.Tests/WebServerEndpointsAndWorkflowTests.cs`
- Test: `tests/Shiro.Tests/ArchitectureTests.cs`

- [ ] **Step 1: Create `AccountApiDispatcher` and `AccountApiDispatcher.Endpoints.cs`**
  Move `ApiRequestContext`, HTTP request parsing, authentication checks, CORS, rate limits, and endpoint implementations (`GetAccounts`, `LaunchAccount`, `SetField`, `GetWebSocketStats`, etc.) into `AccountApiDispatcher`.
- [ ] **Step 2: Update `AccountManager.WebServer.cs` to delegate to `AccountApiDispatcher`**
  Replace monolithic logic in `AccountManager.WebServer.cs` with calls to `AccountApiDispatcher.Instance.DispatchAsync(...)`.
- [ ] **Step 3: Run tests to verify all WebServer endpoints pass**
  Run `dotnet test --filter WebServerEndpointsAndWorkflowTests` and `dotnet test --filter ArchitectureTests`.

---

### Task 2: Extract Roblox Web API & Moderation Services from `Account` Entity

**Files:**
- Create: `src/Shiro/Services/Network/RobloxEconomyService.cs`
- Create: `src/Shiro/Services/Network/RobloxModerationService.cs`
- Create: `src/Shiro/Services/Network/RobloxSocialApiService.cs`
- Create: `src/Shiro/Services/Network/RobloxSettingsApiService.cs`
- Modify: `src/Shiro/Classes/Account.Api.cs`
- Modify: `src/Shiro/Classes/Account.Moderation.cs`
- Modify: `src/Shiro/Classes/Account.SocialAndFriendsApi.cs`
- Modify: `src/Shiro/Classes/Account.SettingsApi.cs`
- Test: `tests/Shiro.Tests/ServerListAndAccountSettingsTests.cs`
- Test: `tests/Shiro.Tests/ArchitectureTests.cs`

- [ ] **Step 1: Create `RobloxEconomyService` & `RobloxModerationService`**
  Implement `FetchRobuxBalanceAsync` and `CheckAccountStatusAsync` with rate-limiting and session cookies in the new services.
- [ ] **Step 2: Create `RobloxSocialApiService` & `RobloxSettingsApiService`**
  Extract friend listing, friend removing, display name updates, and privacy settings requests.
- [ ] **Step 3: Refactor `Account.*Api.cs` to delegate to these services**
  Forward calls from `Account` to the new services so existing callers remain untouched.
- [ ] **Step 4: Run test suite**
  Verify with `dotnet test`.

---

### Task 3: Extract `CommandPaletteCoordinator` from `MainViewModel`

**Files:**
- Create: `src/Shiro/Services/Ui/CommandPaletteCoordinator.cs`
- Modify: `src/Shiro/ViewModels/MainViewModel.CommandPalette.cs`
- Test: `tests/Shiro.Tests/UiUxUpgradeTests.cs`
- Test: `tests/Shiro.Tests/ArchitectureTests.cs`

- [ ] **Step 1: Create `CommandPaletteCoordinator` in `Shiro.Services`**
  Encapsulate built-in command registry, fuzzy query filtering, and keyboard shortcut dispatching.
- [ ] **Step 2: Wire `MainViewModel.CommandPalette.cs` to use `CommandPaletteCoordinator`**
- [ ] **Step 3: Run full verification suite**
  Run `dotnet test` to confirm all 647+ tests pass.
