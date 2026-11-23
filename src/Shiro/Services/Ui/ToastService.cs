#nullable enable
using System;
using System.Collections.ObjectModel;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Input;

namespace Shiro.Services
{
    public static partial class ToastService
    {
        private static readonly System.Threading.Lock _toastLock = new();
        private static long _clearGeneration;
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

        public static void ShowWithUndo(
            string title,
            string message,
            string undoText,
            Action onUndo,
            double durationSeconds = 5.0)
        {
            string label = string.IsNullOrWhiteSpace(undoText)
                ? LocalizationService.Get("Toast_Undo")
                : undoText;
            int executed = 0;
            ShowInternal(title, message, ToastType.Info, durationSeconds, label, () =>
            {
                if (Interlocked.Exchange(ref executed, 1) != 0)
                    return;
                onUndo?.Invoke();
            });
        }

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
            long showClearGen;
            lock (_toastLock)
            {
                showClearGen = _clearGeneration;
            }

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
                    if (_clearGeneration != showClearGen)
                        return;

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
                            ActionText = cleanAction,
                            DurationSeconds = autoDismissDelay?.TotalSeconds ?? 0.0,
                            TimerGeneration = autoDismissDelay.HasValue ? 1 : 0
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
                    toast.DurationSeconds = autoDismissDelay?.TotalSeconds ?? 0.0;
                    if (autoDismissDelay.HasValue)
                    {
                        toast.TimerGeneration++;
                    }
                }
                else
                {
                    NotificationHistoryService.Instance.Record(toast);
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
            bool hasActive;
            lock (_toastLock)
            {
                unchecked { _clearGeneration++; }
                hasActive = ActiveToasts.Count > 0;
            }

            if (hasActive)
            {
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

            NotificationHistoryService.Instance.ResetForTesting();
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
