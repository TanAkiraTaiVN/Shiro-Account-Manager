#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Shiro.Services;
using Xunit;

namespace Shiro.Tests
{
    public partial class ArchitectureTests
    {
        public class SourceInvariants
        {
            [Fact]
            public void Batch_ShouldNotUseTaskRun()
            {
                var file = Path.Combine(RepoRoot, "src", "Shiro", "Services", "Launch", "Batch.cs");
                Assert.True(File.Exists(file), $"File not found: {file}");
                var content = File.ReadAllText(file);
                Assert.False(Regex.IsMatch(content, @"Task\.Run"),
                    "Batch image/place requests must remain async without a thread-pool wrapper.");
            }

            [Fact]
            public void RobloxProcess_ShouldNotUseBlockingLogLookup()
            {
                var file = Path.Combine(RepoRoot, "src", "Shiro", "Services", "Windows", "RobloxProcess.cs");
                Assert.True(File.Exists(file), $"File not found: {file}");
                var content = File.ReadAllText(file);
                Assert.False(Regex.IsMatch(content, @"Task\.Run\(WaitForLogPath"),
                    "Roblox log lookup still uses the removed blocking Task.Run path.");
            }

            [Fact]
            public void Source_ShouldNotUseLegacyNetworking()
            {
                var regex = new Regex(@"\b(WebClient|WebRequest|HttpWebRequest)\b", RegexOptions.Compiled);
                var files = SourceFiles("src/Shiro", "*.cs").ToList();
                var offenders = new List<string>();

                foreach (var file in files)
                {
                    var lines = File.ReadAllLines(file);
                    for (int i = 0; i < lines.Length; i++)
                    {
                        if (regex.IsMatch(lines[i]))
                        {
                            var relativePath = Path.GetRelativePath(RepoRoot, file).Replace('\\', '/');
                            offenders.Add($"{relativePath}:{i + 1}: {lines[i].Trim()}");
                        }
                    }
                }

                Assert.True(offenders.Count == 0,
                    $"Legacy WebClient/WebRequest usage remains: {string.Join("; ", offenders)}");
            }

            [Fact]
            public void AccountDataSerializer_ShouldBeVersioned()
            {
                var file = Path.Combine(RepoRoot, "src", "Shiro", "Services", "Storage", "AccountDataSerializer.cs");
                Assert.True(File.Exists(file), $"File not found: {file}");
                var content = File.ReadAllText(file);
                Assert.True(content.Contains("CurrentSchemaVersion", StringComparison.Ordinal),
                    "Versioned account data serializer is missing.");
            }

            [Fact]
            public void StoragePaths_ShouldHaveDirectoryBoundary()
            {
                var file = Path.Combine(RepoRoot, "src", "Shiro", "Services", "Storage", "StoragePaths.cs");
                Assert.True(File.Exists(file), $"File not found: {file}");
                var content = File.ReadAllText(file);
                Assert.True(content.Contains("IsWithinDirectory", StringComparison.Ordinal),
                    "Browser profile cleanup is missing its storage boundary.");
            }

            [Fact]
            public void AccountDataSerializer_ShouldEnforceSavePasswords()
            {
                var file = Path.Combine(RepoRoot, "src", "Shiro", "Services", "Storage", "AccountDataSerializer.cs");
                Assert.True(File.Exists(file), $"File not found: {file}");
                var content = File.ReadAllText(file);
                Assert.True(content.Contains("includePasswords", StringComparison.Ordinal),
                    "SavePasswords must be enforced while serializing account data.");
            }

            [Fact]
            public void Program_ShouldNotCheckNetFramework472()
            {
                var file = Path.Combine(RepoRoot, "src", "Shiro", "Program.cs");
                Assert.True(File.Exists(file), $"File not found: {file}");
                var content = File.ReadAllText(file);
                Assert.False(Regex.IsMatch(content, @"NET Framework 4\.7\.2"),
                    "The .NET Framework 4.7.2 runtime check is obsolete for the .NET 10 self-contained build.");
            }

            [Fact]
            public void WebAuthPolicy_ShouldNotDependOn_AccountApiDispatcher()
            {
                var text = File.ReadAllText(Path.Combine(RepoRoot, "src", "Shiro", "Services", "Auth", "WebAuthPolicy.cs"));
                Assert.DoesNotContain("AccountApiDispatcher", text);
            }

