#nullable enable
using System;
using System.Collections.Generic;
using System.Threading;
using Shiro.Services;

namespace Shiro.ViewModels.Dialogs
{
    public abstract class DialogViewModelBase : ObservableObject
    {
        private string _statusText = string.Empty;
        private StatusKind _statusKind = StatusKind.Neutral;
        private bool _isBusy;
        private CancellationTokenSource? _operationCts;
        private readonly List<IRaiseCanExecuteChanged> _trackedCommands = new();

        public string StatusText
        {
            get => _statusText;
            private set => SetProperty(ref _statusText, value);
        }

        public StatusKind StatusKind
        {
            get => _statusKind;
            private set => SetProperty(ref _statusKind, value);
        }

        public bool IsBusy
        {
            get => _isBusy;
            protected set
            {
                if (SetProperty(ref _isBusy, value))
                {
                    foreach (var cmd in _trackedCommands)
                    {
                        cmd.RaiseCanExecuteChanged();
                    }
                }
            }
        }

        public event EventHandler<bool?>? CloseRequested;

        protected void SetStatus(string text, StatusKind kind)
        {
            StatusText = text;
            StatusKind = kind;
        }

        protected void RequestClose(bool? result)
        {
            CloseRequested?.Invoke(this, result);
        }

        protected T TrackCommand<T>(T command) where T : IRaiseCanExecuteChanged
        {
            if (command != null && !_trackedCommands.Contains(command))
            {
                _trackedCommands.Add(command);
            }
            return command!;
        }

        protected CancellationToken BeginOperation()
        {
            CancelCurrentOperation();
            _operationCts = new CancellationTokenSource();
            return _operationCts.Token;
        }

        public virtual void OnClosing()
        {
            CancelCurrentOperation();
        }

        private void CancelCurrentOperation()
        {
            var cts = Interlocked.Exchange(ref _operationCts, null);
            if (cts != null)
            {
                SafeExecuteHelper.Try(() => cts.Cancel());
                SafeExecuteHelper.Try(() => cts.Dispose());
            }
        }
    }
}
