#nullable enable
using System;
using System.Text.RegularExpressions;
using Shiro.ViewModels;

namespace Shiro
{
    /// <summary>
    /// High-performance compiled search query for accounts.
    /// Parses search patterns (ranges, suffixes, User IDs, substrings) once,
    /// enabling zero-allocation, sub-millisecond filtering across hundreds of accounts.
    /// </summary>
    public sealed partial class AccountSearchQuery
    {
        public static readonly AccountSearchQuery Empty = new AccountSearchQuery { IsEmpty = true };

        public bool IsEmpty { get; private init; }
        public bool IsRange { get; private init; }
        public bool IsSingleNum { get; private init; }
        public string Prefix { get; private init; } = string.Empty;
        public long RangeStart { get; private init; }
        public long RangeEnd { get; private init; }
        public string Digits { get; private init; } = string.Empty;
        public string QueryText { get; private init; } = string.Empty;

        [GeneratedRegex(@"^(?:([a-zA-Z0-9_\s]+?)\s+)?#?(\d+)\s*(?:-|–|\.\.)\s*#?(\d+)$")]
        private static partial Regex RangeRegex();

        [GeneratedRegex(@"^(?:([a-zA-Z0-9_\s]+?)\s+)?#?(\d+)$")]
        private static partial Regex SingleNumRegex();

        public static AccountSearchQuery Create(string? query)
        {
            if (string.IsNullOrWhiteSpace(query))
                return Empty;

            string trimmed = query.Trim();

            // 1. Check Range Pattern: e.g. "1-10", "#1-10", "1..10", "jackpot 1-10"
            var rangeMatch = RangeRegex().Match(trimmed);
            if (rangeMatch.Success)
            {
                if (long.TryParse(rangeMatch.Groups[2].Value, out long start) &&
                    long.TryParse(rangeMatch.Groups[3].Value, out long end))
                {
                    if (start > end)
                    {
                        (start, end) = (end, start);
                    }

                    return new AccountSearchQuery
                    {
                        IsRange = true,
                        Prefix = rangeMatch.Groups[1].Value.Trim(),
                        RangeStart = start,
                        RangeEnd = end,
                        QueryText = trimmed
                    };
                }
            }

            // 2. Check Number / Suffix Pattern: e.g. "7", "#7", "jackpot 7"
            var singleMatch = SingleNumRegex().Match(trimmed);
            if (singleMatch.Success)
            {
                return new AccountSearchQuery
                {
                    IsSingleNum = true,
                    Prefix = singleMatch.Groups[1].Value.Trim(),
                    Digits = singleMatch.Groups[2].Value,
                    QueryText = trimmed
                };
            }

            // 3. General substring query
            return new AccountSearchQuery
            {
                QueryText = trimmed
            };
        }