            [Fact]
            public void AccountManager_ShouldNotExpose_WebAuthForwarders()
            {
                string[] removed = { "IsLoopbackHostName", "IsTrustedLocalOrigin", "FixedTimeEqualsPassword",
                                     "ExtractHeaderPassword", "ExtractQueryPassword", "ExtractRequestPassword" };
                var present = typeof(AccountManager).GetMethods(BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic)
                    .Select(m => m.Name).Intersect(removed).ToList();
                Assert.Empty(present);
            }

            [Fact]
            public void Source_ShouldNotUseLegacyDllImport()
            {
                var regex = new Regex(@"\[(?:System\.Runtime\.InteropServices\.)?DllImport\b", RegexOptions.Compiled);
                var files = SourceFiles("src/Shiro", "*.cs").ToList();
                var offenders = new List<string>();

                foreach (var file in files)
                {
                    var lines = File.ReadAllLines(file);
                    for (int i = 0; i < lines.Length; i++)
                    {
                        if (regex.IsMatch(lines[i]))
                        {
                            var relativePath = Path.GetRelativePath(RepoRoot, file).Replace('\\', '/');
                            offenders.Add($"{relativePath}:{i + 1}: {lines[i].Trim()}");
                        }
                    }
                }

                Assert.True(offenders.Count == 0,
                    $"Legacy [DllImport] usage remains: {string.Join("; ", offenders)}");
            }

            [Fact]
            public void ObsoleteForegroundHandlers_ShouldBeRemoved()
            {
                var memoryOptimizerMethod = typeof(MemoryOptimizer).GetMethod("NotifyForegroundChanged",
                    BindingFlags.Public | BindingFlags.Static);
                Assert.Null(memoryOptimizerMethod);

                var robloxAudioServiceMethod = typeof(RobloxAudioService).GetMethod("NotifyForegroundChanged",
                    BindingFlags.Public | BindingFlags.Static);
                Assert.Null(robloxAudioServiceMethod);
            }

