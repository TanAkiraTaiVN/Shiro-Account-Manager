# Coverage Exclusions & Baseline

## Enforced Coverage Gates (Task 19 - 10/10 Overhaul)

- **Overall Line Coverage**: 79.82% (Gate: >= 75.0%)
- **Overall Branch Coverage**: 69.32% (Gate: >= 60.0%)
- **Core Security/Persistence Coverage**: 92.83% (Gate: >= 92.0%)

Enforced by `scripts/Check-Coverage.ps1 -MinOverall 75.0 -MinBranch 60.0 -MinCore 92.0`, GitHub Actions workflows (`.github/workflows/build.yml` and `.github/workflows/release.yml`), and validated by `ArchitectureTests.cs`.

## Measured Honest Baseline (Task 1)

- **HonestOverallLine**: 51.36% (Floor: 51.3%)
- **HonestBranch**: 37.34% (Floor: 37.3%)
- **HonestCore**: 90.02% (Floor: 90.0%)

Measured on .NET 10 via Coverlet (`coverage.runsettings`) with the approved exclusion list.

## Approved Coverage Exclusions (`ApprovedCoverageExclusions`)

Only files falling strictly into the categories of generated code, desktop UI presentation, embedded browser controls, or native process interop are permitted in `tests/coverage.runsettings`.

| Pattern | Category | Rationale |
|---|---|---|
| `**/*.g.cs` | Generated Code | Generated compiler output produced during build without custom business logic. |
| `**/*.Designer.cs` | Generated Code | Visual designer-generated layout files containing auto-generated initialization. |
| `**/App.axaml.cs` | UI Framework | Avalonia application entrypoint and framework lifecycle requiring desktop runtime bootstrap. |
| `**/AvaloniaUI/**` | UI Presentation | Avalonia visual tree, windowing, and XAML views requiring desktop GUI display context. |
| `**/tests/**` | Test Harness | Test project source files and mock fixtures excluded from production coverage metrics. |
| `**/RobloxProcess.cs` | Native Interop | Win32 process interop, memory inspection, and OS handle manipulation requiring a live Roblox game process. |
| `**/RobloxWatcher.cs` | Native Interop | Background Win32 process watcher thread polling OS process lifecycle events. |

## Removed Artificial Exclusions

The 17 extra exclusions artificially added in commit `ccb4f6b` have been removed:
`AccountBrowser*.cs`, `RobloxProcess*.cs`, `ChromeLaunchHelper*.cs`, `AvaloniaDialogService.cs`, `AccountLaunchService*.cs`, `RobloxDirectLauncher*.cs`, `RobloxSingletonService.cs`, `AntiAfkService.cs`, `DiscordRpcService.cs`, `Program.cs`, `AccountManager*.cs`, `MemoryOptimizer.cs`, `Presence.cs`, `RobloxWindow*.cs`, `RobloxAudioService.cs`, `SystemTrayService.cs`, `UpdaterService.cs`, `PrerequisiteService.cs`, `RobloxLauncherService.cs`.

These files contain testable business logic and their coverage will be increased honestly through behavioral unit tests in subsequent tasks.

## Accepted async void (`ICommand.Execute`)

`AvaloniaUI/` has **0** `async void` methods (`AsyncVoidHandlers_InViews_ShouldNotExceedBaseline` is locked at baseline `0`). All WebView2 navigation and polling handlers in `FastLoginDialog` are task-based methods with full exception catching and logging.

The only remaining `async void` methods in `src/Shiro` are the two `ICommand.Execute(object? parameter)` implementations in `src/Shiro/ViewModels/RelayCommand.cs` (`AsyncRelayCommand` and `AsyncRelayCommand<T>`), which wrap `ExecuteAsync(parameter)` with cancellation and error handling as required by the synchronous `void ICommand.Execute(object? parameter)` interface contract.

