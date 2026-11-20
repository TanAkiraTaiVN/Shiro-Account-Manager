#nullable enable
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Input;
using Shiro.Services;

namespace Shiro.ViewModels.Dialogs
{
    public sealed class BulkImportViewModel : DialogViewModelBase
    {
        private readonly IAccountRepository _repo;
        private readonly IDialogService _dialogs;
        private readonly IHostUiBridgeService _host;
        private readonly IAppLogger _logger;
        private readonly int _cookieConcurrency;

        private string _inputText = string.Empty;
        private double _progressValue;
        private double _progressMax = 100;
        private bool _isProgressVisible;
        private int _importedCount;

        public BulkImportViewModel(
            IAccountRepository? repo = null,
            IDialogService? dialogs = null,
            IHostUiBridgeService? host = null,
            int cookieConcurrency = 4,
            IAppLogger? logger = null)
        {
            _repo = repo ?? AppServices.AccountRepository;
            _dialogs = dialogs ?? AppServices.Dialogs;
            _host = host ?? AppServices.HostUiBridge;
            _logger = logger ?? AppServices.Logger;
            _cookieConcurrency = Math.Max(1, cookieConcurrency);

            StartImportCommand = TrackCommand(new AsyncRelayCommand(StartImportAsync, () => !IsBusy));
            ClearCommand = TrackCommand(new RelayCommand(Clear, () => !IsBusy));
            CloseCommand = new RelayCommand(() =>
            {
                OnClosing();
                RequestClose(ImportedCount > 0);
            });
        }

        public string InputText
        {
            get => _inputText;
            set => SetProperty(ref _inputText, value);
        }

        public double ProgressValue
        {
            get => _progressValue;
            private set => SetProperty(ref _progressValue, value);
        }

        public double ProgressMax
        {
            get => _progressMax;
            private set => SetProperty(ref _progressMax, value);
        }

        public bool IsProgressVisible
        {
            get => _isProgressVisible;
            private set => SetProperty(ref _isProgressVisible, value);
        }

        public int ImportedCount
        {
            get => _importedCount;
            private set => SetProperty(ref _importedCount, value);
        }

        public IAsyncRelayCommand StartImportCommand { get; }
        public ICommand ClearCommand { get; }
        public ICommand CloseCommand { get; }

        private void Clear()
        {
            InputText = string.Empty;
            SetStatus(LocalizationService.Get("BulkImport.ContentCleared"), StatusKind.Neutral);
        }

