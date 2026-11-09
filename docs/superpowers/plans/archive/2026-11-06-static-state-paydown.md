# Static State Paydown Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Restore a green baseline, then remove the three worst sources of mutable global state (static `RestClient` fields, the public `AccountsList`, and the `AppServices` setters), and move the CI regex checks into tests. Runtime behavior stays the same.

**Architecture:** Five tasks, each one shippable on its own. Task 1 is a gate. Tasks 2–4 each remove one kind of static state. Each also adds a reflection or NetArchTest ratchet in `tests/Shiro.Tests/ArchitectureTests.*.cs`, so that state cannot come back. Task 5 moves the `build.yml` grep checks into xUnit, where the existing `ArchitectureTests` pattern already lives.

**Tech Stack:** .NET 10, Avalonia 11, xUnit + Avalonia.Headless.XUnit, NSubstitute, NetArchTest.eNhancedEdition, coverlet, Microsoft.Extensions.DependencyInjection.

**Spec:** Project review in conversation 00fcecb2 (2026-11-06), which scored the project 7/10. Findings, all confirmed against the code:
- Working tree: 1 failing test out of 1769: `AccountManagerBehaviorTests.ModernActions_LaunchHome_MultiLaunch_JoinServerDirect_CloseRoblox_CoverBranches` (line 324, `Assert.Null(AccountManager.SelectedAccount)` gets an `Account`).
- `AccountManager.cs:51-63`: 13 `public static RestClient` fields. `AccountManager.Lifecycle.cs:78-91` builds a second `RobloxApiClient` and copies its clients into these fields. `RobloxApiClient.cs:34-46` then reads them back (`AccountManager.MainClient ?? _main`). This makes a circular dependency with two client instances.
- `AccountManager.cs:30`: `internal static List<Account> AccountsList` is mutated directly by 46 test sites, which skips `AccountListLock`.
- `AppServices.cs`: public setters call `ReplaceService`, which rebuilds the whole container. Only tests use them (48 sites in 16 files, 0 in `src`). Together with the static state above, this forces `[assembly: CollectionBehavior(DisableTestParallelization = true)]`.
- `.github/workflows/build.yml:47-81`: 7 architecture checks written as PowerShell regex.

Out of scope (already checked and fine): `RelayCommand`/`FastLoginDialog` `async void` (wrapped in try/catch, per `2026-11-05-review-followups.md`).

## Global Constraints
- `dotnet build Shiro.sln -c Release`: 0 errors, 0 warnings.
- `dotnet test tests/Shiro.Tests/Shiro.Tests.csproj -c Release`: 0 failed. The total test count may only go up.
- Coverage gate unchanged: `./scripts/Check-Coverage.ps1 -MinOverall 75.0 -MinBranch 60.0 -MinCore 92.0`. The floors in `tests/coverage-floors.json` may only go up.
- `ArchitectureTests.NoSourceCodeFiles_ShouldExceed500Lines` stays green. New files must be ≤ 500 lines.
- Do not change public behavior, endpoints, base URLs, or persisted file formats.
- One commit per task, using Conventional Commit prefixes (`fix:`, `refactor:`, `test:`, `ci:`).

## Review Focus
1. **Startup order:** code that resolves `AppServices.RobloxApi` before the `AccountManager` constructor runs must get the same client instance that is used after startup. Task 2 test `RobloxApi_IsSingleInstance_AcrossStartup`.
2. **Concurrent account mutation:** tests and production code that add or remove accounts while `GetAccountsSnapshot()` is iterating must not throw `InvalidOperationException`. Task 3 test `AccountStore_ConcurrentAddAndSnapshot_DoesNotThrow`.
3. **Override leakage:** a test that throws while an `AppServices.Override` is active must still restore the previous service. Task 4 test `Override_RestoresPrevious_WhenScopeDisposedAfterException`.
4. **Nested overrides:** two overlapping overrides of different service types must both restore in LIFO order. Task 4 test `Override_Nested_RestoresInLifoOrder`.
5. **Silent CI weakening:** removing the PowerShell step must not drop any of its 7 checks. Task 5 has one `[Fact]` per removed `throw`.

---

### Task 1: Restore green baseline

