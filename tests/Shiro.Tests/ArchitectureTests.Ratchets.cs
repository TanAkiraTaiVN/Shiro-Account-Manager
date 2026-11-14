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
        public void ProxyDeadKey_ShouldOnlyBeUsedInProxyCode()
        {
            var appDir = Path.Combine(RepoRoot, "src", "Shiro");
            var files = Directory.GetFiles(appDir, "*.cs", SearchOption.AllDirectories)
                .Where(f => !f.Replace('\\', '/').Contains("/bin/") && !f.Replace('\\', '/').Contains("/obj/") && !f.EndsWith(".g.cs"));

            var offenders = files
                .Where(f => !Path.GetFileName(f).Contains("Proxy", StringComparison.OrdinalIgnoreCase) &&
                            File.ReadAllText(f).Contains("\"Proxy_Dead\"", StringComparison.Ordinal))
                .Select(Path.GetFileName)
                .ToList();

            Assert.Empty(offenders);
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
                "ViewModels/ServerListViewModel.Games.cs",
                "Services/Launch/AccountLaunchService.cs",
                "ViewModels/ServerListViewModel.ServerOperations.cs",
                "Services/Launch/AutoReconnectService.cs",
                "Services/Network/RobloxModerationService.cs",
                "Services/Launch/RobloxPlaceHelper.cs"
            };

            foreach (var hotspot in requiredHotspots)
            {
                Assert.True(root.TryGetProperty(hotspot, out var entry), $"Missing hotspot floor for {hotspot}");
                Assert.True(entry.TryGetProperty("branch", out var branchProp), $"Missing branch property for {hotspot}");
                double floorVal = branchProp.GetDouble();
                Assert.InRange(floorVal, 0.0, 100.0);
            }
        }
    }
}