        public async Task StartImportAsync()
        {
            if (IsBusy)
                return;

            if (string.IsNullOrWhiteSpace(InputText))
            {
                SetStatus(LocalizationService.Get("BulkImport.PasteAccountListPrompt"), StatusKind.Warning);
                return;
            }

            var classification = AccountLineParser.Classify(InputText);
            int totalLines = classification.Cookies.Count + classification.UserPass.Count + classification.Failed;
            if (totalLines == 0)
            {
                SetStatus(LocalizationService.Get("BulkImport.NoValidAccountsFound"), StatusKind.Warning);
                return;
            }

            IsBusy = true;
            IsProgressVisible = true;
            ProgressMax = totalLines;
            ProgressValue = 0;

            CancellationToken token = BeginOperation();
            int success = 0;
            int failed = classification.Failed;

            try
            {
                if (classification.Cookies.Count > 0)
                {
                    using var cookieGate = new SemaphoreSlim(_cookieConcurrency, _cookieConcurrency);
                    var cookieTasks = classification.Cookies.Select(async item =>
                    {
                        if (token.IsCancellationRequested) return;
                        try
                        {
                            await cookieGate.WaitAsync(token).ConfigureAwait(false);
                        }
                        catch (OperationCanceledException)
                        {
                            return;
                        }

                        try
                        {
                            if (token.IsCancellationRequested) return;
                            var acc = await _repo.AddOrUpdateAsync(item.Cookie, item.Password ?? string.Empty, saveImmediate: false, cancellationToken: token).ConfigureAwait(false);
                            int newSuccess = acc != null && acc.Valid
                                ? Interlocked.Increment(ref success)
                                : Volatile.Read(ref success);
                            int newFailed = acc == null || !acc.Valid
                                ? Interlocked.Increment(ref failed)
                                : Volatile.Read(ref failed);

                            int current = newSuccess + newFailed;
                            AppDispatcher.Post(() =>
                            {
                                ProgressValue = current;
                                SetStatus(LocalizationService.Format("BulkImport.ImportingCookie", current, totalLines), StatusKind.Info);
                            });
                        }
                        catch (OperationCanceledException)
                        {
                            // Cancelled by user or dialog closing
                        }
                        finally
                        {
                            SafeExecuteHelper.Try(() => cookieGate.Release());
                        }
                    }).ToList();

                    await Task.WhenAll(cookieTasks);
                }

                var queuedForFastLogin = new List<Account>();
                if (classification.UserPass.Count > 0 && !token.IsCancellationRequested)
                {
                    var existingByUsername = new Dictionary<string, Account>(StringComparer.OrdinalIgnoreCase);
                    foreach (var acc in _repo.GetAll())
                    {
                        if (acc != null && !string.IsNullOrEmpty(acc.Username))
                            existingByUsername.TryAdd(acc.Username, acc);
                    }

                    var unknownUsernames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                    for (int i = 0; i < classification.UserPass.Count; i++)
                    {
                        string u = classification.UserPass[i].User;
                        if (!string.IsNullOrEmpty(u) && !existingByUsername.ContainsKey(u))
                            unknownUsernames.Add(u);
                    }

                    var resolvedUserIds = new ConcurrentDictionary<string, long>(StringComparer.OrdinalIgnoreCase);
                    if (unknownUsernames.Count > 0)
                    {
                        try
                        {
                            await Parallel.ForEachAsync(
                                unknownUsernames,
                                new ParallelOptions { MaxDegreeOfParallelism = _cookieConcurrency, CancellationToken = token },
                                async (username, ct) =>
                                {
                                    try
                                    {
                                        var (okId, uid, _) = await _repo.LookupUserIdAsync(username, ct).ConfigureAwait(false);
                                        if (okId && uid > 0)
                                            resolvedUserIds[username] = uid;
                                    }
                                    catch (Exception ex)
                                    {
                                        _logger.Debug($"Resolve userId for {username} failed: {ex.Message}");
                                    }
                                });
                        }
                        catch (OperationCanceledException)
                        {
                            // Cancelled mid-lookup
                        }
                    }

                    int upIndex = 0;
                    foreach (var up in classification.UserPass)
                    {
                        if (token.IsCancellationRequested) break;
                        upIndex++;

                        SetStatus(LocalizationService.Format("BulkImport.SavingAccount", StreamerModeService.GetDisplayUsername(up.User), upIndex, classification.UserPass.Count), StatusKind.Info);

                        if (!existingByUsername.TryGetValue(up.User, out Account? targetAcc))
                        {
                            resolvedUserIds.TryGetValue(up.User, out long resolvedUserId);

                            targetAcc = new Account
                            {
                                Username = up.User,
                                Password = up.Pass,
                                UserID = resolvedUserId,
                                Valid = false,
                                Group = "Default",
                                LastUse = DateTime.Now
                            };

                            _repo.AddThreadSafe(targetAcc);
                            existingByUsername[up.User] = targetAcc;
                        }
                        else
                        {
                            targetAcc.Password = up.Pass;
                        }

                        if (!targetAcc.Valid || string.IsNullOrWhiteSpace(targetAcc.SecurityToken))
                        {
                            queuedForFastLogin.Add(targetAcc);
                        }

                        int current = Interlocked.Increment(ref success) + Volatile.Read(ref failed);
                        ProgressValue = current;
                    }
                }

                if (success > 0)
                {
                    try { _repo.Save(true); }
                    catch (Exception ex) { _logger.Warn($"SaveAccounts after bulk import failed: {ex.Message}"); }
                    _host.RefreshAccountView();
                    _host.SetDashboardStatus(LocalizationService.Format("BulkImport.Completed", success));
                }

                ImportedCount = success;
                IsProgressVisible = false;

                if (queuedForFastLogin.Count > 0 && !token.IsCancellationRequested)
                {
                    SetStatus(LocalizationService.Format("BulkImport.OpeningInAppLogin", success, queuedForFastLogin.Count), StatusKind.Success);
                    if (queuedForFastLogin.Count == 1)
                        _dialogs.OpenFastLoginDialog(queuedForFastLogin[0]);
                    else
                        _dialogs.OpenFastLoginDialogBatch(queuedForFastLogin);
                }
                else if (token.IsCancellationRequested)
                {
                    SetStatus(LocalizationService.Format("BulkImport.StoppedWithSuccessCount", success), StatusKind.Warning);
                }
                else
                {
                    SetStatus(
                        LocalizationService.Format("BulkImport.SummaryResult", success, failed),
                        success > 0 ? StatusKind.Success : StatusKind.Error);
                }
            }
            catch (Exception ex)
            {
                SetStatus(LocalizationService.Format("Common.ErrorFormat", ex.Message), StatusKind.Error);
                IsProgressVisible = false;
            }
            finally
            {
                IsBusy = false;
            }
        }
    }
}
