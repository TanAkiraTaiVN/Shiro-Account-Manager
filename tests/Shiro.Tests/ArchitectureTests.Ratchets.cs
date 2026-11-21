#nullable enable
using System;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using Xunit;

namespace Shiro.Tests
{
    public partial class ArchitectureTests
    {
        [Fact]
        public void AsyncVoidHandlers_InViews_ShouldNotExceedBaseline()
        {
            const int Baseline = 0;
            var viewsDir = Path.Combine(RepoRoot, "src", "Shiro", "AvaloniaUI");
            var files = Directory.GetFiles(viewsDir, "*.cs", SearchOption.AllDirectories)
                .Where(f => !f.Replace('\\', '/').Contains("/bin/") && !f.Replace('\\', '/').Contains("/obj/") && !f.EndsWith(".g.cs"));

            int actual = 0;
            foreach (var file in files)
            {
                actual += Regex.Matches(File.ReadAllText(file), @"\basync\s+void\b").Count;
            }

            Assert.True(actual <= Baseline, $"async void count in AvaloniaUI ({actual}) exceeds baseline ({Baseline}).");
        }

        [Fact]
        public void ProxyDeadKey_ShouldBeRemovedFromSourceAndStrings()
        {
            var appDir = Path.Combine(RepoRoot, "src", "Shiro");
            var files = Directory.GetFiles(appDir, "*.cs", SearchOption.AllDirectories)
                .Where(f => !f.Replace('\\', '/').Contains("/bin/") && !f.Replace('\\', '/').Contains("/obj/") && !f.EndsWith(".g.cs"));

            var offenders = files
                .Where(f => File.ReadAllText(f).Contains("\"Proxy_Dead\"", StringComparison.Ordinal))
                .Select(Path.GetFileName)
                .ToList();

            Assert.Empty(offenders);

            string enJson = File.ReadAllText(Path.Combine(appDir, "Resources", "Strings", "en.json"));
            string viJson = File.ReadAllText(Path.Combine(appDir, "Resources", "Strings", "vi.json"));
            Assert.DoesNotContain("\"Proxy_Dead\"", enJson, StringComparison.Ordinal);
            Assert.DoesNotContain("\"Proxy_Dead\"", viJson, StringComparison.Ordinal);
        }

        [Fact]
        public void CoverageFloors_FileExists_AndCoversHotspots()
        {
            var floorsPath = Path.Combine(RepoRoot, "tests", "coverage-floors.json");
            Assert.True(File.Exists(floorsPath), $"Missing {floorsPath}");

            using var doc = System.Text.Json.JsonDocument.Parse(File.ReadAllText(floorsPath));
            var root = doc.RootElement;
            Assert.Equal(System.Text.Json.JsonValueKind.Object, root.ValueKind);

            string[] requiredHotspots =
            {
                "Services/Network/RobloxSettingsApiService.cs",
                "Services/Network/AccountApiDispatcher.Endpoints.cs",
                "Services/Windows/RobloxProcessService.Lifecycle.cs",
                "AccountManager.ModernActions.cs",
                "Services/Storage/AccountWorkflowService.cs",
                "Services/Network/RobloxServerQueryService.cs",
                "AccountManager.cs",
                "Services/Launch/LaunchCoordinator.Helpers.cs",
                "Services/Storage/AccountPersistenceService.cs",
                "Services/Storage/AccountRegistrationService.cs",
                "Services/Launch/AccountLaunchService.Diagnostics.cs",
                "ViewModels/ServerList/GameExplorerTabViewModel.cs",
                "ViewModels/ServerList/GameLibraryTabViewModel.cs",
                "Services/Launch/AccountLaunchService.cs",
                "ViewModels/ServerList/ServerBrowserTabViewModel.Operations.cs",
                "Services/Launch/AutoReconnectService.cs",
                "Services/Network/RobloxModerationService.cs",
                "Services/Launch/RobloxPlaceHelper.cs",
                "Services/Auth/WebAuthPolicy.cs",
                "Services/Network/KestrelWebServer.cs",
                "Services/Storage/AccountRuntimeStateStore.cs",
                "Services/Auth/SessionAuditorService.cs"
            };

            foreach (var hotspot in requiredHotspots)
            {
                Assert.True(root.TryGetProperty(hotspot, out var entry), $"Missing hotspot floor for {hotspot}");
                Assert.True(entry.TryGetProperty("branch", out var branchProp), $"Missing branch property for {hotspot}");
                double floorVal = branchProp.GetDouble();
                Assert.InRange(floorVal, 0.0, 100.0);
            }
        }

        [GeneratedRegex(@"\b(DispatcherFrame|PushFrame)\b")]
        private static partial Regex DispatcherFramePattern();

        [GeneratedRegex(@"\.Wait\s*\(\s*TimeSpan\b")]
        private static partial Regex WaitWithTimeSpanPattern();

        [GeneratedRegex(@"\bThread\.Sleep\b")]
        private static partial Regex ThreadSleepPattern();

        private static List<string> FindSourcePatternOffenders(Regex pattern, HashSet<string> allowRelativePaths)
        {
            string appRoot = Path.Combine(RepoRoot, "src", "Shiro");
            return SourceScan.AppSourceFiles(RepoRoot)
                .Select(f => (FullPath: f, RelPath: Path.GetRelativePath(appRoot, f).Replace('\\', '/')))
                .Where(x => !allowRelativePaths.Contains(x.RelPath))
                .Where(x => pattern.IsMatch(SourceScan.StripCommentsAndStringLiterals(File.ReadAllText(x.FullPath))))
                .Select(x => x.RelPath)
                .OrderBy(n => n, StringComparer.OrdinalIgnoreCase)
                .ToList();
        }