            [Fact]
            public void AccountManager_ShouldNotExpose_WinFormsCompatibilityShims()
            {
                string[] removedMembers = { "IsHandleCreated", "BeginInvoke", "InvokeIfRequired" };
                var type = typeof(AccountManager);
                var presentMethods = type.GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
                    .Select(m => m.Name);
                var presentProperties = type.GetProperties(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
                    .Select(p => p.Name);
                var present = presentMethods.Concat(presentProperties).Intersect(removedMembers).ToList();
                Assert.Empty(present);
            }

            [Fact]
            public void LaunchResult_ShouldNotExpose_LegacyAdapters()
            {
                var toLegacyString = typeof(LaunchResult).GetMethod("ToLegacyString", BindingFlags.Instance | BindingFlags.Public);
                Assert.Null(toLegacyString);

                var fromLegacy = typeof(LaunchResult).GetMethod("FromLegacy", BindingFlags.Static | BindingFlags.Public);
                Assert.Null(fromLegacy);
            }

            [Fact]
            public void LaunchCoordinator_ShouldNotAccept_LegacyStringDelegate()
            {
                var ctors = typeof(LaunchCoordinator).GetConstructors();
                bool hasLegacyCtor = ctors.Any(c =>
                    c.GetParameters().Length == 1 &&
                    c.GetParameters()[0].ParameterType == typeof(Func<LaunchRequest, CancellationToken, Task<string>>));
                Assert.False(hasLegacyCtor, "LaunchCoordinator still has legacy Func<..., Task<string>> constructor.");
            }

            [Fact]
            public void UpdateCheckService_ShouldNotUse_VersionFourFallback()
            {
                var file = Path.Combine(RepoRoot, "src", "Shiro", "Services", "Network", "UpdateCheckService.cs");
                Assert.True(File.Exists(file), $"File not found: {file}");
                var content = File.ReadAllText(file);
                Assert.False(Regex.IsMatch(content, @"new\s+Version\s*\(\s*4\s*,"),
                    "UpdateCheckService still uses obsolete 4.x.x version fallback instead of 5.x.x.");
            }

            [Fact]
            public void LegacyDpiNexusAndEconomyRemnants_ShouldStayRemoved()
            {
                var accountManagerPath = Path.Combine(RepoRoot, "src", "Shiro", "AccountManager.cs");
                var accountManagerContent = File.ReadAllText(accountManagerPath);
                Assert.DoesNotContain("GetSystemMetrics", accountManagerContent, StringComparison.Ordinal);
                Assert.DoesNotContain("ScaleFonts=true", accountManagerContent, StringComparison.Ordinal);

                Assert.Null(typeof(Program).GetProperty("Scale", BindingFlags.Public | BindingFlags.Static));
                Assert.Null(typeof(Program).GetProperty("ScaleFonts", BindingFlags.Public | BindingFlags.Static));
                Assert.Null(typeof(IRobloxApiClient).GetProperty("Economy", BindingFlags.Public | BindingFlags.Instance));
                Assert.Null(typeof(RobloxApiClient).GetProperty("Economy", BindingFlags.Public | BindingFlags.Instance));
                Assert.Null(typeof(StoragePaths).GetProperty("NexusOutputDirectory", BindingFlags.Public | BindingFlags.Static));
                Assert.False(Enum.IsDefined(typeof(AuthScope), "NexusControl"), "AuthScope.NexusControl should be removed.");

                var generalKeys = typeof(AppSettingsKeys.General).GetFields(BindingFlags.Public | BindingFlags.Static).Select(f => f.Name).ToArray();
                Assert.DoesNotContain("WindowScale", generalKeys);
                Assert.DoesNotContain("ScaleFonts", generalKeys);
                foreach (string removedGeneralKey in new[]
                {
                    "SavedFollowUser", "UseEmbeddedBrowser", "FastFlagProfile", "DynamicHardwareThrottling", "SettingsPreset",
                    "FastFlag_DisableShadows", "FastFlag_DisableParticles", "FastFlag_DisablePostFx",
                    "FastFlag_DisableGrass", "FastFlag_LowResTextures", "FastFlag_DisableLightGrid"
                })
                {
                    Assert.DoesNotContain(removedGeneralKey, generalKeys);
                }
                Assert.Contains("EnableBackgroundThrottling", generalKeys);
                Assert.Contains("BackgroundTargetFps", generalKeys);

                Assert.Null(typeof(AppSettingsKeys).GetNestedType("AccountControl"));
                Assert.Null(typeof(AppSettingsKeys.Sections).GetField("AccountControl", BindingFlags.Public | BindingFlags.Static));
                Assert.Null(typeof(AppSettings).GetProperty("AccountControl", BindingFlags.Public | BindingFlags.Static));
                Assert.Null(typeof(AppServices).GetProperty("AccountControlSettings", BindingFlags.Public | BindingFlags.Static));
                Assert.Null(typeof(AppServices).GetProperty("RobloxSessionService", BindingFlags.Public | BindingFlags.Static));
                Assert.Null(typeof(AppServices).GetProperty("RobloxLauncherService", BindingFlags.Public | BindingFlags.Static));

                Assert.Null(typeof(IRobloxApiClient).GetProperty("Develop", BindingFlags.Public | BindingFlags.Instance));
                Assert.Null(typeof(RobloxApiClient).GetProperty("Develop", BindingFlags.Public | BindingFlags.Instance));
                Assert.Null(typeof(Presence).GetMethod("GetPresenceSingular", BindingFlags.Public | BindingFlags.Static));
                Assert.Null(typeof(ProxyBindingService).GetMethod("ClearClients", BindingFlags.Public | BindingFlags.Static));
                Assert.NotNull(typeof(ProxyBindingService).GetMethod("ClearClients", BindingFlags.NonPublic | BindingFlags.Static));
                Assert.Null(typeof(IDialogService).GetMethod("OpenAboutDialog", BindingFlags.Public | BindingFlags.Instance));
                Assert.False(Enum.IsDefined(typeof(SessionHealth), "Unknown"));

                foreach (var deadRelPath in new[]
                {
                    Path.Combine("src", "Shiro", "AvaloniaUI", "Views", "AboutDialog.axaml"),
                    Path.Combine("src", "Shiro", "AvaloniaUI", "Views", "AboutDialog.axaml.cs"),
                    Path.Combine("src", "Shiro", "Services", "Launch", "IRobloxLauncherService.cs"),
                    Path.Combine("src", "Shiro", "Services", "Launch", "RobloxLauncherService.cs"),
                    Path.Combine("src", "Shiro", "Services", "Auth", "IRobloxSessionService.cs"),
                    Path.Combine("src", "Shiro", "Services", "Auth", "RobloxSessionService.cs")
                })
                {
                    Assert.False(File.Exists(Path.Combine(RepoRoot, deadRelPath)), $"Dead file should be deleted: {deadRelPath}");
                }

                string publishPs1 = File.ReadAllText(Path.Combine(RepoRoot, "scripts", "Publish.ps1"));
                Assert.DoesNotContain("'Nexus'", publishPs1, StringComparison.Ordinal);
                Assert.DoesNotContain("'Nexus.lua'", publishPs1, StringComparison.Ordinal);
                Assert.DoesNotContain("'NexusOutput'", publishPs1, StringComparison.Ordinal);
            }
        }
    }
}