**Files:**
- Investigate: `src/Shiro/AccountManager.ModernActions.cs:54` (`ModernJoinServerDirect`), uncommitted changes in `src/Shiro/Services/Launch/AccountLaunchService*.cs`, `src/Shiro/Classes/Account.*.cs`
- Test: `tests/Shiro.Tests/AccountManagerBehaviorTests.cs:322-326`

**Interfaces:** none produced.

- [ ] **Step 1: Classify the failure.** Use superpowers:systematic-debugging. Create a worktree at `HEAD` (`git worktree add ..\shiro-head HEAD`) and run the same filter there:
  `dotnet test tests/Shiro.Tests/Shiro.Tests.csproj -c Release --filter "FullyQualifiedName~ModernActions_LaunchHome_MultiLaunch_JoinServerDirect_CloseRoblox_CoverBranches"`
  If it passes at `HEAD`, the uncommitted work caused it. If it fails at `HEAD`, the bug was already there before the uncommitted work.
- [ ] **Step 2: Fix the root cause in production code.** The test's expectation is correct: a multi-account join must clear `SelectedAccount` and set `SelectedAccounts.Count == 2`. Only change the test if Step 1 proves the expectation itself is outdated, and write down why in the commit message.
- [ ] **Step 3: Full suite.** Run `dotnet test tests/Shiro.Tests/Shiro.Tests.csproj -c Release`. Expected: `Failed: 0, Passed: ≥1769`.
- [ ] **Step 4: Commit.** Ask your human partner how to commit the existing uncommitted work (one commit or split by feature: HBA/session, BloxGen, launch). Commit the fix separately as `fix(launch): …`. Remove the worktree.

---

### Task 2: Single owner for Roblox HTTP clients

**Files:**
- Modify: `src/Shiro/AccountManager.cs:51-63,148` (delete the 13 static fields and the `apiClient` field)
- Modify: `src/Shiro/AccountManager.Lifecycle.cs:74-91` (delete the second `RobloxApiClient` and the copy block)
- Modify: `src/Shiro/Services/Network/RobloxApiClient.cs:34-46` (properties return their own fields), add a test constructor
- Modify: `tests/Shiro.Tests/AccountLauncherAndHelperBehaviorTests.cs:89-122`, `tests/Shiro.Tests/AccountManagerLifecycleAndAccountsBehaviorTests.cs:277-329`
- Test: `tests/Shiro.Tests/ArchitectureTests.StaticState.cs` (new)

**Interfaces:**
- Produces: `internal RobloxApiClient(HttpMessageHandler handler)`. It builds all 13 clients as `new RestClient(new HttpClient(handler, disposeHandler: false), <same base URL as the default ctor>)`.
- Produces: `AccountManager` has no `RestClient`-typed static members.

- [ ] **Step 1: Write failing ratchet tests** in `ArchitectureTests.StaticState.cs` (`public partial class ArchitectureTests`):
```csharp
[Fact]
public void AccountManager_ShouldNotExpose_StaticRestClients()
{
    var offenders = typeof(AccountManager)
        .GetFields(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static)
        .Where(f => f.FieldType == typeof(Shiro.Http.RestClient))
        .Select(f => f.Name).ToList();
    Assert.Empty(offenders);
}

[Fact]
public void RobloxApiClient_ShouldNotDependOn_AccountManager()
{
    var result = Types.InAssembly(CoreAssembly)
        .That().HaveName("RobloxApiClient")
        .ShouldNot().HaveDependencyOn("Shiro.AccountManager")
        .GetResult();
    Assert.True(result.IsSuccessful);
}

[Fact]
public void RobloxApi_IsSingleInstance_AcrossStartup()
{
    var before = AppServices.RobloxApi;
    // construct AccountManager the same way AccountManagerLifecycleAndAccountsBehaviorTests does
    Assert.Same(before, AppServices.RobloxApi);
    Assert.Same(before.Games, AppServices.RobloxApi.Games);
}
```
- [ ] **Step 2: Run them.** `dotnet test … --filter "FullyQualifiedName~ArchitectureTests"`. Expected: the first two FAIL (13 offenders, and a dependency found).
- [ ] **Step 3: Implement.** Delete the static fields and the `apiClient` field. Delete `AccountManager.Lifecycle.cs:78-91` and keep the comment block above it, reworded to say that `AppServices.RobloxApi` is the single owner. Each `RobloxApiClient` property returns its own field (`public RestClient Main => _main;`). Add the `HttpMessageHandler` constructor.
- [ ] **Step 4: Migrate the two tests.** Replace `AccountManager.GamesClient = new RestClient(new HttpClient(handler), …)` and the `UsersClient` swap with `var prev = AppServices.RobloxApi; AppServices.RobloxApi = new RobloxApiClient(handler); try { … } finally { AppServices.RobloxApi = prev; }`. Task 4 converts these to `Override`.
- [ ] **Step 5: Full suite + build.** Expected: 0 failed, 0 warnings.
- [ ] **Step 6: Commit.** `refactor(network): make RobloxApiClient the sole owner of HTTP clients`

