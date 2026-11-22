#nullable enable
using System;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Input;
using Avalonia.Media;
using Avalonia.Media.Immutable;

namespace Shiro.Services
{
    public enum ToastType
    {
        Info,
        Success,
        Warning,
        Error
    }

    public class ToastItem : INotifyPropertyChanged
    {
        private static readonly IBrush SuccessAccentBrush = new ImmutableSolidColorBrush(Color.FromRgb(34, 197, 94));
        private static readonly IBrush WarningAccentBrush = new ImmutableSolidColorBrush(Color.FromRgb(245, 158, 11));
        private static readonly IBrush ErrorAccentBrush = new ImmutableSolidColorBrush(Color.FromRgb(239, 68, 68));
        private static readonly IBrush InfoAccentBrush = new ImmutableSolidColorBrush(Color.FromRgb(56, 189, 248));

        private static readonly IBrush SurfaceBgBrush = new ImmutableSolidColorBrush(Color.FromArgb(250, 17, 17, 23));

        private static readonly IBrush SuccessBorderBrush = new ImmutableSolidColorBrush(Color.FromArgb(95, 34, 197, 94));
        private static readonly IBrush WarningBorderBrush = new ImmutableSolidColorBrush(Color.FromArgb(95, 245, 158, 11));
        private static readonly IBrush ErrorBorderBrush = new ImmutableSolidColorBrush(Color.FromArgb(95, 239, 68, 68));
        private static readonly IBrush InfoBorderBrush = new ImmutableSolidColorBrush(Color.FromArgb(110, 42, 42, 58));

        private static readonly IBrush SuccessBadgeBgBrush = new ImmutableSolidColorBrush(Color.FromArgb(36, 34, 197, 94));
        private static readonly IBrush WarningBadgeBgBrush = new ImmutableSolidColorBrush(Color.FromArgb(36, 245, 158, 11));
        private static readonly IBrush ErrorBadgeBgBrush = new ImmutableSolidColorBrush(Color.FromArgb(36, 239, 68, 68));
        private static readonly IBrush InfoBadgeBgBrush = new ImmutableSolidColorBrush(Color.FromArgb(36, 56, 189, 248));

        private static Geometry? _successIconGeometry;
        private static Geometry? _warningIconGeometry;
        private static Geometry? _errorIconGeometry;
        private static Geometry? _infoIconGeometry;

        private ICommand? _actionCommand;

        public event PropertyChangedEventHandler? PropertyChanged;

