#nullable enable
using System.Collections.Generic;

namespace Shiro.Services
{
    public interface ISelectionStateService
    {
        Account? SelectedAccount { get; set; }
        Account? LastValidAccount { get; set; }
        List<Account> SelectedAccounts { get; set; }
        void SetSelectedAccounts(IEnumerable<Account> accounts);
    }
}
