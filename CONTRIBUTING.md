# Contributing to Shiro Account Manager

Thank you for contributing to **Shiro Account Manager**! To keep the codebase fast, secure, and maintainable, all contributions must adhere to the architectural and quality standards below.

---

## 1. Prerequisites & Build

- **OS**: Windows 10 / 11 (x64)
- **SDK**: [.NET 10.0 SDK](https://dotnet.microsoft.com/download/dotnet/10.0)

```powershell
# Restore dependencies using locked packages
dotnet restore Shiro.sln --runtime win-x64 /p:SelfContained=true --locked-mode

# Build in Release mode (Warnings are treated as errors)
dotnet build Shiro.sln --configuration Release --no-restore --nologo -warnaserror

# Run the full unit & architecture test suite
dotnet test tests/Shiro.Tests/Shiro.Tests.csproj --configuration Release --no-restore
```

---

## 2. Code Coverage Gate

Core security, serialization, and storage classes must maintain **>= 92%** line coverage, overall line coverage must remain **>= 75%**, and branch coverage must remain **>= 60%**.

```powershell
dotnet test "tests/Shiro.Tests/Shiro.Tests.csproj" `
  --configuration Release `
  --collect:"XPlat Code Coverage" `
  --settings "tests/coverage.runsettings" `
  --results-directory "coverage"

./scripts/Check-Coverage.ps1 -CoverageDir "coverage" -MinOverall 75.0 -MinBranch 60.0 -MinCore 92.0
```

---

## 3. Architectural Rules (Enforced by `ArchitectureTests*.cs`)

Every pull request is automatically validated against `tests/Shiro.Tests/ArchitectureTests*.cs` (`ArchitectureTests.cs`, `ArchitectureTests.Budgets.cs`, `ArchitectureTests.Hygiene.cs`, `ArchitectureTests.Layering.cs`, `ArchitectureTests.Ratchets.cs`, `ArchitectureTests.SourceInvariants.cs`, `ArchitectureTests.StaticState.cs`):

1. **Line Budgets (`NoSourceCodeFiles_ShouldExceed500Lines`, `NoClass_ShouldExceedLineBudget_AcrossPartials`, `NoTestFiles_ShouldExceed550Lines`, `NoAxamlFiles_ShouldExceed400Lines`)**:
   - No `.cs` file in `src/Shiro/` may exceed **485 lines**.
   - Partial classes must stay within their total line budget (default **1,150 lines** across partials, with explicit baselines for `Account`, `MainViewModel`, and `ServerListViewModel`).
   - Test `.cs` files in `tests/Shiro.Tests/` may not exceed **550 lines**.
   - `.axaml` files may not exceed **400 lines** or **200 characters per line**.
2. **Strict MVVM & Layer Separation (`ViewModels_ShouldNotDependOn_Views`, `Services_ShouldNotDependOn_ViewModels`, `DomainFolder_ShouldNotDependOn_ViewModelsOrAvaloniaOrWin32Registry`)**:
   - `Shiro.ViewModels` and `Shiro.Services` (except `AvaloniaDialogService`) must never reference `Shiro.AvaloniaUI.Views`. Use `AppServices.Dialogs` (`IDialogService`) or `AppServices.Prompt` (`IUserPrompt`) for UI prompts.
   - `Shiro.Services` must not introduce new dependencies on `Shiro.ViewModels` (ratcheted to the 3 legacy allowlisted files until decoupled).
   - `Shiro.Domain` must remain free of `ViewModels`, `Avalonia`, and `Microsoft.Win32` dependencies.
3. **Dependency Injection & Ambient Static Ratchets (`AppServices`)**:
   - Services under `src/Shiro/Services/**` must resolve dependencies through `AppServices` (`IAccountRepository`, `IRobloxApiClient`, `ISettingsService` such as `AppServices.GeneralSettings`, `IAppLogger` via `AppServices.Logger`, `IDialogService`) instead of calling `AccountManager.*`, `AppSettings.*`, or `Program.Logger` directly (`AmbientStaticAccess_ShouldNotExceedPerFolderBaseline` locks all 9 folders to `(0, 0)`).
4. **Service Modularization & Naming**:
   - All files in `src/Shiro/Services` must be placed in a domain subfolder (`Auth`, `Launch`, `Network`, `Storage`, `Ui`, `Windows`) and end with an approved domain suffix (`Service`, `Helper`, `Client`, `Coordinator`, `Repository`, `Policy`, `Store`, etc.).
5. **Async-First & Exception Hygiene**:
   - Never use `.GetAwaiter().GetResult()`, `httpClient.Send(...)`, `DispatcherFrame`/`PushFrame`, `.Wait(TimeSpan...)`, or `Thread.Sleep(...)` in new `src/Shiro` code (enforced by `SourceFiles_ShouldNotUse_SyncOverAsyncBlocking` and the `ArchitectureTests.Ratchets.cs` allowlists). Use `async`/`await` end-to-end.
   - Do not add empty `catch {}` or untyped `catch` / `catch (Exception)` blocks (locked at baseline `0`). Log exceptions via `AppServices.Logger?.Debug(...)` or use `SafeExecuteHelper.Try(...)` for best-effort cleanup.
6. **Localization, Encoding & Test Assertion Hygiene**:
   - Save all source files as UTF-8 with English comments (`U+FFFD` and Vietnamese diacritics in comments/string literals outside `LocalizationService*` are forbidden).
   - Every `{DynamicResource ...}` key matching `\{DynamicResource\s+([A-Za-z0-9_.]+)\}` (excluding theme `Brush`/`Color`/`Theme`/`Font` resources) used in `.axaml` must be registered in `src/Shiro/Resources/Strings/vi.json` and `en.json` (`DynamicResourcePattern()`).
   - Every `[Fact]`/`[Theory]` test method must assert on concrete values (`TestMethods_ShouldAssertOnValues_NotOnlyNullness`); keep inline `{` / `}` characters out of test method bodies.

---

## Release Build

To produce the single-file self-contained distributable in `build/Release`, run the canonical publish script:

```powershell
powershell -ExecutionPolicy Bypass -File .\scripts\Publish.ps1
```

Or invoke `dotnet publish` directly after locked restore:

```powershell
dotnet restore src/Shiro/Shiro.csproj --runtime win-x64 /p:SelfContained=true --locked-mode
dotnet publish src/Shiro/Shiro.csproj --configuration Release --runtime win-x64 --self-contained true --no-restore --output build/Release /p:PublishDocumentationFiles=false /p:PublishSingleFile=true /p:IncludeNativeLibrariesForSelfExtract=true /p:EnableCompressionInSingleFile=true
```
