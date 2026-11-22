#nullable enable
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using Avalonia.Headless.XUnit;
using Avalonia.Media;
using Shiro.Services;
using Shiro.ViewModels;
using Xunit;

namespace Shiro.Tests
{
    [Collection("SerialGlobalState")]
    public sealed class Phase3BuildAssetsAndFlagTests
    {
        private static string FindRepoRoot()
        {
            var dir = new DirectoryInfo(AppContext.BaseDirectory);
            while (dir != null)
            {
                if (File.Exists(Path.Combine(dir.FullName, "Shiro.sln")))
                    return dir.FullName;
                dir = dir.Parent;
            }
            throw new InvalidOperationException("Could not locate Shiro.sln from AppContext.BaseDirectory.");
        }

        [Fact]
        public void ShiroAssembly_EmbedsCompressedFlagsZip_AndExcludesDuplicateFlagAvaloniaResources()
        {
            var asm = typeof(FlagIconService).Assembly;
            string[] resourceNames = asm.GetManifestResourceNames();

            Assert.Contains("Shiro.Resources.Flags.zip", resourceNames);
            Assert.DoesNotContain(resourceNames,
                name => name.Contains("Flags", StringComparison.OrdinalIgnoreCase) &&
                        name.EndsWith(".svg", StringComparison.OrdinalIgnoreCase));

            using Stream? zipStream = asm.GetManifestResourceStream("Shiro.Resources.Flags.zip");
            Assert.NotNull(zipStream);
            using var archive = new ZipArchive(zipStream!, ZipArchiveMode.Read);
            Assert.Equal(257, archive.Entries.Count);

            string repoRoot = FindRepoRoot();
            string csproj = File.ReadAllText(Path.Combine(repoRoot, "src", "Shiro", "Shiro.csproj"));
            Assert.Contains("<Optimize Condition=\"'$(Configuration)' == 'Release'\">true</Optimize>", csproj);
            Assert.Contains("<Optimize Condition=\"'$(Configuration)' != 'Release'\">false</Optimize>", csproj);
            Assert.Contains("PackFlagsArchive", csproj);
            Assert.Contains("IncludePackedFlagsArchive", csproj);
            Assert.DoesNotContain("<AvaloniaResource Include=\"Resources\\Flags\\*.svg\"", csproj);
            Assert.DoesNotContain("<None Update=\"Resources\\**\\*\"", csproj);
            Assert.False(Directory.Exists(Path.Combine(AppContext.BaseDirectory, "Resources")));
            string? shiroAsmDir = Path.GetDirectoryName(asm.Location);
            Assert.NotNull(shiroAsmDir);
            Assert.False(Directory.Exists(Path.Combine(shiroAsmDir!, "Resources")));
        }

        [AvaloniaFact]
        public void FlagIconService_TryOpenFromEmbeddedArchive_LoadsAllDatacenterAndSampleFlags()
        {
            Assert.False(Directory.Exists(Path.Combine(AppContext.BaseDirectory, "Resources")));

            string[] codes = { "us", "vn", "sg", "eu", "rs", "bo", "gt", "bz", "ni", "US" };
            foreach (string code in codes)
            {
                using Stream? stream = FlagIconService.TryOpenFromEmbeddedArchive(code);
                Assert.NotNull(stream);
                using var reader = new StreamReader(stream!);
                string svg = reader.ReadToEnd();
                Assert.Contains("<svg", svg, StringComparison.OrdinalIgnoreCase);
                Assert.Contains("</svg>", svg, StringComparison.OrdinalIgnoreCase);
            }

            Assert.Null(FlagIconService.TryOpenFromEmbeddedArchive(string.Empty));
            Assert.Null(FlagIconService.TryOpenFromEmbeddedArchive("   "));
            Assert.Null(FlagIconService.TryOpenFromEmbeddedArchive("zz"));
            Assert.Null(FlagIconService.TryOpenFromEmbeddedArchive("usa"));

            FlagIconService.ClearCacheForTest();
            try
            {
                var eu = Assert.IsType<DrawingImage>(FlagIconService.GetFlagImage("eu"));
                var euRoot = Assert.IsType<DrawingGroup>(eu.Drawing);
                Assert.Equal(122, euRoot.Children.Count);

                var ag = Assert.IsType<DrawingImage>(FlagIconService.GetFlagImage("ag"));
                var agRoot = Assert.IsType<DrawingGroup>(ag.Drawing);
                Assert.True(agRoot.Children.Count >= 2);

                Parallel.ForEach(
                    new[] { "us", "vn", "sg", "eu", "rs", "bo", "gt", "bz", "ni", "gb", "jp", "de", "fr", "br", "au", "ca" },
                    code =>
                    {
                        using Stream? s = FlagIconService.TryOpenFromEmbeddedArchive(code);
                        Assert.NotNull(s);
                        using var sr = new StreamReader(s!);
                        string xml = sr.ReadToEnd();
                        Assert.Contains("<svg", xml, StringComparison.OrdinalIgnoreCase);
                    });
            }
            finally
            {
                FlagIconService.ClearCacheForTest();
            }
        }

