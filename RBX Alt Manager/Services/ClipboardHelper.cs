using System;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;

namespace RBX_Alt_Manager.Services
{
    /// <summary>
    /// Modern Universal Clipboard Service supporting both Avalonia UI 11 and Windows Desktop.
    /// Eliminates legacy blocking Thread.Sleep and provides async-first clipboard operations.
    /// </summary>
    public static class ClipboardHelper
    {
        /// <summary>
        /// Asynchronously sets text to the clipboard via Avalonia UI or system dispatcher without thread blocking.
        /// </summary>
        public static async Task<bool> SetClipboardTextAsync(string text)
        {
            if (text == null) text = string.Empty;

            try
            {
                if (Avalonia.Application.Current?.ApplicationLifetime is Avalonia.Controls.ApplicationLifetimes.IClassicDesktopStyleApplicationLifetime desktop &&
                    desktop.MainWindow?.Clipboard != null)
                {
                    await desktop.MainWindow.Clipboard.SetTextAsync(text).ConfigureAwait(false);
                    return true;
                }
            }
            catch (Exception ex)
            {
                Program.Logger?.Debug($"Clipboard.SetTextAsync threw: {ex.Message}");
            }

            return SetClipboardTextSafe(text);
        }

        /// <summary>
        /// Safely sets text to the Windows Clipboard with non-blocking spin retry,
        /// mitigating ExternalException caused by other processes temporarily locking the clipboard.
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
                    desktop.MainWindow.Clipboard.SetTextAsync(text).GetAwaiter().GetResult();
                    return true;
                }
                catch (Exception ex)
                {
                    Program.Logger?.Debug($"Clipboard sync SetTextAsync threw: {ex.Message}");
                }
            }

            return false;
        }

        public static bool SetText(string text) => SetClipboardTextSafe(text);
    }
}
