#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
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
                var file = Path.Combine(RepoRoot, "src", "Shiro", "Classes", "Batch.cs");
                Assert.True(File.Exists(file), $"File not found: {file}");
                var content = File.ReadAllText(file);
                Assert.False(Regex.IsMatch(content, @"Task\.Run"),
                    "Batch image/place requests must remain async without a thread-pool wrapper.");
            }

            [Fact]
            public void RobloxProcess_ShouldNotUseBlockingLogLookup()
            {
                var file = Path.Combine(RepoRoot, "src", "Shiro", "Classes", "RobloxProcess.cs");
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
                var file = Path.Combine(RepoRoot, "src", "Shiro", "Classes", "Program.cs");
                Assert.True(File.Exists(file), $"File not found: {file}");
                var content = File.ReadAllText(file);
                Assert.False(Regex.IsMatch(content, @"NET Framework 4\.7\.2"),
                    "The .NET Framework 4.7.2 runtime check is obsolete for the .NET 10 self-contained build.");
            }
        }
    }
}
