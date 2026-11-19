# P3 Deep Architectural Refactoring (DI, ViewModel Split, Domain Purity & Async UI) Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.
> **Prerequisite:** Execute Waves **P1** and **P2** first so dead code, duplication, and critical runtime bugs are resolved before decomposing god-classes and flipping parallel tests on.

**Goal:** Migrate Shiro from static service-locator/delegate-bag singletons and `[ModuleInitializer]` hooks to an explicit Composition Root (`AppBootstrap`) with constructor injection, decompose `MainViewModel` (`3,027` LOC), `ServerListViewModel` (`1,625` LOC), and `Account` (`2,182` LOC) below standard `1,150`-line partial budgets, extract `HbaBrowserScripts.cs`, replace `DispatcherFrame.PushFrame` / `done.Wait(10m)` dialog blocking waits with pure `async/await` modals (`MessageBoxDialog`, `InputDialog`), unify `SettingsDocument`, and enable true parallel xUnit execution (`DisableTestParallelization = false`).

**Architecture:** Stage the refactor in 4 runnable increments where `dotnet build -c Release -warnaserror`, full test suite, and `Check-Coverage.ps1` pass at every commit boundary:
1. `AppBootstrap` + `IAccountRepository.AccountsChanged` + zero `Services !-> ViewModels` references (`AccountWorkflowService`, `ISettingsSideEffects`, `ToastService`, `AccountRepository.EnsureBackendInitialized`).
2. Child sub-ViewModel decomposition for `MainViewModel` & `ServerListViewModel` with compiled `x:DataType` bindings preserved.
3. Domain purity for `Account` (extracting HTTP/session transport out of `.SessionState.cs`, `.HbaBinding.cs`, `.Api.cs` into services) + `HbaBrowserScripts.cs`.
4. Pure async `IDialogService` (`MessageBoxDialog`/`InputDialog`) + `SettingsDocument` batched save + instance-scoped test contexts unlocking `[assembly: CollectionBehavior(DisableTestParallelization = false)]`.

**Tech Stack:** .NET 10 (`win-x64`), C# 14, `Microsoft.Extensions.DependencyInjection` Composition Root, Avalonia 11 MVVM, xUnit, NSubstitute.

