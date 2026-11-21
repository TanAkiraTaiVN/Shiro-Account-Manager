#nullable enable
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Shiro.Services;

namespace Shiro.ViewModels
{
    /// <summary>
    /// Shared command and range-selection actions executed by <see cref="AccountCollectionViewModel"/>.
    /// Keeps workflow plumbing decoupled from property-notification boilerplate.
    /// </summary>
    internal static class AccountCollectionCommandActions
    {
        internal static void ValidateSingleSession(
            AccountViewModel? accountVm,
            IAccountRepository repository,
            Action<Account, bool, bool> updateValidity,
            Action<string> postStatus)
        {
            if (accountVm?.Model == null) return;
            _ = Task.Run(async () =>
            {
                bool ok = await accountVm.Model.ValidateSessionAsync().ConfigureAwait(false);
                repository.Save(true);
                updateValidity(accountVm.Model, ok, true);
                string shownName = StreamerModeService.GetDisplayUsername(accountVm.Username);
                postStatus(ok
                    ? LocalizationService.Format("Main.SessionValidToast", shownName)
                    : (accountVm.Model.IsBannedOrLocked
                        ? $"{shownName}: {accountVm.Model.AccountStatusIssue}!"
                        : LocalizationService.Format("Main.SessionExpiredToast", shownName)));
            });
        }

        internal static void SavePlaceIdToAccount(
            AccountViewModel? accountVm,
            IAccountRepository repository,
            string? rawPlaceId,
            string? rawJobId)
        {
            if (accountVm?.Model == null || !long.TryParse(rawPlaceId?.Trim(), out long pid) || pid <= 0)
                return;

            accountVm.Model.SetField("SavedPlaceId", pid.ToString(CultureInfo.InvariantCulture));
            if (!string.IsNullOrWhiteSpace(rawJobId))
                accountVm.Model.SetField("SavedJobId", rawJobId.Trim());
            repository.SaveAsync().Forget();
        }

        internal static void ReLoginSelected(IReadOnlyList<AccountViewModel> selected, Action<string> setStatus)
        {
            if (selected.Count == 0)
            {
                setStatus(LocalizationService.Get("Main.SelectAccountBeforeRelogin"));
                return;
            }
            var models = selected.Where(a => a.Model != null).Select(a => a.Model!).ToList();
            if (models.Count == 1) AppServices.Dialogs.OpenFastLoginDialog(models[0]);
            else if (models.Count > 1) AppServices.Dialogs.OpenFastLoginDialogBatch(models);
        }

        internal static void ValidateSessionSelected(
            IReadOnlyList<AccountViewModel> selected,
            IAccountWorkflowHost host,
            Action<string> setStatus,
            Action<string> postStatus)
        {
            if (selected.Count == 0)
            {
                setStatus(LocalizationService.Get("Main.SelectAccountBeforeCheckSession"));
                return;
            }
            AppServices.AccountWorkflowService.ValidateSessionSelected(selected.ToList(), host, postStatus);
        }

        internal static async Task RefreshAllRobuxAsync(
            IReadOnlyList<AccountViewModel> accounts,
            IAccountRepository repository,
            Action<string> setStatus,
            Action onCompleted)
        {
            var validAccounts = accounts.Where(a => a.Valid && a.UserId > 0 && a.Model != null).ToList();
            if (validAccounts.Count == 0)
            {
                setStatus(LocalizationService.Get("Main.NoValidAccountsForRobux"));
                return;
            }

            int total = validAccounts.Count;
            setStatus(LocalizationService.Format("Main.RefreshingRobuxMultiple", total));
            int updated = 0;

            using var concurrencyGate = new SemaphoreSlim(4, 4);
            var tasks = validAccounts.Select(async acc =>
            {
                await concurrencyGate.WaitAsync().ConfigureAwait(false);
                try
                {
                    await acc.Model!.FetchRobuxBalanceAsync().ConfigureAwait(false);
                    int current = Interlocked.Increment(ref updated);
                    AppDispatcher.Post(() =>
                    {
                        acc.NotifyRobuxChanged();
                        setStatus(LocalizationService.Format("Main.RefreshingRobuxProgress", current, total, StreamerModeService.GetDisplayUsername(acc.Username)));
                    });
                }
                finally { concurrencyGate.Release(); }
            }).ToArray();

            await Task.WhenAll(tasks);
            await repository.SaveAsync(true);
            onCompleted();
            setStatus(LocalizationService.Format("Main.RefreshingRobuxCompleted", updated));
        }

