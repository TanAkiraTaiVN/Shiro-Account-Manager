#nullable enable
using System.Threading.Tasks;
using System.Threading;
using Shiro.Classes;
using PuppeteerSharp;
using PuppeteerSharp.BrowserData;
using System;
using System.IO;
using System.Linq;

namespace Shiro.Services
{
    public sealed class BrowserService
    {
        public static BrowserService Default { get; } = new BrowserService();

        private static string? cachedInstalledBrowserPath;

        private static ReadOnlySpan<byte> SuppressedPasswordPrefsUtf8 =>
            """
            {
              "credentials_enable_service": false,
              "profile": {
                "password_manager_enabled": false
              },
              "autofill": {
                "profile_enabled": false,
                "credit_card_enabled": false,
                "address_enabled": false
              },
              "password_manager": {
                "enabled": false
              }
            }
            """u8;

        /// <summary>
        /// Returns the preferred installed browser executable. Chrome is
        /// deliberately first; Edge is only used when Chrome is unavailable.
        /// Keeping this choice here makes sign-in and sign-up use the same
        /// browser policy as the rest of the application.
        /// </summary>
        public static string? FindInstalledBrowserExecutable()
        {
            string? cached = Volatile.Read(ref cachedInstalledBrowserPath);
            if (!string.IsNullOrEmpty(cached) && File.Exists(cached))
                return cached;

            string[] candidates = AccountBrowser.FindSystemBrowserExecutables();
            string? edgeFallback = null;
            foreach (string path in candidates)
            {
                string fileName = Path.GetFileName(path);
                if (string.Equals(fileName, "chrome.exe", StringComparison.OrdinalIgnoreCase))
                {
                    Volatile.Write(ref cachedInstalledBrowserPath, path);
                    return path;
                }
                if (edgeFallback == null && string.Equals(fileName, "msedge.exe", StringComparison.OrdinalIgnoreCase))
                {
                    edgeFallback = path;
                }
            }

            if (edgeFallback != null)
                Volatile.Write(ref cachedInstalledBrowserPath, edgeFallback);

            return edgeFallback;
        }

        /// <summary>
        /// Resolves an isolated browser executable without ever opening the
        /// user's default browser profile. Chrome/Edge are preferred; a
        /// Chromium copy cached by an older Shiro version is the last resort.
        /// Nothing is downloaded: Edge ships with Windows 10/11.
        /// </summary>
        public static Task<string> ResolveBrowserExecutableAsync(
            CancellationToken cancellationToken = default(CancellationToken))
        {
            cancellationToken.ThrowIfCancellationRequested();

            string? installed = FindInstalledBrowserExecutable();
            if (!string.IsNullOrWhiteSpace(installed))
                return Task.FromResult(installed);

            string? cached = AccountBrowser.FindCachedChromiumExecutable();
            if (!string.IsNullOrWhiteSpace(cached))
            {
                Program.Logger?.Warn($"Chrome and Edge were not found; using previously cached Chromium at {cached}.");
                return Task.FromResult(cached);
            }

            return Task.FromException<string>(new FileNotFoundException(AccountBrowser.BrowserNotFoundMessage));
        }

        public static readonly string[] NoPasswordManagerSwitches = new[]
        {
            "--disable-save-password-bubble",
            "--disable-single-click-autofill",
            "--disable-autofill-keyboard-accessory-view",
            "--disable-search-engine-choice-screen",
            "--disable-sync",
            "--password-store=basic",
            "--disable-features=AutofillServerCommunication,PasswordManager,SavePasswordBubble,PasswordLeakDetection,msImplicitSignin,msEdgeSidebarV2"
        };

        public static void SuppressPasswordManager(string profilePath)
        {
            if (string.IsNullOrWhiteSpace(profilePath))
                return;

            try
            {
                string defaultDir = Path.Combine(profilePath, "Default");
                Directory.CreateDirectory(defaultDir);
                string prefsPath = Path.Combine(defaultDir, "Preferences");
                using var fs = new FileStream(prefsPath, FileMode.Create, FileAccess.Write, FileShare.Read);
                fs.Write(SuppressedPasswordPrefsUtf8);
            }
            catch (Exception ex)
            {
                Program.Logger?.Debug($"SuppressPasswordManager failed: {ex.Message}");
            }
        }

        private static readonly SemaphoreSlim SignInGate = new SemaphoreSlim(2, 2);

        public async Task<Account?> SignInAsync(
            string? username = null,
            string? password = null,
            int windowSlot = 0,
            bool submitCredentials = true,
            CancellationToken cancellationToken = default(CancellationToken))
        {
            await SignInGate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                return await RobloxBrowserLogin.LoginAsync(
                    username,
                    password,
                    windowSlot,
                    submitCredentials,
                    cancellationToken: cancellationToken).ConfigureAwait(false);
            }
            finally
            {
                SignInGate.Release();
            }
        }

        public async Task<Account?> SignUpAsync(
            string? username = null,
            string? password = null,
            CancellationToken cancellationToken = default(CancellationToken))
        {
            return await RobloxBrowserSignup.OpenAsync(username!, password!, cancellationToken).ConfigureAwait(false);
        }

        public Task<string> OpenHomeAsync(
            Account account,
            bool preferChrome,
            CancellationToken cancellationToken = default(CancellationToken))
        {
            if (account == null)
                return Task.FromResult("ERROR: Account was not found.");

            // Keep the Home decision in one browser boundary while allowing
            // Account to retain the native Roblox protocol and proxy-aware
            // Chromium fallbacks that existing users rely on.
            return account.LaunchAppHome(preferChrome, cancellationToken);
        }
    }
}
