# Contributing to Shiro Account Manager

Thank you for contributing to **Shiro Account Manager**! To keep the codebase fast, secure, and maintainable, all contributions must adhere to the architectural and quality standards below.

---

## 1. Prerequisites & Build

- **OS**: Windows 10 / 11 (x64)
- **SDK**: [.NET 10.0 SDK](https://dotnet.microsoft.com/download/dotnet/10.0)

```powershell
# Restore dependencies using locked packages
dotnet restore Shiro.sln --runtime win-x64

# Build in Release mode (Warnings are treated as errors)
dotnet build Shiro.sln --configuration Release

# Run the full unit & architecture test suite
dotnet test tests/Shiro.Tests/Shiro.Tests.csproj --configuration Release
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

## 3. Architectural Rules (Enforced by `ArchitectureTests.cs`)

Every pull request is automatically validated against `tests/Shiro.Tests/ArchitectureTests.cs`:

1. **500-Line File Limit (`NoSourceCodeFiles_ShouldExceed500Lines`)**:
   - No `.cs` file in `src/Shiro` may exceed **500 lines**. Split large classes into cohesive `partial` files or extract domain services.
2. **Partial Class Line Budgets (`NoClass_ShouldExceedLineBudget_AcrossPartials`)**:
   - Partial classes must stay within their total line budget (default `1,200` lines across partials).
3. **Strict MVVM Separation (`ViewModels_ShouldNotDependOn_Views`)**:
   - `Shiro.ViewModels` and `Shiro.Services` (except `AvaloniaDialogService`) must never reference `Shiro.AvaloniaUI.Views`. Use `AppServices.Dialogs` (`IDialogService`) for UI prompts.
4. **Dependency Injection & Decoupling (`AppServices`)**:
   - Services under `src/Shiro/Services/**` must use `AppServices.AccountRepository`, `AppServices.RobloxApi`, and `AppSettings` instead of calling `AccountManager.*` static fields/methods directly.
5. **Service Modularization & Naming**:
   - All files in `src/Shiro/Services` must be placed in a domain subfolder (`Auth`, `Launch`, `Network`, `Storage`, `Ui`, `Windows`) and end with an approved domain suffix (`Service`, `Helper`, `Client`, `Coordinator`, `Repository`, `Policy`, `Store`, etc.).
6. **Zero Sync-over-Async & Exception Hygiene**:
   - Never use `.GetAwaiter().GetResult()` in `src/Shiro`. Use `async`/`await` end-to-end.
   - Do not add empty `catch {}` blocks. Log exceptions via `Program.Logger?.Debug(...)` or use `SafeExecuteHelper.Try(...)` for best-effort cleanup.
7. **Localization & UTF-8 Hygiene**:
   - Save all source files as UTF-8.
   - Every `{DynamicResource Loc_*}` key used in `.axaml` must be registered in `src/Shiro/Resources/Strings/vi.json` and `en.json`.
