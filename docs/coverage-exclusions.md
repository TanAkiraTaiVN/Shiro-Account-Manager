# Coverage Exclusions & Baseline

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
| `**/RobloxBrowserLogin*.cs` | WebView Interop | Embedded browser login dialog managing web navigation and interactive Roblox session handling. |
| `**/RobloxBrowserSignup*.cs` | WebView Interop | Embedded browser signup dialog managing web navigation and interactive signup forms. |
| `**/AccountBrowser.cs` | WebView Interop | Embedded browser window wrapper handling direct cookie injection and interactive browser navigation. |
| `**/ChromiumHomeBrowser.cs` | WebView Interop | Embedded Chromium/CefSharp view displaying the Roblox home portal inside the client UI. |
| `**/RobloxProcess.cs` | Native Interop | Win32 process interop, memory inspection, and OS handle manipulation requiring a live Roblox game process. |
| `**/RobloxWatcher.cs` | Native Interop | Background Win32 process watcher thread polling OS process lifecycle events. |

## Removed Artificial Exclusions

The 17 extra exclusions artificially added in commit `ccb4f6b` have been removed:
`AccountBrowser*.cs`, `RobloxProcess*.cs`, `ChromeLaunchHelper*.cs`, `AvaloniaDialogService.cs`, `AccountLaunchService*.cs`, `RobloxDirectLauncher*.cs`, `RobloxSingletonService.cs`, `AntiAfkService.cs`, `DiscordRpcService.cs`, `Program.cs`, `AccountManager*.cs`, `MemoryOptimizer.cs`, `Presence.cs`, `RobloxWindow*.cs`, `RobloxAudioService.cs`, `SystemTrayService.cs`, `UpdaterService.cs`, `PrerequisiteService.cs`, `RobloxLauncherService.cs`.

These files contain testable business logic and their coverage will be increased honestly through behavioral unit tests in subsequent tasks.
