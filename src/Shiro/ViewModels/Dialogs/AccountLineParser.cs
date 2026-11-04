#nullable enable
using System;
using System.Collections.Generic;
using Shiro.Classes;

namespace Shiro.ViewModels.Dialogs
{
    public sealed record LineClassification(
        IReadOnlyList<(string Cookie, string? Password)> Cookies,
        IReadOnlyList<(string User, string Pass)> UserPass,
        int Failed);

    public static class AccountLineParser
    {
        public static LineClassification Classify(string? raw)
        {
            var cookies = new List<(string Cookie, string? Password)>();
            var userPass = new List<(string User, string Pass)>();
            int failed = 0;

            if (string.IsNullOrWhiteSpace(raw))
                return new LineClassification(cookies, userPass, failed);

            var lines = raw.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            foreach (var line in lines)
            {
                var (cookie, password) = ParseAccountLine(line);
                if (!string.IsNullOrEmpty(cookie))
                {
                    cookies.Add((cookie, password));
                }
                else
                {
                    ReadOnlySpan<char> lineSpan = line.AsSpan();
                    int sep = lineSpan.IndexOf(':');
                    if (sep <= 0) sep = lineSpan.IndexOf('|');
                    if (sep <= 0) sep = lineSpan.IndexOf('\t');

                    if (sep > 0 && sep < lineSpan.Length - 1)
                    {
                        ReadOnlySpan<char> uSpan = lineSpan.Slice(0, sep).Trim();
                        ReadOnlySpan<char> pSpan = lineSpan.Slice(sep + 1).Trim();
                        if (!uSpan.IsEmpty && !pSpan.IsEmpty)
                        {
                            userPass.Add((uSpan.ToString(), pSpan.ToString()));
                            continue;
                        }
                    }
                    failed++;
                }
            }

            return new LineClassification(cookies, userPass, failed);
        }

        public static (string? Cookie, string? Password) ParseAccountLine(string? line)
        {
            if (string.IsNullOrWhiteSpace(line))
                return (null, null);

            line = line.Trim();

            int warningIdx = line.IndexOf("_|WARNING", StringComparison.Ordinal);
            int scanLength = warningIdx >= 0 ? warningIdx : line.Length;
            int pipeCount = 0;
            int colonCount = 0;
            for (int i = 0; i < scanLength; i++)
            {
                if (line[i] == '|') pipeCount++;
                else if (line[i] == ':') colonCount++;
            }

            string[]? parts = null;
            if (pipeCount >= 2)
            {
                parts = line.Split(new[] { '|' }, 3);
            }
            else if (colonCount >= 2)
            {
                parts = line.Split(new[] { ':' }, 3);
            }

            if (parts != null && parts.Length == 3)
            {
                string candidateCookie = parts[2].Trim();
                if (candidateCookie.Contains("_|WARNING") || candidateCookie.Length > 80)
                {
                    string pass = parts[1].Trim();
                    string? cleaned = CleanCookie(candidateCookie);
                    if (!string.IsNullOrEmpty(cleaned))
                        return (cleaned, pass);
                }
            }

            string? singleCookie = CleanCookie(line);
            return (!string.IsNullOrEmpty(singleCookie) ? singleCookie : null, null);
        }

        public static string? CleanCookie(string? line)
        {
            if (string.IsNullOrWhiteSpace(line))
                return null;

            int warningIdx = line.IndexOf("_|WARNING", StringComparison.Ordinal);
            if (warningIdx >= 0)
            {
                return Account.SanitizeCookie(line.Substring(warningIdx));
            }

            string sanitized = Account.SanitizeCookie(line);
            if (!string.IsNullOrEmpty(sanitized) && sanitized.Length > 80)
            {
                return sanitized;
            }

            return null;
        }
    }
}
