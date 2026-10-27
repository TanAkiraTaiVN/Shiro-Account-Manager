#nullable enable
using System;

namespace RBX_Alt_Manager.ViewModels
{
    public class AccountRowViewModel : IEquatable<AccountRowViewModel>
    {
        public AccountViewModel? Item1 { get; }
        public AccountViewModel? Item2 { get; }
        public AccountViewModel? Item3 { get; }

        public bool HasItem2 => Item2 != null;
        public bool HasItem3 => Item3 != null;

        public AccountRowViewModel(AccountViewModel? item1, AccountViewModel? item2 = null, AccountViewModel? item3 = null)
        {
            Item1 = item1;
            Item2 = item2;
            Item3 = item3;
        }

        public bool Equals(AccountRowViewModel? other)
        {
            if (other is null) return false;
            if (ReferenceEquals(this, other)) return true;
            return ReferenceEquals(Item1, other.Item1) &&
                   ReferenceEquals(Item2, other.Item2) &&
                   ReferenceEquals(Item3, other.Item3);
        }

        public override bool Equals(object? obj) => Equals(obj as AccountRowViewModel);

        public override int GetHashCode()
        {
            unchecked
            {
                int hash = 17;
                hash = hash * 31 + (Item1 != null ? Item1.GetHashCode() : 0);
                hash = hash * 31 + (Item2 != null ? Item2.GetHashCode() : 0);
                hash = hash * 31 + (Item3 != null ? Item3.GetHashCode() : 0);
                return hash;
            }
        }
    }
}
