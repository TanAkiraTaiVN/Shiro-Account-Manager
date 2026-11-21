#nullable enable
using System;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Shiro.Services;

namespace Shiro
{
    public static class Utilities
    {
        public static bool PostMessage(IntPtr hWnd, uint msg, nuint wParam, nint lParam) =>
            Win32TrayHelper.PostMessage(hWnd, msg, wParam, lParam);

        public static void QueueRefreshNotificationArea(int delayMs = 350) =>
            Win32TrayHelper.QueueRefreshNotificationArea(delayMs);

        public static void RefreshNotificationArea() =>
            Win32TrayHelper.RefreshNotificationArea();

        public static void ThoroughCleanNotificationArea(bool forceOpenFlyout = false) =>
            Win32TrayHelper.ThoroughCleanNotificationArea(forceOpenFlyout);

        public static T Next<T>(this T src) where T : struct, Enum
        {
            T[] arr = Enum.GetValues<T>();
            int j = Array.IndexOf(arr, src) + 1;
            return (arr.Length == j) ? arr[0] : arr[j];
        }

        public static T Clamp<T>(this T val, T min, T max) where T : IComparable<T>
        {
            if (val.CompareTo(min) < 0) return min;
            if (val.CompareTo(max) > 0) return max;
            return val;
        }

        public static bool TryParseJson<T>(this string @this, [NotNullWhen(true)] out T? result) =>
            JsonExtensions.TryParseJson(@this, out result);

        public static bool TryParseFast<T>(this string @this, [NotNullWhen(true)] out T? result) =>
            JsonExtensions.TryParseFast(@this, out result);

        public static void InvokeIfRequired(this Avalonia.Threading.Dispatcher dispatcher, Action? action)
        {
            if (action == null) return;
            AppDispatcher.Post(action);
        }

        public static bool TryValidateWebUrl(string? url, [NotNullWhen(true)] out Uri? uri)
        {
            uri = null;
            if (string.IsNullOrWhiteSpace(url))
                return false;

            if (!Uri.TryCreate(url.Trim(), UriKind.Absolute, out Uri? parsed))
                return false;

            if (!string.Equals(parsed.Scheme, Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase) &&
                !string.Equals(parsed.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase))
                return false;

            uri = parsed;
            return true;
        }

        public static void OpenUrl(string? url)
        {
            if (!TryValidateWebUrl(url, out Uri? uri))
            {
                if (!string.IsNullOrWhiteSpace(url))
                    AppServices.Logger.Warn($"Blocked unsafe URL scheme in OpenUrl: '{LogRedactor.Redact(url)}'");
                return;
            }

            try
            {
                Process.Start(new ProcessStartInfo(uri.AbsoluteUri) { UseShellExecute = true });
            }
            catch (Exception ex)
            {
                AppServices.Logger.Warn($"Failed to open URL '{LogRedactor.Redact(uri.AbsoluteUri)}': {ex.Message}");
            }
        }

        public static void OpenFile(string? filePath)
        {
            if (string.IsNullOrWhiteSpace(filePath)) return;
            try
            {
                Process.Start(new ProcessStartInfo(filePath) { UseShellExecute = true });
            }
            catch (Exception ex)
            {
                AppServices.Logger.Warn($"Failed to open file '{filePath}': {ex.Message}");
            }
        }

        public static string SHA256(string input) => CryptoHelper.SHA256(input);

        public static string? FileSHA256(this string fileName) => CryptoHelper.FileSHA256(fileName);

        public static double ToRobloxTick(this DateTime date) => RobloxPlaceHelper.ToRobloxTick(date);

        public static string? GetCommandLine(this Process process) => ProcessExtensions.GetCommandLine(process);

        public static Task<string> GetRandomJobId(
            long placeId,
            bool chooseLowestServer = false,
            CancellationToken cancellationToken = default) =>
            RobloxPlaceHelper.GetRandomJobId(placeId, chooseLowestServer, cancellationToken);

        public static void RecursiveDelete(this DirectoryInfo? baseDir)
        {
            if (baseDir == null || !baseDir.Exists) return;

            foreach (var dir in baseDir.EnumerateDirectories())
                RecursiveDelete(dir);

            foreach (var file in baseDir.EnumerateFiles())
            {
                file.IsReadOnly = false;
                file.Delete();
            }

            baseDir.Delete(true);
        }

        public static async Task<bool> YesNoPromptAsync(string? Caption, string? Instruction, string? Text, bool CanSave = true, bool SaveIfNo = true)
        {
            string promptKey = $"{Caption}.{Instruction}.{Text}";
            string sha256Hash = CryptoHelper.SHA256(promptKey);

            var prompts = AppServices.PromptsSettings;
            if (CanSave)
            {
                if (prompts != null && prompts.Exists(sha256Hash))
                    return prompts.Get<bool>(sha256Hash);
            }

            string promptMessage = string.IsNullOrWhiteSpace(Instruction) ? (Text ?? string.Empty) : $"{Instruction}\n\n{Text}";
            bool isYes = await AppDispatcher.InvokeTaskAsync(() =>
                AppServices.Dialogs.ShowConfirmationAsync(promptMessage, Caption ?? "Shiro")).ConfigureAwait(false);
            if (CanSave && prompts != null)
            {
                if (SaveIfNo || isYes)
                {
                    prompts.Set(sha256Hash, isYes ? "true" : "false");
                    AppServices.SaveSettings();
                }
            }

            return isYes;
        }

        public static double MapValue(double input, double il, double ih, double ol, double oh) =>
            RobloxPlaceHelper.MapValue(input, il, ih, ol, oh);

        public static bool SetClipboardTextSafe(string? text) =>
            ClipboardHelper.SetClipboardTextSafe(text ?? string.Empty);

        public static bool IsConnectedToInternet()
        {
            try
            {
                return System.Net.NetworkInformation.NetworkInterface.GetIsNetworkAvailable();
            }
            catch (Exception ex)
            {
                AppServices.Logger.Debug("[Utilities] GetIsNetworkAvailable failed, defaulting to true", ex);
                return true;
            }
        }
    }
}
