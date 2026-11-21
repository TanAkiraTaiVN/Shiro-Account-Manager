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

        private static readonly SemaphoreSlim SignInGate = new SemaphoreSlim(1, 1);
        private static readonly AsyncLocal<Func<string?, string?, int, bool, CancellationToken, Task<Account?>>?> _testSignInOverride = new();

        internal static Func<string?, string?, int, bool, CancellationToken, Task<Account?>>? TestSignInOverride
        {
            get => _testSignInOverride.Value;
            set => _testSignInOverride.Value = value;
        }

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
