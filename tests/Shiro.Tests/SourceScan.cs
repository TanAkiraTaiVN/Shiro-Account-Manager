#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;

namespace Shiro.Tests
{
    internal static class SourceScan
    {
        private static readonly Regex LiteralRegex = new(
            @"(?<comment>//[^\r\n]*|/\*[\s\S]*?\*/)|""""""[\s\S]*?""""""|@""(?:[^""]|"""")*""|\$@""(?:[^""]|"""")*""|@\$""(?:[^""]|"""")*""|""(?:\\.|[^""\\\r\n])*""",
            RegexOptions.Compiled);

        public static string StripStringLiterals(string source)
        {
            if (string.IsNullOrEmpty(source))
            {
                return source;
            }

            return LiteralRegex.Replace(source, m => m.Groups["comment"].Success ? m.Value : "\"\"");
        }

        public static IEnumerable<string> AppSourceFiles(string repoRoot)
        {
            var appDir = Path.Combine(repoRoot, "src", "Shiro");
            return Directory.GetFiles(appDir, "*.cs", SearchOption.AllDirectories)
                .Where(f =>
                {
                    var normalized = f.Replace('\\', '/');
                    return !normalized.Contains("/bin/")
                        && !normalized.Contains("/obj/")
                        && !normalized.EndsWith(".g.cs", StringComparison.OrdinalIgnoreCase);
                });
        }
    }
}
