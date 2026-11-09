﻿using Shiro.Services;
using Shiro.ViewModels;
using Xunit;

namespace Shiro.Tests
{
    public class AccountSearchQueryTests
    {
        [Theory]
        [InlineData("1-10", "Bot5", true)]
        [InlineData("1-10", "Bot10", true)]
        [InlineData("1-10", "Bot11", false)]
        [InlineData("5-1", "Bot3", true)] // Reversed range
        [InlineData("jackpot 1-5", "jackpot_bot3", true)]
        [InlineData("jackpot 1-5", "other_bot3", false)]
        public void AccountSearchQuery_RangeSearch_MatchesCorrectly(string query, string username, bool shouldMatch)
        {
            var acc = new AccountViewModel(new Account { Username = username });
            var q = AccountSearchQuery.Create(query);

            Assert.True(q.IsRange);
            Assert.Equal(shouldMatch, q.IsMatch(acc));
        }

        [Theory]
        [InlineData("7", "Jackpot7", true)]
        [InlineData("7", "Jackpot70", false)]
        [InlineData("7", "7", true)]
        [InlineData("jackpot 7", "jackpot_bot7", true)]
        [InlineData("jackpot 7", "other_bot7", false)]
        public void AccountSearchQuery_SingleNumber_MatchesSuffixCorrectly(string query, string username, bool shouldMatch)
        {
            var acc = new AccountViewModel(new Account { Username = username });
            var q = AccountSearchQuery.Create(query);

            Assert.True(q.IsSingleNum);
            Assert.Equal(shouldMatch, q.IsMatch(acc));
        }

        [Fact]
        public void AccountSearchQuery_UserIdSearch_MatchesLongDigits()
        {
            var acc = new AccountViewModel(new Account { Username = "RandomUser", UserID = 123456789 });
            var q = AccountSearchQuery.Create("12345");

            Assert.True(q.IsMatch(acc));
        }

        [Fact]
        public void AccountSearchQuery_GeneralSubstring_MatchesUsernameOrAliasOrGroup()
        {
            var acc = new AccountViewModel(new Account
            {
                Username = "AlphaPlayer",
                Alias = "MainFarmer",
                Group = "VIPGroup"
            });

            Assert.True(AccountSearchQuery.Create("alpha").IsMatch(acc));
            Assert.True(AccountSearchQuery.Create("farmer").IsMatch(acc));
            Assert.True(AccountSearchQuery.Create("vip").IsMatch(acc));
            Assert.False(AccountSearchQuery.Create("nonexistent").IsMatch(acc));
        }

        [Theory]
        [InlineData("User123", 123)]
        [InlineData("Jackpot007", 7)]
        [InlineData("NoNumber", -1)]
        [InlineData("", -1)]
        public void AccountSearchHelper_GetSuffixNumberSpan_ExtractsNumberAccurately(string text, long expected)
        {
            long result = AccountSearchHelper.GetSuffixNumber(text);
            Assert.Equal(expected, result);
        }

        [Theory]
        [InlineData("Player7", "7", true)]
        [InlineData("Player70", "7", false)]
        [InlineData("Player007", "007", true)]
        public void AccountSearchHelper_EndsWithDigits_WorksAccurately(string text, string digits, bool expected)
        {
            bool result = AccountSearchHelper.EndsWithDigits(text, digits);
            Assert.Equal(expected, result);
        }
    }
}
