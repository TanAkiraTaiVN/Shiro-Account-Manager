using System.Threading.Tasks;
using System.Threading;
using RBX_Alt_Manager.Classes;
using PuppeteerSharp;
using PuppeteerSharp.BrowserData;
using System;
using System.IO;
using System.Linq;

namespace RBX_Alt_Manager.Services
{
    public sealed class BrowserService
    {
        public static BrowserService Default { get; } = new BrowserService();

        private static readonly SemaphoreSlim fallbackBrowserGate = new SemaphoreSlim(1, 1);
        private static string fallbackBrowserPath;
        private static string cachedInstalledBrowserPath;

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
        public static string FindInstalledBrowserExecutable()
        {
            string cached = Volatile.Read(ref cachedInstalledBrowserPath);
            if (!string.IsNullOrEmpty(cached) && File.Exists(cached))
                return cached;

            string[] candidates = AccountBrowser.FindSystemBrowserExecutables();
            string edgeFallback = null;
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
        /// user's default browser profile. If Chrome and Edge are missing,
        /// use the existing PuppeteerSharp fallback under Shiro's cache.
        /// </summary>
        public static async Task<string> ResolveBrowserExecutableAsync(
            CancellationToken cancellationToken = default(CancellationToken))
        {
            cancellationToken.ThrowIfCancellationRequested();

            string installed = FindInstalledBrowserExecutable();
            if (!string.IsNullOrWhiteSpace(installed))
                return installed;

            await fallbackBrowserGate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                if (!string.IsNullOrWhiteSpace(fallbackBrowserPath) && File.Exists(fallbackBrowserPath))
                    return fallbackBrowserPath;

                Program.Logger.Warn("Chrome and Edge were not found; downloading the isolated Chromium fallback.");
                InstalledBrowser revision = await AccountBrowser.Fetcher
                    .DownloadAsync(Chrome.DefaultBuildId)
                    .ConfigureAwait(false);
                cancellationToken.ThrowIfCancellationRequested();

                string executable = revision?.GetExecutablePath();
                if (string.IsNullOrWhiteSpace(executable) || !File.Exists(executable))
                    throw new FileNotFoundException("The Chromium fallback executable was not available.", executable);

                fallbackBrowserPath = executable;
                return executable;
            }
            finally
            {
                fallbackBrowserGate.Release();
            }
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
            catch { }
        }

        private static readonly SemaphoreSlim SignInGate = new SemaphoreSlim(2, 2);

        public async Task<Account> SignInAsync(
            string username = null,
            string password = null,
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

        public Task<Account> SignUpAsync(
            string username = null,
            string password = null,
            CancellationToken cancellationToken = default(CancellationToken))
        {
            return RobloxBrowserSignup.OpenAsync(username, password, cancellationToken);
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