---

### Task 3: Encapsulate the account list

**Files:**
- Modify: `src/Shiro/AccountManager.cs:30,185-276`, `AccountManager.Accounts.cs:59-206`, `AccountManager.Persistence.cs:127-275`, `AccountManager.BackupRecovery.cs:117`
- Modify: all test files that touch `AccountsList` (46 sites; find them with `Select-String -Path tests -Pattern AccountsList -Recurse`)
- Test: `tests/Shiro.Tests/ArchitectureTests.StaticState.cs`, `tests/Shiro.Tests/AccountStoreConcurrencyTests.cs` (new)

**Interfaces:**
- Consumes (already exist, keep their signatures): `AccountManager.GetAccountsSnapshot() : IReadOnlyList<Account>`, `AccountManager.AccountCount : int`, `AccountManager.AddAccountThreadSafe(Account)`, `AccountManager.ReplaceAccountsList(List<Account>)`.
- Produces: `AccountsList` becomes `private static List<Account> _accounts`. Every read and write takes `AccountListLock`. For tests, add `internal static void ResetAccountsForTests(IEnumerable<Account> accounts)`, which is `ReplaceAccountsList(accounts.ToList())`.

- [ ] **Step 1: Write failing tests.**
```csharp
[Fact]
public void AccountManager_AccountList_IsNotAccessibleOutsideClass()
{
    var field = typeof(AccountManager).GetField("AccountsList",
        BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static);
    Assert.True(field is null || field.IsPrivate);
}
```
In `AccountStoreConcurrencyTests` (`[Collection("StaticSeams")]`): `AccountStore_ConcurrentAddAndSnapshot_DoesNotThrow`. Run 4 tasks × 500 `AddAccountThreadSafe` calls while 2 tasks call `GetAccountsSnapshot()` in a loop. Assert no exception and `AccountCount == 2000`.
- [ ] **Step 2: Run.** Expected: the reflection test FAILS (the field is `internal`). The concurrency test may pass; keep it as a regression guard.
- [ ] **Step 3: Implement.** Rename the field to private `_accounts`. Add `lock (AccountListLock)` wherever it is missing (check lines 59, 141, 185-221 and Persistence 127/177). Move read-only callers in other partials onto `GetAccountsSnapshot()`.
- [ ] **Step 4: Migrate the tests.** Replace `AccountManager.AccountsList = …`/`.Add`/`.Clear` with `ResetAccountsForTests(...)`/`AddAccountThreadSafe(...)`. Replace reads with `GetAccountsSnapshot()`.
- [ ] **Step 5: Full suite + build.** Expected: 0 failed, 0 warnings, and every file still ≤ 500 lines.
- [ ] **Step 6: Commit.** `refactor(accounts): encapsulate account list behind AccountListLock`

---

### Task 4: Scoped service overrides instead of container rebuilds

**Files:**
- Modify: `src/Shiro/Services/AppServices.cs` (add `Override<T>`, remove every `set =>` accessor and `ReplaceService`)
- Modify: 16 test files with `AppServices.X = …` (48 sites, listed by `Select-String -Path tests -Recurse -Pattern 'AppServices\.\w+\s*=[^=>]'`), plus the 2 sites from Task 2 Step 4
- Test: `tests/Shiro.Tests/AppServicesOverrideTests.cs` (new, `[Collection("StaticSeams")]`)

**Interfaces:**
- Produces: `internal static IDisposable AppServices.Override<T>(T instance) where T : class`. It registers `instance` as `T` in a new provider (all other registrations are copied from the current provider) and returns a scope. Disposing the scope restores the exact `IServiceProvider` that was active before. The scopes form a stack: disposing out of order throws `InvalidOperationException`.

