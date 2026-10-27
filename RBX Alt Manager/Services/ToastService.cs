#nullable enable
using System;
using System.Collections.ObjectModel;
using System.Threading.Tasks;
using Avalonia.Media;
using RBX_Alt_Manager.Classes;

namespace RBX_Alt_Manager.Services
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

        private static readonly IBrush SuccessBgBrush = new SolidColorBrush(Color.FromArgb(230, 16, 28, 20));
        private static readonly IBrush WarningBgBrush = new SolidColorBrush(Color.FromArgb(230, 32, 24, 14));
        private static readonly IBrush ErrorBgBrush = new SolidColorBrush(Color.FromArgb(230, 32, 16, 16));
        private static readonly IBrush InfoBgBrush = new SolidColorBrush(Color.FromArgb(230, 16, 20, 30));

        public string Id { get; } = Guid.NewGuid().ToString("N");
        public string Title { get; set; } = string.Empty;
        public string Message { get; set; } = string.Empty;
        public ToastType Type { get; set; }
        public DateTime CreatedAt { get; } = DateTime.UtcNow;

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

        public IBrush BackgroundBrush => Type switch
        {
            ToastType.Success => SuccessBgBrush,
            ToastType.Warning => WarningBgBrush,
            ToastType.Error => ErrorBgBrush,
            _ => InfoBgBrush
        };
    }

    public static class ToastService
    {
        public static ObservableCollection<ToastItem> ActiveToasts { get; } = [];

        public static void ShowSuccess(string title, string message = "") => Show(title, message, ToastType.Success);
        public static void ShowInfo(string title, string message = "") => Show(title, message, ToastType.Info);
        public static void ShowWarning(string title, string message = "") => Show(title, message, ToastType.Warning);
        public static void ShowError(string title, string message = "") => Show(title, message, ToastType.Error);

        public static void Show(string title, string message, ToastType type, double durationSeconds = 3.5)
        {
            AppDispatcher.Post(() =>
            {
                var toast = new ToastItem
                {
                    Title = title,
                    Message = message,
                    Type = type
                };

                // Limit active toasts to 4 to avoid screen clutter
                while (ActiveToasts.Count >= 4)
                {
                    ActiveToasts.RemoveAt(0);
                }

                ActiveToasts.Add(toast);

                _ = Task.Run(async () =>
                {
                    await Task.Delay(TimeSpan.FromSeconds(durationSeconds)).ConfigureAwait(false);
                    AppDispatcher.Post(() => Dismiss(toast));
                });
            });
        }

        public static void Dismiss(ToastItem toast)
        {
            if (toast == null) return;

            AppDispatcher.Post(() =>
            {
                ActiveToasts.Remove(toast);
            });
        }
    }
}
