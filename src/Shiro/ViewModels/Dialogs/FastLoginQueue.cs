#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using Shiro.Services;

namespace Shiro.ViewModels.Dialogs
{
    public sealed class FastLoginQueue
    {
        private readonly List<Account> _accounts = new();
        private readonly Action<IEnumerable<long>> _persistRemaining;
        private int _index;

        public FastLoginQueue(
            IEnumerable<Account?>? accounts,
            Action<IEnumerable<long>>? persistRemaining = null)
        {
            _persistRemaining = persistRemaining ?? ReloginQueueStore.Save;
            if (accounts != null)
            {
                foreach (var acc in accounts)
                {
                    if (acc != null)
                        _accounts.Add(acc);
                }
            }

            if (_accounts.Count > 0)
            {
                MoveTo(0);
            }
        }

        public int Count => _accounts.Count;

        public int Index => _index;

        public Account? Current =>
            _index >= 0 && _index < _accounts.Count ? _accounts[_index] : null;

        public bool HasNext => Count > 1 && _index + 1 < Count;

        public string Title
        {
            get
            {
                var current = Current;
                string user = StreamerModeService.GetDisplayUsername(current?.Username);
                return Count > 1
                    ? LocalizationService.Format("FastLogin.DialogProgressTitle", user, _index + 1, Count)
                    : LocalizationService.Format("FastLogin.DialogSingleTitle", user);
            }
        }

        public string Subtitle
        {
            get
            {
                var current = Current;
                return !string.IsNullOrEmpty(current?.Password)
                    ? LocalizationService.Get("FastLogin.AutoFillUserPassTip")
                    : LocalizationService.Get("FastLogin.AutoFillUserOnlyTip");
            }
        }

        public bool MoveTo(int index)
        {
            if (index < 0 || index >= _accounts.Count)
                return false;

            _index = index;
            if (_accounts.Count > 1)
            {
                _persistRemaining(_accounts.Skip(index).Select(a => a.UserID));
            }
            return true;
        }

        public bool MoveNext() => MoveTo(_index + 1);
    }
}
