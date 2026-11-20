#nullable enable
using System.Collections.Generic;

namespace Shiro.Services
{
    public interface IAccountWorkflowItem
    {
        Account Model { get; }
        string Username { get; }
        string ShownUsername { get; }
        string ShownAlias { get; }
        string ShownUserId { get; }
        string Alias { get; set; }
        string Group { get; set; }
        string Description { get; set; }
        bool Pinned { get; set; }
        void NotifyAllPropertiesChanged();
    }

    public interface IAccountWorkflowHost
    {
        string PlaceId { get; }
        IReadOnlyList<IAccountWorkflowItem> GetSelectedWorkflowItems();
        IReadOnlyList<Account> GetSelectedAccounts();
        void UpdateAccountValidity(Account account, bool isValid, bool refreshFilterAndCounts = true);
        void NotifyCounts();
        void RebuildLookupIndex();
        void RebuildGroupList(bool applyFilterOnReset = true);
        void ApplyFilter();
        void RemoveWorkflowItems(params IAccountWorkflowItem[] toRemove);
    }
}