        internal static async Task CheckRobuxSingleAsync(
            AccountViewModel? accountVm,
            IAccountRepository repository,
            Action<string> setStatus,
            Action onCompleted)
        {
            if (accountVm?.Model == null) return;
            string shownName = StreamerModeService.GetDisplayUsername(accountVm.Username);
            setStatus(LocalizationService.Format("Main.CheckingRobuxSingle", shownName));
            long bal = await accountVm.Model.FetchRobuxBalanceAsync();
            accountVm.NotifyRobuxChanged();
            await repository.SaveAsync(true);
            onCompleted();
            setStatus(bal >= 0
                ? LocalizationService.Format("Main.RobuxBalanceResult", shownName, StreamerModeService.ShouldMaskRobux() ? (object)"***" : bal)
                : LocalizationService.Format("Main.CannotFetchRobux", shownName));
        }

        internal static void ReLoginAllExpired(IReadOnlyList<AccountViewModel> accounts, Action<string> setStatus)
        {
            try
            {
                int skippedLocked = 0;
                var expired = new List<Account>();
                for (int i = 0; i < accounts.Count; i++)
                {
                    var a = accounts[i];
                    if (!a.IsExpired || a.Model == null) continue;
                    if (a.Model.IsBannedOrLocked) { skippedLocked++; continue; }
                    expired.Add(a.Model);
                }

                if (expired.Count == 0)
                {
                    setStatus(skippedLocked > 0
                        ? LocalizationService.Format("Main.NoNormalExpiredAccounts", skippedLocked)
                        : LocalizationService.Get("Main.NoExpiredAccountsFound"));
                    return;
                }

                if (skippedLocked > 0)
                    setStatus(LocalizationService.Format("Main.ReloggingInExpiredAccounts", expired.Count, skippedLocked));
                if (expired.Count == 1) AppServices.Dialogs.OpenFastLoginDialog(expired[0]);
                else AppServices.Dialogs.OpenFastLoginDialogBatch(expired);
            }
            catch (Exception ex)
            {
                setStatus(LocalizationService.Get("Main.BatchReloginErrorPrefix") + ex.Message);
            }
        }

        internal static async Task SelectByRangePromptAsync(Func<string, int> selectRange, Action<string> setStatus)
        {
            string prompt = LocalizationService.Get("Main.EnterSelectionRangePrompt");
            string title = LocalizationService.Get("Main.SelectByRangeTitle");
            string input = await AppServices.Dialogs.ShowInputPromptAsync(prompt, title);
            if (string.IsNullOrWhiteSpace(input) || input == "/UC") return;

            await AppDispatcher.InvokeAsync(() =>
            {
                int picked = selectRange(input);
                if (picked > 0)
                {
                    string msg = LocalizationService.Format("Main.SelectedByRangeResult", picked, input);
                    setStatus(msg);
                    ToastService.ShowSuccess(title, msg);
                }
                else
                {
                    string warn = LocalizationService.Format("Main.NoAccountsMatchedRange", input);
                    setStatus(warn);
                    ToastService.ShowWarning(title, warn);
                }
            });
        }

        internal static List<int> ParseRangeIndices(string? expression, int totalCount)
        {
            if (string.IsNullOrWhiteSpace(expression) || totalCount <= 0)
                return new List<int>();

            var matched = new HashSet<int>();
            var tokens = expression.Split(new[] { ',', ';', ' ' }, StringSplitOptions.RemoveEmptyEntries);
            foreach (string rawToken in tokens)
            {
                string token = rawToken.Trim();
                int dashIdx = token.IndexOf('-');
                if (dashIdx > 0 && dashIdx < token.Length - 1)
                {
                    if (int.TryParse(token.AsSpan(0, dashIdx), out int from) &&
                        int.TryParse(token.AsSpan(dashIdx + 1), out int to))
                    {
                        int lo = Math.Max(1, Math.Min(from, to));
                        int hi = Math.Min(totalCount, Math.Max(from, to));
                        for (int idx = lo; idx <= hi; idx++) matched.Add(idx - 1);
                    }
                }
                else if (int.TryParse(token, out int single) && single >= 1 && single <= totalCount)
                {
                    matched.Add(single - 1);
                }
            }

            return matched.OrderBy(x => x).ToList();
        }
    }
}
