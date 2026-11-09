#nullable enable
using Shiro.ViewModels;

namespace Shiro
{
    /// <summary>
    /// View-model adapter extensions for <see cref="AccountSearchQuery"/> and <see cref="AccountSearchHelper"/>,
    /// enabling search matching over <see cref="AccountViewModel"/> without leaking view-model dependencies into the Domain layer.
    /// </summary>
    public static class AccountSearchViewModelExtensions
    {
        public static bool IsMatch(this AccountSearchQuery query, AccountViewModel? acc)
        {
            if (query == null || acc == null) return false;
            return query.IsMatch(acc.Username, acc.Alias, acc.Group, acc.UserId);
        }

        public static bool IsMatch(AccountViewModel? acc, string? query) =>
            AccountSearchQuery.Create(query).IsMatch(acc);
    }
}
