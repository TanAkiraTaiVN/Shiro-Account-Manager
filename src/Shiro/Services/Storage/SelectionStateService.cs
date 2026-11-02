#nullable enable
using System.Collections.Generic;

namespace Shiro.Services
{
    public sealed class SelectionStateService : ISelectionStateService
    {
        public static readonly SelectionStateService Default = new();

        private readonly System.Threading.Lock _sync = new();
        private List<Account> _selectedAccounts = new();

        public Account? SelectedAccount { get; set; }
        public Account? LastValidAccount { get; set; }

        public List<Account> SelectedAccounts
        {
            get
            {
                lock (_sync)
                {
                    return _selectedAccounts;
                }
            }
            set
            {
                lock (_sync)
                {
                    _selectedAccounts = value ?? new List<Account>();
                }
            }
        }

        public void SetSelectedAccounts(IEnumerable<Account> accounts)
        {
            lock (_sync)
            {
                _selectedAccounts = accounts != null ? new List<Account>(accounts) : new List<Account>();
            }
        }
    }
}