        [AvaloniaFact]
        public void ServerItemViewModel_FlagImage_CachesAcrossRepeatedHasFlagImageAndFlagImageReads()
        {
            FlagIconService.ClearCacheForTest();
            try
            {
                var vm = new ServerItemViewModel
                {
                    CountryCode = "US",
                    Region = "Virginia"
                };

                Assert.True(vm.HasFlagImage);
                IImage? first = vm.FlagImage;
                Assert.NotNull(first);

                // Clear FlagIconService LRU cache; repeated VM reads must hit VM-local cache
                // without re-entering FlagIconService.GetFlagImage.
                FlagIconService.ClearCacheForTest();
                Assert.Equal(0, FlagIconService.CachedCountForTest);

                Assert.True(vm.HasFlagImage);
                IImage? second = vm.FlagImage;
                Assert.Same(first, second);
                Assert.Equal(0, FlagIconService.CachedCountForTest);

                vm.CountryCode = "VN";
                Assert.True(vm.HasFlagImage);
                IImage? vn = vm.FlagImage;
                Assert.NotNull(vn);
                Assert.NotSame(first, vn);

                vm.CountryCode = string.Empty;
                vm.Region = string.Empty;
                Assert.False(vm.HasFlagImage);
                Assert.Null(vm.FlagImage);

                vm.Region = "EU-Central";
                Assert.True(vm.HasFlagImage);
                IImage? eu = vm.FlagImage;
                Assert.NotNull(eu);

                vm.Region = "AP-Singapore";
                IImage? sg = vm.FlagImage;
                Assert.NotNull(sg);
                Assert.NotSame(eu, sg);
            }
            finally
            {
                FlagIconService.ClearCacheForTest();
            }
        }

        [Fact]
        public void PublishScript_PreservesIntermediateObjFolder_WhileCleaningReleaseOutputs()
        {
            string repoRoot = FindRepoRoot();
            string publishScript = File.ReadAllText(Path.Combine(repoRoot, "scripts", "Publish.ps1"));

            Match match = Regex.Match(publishScript, @"\$legacyFolders\s*=\s*@\(([^)]*)\)");
            Assert.True(match.Success, "Expected $legacyFolders array in Publish.ps1.");

            string legacyItems = match.Groups[1].Value;
            Assert.Contains("'Release-Latest'", legacyItems);
            Assert.DoesNotContain("'obj'", legacyItems, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("'artifacts'", legacyItems, StringComparison.OrdinalIgnoreCase);
        }

        [Fact]
        public void StartScript_QuotesArgumentsWithSpacesProperly()
        {
            string repoRoot = FindRepoRoot();
            string startScriptPath = Path.Combine(repoRoot, "start.ps1");
            string startScript = File.ReadAllText(startScriptPath);

            Assert.DoesNotContain("ArgumentList", startScript);
            Assert.Contains("Format-WindowsArgument", startScript);
            Assert.Contains("[string[]]$quotedArgs = @(foreach", startScript);

            // Extract Format-WindowsArgument and the exact $quotedArgs line from start.ps1, then exercise both in PowerShell.
            Match fnMatch = Regex.Match(
                startScript,
                @"function\s+Format-WindowsArgument\s*\{[\s\S]*?\r?\n\}",
                RegexOptions.Multiline);
            Assert.True(fnMatch.Success, "Expected Format-WindowsArgument function in start.ps1.");

            Match joinBlockMatch = Regex.Match(
                startScript,
                @"if\s*\(\$launchArgs\.Count\s*-gt\s*0\)\s*\{[\s\S]*?\r?\n\}",
                RegexOptions.Multiline);
            Assert.True(joinBlockMatch.Success, "Expected $launchArgs quoting block in start.ps1.");

            string psCommand = fnMatch.Value + "\n" +
                "$cases = @('--tray', 'arg with spaces', 'C:\\My Folder\\', 'say \"hi\"', \"tab`targ\", '')\n" +
                "$multi = ($cases | ForEach-Object { Format-WindowsArgument $_ }) -join '|'\n" +
                "$psi = [pscustomobject]@{ Arguments = '' }\n" +
                "$launchArgs = [System.Collections.Generic.List[string]]::new()\n" +
                "$launchArgs.Add('C:\\My Folder')\n" +
                joinBlockMatch.Value + "\n" +
                "$isStringArray = $quotedArgs -is [string[]]\n" +
                "$singleJoined = $psi.Arguments\n" +
                "\"$multi`n$isStringArray`n$singleJoined\"";

            var psi = new ProcessStartInfo
            {
                FileName = "powershell.exe",
                Arguments = "-NoProfile -NonInteractive -EncodedCommand " +
                            Convert.ToBase64String(System.Text.Encoding.Unicode.GetBytes(psCommand)),
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };

            using Process? proc = Process.Start(psi);
            Assert.NotNull(proc);
            string stdout = proc!.StandardOutput.ReadToEnd().Trim();
            string stderr = proc.StandardError.ReadToEnd().Trim();
            Assert.True(proc.WaitForExit(15000), "powershell.exe timed out.");
            Assert.Equal(0, proc.ExitCode);
            Assert.Equal(string.Empty, stderr);

            string[] lines = stdout.Split(new[] { "\r\n", "\n" }, StringSplitOptions.None);
            Assert.Equal(3, lines.Length);

            string[] results = lines[0].Split('|');
            Assert.Equal(
                new[]
                {
                    "--tray",
                    "\"arg with spaces\"",
                    "\"C:\\My Folder\\\\\"",
                    "\"say \\\"hi\\\"\"",
                    "\"tab\targ\"",
                    "\"\""
                },
                results);
            Assert.Equal("True", lines[1]);
            Assert.Equal("\"C:\\My Folder\"", lines[2]);
        }
    }
}