- [ ] **Step 1: Write failing tests:**
  - `Override_ReplacesService_WithinScope`: inside `using (AppServices.Override<IToastNotifier>(fake))`, `Assert.Same(fake, AppServices.Toasts)`.
  - `Override_RestoresPrevious_WhenScopeDisposedAfterException`: capture `prev = AppServices.Toasts`, throw inside the `using`, catch it outside, then `Assert.Same(prev, AppServices.Toasts)`.
  - `Override_Nested_RestoresInLifoOrder`: override `IToastNotifier`, then `IBloxGenService`. Dispose the inner scope: the toast is still the fake and BloxGen is restored. Dispose the outer scope: both are restored.
  - `Override_DisposedOutOfOrder_Throws`: `Assert.Throws<InvalidOperationException>` when the outer scope is disposed first.
- [ ] **Step 2: Run.** Expected: compile failure, because `Override` does not exist.
- [ ] **Step 3: Implement `Override<T>`** using a `Stack<IServiceProvider>` guarded by `_initLock`.
- [ ] **Step 4: Migrate all 50 test sites** to `using var _ = AppServices.Override<IInterface>(fake);`. Remove the try/finally restore blocks that this replaces.
- [ ] **Step 5: Remove the setters and `ReplaceService`.** The build proves that no caller is left.
- [ ] **Step 6: Full suite + build.** Expected: 0 failed, 0 warnings.
- [ ] **Step 7: Commit.** `refactor(di): replace AppServices setters with scoped Override<T>`

---

### Task 5: Move CI grep checks into architecture tests

**Files:**
- Create: `tests/Shiro.Tests/ArchitectureTests.SourceInvariants.cs` (`public partial class ArchitectureTests`, uses the existing `RepoRoot`)
- Modify: `.github/workflows/build.yml:47-81` (delete the "Verify architecture boundaries" step)

**Interfaces:** Consumes `ArchitectureTests.RepoRoot` (already defined in `ArchitectureTests.cs`).

- [ ] **Step 1: Write one `[Fact]` per removed `throw`,** keeping the same file paths, patterns, and failure messages:
  - `Batch_ShouldNotUseTaskRun`: `src/Shiro/Classes/Batch.cs` does not contain `Task.Run`.
  - `RobloxProcess_ShouldNotUseBlockingLogLookup`: `src/Shiro/Classes/RobloxProcess.cs` does not contain `Task.Run(WaitForLogPath`.
  - `Source_ShouldNotUseLegacyNetworking`: no `src/Shiro/**/*.cs` matches `\b(WebClient|WebRequest|HttpWebRequest)\b`. List the offenders in the message.
  - `AccountDataSerializer_ShouldBeVersioned`: `AccountDataSerializer.cs` contains `CurrentSchemaVersion`.
  - `StoragePaths_ShouldHaveDirectoryBoundary`: `StoragePaths.cs` contains `IsWithinDirectory`.
  - `AccountDataSerializer_ShouldEnforceSavePasswords`: `AccountDataSerializer.cs` contains `includePasswords`.
  - `Program_ShouldNotCheckNetFramework472`: `Program.cs` does not contain `NET Framework 4.7.2`.
- [ ] **Step 2: Prove each test can fail.** For each test in turn, change its input temporarily (for example, add `// Task.Run` to `Batch.cs`), run `--filter FullyQualifiedName~SourceInvariants`, expect exactly that test to FAIL, then revert.
- [ ] **Step 3: Delete the CI step** (`build.yml` lines 47-81). Keep the "Verify release layout" step (it checks build output, not source).
- [ ] **Step 4: Full suite.** Expected: 0 failed, and the test count goes up by 7.
- [ ] **Step 5: Commit.** `ci: move source invariants from workflow regex into ArchitectureTests`

---

## Decision left to your human partner (not a task)
`ArchitectureTests.NoSourceCodeFiles_ShouldExceed500Lines` is why classes like `AccountManager` (11 partials) and `Account` (8+ partials) get split by line count rather than by responsibility. Options: (a) keep it; (b) apply it only to non-`partial` types and add a ratchet on the number of partial fragments per type; (c) remove it. A follow-up plan that extracts real services out of the `AccountManager` partials depends on this choice.