        [Theory]
        [InlineData("{DynamicResource Theme.AccentBrush}", "Theme.AccentBrush")]
        [InlineData("{DynamicResource Settings.ExportVaultBtn}", "Settings.ExportVaultBtn")]
        public void ArchitectureTests_DynamicResourceRegex_CapturesDottedKeys(string sample, string expectedKey)
        {
            var match = DynamicResourcePattern().Match(sample);
            Assert.True(match.Success);
            Assert.Equal(expectedKey, match.Groups[1].Value);
        }

        [Fact]
        public void DispatcherFrame_ShouldNotBeUsedInSourceFiles()
        {
            var allow = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var offenders = FindSourcePatternOffenders(DispatcherFramePattern(), allow);
            Assert.Empty(offenders);
        }

        [Fact]
        public void WaitWithTimeSpan_ShouldNotExceedAllowList()
        {
            var allow = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            {
                "AccountManager.Lifecycle.cs",
                "Services/Storage/AppLogger.cs"
            };
            var offenders = FindSourcePatternOffenders(WaitWithTimeSpanPattern(), allow);
            Assert.Empty(offenders);
        }

        [Fact]
        public void ThreadSleep_ShouldNotExceedAllowList()
        {
            var allow = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            {
                "Services/Storage/StoragePaths.VaultKey.cs"
            };
            var offenders = FindSourcePatternOffenders(ThreadSleepPattern(), allow);
            Assert.Empty(offenders);
        }

        [Fact]
        public void RobloxProcessLogReader_ShouldBeExtractedFromRobloxProcess()
        {
            string windowsDir = Path.Combine(RepoRoot, "src", "Shiro", "Services", "Windows");
            string logReaderPath = Path.Combine(windowsDir, "RobloxProcess.LogReader.cs");
            string processPath = Path.Combine(windowsDir, "RobloxProcess.cs");

            Assert.True(File.Exists(logReaderPath), "Expected RobloxProcess.LogReader.cs to exist.");
            string logReader = File.ReadAllText(logReaderPath);
            string process = File.ReadAllText(processPath);

            Assert.Contains("internal static string[] ReadAppendedLines", logReader, StringComparison.Ordinal);
            Assert.Contains("internal static string[] SplitCompletedLogLines", logReader, StringComparison.Ordinal);
            Assert.Contains("internal static void RaiseLogFileReadSafely", logReader, StringComparison.Ordinal);
            Assert.Contains("internal static bool ShouldAllowKill", logReader, StringComparison.Ordinal);
            Assert.DoesNotContain("internal static string[] ReadAppendedLines", process, StringComparison.Ordinal);
            Assert.DoesNotContain("internal static string[] SplitCompletedLogLines", process, StringComparison.Ordinal);
        }

        [Fact]
        public void Workflows_And_TestLockfile_ShouldEnforceHardenedCiGates()
        {
            string testCsproj = File.ReadAllText(Path.Combine(RepoRoot, "tests", "Shiro.Tests", "Shiro.Tests.csproj"));
            Assert.Contains("<RestorePackagesWithLockFile>true</RestorePackagesWithLockFile>", testCsproj, StringComparison.Ordinal);
            Assert.True(File.Exists(Path.Combine(RepoRoot, "tests", "Shiro.Tests", "packages.lock.json")),
                "Expected tests/Shiro.Tests/packages.lock.json to exist.");

            string buildYml = File.ReadAllText(Path.Combine(RepoRoot, ".github", "workflows", "build.yml"));
            Assert.Contains("--runtime win-x64 /p:SelfContained=true --locked-mode", buildYml, StringComparison.Ordinal);
            Assert.Contains("-warnaserror", buildYml, StringComparison.Ordinal);
            Assert.Contains("--no-restore", buildYml, StringComparison.Ordinal);
            Assert.Contains("if: failure()", buildYml, StringComparison.Ordinal);
            Assert.Contains("path: coverage/", buildYml, StringComparison.Ordinal);

            string releaseYml = File.ReadAllText(Path.Combine(RepoRoot, ".github", "workflows", "release.yml"));
            Assert.Contains("--runtime win-x64 /p:SelfContained=true --locked-mode", releaseYml, StringComparison.Ordinal);
            Assert.Contains("-warnaserror", releaseYml, StringComparison.Ordinal);
            Assert.Contains("--no-restore", releaseYml, StringComparison.Ordinal);
            Assert.Contains("if: failure()", releaseYml, StringComparison.Ordinal);
            Assert.Contains("path: coverage/", releaseYml, StringComparison.Ordinal);
        }

        [Fact]
        public void ReleaseWorkflow_UnitTestsAndCoverageGate_MustFailOnNonZeroTestExitCode()
        {
            string workflowPath = Path.Combine(RepoRoot, ".github", "workflows", "release.yml");
            string content = File.ReadAllText(workflowPath);

            bool hasExplicitExitGuard =
                content.Contains("$LASTEXITCODE -ne 0", StringComparison.Ordinal) &&
                content.Contains("exit $LASTEXITCODE", StringComparison.Ordinal);

            Assert.True(
                hasExplicitExitGuard,
                "release.yml must explicitly fail when dotnet test exits non-zero before running Check-Coverage.ps1.");
        }
    }
}