        public bool IsMatch(AccountViewModel? acc)
        {
            if (acc == null) return false;
            if (IsEmpty) return true;

            string username = acc.Username ?? string.Empty;
            string alias = acc.Alias ?? string.Empty;
            string group = acc.Group ?? string.Empty;

            // 1. Range Search
            if (IsRange)
            {
                if (!string.IsNullOrEmpty(Prefix))
                {
                    if (!username.Contains(Prefix, StringComparison.OrdinalIgnoreCase) &&
                        !alias.Contains(Prefix, StringComparison.OrdinalIgnoreCase))
                    {
                        return false;
                    }
                }

                long accNum = AccountSearchHelper.GetSuffixNumberSpan(username);
                if (accNum >= 0 && accNum >= RangeStart && accNum <= RangeEnd)
                    return true;

                long aliasNum = AccountSearchHelper.GetSuffixNumberSpan(alias);
                if (aliasNum >= 0 && aliasNum >= RangeStart && aliasNum <= RangeEnd)
                    return true;

                return false;
            }

            // 2. Single Number / Suffix Search
            if (IsSingleNum)
            {
                // Long number (>= 5 digits) with no prefix -> Likely Roblox User ID search
                if (string.IsNullOrEmpty(Prefix) && Digits.Length >= 5 && acc.UserId > 0)
                {
                    Span<char> idBuf = stackalloc char[24];
                    if (acc.UserId.TryFormat(idBuf, out int written) &&
                        idBuf[..written].IndexOf(Digits.AsSpan(), StringComparison.Ordinal) >= 0)
                    {
                        return true;
                    }
                }

                if (!string.IsNullOrEmpty(Prefix))
                {
                    if (!username.Contains(Prefix, StringComparison.OrdinalIgnoreCase) &&
                        !alias.Contains(Prefix, StringComparison.OrdinalIgnoreCase))
                    {
                        return false;
                    }
                }

                // Check exact suffix number (e.g. AD_Jackpot7 ends with "7")
                if (AccountSearchHelper.EndsWithDigits(username, Digits))
                    return true;

                if (AccountSearchHelper.EndsWithDigits(alias, Digits))
                    return true;

                // If prefix was explicitly specified (e.g. "jackpot 7"), also check substring
                if (!string.IsNullOrEmpty(Prefix) &&
                    (username.Contains(QueryText, StringComparison.OrdinalIgnoreCase) ||
                     alias.Contains(QueryText, StringComparison.OrdinalIgnoreCase)))
                {
                    return true;
                }

                // If pure short digits (e.g. "7"): only match if username/alias exactly equals digits or ends with it
                if (string.IsNullOrEmpty(Prefix))
                {
                    return username.Equals(Digits, StringComparison.OrdinalIgnoreCase) ||
                           alias.Equals(Digits, StringComparison.OrdinalIgnoreCase);
                }
            }

            // 3. General substring search
            if (username.Contains(QueryText, StringComparison.OrdinalIgnoreCase) ||
                alias.Contains(QueryText, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }

            if (group.Contains(QueryText, StringComparison.OrdinalIgnoreCase))
                return true;

            // Only search by User ID if query has at least 4 digits to prevent single digits matching random UserIDs
            if (QueryText.Length >= 4 && acc.UserId > 0)
            {
                Span<char> idBuf = stackalloc char[24];
                if (acc.UserId.TryFormat(idBuf, out int written) &&
                    idBuf[..written].IndexOf(QueryText.AsSpan(), StringComparison.Ordinal) >= 0)
                {
                    return true;
                }
            }

            return false;
        }
    }

    public static class AccountSearchHelper
    {
        public static bool IsMatch(AccountViewModel? acc, string? query)
        {
            var q = AccountSearchQuery.Create(query);
            return q.IsMatch(acc);
        }

        public static long GetSuffixNumber(string? text) => GetSuffixNumberSpan(text.AsSpan());

        public static long GetSuffixNumberSpan(ReadOnlySpan<char> span)
        {
            span = span.TrimEnd();
            if (span.IsEmpty) return -1;

            int end = span.Length - 1;
            int start = end;

            while (start >= 0 && char.IsDigit(span[start]))
            {
                start--;
            }

            start++; // First digit index
            if (start > end) return -1;

            ReadOnlySpan<char> digitSlice = span.Slice(start, end - start + 1);
            if (long.TryParse(digitSlice, out long val))
                return val;

            return -1;
        }

        public static bool EndsWithDigits(ReadOnlySpan<char> text, ReadOnlySpan<char> digits)
        {
            text = text.TrimEnd();
            if (text.IsEmpty || digits.IsEmpty) return false;

            int end = text.Length - 1;
            int start = end;

            while (start >= 0 && char.IsDigit(text[start]))
            {
                start--;
            }

            start++;
            if (start > end) return false;

            ReadOnlySpan<char> digitSlice = text.Slice(start, end - start + 1);
            return digitSlice.SequenceEqual(digits);
        }
    }
}