**Spec:** System Audit Report (`C:/Users/lenovo/.gemini/antigravity/brain/d3d9db7e-c20c-4d8a-b056-391721b18014/system-audit-report.md`) — Section 1.3, Section 6 (#1–#6), & P3 Roadmap.

---

## Global Constraints

- `TreatWarningsAsErrors=true`, `<Nullable>enable</Nullable>`, strict ASCII C# comments & logs.
- Line budgets (`ArchitectureTests.Budgets.cs`): every `.cs <= 485`; `tests <= 550`; upon finishing Tasks 2–3, remove legacy overrides in `ArchitectureTests.Budgets.cs:67-73` (`Account = 2300`, `MainViewModel = 3050`, `ServerListViewModel = 1640`) so every class obeys `DefaultBudget = 1150`.
- Enforce zero `Services !-> ViewModels` coupling in `ArchitectureTests.Layering.cs` and shrink `ArchitectureTests.StaticState.cs:84-96` 10-file `.Default` allowlist down to `AppBootstrap`.
- Update `tests/coverage-floors.json` paths cleanly when refactored methods relocate into new service classes, preserving overall `>= 75%`, branch `>= 60%`, Core `>= 92%`.

---

### Task 1: Build `AppBootstrap` Composition Root, Reactive `IAccountRepository.AccountsChanged` & Decouple Services from ViewModels

**Files:**
- Create: `src/Shiro/Services/Composition/AppBootstrap.cs` (wires DI container explicitly, registers singleton `ISettingsService` section adapters once instead of per-access `new DelegatingSectionSettingsService` in `AppServices.cs:243-246`, manages deterministic `IAsyncDisposable` / timer shutdown)
- Modify: `src/Shiro/AvaloniaUI/App.axaml.cs`, `src/Shiro/Program.cs`
- Modify: `src/Shiro/AccountManager.cs`, `.Lifecycle.cs`, `.RecentGamesAndSettings.cs:12,33,103-115` (`[ModuleInitializer] RegisterModuleHooks()` removed; `_deferredSettingsSaveTimer` disposed cleanly on shutdown), `.Launch.cs`, `.ModernActions.cs`
- Modify: `src/Shiro/Services/AppServices.cs` (`45-75,208-253` — trim static delegate/ambient fallbacks behind `AppBootstrap`)
- Modify: `src/Shiro/Services/Storage/IAccountRepository.cs` & `AccountRepository.cs:214-240,330-333`
- Modify: `src/Shiro/Services/Storage/AccountWorkflowService.cs:8,303,372`, `Services/Ui/ISettingsSideEffects.cs:7,45,98,137`, `Services/Ui/ToastService.cs:10,58,196` (delete `using Shiro.ViewModels`)
- Modify: `tests/Shiro.Tests/ArchitectureTests.Layering.cs` & `.StaticState.cs:84-96`
- Test: `tests/Shiro.Tests/P3CompositionAndArchitectureTests.cs`, `MvvmFoundationTests.cs`, `RefactoredServicesTests.cs`

**Interfaces:**
- **Added on `IAccountRepository`:** `event EventHandler<AccountsChangedEventArgs>? AccountsChanged;` raised after synchronized `ReplaceAll`, `AddThreadSafe`, and delete mutations so Bulk Import, Recovery, FastLogin, API `ImportCookie`, and Delete update UI uniformly.
- **New (`AppBootstrap.cs`):** `public sealed class AppBootstrap : IAsyncDisposable` exposing `IServiceProvider Services` and `Task InitializeAsync(...)`.

- [ ] **Step 1: Write failing architecture & reactivity tests in `P3CompositionAndArchitectureTests.cs` (TDD Red)**
  ```csharp
  [Fact]
  public void ServicesNamespace_HasZeroReferencesToViewModels()
  {
      var hits = Directory.EnumerateFiles(Path.Combine(RepoRoot, "src", "Shiro", "Services"), "*.cs", SearchOption.AllDirectories)
          .Where(f => File.ReadAllText(f).Contains("using Shiro.ViewModels", StringComparison.Ordinal))
          .ToList();
      Assert.Empty(hits);
  }

  [Fact]
  public void AccountRepository_ReplaceAll_RaisesAccountsChangedAndPreservesLockSafety()
  {
      var repo = new AccountRepository();
      int raisedCount = -1;
      repo.AccountsChanged += (_, e) => raisedCount = e.Accounts.Count;
      repo.ReplaceAll(new[] { new Account { Username = "u1" } });
      Assert.Equal(1, raisedCount);
  }
  ```
  Plus failing checks asserting zero `[ModuleInitializer]` attributes under `src/Shiro/`, clean `AppBootstrap.DisposeAsync()` releasing `_deferredSettingsSaveTimer`, and singleton section settings identity across reads (`ReferenceEquals`).
- [ ] **Step 2: Run targeted tests expecting Red**
  `dotnet test Shiro.sln -c Release --nologo --filter "FullyQualifiedName~P3CompositionAndArchitectureTests|FullyQualifiedName~ArchitectureTests"`
- [ ] **Step 3: Implement `AppBootstrap`, reactive `IAccountRepository`, and service/VM decoupling**
  - Replace `[ModuleInitializer] RegisterModuleHooks()` (`AccountManager.RecentGamesAndSettings.cs:103`) with explicit `AppBootstrap` initialization; remove `EnsureBackendInitialized()` dependency from `AccountRepository.cs:330` and protect `ReplaceAll` + `AccountsChanged` snapshot dispatch cleanly inside `_syncRoot`.
  - Introduce domain DTO/callback interfaces where `AccountWorkflowService`, `ISettingsSideEffects`, and `ToastService` previously imported `Shiro.ViewModels`.
- [ ] **Step 4: Run Release `-warnaserror` + full test suite + `ArchitectureTests`**
  `dotnet build Shiro.sln -c Release --nologo -warnaserror && dotnet test Shiro.sln -c Release --no-build --nologo`
- [ ] **Step 5: Stage & commit Task 1**
  `git add src/Shiro/ tests/Shiro.Tests/`
  `git commit -m "refactor(di): introduce AppBootstrap composition root, reactive IAccountRepository, and decouple Services from ViewModels"`

---

### Task 2: Decompose `MainViewModel` (`3,027` -> `< 450`) & `ServerListViewModel` (`1,625` -> `< 450`) into Child Sub-ViewModels

**Files:**
- Create under `src/Shiro/ViewModels/Dashboard/`:
  - `AccountCollectionViewModel.cs`
  - `LaunchBarViewModel.cs`
  - `ToolsStatusViewModel.cs`
  - `CommandPaletteViewModel.cs`
- Create under `src/Shiro/ViewModels/ServerList/`:
  - `ServerBrowserTabViewModel.cs`
  - `GameExplorerTabViewModel.cs`
  - `GameLibraryTabViewModel.cs`
- Create: `src/Shiro/Services/Storage/ServerWatcherConfigService.cs`
- Modify: `src/Shiro/ViewModels/MainViewModel*.cs`, `src/Shiro/ViewModels/ServerListViewModel*.cs` (note: keep `CookieImportViewModel` for `FastLoginDialog`, never `FastLoginDialogViewModel`)
- Update compiled `x:DataType` bindings: `MainWindow.axaml`, `DashboardView.axaml`, `Panels/*.axaml`, `ServerListView.axaml`, `ServerListResultsPanel.axaml`
- Tighten: `tests/Shiro.Tests/ArchitectureTests.Budgets.cs:71-72` (remove `["MainViewModel"] = 3050` & `["ServerListViewModel"] = 1640` legacy overrides so both obey `DefaultBudget = 1150`)
- Test: `tests/Shiro.Tests/P3CompositionAndArchitectureTests.cs`, `MainViewModelStatusAndCountsTests.cs`, `ServerListViewModelBehaviorTests.cs`

**Interfaces:**
- **New child VMs on `MainViewModel`:** `AccountCollectionViewModel AccountsVm { get; }`, `LaunchBarViewModel LaunchBar { get; }`, `ToolsStatusViewModel ToolsStatus { get; }`, `CommandPaletteViewModel CommandPalette { get; }`.
- **New child VMs on `ServerListViewModel`:** `ServerBrowserTabViewModel BrowserTab { get; }`, `GameExplorerTabViewModel ExplorerTab { get; }`, `GameLibraryTabViewModel LibraryTab { get; }`.

- [ ] **Step 1: Add child-VM property/command tests & remove `MainViewModel`/`ServerListViewModel` overrides in `ArchitectureTests.Budgets.cs:71-72` (TDD Red)**
  ```csharp
  [Fact]
  public void MainViewModel_ExposesComposedChildViewModels_Under1150Budget()
  {
      using var vm = new MainViewModel();
      Assert.NotNull(vm.AccountsVm);
      Assert.NotNull(vm.LaunchBar);
      Assert.NotNull(vm.ToolsStatus);
      Assert.NotNull(vm.CommandPalette);
  }
  ```
- [ ] **Step 2: Run ViewModel + Architecture tests expecting Red**
  `dotnet test Shiro.sln -c Release --nologo --filter "FullyQualifiedName~ArchitectureTests|FullyQualifiedName~MainViewModel|FullyQualifiedName~ServerListViewModel|FullyQualifiedName~P3CompositionAndArchitectureTests"`
- [ ] **Step 3: Decompose `MainViewModel` & `ServerListViewModel` and rebind `.axaml` views (`0` compiled-binding warnings, every `.cs <= 485`, class totals `<= 1150`)**
- [ ] **Step 4: Run Release `-warnaserror` + full test suite + `ArchitectureTests`**
  `dotnet build Shiro.sln -c Release --nologo -warnaserror && dotnet test Shiro.sln -c Release --no-build --nologo`
- [ ] **Step 5: Stage & commit Task 2**
  `git add src/Shiro/ViewModels/ src/Shiro/Services/Storage/ServerWatcherConfigService.cs src/Shiro/AvaloniaUI/ tests/Shiro.Tests/`
  `git commit -m "refactor(vm): decompose MainViewModel and ServerListViewModel below 1150-line budget"`

---

### Task 3: Extract HTTP/Session Transport Out of `Account` (`2,182` -> `< 950`) & Extract `HbaBrowserScripts.cs`

**Files:**
- Create:
  - `src/Shiro/Services/Auth/RobloxAccountSessionClient.cs` (session refresh, cookie rotation, CSRF retry & ticket logic out of `Account.SessionState*.cs` & `.Api.cs`)
  - `src/Shiro/Services/Network/RobloxAccountApiService.cs`
  - `src/Shiro/Services/Auth/HbaBrowserScripts.cs` (inline JS hooks extracted from `RobloxHbaService.BrowserHooks.cs` `344` LOC)
- Shrink: `src/Shiro/Domain/Account.cs` (`470`), `.SessionState.cs` (`438`), `.HbaBinding.cs` (`319`), `.Api.cs` (`303`), `.SessionState.CookieRotation.cs` (`266`), `.HbaBinding.SessionSync.cs` (`253`) so `Account` partial total `< 950 <= 1,150`.
- Tighten: `tests/Shiro.Tests/ArchitectureTests.Budgets.cs:70` (drop `["Account"] = 2300` legacy exception — `ArchitectureTests.Budgets.cs` enforces pure `DefaultBudget = 1150` globally!).
- Update `tests/coverage-floors.json` paths if covered methods relocate into `RobloxAccountSessionClient` / `RobloxAccountApiService` (preserving `>=` branch floors).
- Test: `tests/Shiro.Tests/P3CompositionAndArchitectureTests.cs` & existing `Account*` / `Hba*` tests.

**Interfaces:**
- **New (`RobloxAccountSessionClient.cs`):** `Task<bool> ValidateSessionAsync(Account account, CancellationToken ct = default)` & `Task<(bool Success, string Ticket)> GetAuthTicketAsync(Account account, CancellationToken ct = default)`.
- **New (`HbaBrowserScripts.cs`):** `internal static string BuildCollectionScript(...)` & `BuildRestoreModerationScript(...)` called by `RobloxHbaService.BrowserHooks.cs`.

- [ ] **Step 1: Add transport extraction tests & remove `["Account"] = 2300` override in `ArchitectureTests.Budgets.cs:70` (TDD Red)**
  ```csharp
  [Fact]
  public void DomainAccount_DoesNotImportHttpClientOrRestInternalsDirectly()
  {
      var domainFiles = Directory.EnumerateFiles(Path.Combine(RepoRoot, "src", "Shiro", "Domain"), "*.cs", SearchOption.AllDirectories);
      Assert.All(domainFiles, f => Assert.DoesNotContain("System.Net.Http", File.ReadAllText(f)));
      Assert.False(typeof(RobloxAccountSessionClient).IsAbstract);
      Assert.False(string.IsNullOrWhiteSpace(HbaBrowserScripts.BuildCollectionScript()));
  }
  ```
- [ ] **Step 2: Run targeted tests expecting Red**
  `dotnet test Shiro.sln -c Release --nologo --filter "FullyQualifiedName~ArchitectureTests|FullyQualifiedName~P3CompositionAndArchitectureTests|FullyQualifiedName~Hba"`
- [ ] **Step 3: Extract HTTP/session transport out of `Account` & extract `HbaBrowserScripts.cs`**
- [ ] **Step 4: Run Release `-warnaserror` + full test suite + `ArchitectureTests` + `Check-Coverage.ps1`**
  `dotnet build Shiro.sln -c Release --nologo -warnaserror && dotnet test Shiro.sln -c Release --no-build --nologo`
  `powershell.exe -NoProfile -ExecutionPolicy Bypass -File .\scripts\Check-Coverage.ps1`
- [ ] **Step 5: Stage & commit Task 3**
  `git add src/Shiro/Domain/ src/Shiro/Services/Auth/ src/Shiro/Services/Network/ tests/coverage-floors.json tests/Shiro.Tests/`
  `git commit -m "refactor(domain): extract HTTP/session transport from Account and separate HbaBrowserScripts"`

---

### Task 4: Migrate Dialogs to Pure `async/await`, Unify `SettingsDocument` & Enable Parallel Test Execution

**Files:**
- Modify: `src/Shiro/AvaloniaUI/Views/MessageBoxDialog.axaml.cs:234-286`, `src/Shiro/AvaloniaUI/Views/InputDialog.axaml.cs:115-166`, `src/Shiro/Services/Ui/{IDialogService.cs,AvaloniaDialogService.cs}` (eliminate ALL `DispatcherFrame.PushFrame` & `done.Wait(TimeSpan.FromMinutes(10))` blocking waits via native async `ShowAsync`/`PromptAsync` returning `Task<T>`)
- Create/Modify: `src/Shiro/Services/Storage/SettingsDocument.cs` / `IniFile.cs` (single batched atomic JSON write per Save)
- Isolate all 15+ static test overrides (`AppServices.Override<T>`, `StoragePaths` overrides, `RobloxDirectLauncher.CookieGuard`, `SessionAuditorService.TestHttpClientFactory`, `AccountLaunchService*.cs`, `RobloxHbaService.Network`, `AutoReconnectService`, `BrowserService`, `LaunchCoordinator`, `LaunchQueuePacingHelper`, `DiscordRpcService`, `CryptoHelper`, `ISettingsSideEffects`, `ProcessExtensions`, `RobloxProcessService`) into `AsyncLocal<T>` or instance-scoped test contexts
- Modify: `tests/Shiro.Tests/StoragePathsTestCollection.cs:3` -> `[assembly: CollectionBehavior(DisableTestParallelization = false)]`
- Modify: `tests/Shiro.Tests/ArchitectureTests.Budgets.cs` / `.Layering.cs` (ban `DispatcherFrame` / `.PushFrame` / `.Wait(TimeSpan)` across `src/Shiro/`)
- Test: `tests/Shiro.Tests/P3CompositionAndArchitectureTests.cs`, `DialogSmokeTests*.cs`

**Interfaces:**
- **Modified (`IDialogService`):** Expose `Task<MessageBoxResult> ShowMessageBoxAsync(...)` / `Task<string?> ShowInputPromptAsync(...)` / `Task<string> ShowSecretPromptAsync(...)`, migrating remaining UI callers to pure `async/await` commands without nested dispatcher loops.
- **New (`SettingsDocument.cs`):** `public sealed class SettingsDocument` performing single batched write on `FlushAsync` / `Save`.

- [ ] **Step 1: Add pure-async dialog completion test, `SettingsDocument` batch test, & `DispatcherFrame.PushFrame` architecture ban (TDD Red)**
  ```csharp
  [Fact]
  public void SourceFiles_MustNotUseDispatcherFramePushFrameOrBlockingDialogWait()
  {
      var offenders = Directory.EnumerateFiles(Path.Combine(RepoRoot, "src", "Shiro"), "*.cs", SearchOption.AllDirectories)
          .Where(f => { string t = File.ReadAllText(f); return t.Contains("DispatcherFrame", StringComparison.Ordinal) || t.Contains("done.Wait(TimeSpan", StringComparison.Ordinal); })
          .ToList();
      Assert.Empty(offenders);
  }

  [Fact]
  public void SettingsDocument_MultiSectionUpdate_WritesDiskAtomicallyOnce()
  {
      int writeCount = 0;
      var doc = new SettingsDocument(path, onAtomicWrite: () => writeCount++);
      doc.Set("General", "Language", "en");
      doc.Set("Watcher", "NoConnectionTimeout", "20");
      doc.Save();
      Assert.Equal(1, writeCount);
  }
  ```
- [ ] **Step 2: Run targeted tests expecting Red**
  `dotnet test Shiro.sln -c Release --nologo --filter "FullyQualifiedName~P3CompositionAndArchitectureTests|FullyQualifiedName~DialogSmokeTests|FullyQualifiedName~ArchitectureTests"`
- [ ] **Step 3: Migrate dialogs to pure `async/await`, unify `SettingsDocument`, and scope all static test overrides cleanly**
- [ ] **Step 4: Flip `DisableTestParallelization = false` in `StoragePathsTestCollection.cs:3` and run 5x consecutive parallel stress test runs + `Check-Coverage.ps1`**
  ```powershell
  dotnet build Shiro.sln --configuration Release --nologo -warnaserror
  1..5 | % { dotnet test Shiro.sln --configuration Release --no-build --nologo }
  powershell.exe -NoProfile -ExecutionPolicy Bypass -File .\scripts\Check-Coverage.ps1
  ```
  Expected: 5/5 parallel test runs `Passed >= 2189, Failed: 0`, zero flakiness, `0` warnings, Core `>= 92.0%`.
- [ ] **Step 5: Stage & commit Task 4**
  `git add src/Shiro/ tests/Shiro.Tests/`
  `git commit -m "refactor(async,tests): eliminate DispatcherFrame blocking waits, unify SettingsDocument, and enable parallel xUnit"`

---

## Execution Handoff

Plan complete. Recommend executing Wave P3 sequentially after Waves P1 and P2 using either:
1. **Subagent-driven execution** (`superpowers:subagent-driven-development`) *(recommended)* — isolates each architectural increment behind build, test, and coverage gates.
2. **Inline execution** (`superpowers:executing-plans`) — steps through Tasks 1–4 directly in session.