        public string Id { get; } = Guid.NewGuid().ToString("N");
        public string Title { get; set; } = string.Empty;
        public string Message { get; set; } = string.Empty;
        public ToastType Type { get; set; }
        public DateTime CreatedAt { get; } = DateTime.UtcNow;
        public ICommand DismissCommand { get; }
        public string ActionText { get; set; } = string.Empty;
        public ICommand? ActionCommand
        {
            get => _actionCommand;
            set
            {
                if (!ReferenceEquals(_actionCommand, value))
                {
                    _actionCommand = value;
                    PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(ActionCommand)));
                    PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(HasAction)));
                }
            }
        }
        internal CancellationTokenSource? AutoDismissCts { get; set; }

        internal sealed class ToastActionCommand : ICommand
        {
            private readonly Action _execute;

            public ToastActionCommand(Action execute)
            {
                _execute = execute ?? throw new ArgumentNullException(nameof(execute));
            }

            public event EventHandler? CanExecuteChanged
            {
                add { }
                remove { }
            }

            public bool CanExecute(object? parameter) => true;

            public void Execute(object? parameter) => _execute();
        }

        public ToastItem()
        {
            DismissCommand = new ToastActionCommand(() => ToastService.Dismiss(this));
        }

        public bool HasMessage => !string.IsNullOrWhiteSpace(Message);
        public bool HasAction => ActionCommand != null && !string.IsNullOrWhiteSpace(ActionText);

        public Geometry? IconGeometry => Type switch
        {
            ToastType.Success => _successIconGeometry ??= TryParseGeometry("M 20,6 L 9,17 L 4,12"),
            ToastType.Warning => _warningIconGeometry ??= TryParseGeometry("M 12,22 C 12,22 20,18 20,12 L 20,5 L 12,2 L 4,5 L 4,12 C 4,18 12,22 12,22 Z M 12,8 L 12,13 M 12,16.5 L 12.02,16.5"),
            ToastType.Error => _errorIconGeometry ??= TryParseGeometry("M 18,6 L 6,18 M 6,6 L 18,18"),
            _ => _infoIconGeometry ??= TryParseGeometry("M 12,2 A 10,10 0 1 0 12,22 A 10,10 0 1 0 12,2 Z M 12,16 L 12,12 M 12,8 L 12.01,8")
        };

        private static Geometry? TryParseGeometry(string pathData)
        {
            try
            {
                return StreamGeometry.Parse(pathData);
            }
            catch (Exception ex)
            {
                AppServices.Logger.Debug("[ToastItem] TryParseGeometry failed", ex);
                return null;
            }
        }

        public IBrush AccentBrush => Type switch
        {
            ToastType.Success => SuccessAccentBrush,
            ToastType.Warning => WarningAccentBrush,
            ToastType.Error => ErrorAccentBrush,
            _ => InfoAccentBrush
        };

        public IBrush BackgroundBrush => SurfaceBgBrush;

        public IBrush BorderBrush => Type switch
        {
            ToastType.Success => SuccessBorderBrush,
            ToastType.Warning => WarningBorderBrush,
            ToastType.Error => ErrorBorderBrush,
            _ => InfoBorderBrush
        };

        public IBrush BadgeBackground => Type switch
        {
            ToastType.Success => SuccessBadgeBgBrush,
            ToastType.Warning => WarningBadgeBgBrush,
            ToastType.Error => ErrorBadgeBgBrush,
            _ => InfoBadgeBgBrush
        };
    }

    public static partial class ToastService
    {
        private static readonly System.Threading.Lock _toastLock = new();
        public static ObservableCollection<ToastItem> ActiveToasts { get; } = [];

        public static void ShowSuccess(string title, string message = "", double durationSeconds = 3.0) => Show(title, message, ToastType.Success, durationSeconds);
        public static void ShowInfo(string title, string message = "", double durationSeconds = 3.0) => Show(title, message, ToastType.Info, durationSeconds);
        public static void ShowWarning(string title, string message = "", double durationSeconds = 3.5) => Show(title, message, ToastType.Warning, durationSeconds);

        public static void ShowWarningWithAction(
            string title,
            string message,
            string actionText,
            Action onAction,
            double durationSeconds = 10.0)
            => ShowInternal(title, message, ToastType.Warning, durationSeconds, actionText, onAction);

        public static void ShowError(string title, string message = "", double durationSeconds = 4.0) => Show(title, message, ToastType.Error, durationSeconds);

        public static void Show(string title, string message, ToastType type, double durationSeconds = 3.0)
            => ShowInternal(title, message, type, durationSeconds, null, null);

        private static ICommand? BuildActionCommand(ToastItem toast, string cleanAction, Action? onAction)
        {
            if (onAction == null || string.IsNullOrWhiteSpace(cleanAction))
                return null;

            return new ToastItem.ToastActionCommand(() =>
            {
                try
                {
                    onAction();
                }
                finally
                {
                    Dismiss(toast);
                }
            });
        }

        private static void ShowInternal(
            string title,
            string message,
            ToastType type,
            double durationSeconds,
            string? actionText,
            Action? onAction)
        {
            AppDispatcher.Post(() =>
            {
                ToastItem toast;
                TimeSpan? autoDismissDelay = ResolveAutoDismissDelay(durationSeconds);
                ToastItem? coalescedToast = null;
                string cleanTitle = title ?? string.Empty;
                string cleanMessage = message ?? string.Empty;
                string cleanAction = actionText ?? string.Empty;

                lock (_toastLock)
                {
                    // Coalesce identical consecutive toasts and extend their auto-dismiss timer cleanly.
                    for (int i = ActiveToasts.Count - 1; i >= 0; i--)
                    {
                        var existing = ActiveToasts[i];
                        if (existing.Type == type &&
                            string.Equals(existing.Title, cleanTitle, StringComparison.Ordinal) &&
                            string.Equals(existing.Message, cleanMessage, StringComparison.Ordinal) &&
                            string.Equals(existing.ActionText, cleanAction, StringComparison.Ordinal))
                        {
                            coalescedToast = existing;
                            break;
                        }
                    }

                    if (coalescedToast == null)
                    {
                        toast = new ToastItem
                        {
                            Title = cleanTitle,
                            Message = cleanMessage,
                            Type = type,
                            ActionText = cleanAction
                        };

                        toast.ActionCommand = BuildActionCommand(toast, cleanAction, onAction);

                        // Limit active toasts to 2 to keep the viewport clean and unobstructed
                        while (ActiveToasts.Count >= 2)
                        {
                            if (ActiveToasts.Count > 0)
                            {
                                CancelAndDisposeToastTimer(ActiveToasts[0]);
                                ActiveToasts.RemoveAt(0);
                            }
                        }

                        ActiveToasts.Add(toast);
                    }
                    else
                    {
                        toast = coalescedToast;
                    }
                }

                if (coalescedToast != null)
                {
                    toast.ActionCommand = BuildActionCommand(toast, cleanAction, onAction);
                }
                else
                {
                    PlayNotificationSound(type);
                }

                if (autoDismissDelay is TimeSpan delay)
                {
                    ScheduleAutoDismiss(toast, delay);
                }
                else
                {
                    lock (_toastLock) { CancelAndDisposeToastTimer(toast); }
                }
            });
        }

        private static void CancelAndDisposeToastTimer(ToastItem toast)
        {
            var old = toast.AutoDismissCts;
            toast.AutoDismissCts = null;
            if (old != null)
            {
                SafeExecuteHelper.Try(old.Cancel);
                SafeExecuteHelper.Try(old.Dispose);
            }
        }

        private static void ScheduleAutoDismiss(ToastItem toast, TimeSpan delay)
        {
            CancellationToken token;
            lock (_toastLock)
            {
                CancelAndDisposeToastTimer(toast);
                var cts = new CancellationTokenSource();
                toast.AutoDismissCts = cts;
                token = cts.Token;
            }

            _ = Task.Run(async () =>
            {
                try
                {
                    await Task.Delay(delay, token).ConfigureAwait(false);
                    if (!token.IsCancellationRequested)
                        AppDispatcher.Post(() => Dismiss(toast));
                }
                catch (OperationCanceledException)
                {
                    // Coalesced toast timer was superseded or toast dismissed manually.
                }
            });
        }

        private const double MaxAutoDismissSeconds = 86_400.0;

        internal static TimeSpan? ResolveAutoDismissDelay(double durationSeconds)
        {
            if (!double.IsFinite(durationSeconds) || durationSeconds <= 0)
                return null;

            double clampedSeconds = Math.Min(durationSeconds, MaxAutoDismissSeconds);
            return TimeSpan.FromSeconds(clampedSeconds);
        }

        public static void Dismiss(ToastItem? toast)
        {
            if (toast == null) return;

            AppDispatcher.Post(() =>
            {
                lock (_toastLock)
                {
                    CancelAndDisposeToastTimer(toast);
                    ActiveToasts.Remove(toast);
                }
            });
        }

        public static void ClearAll()
        {
            lock (_toastLock)
            {
                if (ActiveToasts.Count == 0)
                    return;
            }

            AppDispatcher.Post(() =>
            {
                lock (_toastLock)
                {
                    foreach (var active in ActiveToasts)
                        CancelAndDisposeToastTimer(active);
                    ActiveToasts.Clear();
                }
            });
        }

        private const uint MB_ICONHAND = 0x00000010;
        private const uint MB_ICONEXCLAMATION = 0x00000030;
        private const uint MB_ICONASTERISK = 0x00000040;

        [LibraryImport("user32.dll", SetLastError = true)]
        private static partial int MessageBeep(uint uType);

        public static void PlayNotificationSound(ToastType type = ToastType.Info)
        {
            // Quiet by default: avoid jarring raw Win32 system error/asterisk beeps unless user explicitly opts in.
            if (!AppServices.GeneralSettings.Get<bool>("PlayNotificationSound", false))
                return;

            try
            {
                if (OperatingSystem.IsWindows())
                {
                    switch (type)
                    {
                        case ToastType.Error:
                            _ = MessageBeep(MB_ICONHAND);
                            break;
                        case ToastType.Warning:
                            _ = MessageBeep(MB_ICONEXCLAMATION);
                            break;
                        default:
                            _ = MessageBeep(MB_ICONASTERISK);
                            break;
                    }
                }
            }
            catch (Exception ex)
            {
                AppServices.Logger?.Debug($"PlayNotificationSound threw: {ex.Message}");
            }
        }
    }
}
