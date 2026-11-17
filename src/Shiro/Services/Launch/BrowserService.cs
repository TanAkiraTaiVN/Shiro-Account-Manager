#nullable enable
using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace Shiro.Services
{
    public sealed class BrowserService
    {
        public static BrowserService Default { get; } = new BrowserService();

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
                AppServices.Logger?.Debug($"SuppressPasswordManager failed: {ex.Message}");
            }
        }

        private static readonly SemaphoreSlim SignInGate = new SemaphoreSlim(2, 2);
        internal static Func<string?, string?, int, bool, CancellationToken, Task<Account?>>? TestSignInOverride;

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
                if (TestSignInOverride != null)
                {
                    return await TestSignInOverride(username, password, windowSlot, submitCredentials, cancellationToken).ConfigureAwait(false);
                }

                Account? target = (!string.IsNullOrWhiteSpace(username) || !string.IsNullOrWhiteSpace(password))
                    ? new Account { Username = username ?? string.Empty, Password = password ?? string.Empty }
                    : null;
                return await AppServices.Dialogs.SignInFastLoginAsync(target, cancellationToken).ConfigureAwait(false);
            }
            finally
            {
                SignInGate.Release();
            }
        }

        public Task<string> OpenHomeAsync(
            Account account,
            bool preferChrome,
            CancellationToken cancellationToken = default(CancellationToken))
        {
            if (account == null)
                return Task.FromResult("ERROR: Account was not found.");

            return account.LaunchAppHome(preferChrome, cancellationToken);
        }

        public Task<LaunchResult> OpenHomeResultAsync(
            Account account,
            bool preferChrome,
            CancellationToken cancellationToken = default(CancellationToken))
        {
            if (account == null)
                return Task.FromResult(LaunchResult.Fail(LaunchOutcome.Failed, "ERROR: Account was not found."));

            return account.LaunchAppHomeResult(preferChrome, cancellationToken);
        }
    }
}
