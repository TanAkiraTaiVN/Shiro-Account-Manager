#nullable enable
using System;

namespace Shiro.ViewModels
{
    public class AccountRowViewModel : IEquatable<AccountRowViewModel>
    {
        public int ColumnCount { get; }
        public AccountViewModel? Item1 { get; }
        public AccountViewModel? Item2 { get; }
        public AccountViewModel? Item3 { get; }
        public AccountViewModel? Item4 { get; }

        public bool HasItem1 => Item1 != null;
        public bool HasItem2 => Item2 != null && ColumnCount >= 2;
        public bool HasItem3 => Item3 != null && ColumnCount >= 3;
        public bool HasItem4 => Item4 != null && ColumnCount >= 4;

        public AccountRowViewModel(
            AccountViewModel? item1,
            AccountViewModel? item2 = null,
            AccountViewModel? item3 = null)
            : this(3, item1, item2, item3, null)
        {
        }

        public AccountRowViewModel(
            int columnCount,
            AccountViewModel? item1,
            AccountViewModel? item2 = null,
            AccountViewModel? item3 = null,
            AccountViewModel? item4 = null)
        {
            ColumnCount = Math.Clamp(columnCount, 1, 6);
            Item1 = item1;
            Item2 = item2;
            Item3 = item3;
            Item4 = item4;
        }

        public bool Equals(AccountRowViewModel? other)
        {
            if (other is null) return false;
            if (ReferenceEquals(this, other)) return true;
            return ColumnCount == other.ColumnCount &&
                   ReferenceEquals(Item1, other.Item1) &&
                   ReferenceEquals(Item2, other.Item2) &&
                   ReferenceEquals(Item3, other.Item3) &&
                   ReferenceEquals(Item4, other.Item4);
        }

        public override bool Equals(object? obj) => Equals(obj as AccountRowViewModel);

        public override int GetHashCode()
        {
            unchecked
            {
                int hash = 17;
                hash = hash * 31 + ColumnCount;
                hash = hash * 31 + (Item1 != null ? Item1.GetHashCode() : 0);
                hash = hash * 31 + (Item2 != null ? Item2.GetHashCode() : 0);
                hash = hash * 31 + (Item3 != null ? Item3.GetHashCode() : 0);
                hash = hash * 31 + (Item4 != null ? Item4.GetHashCode() : 0);
                return hash;
            }
        }
    }
}
