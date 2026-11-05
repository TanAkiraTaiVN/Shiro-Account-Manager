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

            foreach (ReadOnlySpan<char> rawLine in raw.AsSpan().EnumerateLines())
            {
                ReadOnlySpan<char> lineSpan = rawLine.Trim();
                if (lineSpan.IsEmpty)
                    continue;

                if (TryParseAccountLineSpan(lineSpan, out string? cookie, out string? password) &&
                    !string.IsNullOrEmpty(cookie))
                {
                    cookies.Add((cookie, password));
                }
                else
                {
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

            TryParseAccountLineSpan(line.AsSpan().Trim(), out string? cookie, out string? password);
            return (cookie, password);
        }

        private static bool TryParseAccountLineSpan(ReadOnlySpan<char> line, out string? cookie, out string? password)
        {
            cookie = null;
            password = null;
            if (line.IsEmpty)
                return false;

            int warningIdx = line.IndexOf("_|WARNING".AsSpan(), StringComparison.Ordinal);
            int scanLength = warningIdx >= 0 ? warningIdx : line.Length;
            int pipeCount = 0;
            int colonCount = 0;
            for (int i = 0; i < scanLength; i++)
            {
                char c = line[i];
                if (c == '|') pipeCount++;
                else if (c == ':') colonCount++;
            }

            char sep = '\0';
            if (pipeCount >= 2)
                sep = '|';
            else if (colonCount >= 2)
                sep = ':';

            if (sep != '\0')
            {
                int firstSep = line.IndexOf(sep);
                if (firstSep >= 0 && firstSep + 1 < line.Length)
                {
                    ReadOnlySpan<char> afterFirst = line.Slice(firstSep + 1);
                    int secondSepRel = afterFirst.IndexOf(sep);
                    if (secondSepRel >= 0)
                    {
                        ReadOnlySpan<char> passSpan = afterFirst.Slice(0, secondSepRel).Trim();
                        ReadOnlySpan<char> candidateCookieSpan = afterFirst.Slice(secondSepRel + 1).Trim();
                        if (candidateCookieSpan.Length > 80 ||
                            candidateCookieSpan.IndexOf("_|WARNING".AsSpan(), StringComparison.Ordinal) >= 0)
                        {
                            string? cleaned = CleanCookieSpan(candidateCookieSpan);
                            if (!string.IsNullOrEmpty(cleaned))
                            {
                                cookie = cleaned;
                                password = passSpan.ToString();
                                return true;
                            }
                        }
                    }
                }
            }

            string? singleCookie = CleanCookieSpan(line);
            if (!string.IsNullOrEmpty(singleCookie))
            {
                cookie = singleCookie;
                return true;
            }

            return false;
        }

        public static string? CleanCookie(string? line)
        {
            if (string.IsNullOrWhiteSpace(line))
                return null;

            return CleanCookieSpan(line.AsSpan());
        }

        private static string? CleanCookieSpan(ReadOnlySpan<char> span)
        {
            span = span.Trim();
            if (span.IsEmpty)
                return null;

            int warningIdx = span.IndexOf("_|WARNING".AsSpan(), StringComparison.Ordinal);
            if (warningIdx >= 0)
            {
                return Account.SanitizeCookie(span.Slice(warningIdx).ToString());
            }

            if (span.Length > 80)
            {
                string sanitized = Account.SanitizeCookie(span.ToString());
                if (!string.IsNullOrEmpty(sanitized) && sanitized.Length > 80)
                    return sanitized;
            }

            return null;
        }
    }
}
