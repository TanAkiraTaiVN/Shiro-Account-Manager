#nullable enable
using System;
using System.Text.RegularExpressions;

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

        [GeneratedRegex(@"^(?:([\p{L}\p{N}_\-\s]+?)\s+)?#?(\d+)\s*(?:-|–|\.\.)\s*#?(\d+)$")]
        private static partial Regex RangeRegex();

        [GeneratedRegex(@"^(?:([\p{L}\p{N}_\-\s]+?)\s+)?#?(\d+)$")]
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

        public bool IsMatch(Account? acc) => acc != null && IsMatch(acc.Username, acc.Alias, acc.Group, acc.UserID);

        public bool IsMatch(string? username, string? alias, string? group, long userId)
        {
            if (IsEmpty) return true;

            string user = username ?? string.Empty;
            string al = alias ?? string.Empty;
            string grp = group ?? string.Empty;

            // 1. Range Search
            if (IsRange)
            {
                if (!string.IsNullOrEmpty(Prefix))
                {
                    if (!user.Contains(Prefix, StringComparison.OrdinalIgnoreCase) &&
                        !al.Contains(Prefix, StringComparison.OrdinalIgnoreCase))
                    {
                        return false;
                    }
                }

                long accNum = AccountSearchHelper.GetSuffixNumberSpan(user);
                if (accNum >= 0 && accNum >= RangeStart && accNum <= RangeEnd)
                    return true;

                long aliasNum = AccountSearchHelper.GetSuffixNumberSpan(al);
                if (aliasNum >= 0 && aliasNum >= RangeStart && aliasNum <= RangeEnd)
                    return true;

                return false;
            }

            // 2. Single Number / Suffix Search
            if (IsSingleNum)
            {
                // Long number (>= 5 digits) with no prefix -> Likely Roblox User ID search
                if (string.IsNullOrEmpty(Prefix) && Digits.Length >= 5 && userId > 0)
                {
                    Span<char> idBuf = stackalloc char[24];
                    if (userId.TryFormat(idBuf, out int written) &&
                        idBuf[..written].IndexOf(Digits.AsSpan(), StringComparison.Ordinal) >= 0)
                    {
                        return true;
                    }
                }

                if (!string.IsNullOrEmpty(Prefix))
                {
                    if (!user.Contains(Prefix, StringComparison.OrdinalIgnoreCase) &&
                        !al.Contains(Prefix, StringComparison.OrdinalIgnoreCase))
                    {
                        return false;
                    }
                }

                // Check exact suffix number (e.g. AD_Jackpot7 ends with "7") or zero-padded numeric equivalence ("Bot07" <-> "7")
                long? querySuffixNum = long.TryParse(Digits, out long parsedSuffixNum) ? parsedSuffixNum : null;
                if (AccountSearchHelper.EndsWithDigits(user, Digits) ||
                    (querySuffixNum.HasValue && AccountSearchHelper.GetSuffixNumberSpan(user) == querySuffixNum.Value))
                    return true;

                if (AccountSearchHelper.EndsWithDigits(al, Digits) ||
                    (querySuffixNum.HasValue && AccountSearchHelper.GetSuffixNumberSpan(al) == querySuffixNum.Value))
                    return true;

                // If prefix was explicitly specified (e.g. "jackpot 7"), also check substring
                if (!string.IsNullOrEmpty(Prefix) &&
                    (user.Contains(QueryText, StringComparison.OrdinalIgnoreCase) ||
                     al.Contains(QueryText, StringComparison.OrdinalIgnoreCase)))
                {
                    return true;
                }

                // Match exact name/alias or Group substring for short (< 4 digit) queries while avoiding partial UserID/Username matches
                if (string.IsNullOrEmpty(Prefix) && Digits.Length < 4)
                {
                    return user.Equals(Digits, StringComparison.OrdinalIgnoreCase) ||
                           al.Equals(Digits, StringComparison.OrdinalIgnoreCase) ||
                           grp.Contains(QueryText, StringComparison.OrdinalIgnoreCase);
                }
            }

            // 3. General substring search
            if (user.Contains(QueryText, StringComparison.OrdinalIgnoreCase) ||
                al.Contains(QueryText, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }

            if (grp.Contains(QueryText, StringComparison.OrdinalIgnoreCase))
                return true;

            // Only search by User ID if query has at least 4 digits to prevent single digits matching random UserIDs
            if (QueryText.Length >= 4 && userId > 0)
            {
                Span<char> idBuf = stackalloc char[24];
                if (userId.TryFormat(idBuf, out int written) &&
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
        public static bool IsMatch(Account? acc, string? query) =>
            AccountSearchQuery.Create(query).IsMatch(acc);

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
