#nullable enable
using System;
using System.Collections.ObjectModel;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using System.Windows.Input;
using Avalonia.Media;
using LucideAvalonia.Enum;
using Shiro.Classes;
using Shiro.ViewModels;

namespace Shiro.Services
{
    public enum ToastType
    {
        Info,
        Success,
        Warning,
        Error
    }

    public class ToastItem
    {
        private static readonly IBrush SuccessAccentBrush = new SolidColorBrush(Color.FromRgb(34, 197, 94));
        private static readonly IBrush WarningAccentBrush = new SolidColorBrush(Color.FromRgb(245, 158, 11));
        private static readonly IBrush ErrorAccentBrush = new SolidColorBrush(Color.FromRgb(239, 68, 68));
        private static readonly IBrush InfoAccentBrush = new SolidColorBrush(Color.FromRgb(56, 189, 248));

        private static readonly IBrush SurfaceBgBrush = new SolidColorBrush(Color.FromArgb(250, 17, 17, 23));

        private static readonly IBrush SuccessBorderBrush = new SolidColorBrush(Color.FromArgb(95, 34, 197, 94));
        private static readonly IBrush WarningBorderBrush = new SolidColorBrush(Color.FromArgb(95, 245, 158, 11));
        private static readonly IBrush ErrorBorderBrush = new SolidColorBrush(Color.FromArgb(95, 239, 68, 68));
        private static readonly IBrush InfoBorderBrush = new SolidColorBrush(Color.FromArgb(110, 42, 42, 58));

        private static readonly IBrush SuccessBadgeBgBrush = new SolidColorBrush(Color.FromArgb(36, 34, 197, 94));
        private static readonly IBrush WarningBadgeBgBrush = new SolidColorBrush(Color.FromArgb(36, 245, 158, 11));
        private static readonly IBrush ErrorBadgeBgBrush = new SolidColorBrush(Color.FromArgb(36, 239, 68, 68));
        private static readonly IBrush InfoBadgeBgBrush = new SolidColorBrush(Color.FromArgb(36, 56, 189, 248));

        private static Geometry? _successIconGeometry;
        private static Geometry? _warningIconGeometry;
        private static Geometry? _errorIconGeometry;
        private static Geometry? _infoIconGeometry;

        public string Id { get; } = Guid.NewGuid().ToString("N");
        public string Title { get; set; } = string.Empty;
        public string Message { get; set; } = string.Empty;
        public ToastType Type { get; set; }
        public DateTime CreatedAt { get; } = DateTime.UtcNow;
        public ICommand DismissCommand { get; }

        public ToastItem()
        {
            DismissCommand = new RelayCommand(() => ToastService.Dismiss(this));
        }

        public bool HasMessage => !string.IsNullOrWhiteSpace(Message);

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
            catch
            {
                return null;
            }
        }

        public LucideIconNames IconKind => Type switch
        {
            ToastType.Success => LucideIconNames.Check,
            ToastType.Warning => LucideIconNames.ShieldAlert,
            ToastType.Error => LucideIconNames.X,
            _ => LucideIconNames.Info
        };

        public string Glyph => Type switch
        {
            ToastType.Success => "✓",
            ToastType.Warning => "⚠",
            ToastType.Error => "✕",
            _ => "ℹ"
        };

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
        public static void ShowError(string title, string message = "", double durationSeconds = 4.0) => Show(title, message, ToastType.Error, durationSeconds);

        public static void Show(string title, string message, ToastType type, double durationSeconds = 3.0)
        {
            AppDispatcher.Post(() =>
            {
                ToastItem toast;
                lock (_toastLock)
                {
                    string cleanTitle = title ?? string.Empty;
                    string cleanMessage = message ?? string.Empty;

                    // Coalesce identical consecutive toasts or replace same-title progress toast
                    for (int i = ActiveToasts.Count - 1; i >= 0; i--)
                    {
                        var existing = ActiveToasts[i];
                        if (existing.Type == type &&
                            string.Equals(existing.Title, cleanTitle, StringComparison.Ordinal) &&
                            string.Equals(existing.Message, cleanMessage, StringComparison.Ordinal))
                        {
                            return;
                        }
                    }

                    toast = new ToastItem
                    {
                        Title = cleanTitle,
                        Message = cleanMessage,
                        Type = type
                    };

                    // Limit active toasts to 2 to keep the viewport clean and unobstructed
                    while (ActiveToasts.Count >= 2)
                    {
                        if (ActiveToasts.Count > 0)
                            ActiveToasts.RemoveAt(0);
                    }

                    ActiveToasts.Add(toast);
                }

                PlayNotificationSound(type);

                if (durationSeconds > 0)
                {
                    _ = Task.Run(async () =>
                    {
                        await Task.Delay(TimeSpan.FromSeconds(durationSeconds)).ConfigureAwait(false);
                        AppDispatcher.Post(() => Dismiss(toast));
                    });
                }
            });
        }

        public static void Dismiss(ToastItem? toast)
        {
            if (toast == null) return;

            AppDispatcher.Post(() =>
            {
                lock (_toastLock)
                {
                    ActiveToasts.Remove(toast);
                }
            });
        }

        public static void ClearAll()
        {
            AppDispatcher.Post(() =>
            {
                lock (_toastLock)
                {
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
            if (!AppSettings.General.Get<bool>("PlayNotificationSound", false))
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
                Program.Logger?.Debug($"PlayNotificationSound threw: {ex.Message}");
            }
        }
    }
}
