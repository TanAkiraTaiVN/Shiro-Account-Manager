#nullable enable
using System;
using System.Threading.Tasks;

namespace Shiro.Services
{
    /// <summary>
    /// Universal Clipboard helper supporting Avalonia UI 11 desktop clipboard operations
    /// with safe UI-thread marshaling.
    /// </summary>
    public static class ClipboardHelper
    {
        /// <summary>
        /// Asynchronously sets text to the clipboard, marshaling onto the Avalonia UI thread when needed.
        /// </summary>
        public static async Task<bool> SetClipboardTextAsync(string text)
        {
            if (text == null) text = string.Empty;

            try
            {
                if (Avalonia.Application.Current?.ApplicationLifetime is not Avalonia.Controls.ApplicationLifetimes.IClassicDesktopStyleApplicationLifetime desktop)
                    return false;

                if (Avalonia.Threading.Dispatcher.UIThread.CheckAccess())
                {
                    return await SetDesktopClipboardTextOnUiThreadAsync(desktop, text).ConfigureAwait(false);
                }

                return await Avalonia.Threading.Dispatcher.UIThread
                    .InvokeAsync(() => SetDesktopClipboardTextOnUiThreadAsync(desktop, text))
                    .ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                AppServices.Logger?.Debug($"Clipboard.SetTextAsync threw: {ex.Message}");
            }

            return false;
        }

        private static async Task<bool> SetDesktopClipboardTextOnUiThreadAsync(
            Avalonia.Controls.ApplicationLifetimes.IClassicDesktopStyleApplicationLifetime desktop,
            string text)
        {
            var clipboard = desktop.MainWindow?.Clipboard;
            if (clipboard == null)
                return false;

            await clipboard.SetTextAsync(text).ConfigureAwait(false);
            return true;
        }

        /// <summary>
        /// Safely posts text to the Avalonia Desktop clipboard, dispatching onto the UI thread if called from a background thread.
        /// </summary>
        public static bool SetClipboardTextSafe(string text)
        {
            if (text == null) text = string.Empty;

            // Attempt Avalonia Desktop clipboard first
            if (Avalonia.Application.Current?.ApplicationLifetime is Avalonia.Controls.ApplicationLifetimes.IClassicDesktopStyleApplicationLifetime desktop &&
                desktop.MainWindow?.Clipboard != null)
            {
                try
                {
                    if (Avalonia.Threading.Dispatcher.UIThread.CheckAccess())
                    {
                        desktop.MainWindow.Clipboard.SetTextAsync(text).Forget();
                        return true;
                    }

                    Avalonia.Threading.Dispatcher.UIThread.Post(() =>
                    {
                        try { desktop.MainWindow.Clipboard.SetTextAsync(text).Forget(); }
                        catch (Exception ex) { AppServices.Logger?.Debug($"Clipboard dispatched SetTextAsync threw: {ex.Message}"); }
                    });
                    return true;
                }
                catch (Exception ex)
                {
                    AppServices.Logger?.Debug($"Clipboard sync SetTextAsync threw: {ex.Message}");
                }
            }

            return false;
        }

        public static bool SetText(string text) => SetClipboardTextSafe(text);
    }
}
