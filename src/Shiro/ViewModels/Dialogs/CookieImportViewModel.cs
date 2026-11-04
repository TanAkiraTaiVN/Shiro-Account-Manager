#nullable enable
using System;
using System.Threading.Tasks;
using Shiro.Classes;
using Shiro.Services;

namespace Shiro.ViewModels.Dialogs
{
    public sealed class CookieImportViewModel : DialogViewModelBase
    {
        private readonly IAccountRepository _repo;
        private readonly Func<string, Task<Account>> _verify;
        private readonly IHostUiBridgeService _host;
        private readonly IAppLogger _logger;
        private string _password;

        private string _cookie = string.Empty;
        private string _alias = string.Empty;
        private string _group = string.Empty;
        private string _previewUsername = string.Empty;
        private string _lastVerifiedCookie = string.Empty;
        private Account? _lastVerifiedAccount;

        public CookieImportViewModel(
            IAccountRepository? repo = null,
            Func<string, Task<Account>>? verify = null,
            IHostUiBridgeService? host = null,
            string password = "",
            IAppLogger? logger = null)
        {
            _repo = repo ?? AppServices.AccountRepository;
            _verify = verify ?? (c => Account.CreateAsync(c));
            _host = host ?? AppServices.HostUiBridge;
            _password = password ?? string.Empty;
            _logger = logger ?? AppServices.Logger;

            SetStatus(LocalizationService.Get("FastLogin.PasteCookiePrompt"), StatusKind.Neutral);
            VerifyCommand = TrackCommand(new AsyncRelayCommand(VerifyAsync, () => !IsBusy));
            AddCommand = TrackCommand(new AsyncRelayCommand(AddAsync, () => !IsBusy));
        }

        public string Cookie
        {
            get => _cookie;
            set => SetProperty(ref _cookie, value);
        }

        public string Alias
        {
            get => _alias;
            set => SetProperty(ref _alias, value);
        }

        public string Group
        {
            get => _group;
            set => SetProperty(ref _group, value);
        }

        public string PreviewUsername
        {
            get => _previewUsername;
            private set => SetProperty(ref _previewUsername, value);
        }

        public Account? CreatedAccount { get; private set; }

        public Action? OnAccountCreated { get; set; }

        public IAsyncRelayCommand VerifyCommand { get; }
        public IAsyncRelayCommand AddCommand { get; }

        public void LoadFromAccount(Account account)
        {
            _password = account.Password ?? string.Empty;
            Alias = account.Alias ?? string.Empty;
            Group = account.Group ?? string.Empty;
            if (!string.IsNullOrEmpty(account.SecurityToken))
            {
                Cookie = account.SecurityToken;
            }
        }

        public async Task VerifyAsync()
        {
            if (IsBusy) return;
            string cookie = Account.SanitizeCookie(Cookie ?? string.Empty);
            if (string.IsNullOrEmpty(cookie))
            {
                SetStatus(LocalizationService.Get("FastLogin.PasteCookiePrompt"), StatusKind.Error);
                return;
            }

            IsBusy = true;
            SetStatus(LocalizationService.Get("FastLogin.CheckingRobloxApi"), StatusKind.Info);
            try
            {
                var account = await _verify(cookie);
                if (account != null && account.Valid)
                {
                    _lastVerifiedCookie = cookie;
                    _lastVerifiedAccount = account;
                    PreviewUsername = $"{account.Username} (ID: {account.UserID})";
                    SetStatus(LocalizationService.Get("FastLogin.CookieValidAndVerified"), StatusKind.Success);
                }
                else
                {
                    _lastVerifiedCookie = string.Empty;
                    _lastVerifiedAccount = null;
                    PreviewUsername = LocalizationService.Get("FastLogin.InvalidAccountTitle");
                    SetStatus(LocalizationService.Get("FastLogin.CookieExpiredOrInvalid"), StatusKind.Error);
                }
            }
            catch (Exception ex)
            {
                _lastVerifiedCookie = string.Empty;
                _lastVerifiedAccount = null;
                SetStatus(LocalizationService.Format("Common.ErrorFormat", ex.Message), StatusKind.Error);
            }
            finally
            {
                IsBusy = false;
            }
        }

        public async Task AddAsync()
        {
            if (IsBusy) return;
            string cookie = Account.SanitizeCookie(Cookie ?? string.Empty);
            if (string.IsNullOrEmpty(cookie))
            {
                SetStatus(LocalizationService.Get("FastLogin.EnterValidCookiePrompt"), StatusKind.Error);
                return;
            }

            IsBusy = true;
            try
            {
                Account? newAcc;
                if (_lastVerifiedAccount != null &&
                    _lastVerifiedAccount.Valid &&
                    string.Equals(_lastVerifiedCookie, cookie, StringComparison.Ordinal))
                {
                    newAcc = _repo.RegisterValidated(
                        _lastVerifiedAccount,
                        _lastVerifiedAccount.SecurityToken,
                        _password,
                        saveImmediate: true);
                }
                else
                {
                    newAcc = await _repo.AddOrUpdateAsync(cookie, _password);
                }

                if (newAcc != null && newAcc.Valid)
                {
                    string? alias = Alias?.Trim();
                    string? group = Group?.Trim();
                    bool metadataUpdated = false;

                    if (!string.IsNullOrEmpty(alias))
                    {
                        newAcc.Alias = alias;
                        metadataUpdated = true;
                    }
                    if (!string.IsNullOrEmpty(group))
                    {
                        newAcc.Group = group;
                        metadataUpdated = true;
                    }

                    if (metadataUpdated)
                    {
                        try
                        {
                            await _repo.SaveAsync(true);
                        }
                        catch (Exception ex)
                        {
                            _logger.Warn($"SaveAccountsAsync after fast login failed: {ex.Message}");
                        }
                    }

                    CreatedAccount = newAcc;
                    OnAccountCreated?.Invoke();
                    _host.SetDashboardStatus(LocalizationService.Format("FastLogin.AccountAddedSuccess", newAcc.Username));
                    RequestClose(true);
                }
                else
                {
                    SetStatus(LocalizationService.Get("FastLogin.CookieFailedOrExpired"), StatusKind.Error);
                }
            }
            catch (Exception ex)
            {
                SetStatus(LocalizationService.Format("Common.ErrorFormat", ex.Message), StatusKind.Error);
            }
            finally
            {
                IsBusy = false;
            }
        }
    }
}
