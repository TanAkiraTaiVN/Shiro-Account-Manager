#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Shiro.ViewModels;

namespace Shiro.Services
{
    public partial class AccountWorkflowService
    {
        [GeneratedRegex(@"_\|WARNING:-[^\r\n""]+")]
        private static partial Regex CookieRegex();

        [GeneratedRegex(@"_\|WARNING:-[^;\r\n""]+")]
        private static partial Regex CookieCleanRegex();

        private static readonly Lazy<AccountWorkflowService> _defaultInstance = new(() => new AccountWorkflowService(AppServices.Logger));
        public static AccountWorkflowService Default => _defaultInstance.Value;

        private readonly IAppLogger _logger;

        public AccountWorkflowService() : this(AppServices.Logger) { }

        public AccountWorkflowService(IAppLogger logger)
        {
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        }

        public async Task CheckAllSessionsAsync(
            MainViewModel viewModel,
            Action<string> statusCallback)
        {
            var list = AppServices.AccountRepository.GetAll();
            if (list == null || list.Count == 0) return;

            statusCallback?.Invoke(Shiro.Services.LocalizationService.Get("AccountWorkflow.CheckingAllSessions"));

            int total = list.Count;
            int checkedCount = 0;
            int expiredCount = 0;

            var parallelOptions = new ParallelOptions
            {
                MaxDegreeOfParallelism = 12,
                CancellationToken = CancellationToken.None
            };

            await Parallel.ForEachAsync(list, parallelOptions, async (acc, ct) =>
            {
                try
                {
                    bool isValid = await RobloxRateLimitCoordinator.Instance.ExecuteWithPacingAsync(
                        RateLimitCategory.SessionCheck,
                        () => acc.ValidateSessionAsync(ct),
                        minIntervalMs: 0,
                        cancellationToken: ct).ConfigureAwait(false);

                    int c = Interlocked.Increment(ref checkedCount);
                    if (!isValid) Interlocked.Increment(ref expiredCount);

                    string state = isValid ? Shiro.Services.LocalizationService.Get("AccountWorkflow.StatusValid") : (acc.IsBannedOrLocked ? acc.AccountStatusIssue : Shiro.Services.LocalizationService.Get("KPI_Expired"));
                    statusCallback?.Invoke(Shiro.Services.LocalizationService.Format("AccountWorkflow.CheckingProgressWithExpired", c, total, acc.Username, state, expiredCount));
                    viewModel?.UpdateAccountValidity(acc, isValid, refreshFilterAndCounts: false);
                    AppDispatcher.Post(() => viewModel?.NotifyCounts());
                }
                catch (Exception ex)
                {
                    _logger.Warn($"[CheckSessions] {acc.Username}: {ex.Message}");
                    int c = Interlocked.Increment(ref checkedCount);
                    if (!acc.Valid) Interlocked.Increment(ref expiredCount);
                }
            }).ConfigureAwait(false);
            await AppServices.AccountRepository.SaveAsync(true).ConfigureAwait(false);

            var bannedAccounts = list.Where(a => a.IsBannedOrLocked).ToList();
            var expiredAccounts = list.Where(a => !a.Valid && !a.IsBannedOrLocked).ToList();
            var validAccounts = list.Where(a => a.Valid && !a.IsBannedOrLocked).ToList();

            int valid = validAccounts.Count;
            int expired = expiredAccounts.Count;
            int banned = bannedAccounts.Count;

            AppDispatcher.Post(() =>
            {
                viewModel?.NotifyCounts();
                viewModel?.ApplyFilter();
            });

            statusCallback?.Invoke((expired > 0 || banned > 0)
                ? Shiro.Services.LocalizationService.Format("AccountWorkflow.ScanCompletedWithBans", valid, expired, banned)
                : Shiro.Services.LocalizationService.Format("AccountWorkflow.ScanCompletedAllHealthy", total));

            string msg = (expired > 0 || banned > 0)
                ? Shiro.Services.LocalizationService.Get("AccountWorkflow.SessionCheckResultHeader") +
                  Shiro.Services.LocalizationService.Format("AccountWorkflow.TotalAccountsSummary", total) +
                  Shiro.Services.LocalizationService.Format("AccountWorkflow.ReadyAccountsSummary", valid) +
                  Shiro.Services.LocalizationService.Format("AccountWorkflow.ExpiredSessionsSummary", expired) +
                  Shiro.Services.LocalizationService.Format("AccountWorkflow.BannedAccountsSummary", banned) +
                  (banned > 0
                      ? Shiro.Services.LocalizationService.Get("AccountWorkflow.BannedAccountsListHeader") +
                        string.Join("\n", bannedAccounts.Take(5).Select(a => $"• {a.Username}: {a.AccountStatusIssue}")) +
                        (bannedAccounts.Count > 5 ? Shiro.Services.LocalizationService.Format("AccountWorkflow.MoreAccountsTruncated", bannedAccounts.Count - 5) : "") + "\n\n"
                      : "") +
                  (expired == total
                      ? Shiro.Services.LocalizationService.Get("AccountWorkflow.SpecialNoteAllExpired") +
                        Shiro.Services.LocalizationService.Get("AccountWorkflow.IpChangedWarning")
                      : "") +
                  Shiro.Services.LocalizationService.Get("AccountWorkflow.CookieRotationAutoRenewed") +
                  Shiro.Services.LocalizationService.Get("AccountWorkflow.CategorizedTabsNotification")
                : Shiro.Services.LocalizationService.Get("AccountWorkflow.SessionCheckResultHeader") +
                  Shiro.Services.LocalizationService.Format("AccountWorkflow.TotalAccountsSummary", total) +
                  Shiro.Services.LocalizationService.Format("AccountWorkflow.ReadyAccountsSummary", valid) +
                  Shiro.Services.LocalizationService.Get("AccountWorkflow.ZeroExpiredSessions") +
                  Shiro.Services.LocalizationService.Get("AccountWorkflow.ZeroBannedAccounts") +
                  Shiro.Services.LocalizationService.Get("AccountWorkflow.AllAccountsValidAndRotated");

            if (expired > 0 || banned > 0)
                AppServices.Dialogs.ShowWarning(msg, Shiro.Services.LocalizationService.Get("AccountWorkflow.SessionCheckDialogTitle"));
            else
                AppServices.Dialogs.ShowInfo(msg, Shiro.Services.LocalizationService.Get("AccountWorkflow.SessionCheckDialogTitle"));
        }

        public async Task ReLoginWithCookieAsync(
            AccountViewModel accountVm,
            MainViewModel viewModel,
            Action<string> statusCallback)
        {
            if (accountVm?.Model == null) return;
            var acc = accountVm;

            string? cookie = await AppDispatcher.InvokeAsync(() =>
                AppServices.Dialogs.ShowInputPrompt(
                    Shiro.Services.LocalizationService.Format("AccountWorkflow.EnterNewCookiePrompt", acc.Username),
                    Shiro.Services.LocalizationService.Format("AccountWorkflow.UpdateCookieDialogTitle", acc.Username),
                    string.Empty)).ConfigureAwait(false);

            if (!string.IsNullOrWhiteSpace(cookie) && cookie != "/UC")
            {
                var match = CookieCleanRegex().Match(cookie);
                string token = Account.SanitizeCookie(match.Success ? match.Value : cookie);

                string previousToken = acc.Model.SecurityToken;
                bool wasValid = acc.Model.Valid;
                string previousIssue = acc.Model.AccountStatusIssue;

                acc.Model.SecurityToken = token;
                statusCallback?.Invoke(Shiro.Services.LocalizationService.Format("AccountWorkflow.ValidatingNewSession", acc.Username));
                bool ok = await acc.Model.ValidateSessionAsync().ConfigureAwait(false);

                if (ok)
                {
                    acc.Model.LastUse = DateTime.Now;
                    acc.Model.CSRFToken = null;
                    acc.Model.TokenSet = DateTime.MinValue;
                    acc.Model.LastAuthTicketRateLimited = false;
                    await AppServices.AccountRepository.SaveAsync(true).ConfigureAwait(false);
                    viewModel?.UpdateAccountValidity(acc.Model, true);
                    statusCallback?.Invoke(Shiro.Services.LocalizationService.Format("AccountWorkflow.UpdateCookieSuccess", acc.Username));
                }
                else
                {
                    if (wasValid && !string.IsNullOrWhiteSpace(previousToken))
                    {
                        acc.Model.SecurityToken = previousToken;
                        acc.Model.Valid = true;
                        acc.Model.AccountStatusIssue = previousIssue;
                        viewModel?.UpdateAccountValidity(acc.Model, true);
                        statusCallback?.Invoke(Shiro.Services.LocalizationService.Format("AccountWorkflow.InvalidCookieRetainedOld", acc.Username));
                    }
                    else
                    {
                        await AppServices.AccountRepository.SaveAsync(true).ConfigureAwait(false);
                        viewModel?.UpdateAccountValidity(acc.Model, false);
                        statusCallback?.Invoke(Shiro.Services.LocalizationService.Format("AccountWorkflow.InvalidOrExpiredCookie", acc.Username));
                    }
                }
            }
        }

        public void SetAlias(AccountViewModel acc, Action<string> statusCallback)
        {
            if (acc?.Model == null) return;
            string current = acc.Model.Alias ?? string.Empty;
            string input = AppServices.Dialogs.ShowInputPrompt(
                Shiro.Services.LocalizationService.Format("AccountWorkflow.EnterAliasPrompt", acc.Username),
                Shiro.Services.LocalizationService.Get("AccountWorkflow.ChangeAliasTitle"),
                current);

            if (input != null && input != "/UC")
            {
                string nextAlias = string.IsNullOrWhiteSpace(input) ? string.Empty : input.Trim();
                acc.Alias = nextAlias;
                AppServices.AccountRepository.QueueDeferredSave();
                statusCallback?.Invoke(Shiro.Services.LocalizationService.Format("AccountWorkflow.AliasChanged", acc.Username, acc.Alias));
            }
        }

        public void SetGroup(AccountViewModel acc, MainViewModel viewModel, Action<string> statusCallback)
        {
            if (acc?.Model == null) return;
            string currentGroup = !string.IsNullOrWhiteSpace(acc.Model.Group) ? acc.Model.Group : "Default";
            string groupName = AppServices.Dialogs.ShowInputPrompt(
                Shiro.Services.LocalizationService.Format("AccountWorkflow.EnterGroupPrompt", acc.Username),
                Shiro.Services.LocalizationService.Get("AccountWorkflow.AssignGroupTitle"),
                currentGroup);

            if (groupName != null && groupName != "/UC")
            {
                groupName = string.IsNullOrWhiteSpace(groupName) ? "Default" : groupName.Trim();
                acc.Group = groupName;
                AppServices.AccountRepository.QueueDeferredSave();
                viewModel?.RebuildGroupList();
                viewModel?.ApplyFilter();
                statusCallback?.Invoke(Shiro.Services.LocalizationService.Format("AccountWorkflow.MovedToGroup", acc.Username, groupName));
            }
        }

        public void SetDescription(AccountViewModel acc, Action<string> statusCallback)
        {
            if (acc?.Model == null) return;
            string current = acc.Model.Description ?? string.Empty;
            string newDesc = AppServices.Dialogs.ShowInputPrompt(
                Shiro.Services.LocalizationService.Format("AccountWorkflow.EnterNotesPrompt", acc.Username),
                Shiro.Services.LocalizationService.Get("AccountWorkflow.ChangeNotesTitle"),
                current);

            if (newDesc != null && newDesc != "/UC")
            {
                acc.Description = newDesc;
                AppServices.AccountRepository.QueueDeferredSave();
                statusCallback?.Invoke(Shiro.Services.LocalizationService.Format("AccountWorkflow.NotesUpdated", acc.Username));
            }
        }

        public void TogglePin(AccountViewModel acc, MainViewModel viewModel, Action<string> statusCallback)
        {
            if (acc?.Model == null) return;
            acc.Pinned = !acc.Pinned;
            AppServices.AccountRepository.QueueDeferredSave();
            viewModel?.NotifyCounts();
            viewModel?.ApplyFilter();
            statusCallback?.Invoke(acc.Pinned ? Shiro.Services.LocalizationService.Format("AccountWorkflow.PinnedAccount", acc.Username) : Shiro.Services.LocalizationService.Format("AccountWorkflow.UnpinnedAccount", acc.Username));
        }

        public void CopyUsername(AccountViewModel acc, Action<string> statusCallback)
        {
            if (acc?.Model == null || string.IsNullOrEmpty(acc.Model.Username)) return;
            ClipboardHelper.SetClipboardTextSafe(acc.Model.Username);
            statusCallback?.Invoke(Shiro.Services.LocalizationService.Format("AccountWorkflow.CopiedUsername", acc.Model.Username));
        }

        public void CopyUserPass(AccountViewModel acc, Action<string> statusCallback)
        {
            if (acc?.Model != null)
            {
                ClipboardHelper.SetClipboardTextSafe($"{acc.Model.Username}:{(acc.Model.Password ?? string.Empty)}");
                statusCallback?.Invoke(Shiro.Services.LocalizationService.Format("AccountWorkflow.CopiedUserPass", acc.Model.Username));
            }
        }

        public void CopyUserId(AccountViewModel acc, Action<string> statusCallback)
        {
            if (acc?.Model == null || acc.Model.UserID <= 0) return;
            ClipboardHelper.SetClipboardTextSafe(acc.Model.UserID.ToString());
            statusCallback?.Invoke(Shiro.Services.LocalizationService.Format("AccountWorkflow.CopiedUserId", acc.Model.UserID));
        }

        public void CopyProfileUrl(AccountViewModel acc, Action<string> statusCallback)
        {
            if (acc?.Model == null || acc.Model.UserID <= 0) return;
            ClipboardHelper.SetClipboardTextSafe($"https://www.roblox.com/users/{acc.Model.UserID}/profile");
            statusCallback?.Invoke(Shiro.Services.LocalizationService.Format("AccountWorkflow.CopiedProfileLink", acc.Username));
        }

        public void CopySecurityToken(AccountViewModel acc, Action<string> statusCallback)
        {
            if (acc?.Model == null || string.IsNullOrEmpty(acc.Model.SecurityToken)) return;
            ClipboardHelper.SetClipboardTextSafe(acc.Model.SecurityToken);
            statusCallback?.Invoke(Shiro.Services.LocalizationService.Format("AccountWorkflow.CopiedSecurityToken", acc.Username));
        }

        public void RemoveAccount(AccountViewModel acc, MainViewModel viewModel, Action<string> statusCallback)
        {
            if (acc?.Model == null) return;
            bool confirmed = AppServices.Dialogs.ShowConfirmation(
                Shiro.Services.LocalizationService.Format("AccountWorkflow.DeleteAccountPrompt", acc.Username),
                Shiro.Services.LocalizationService.Get("AccountWorkflow.ConfirmDeleteTitle"));

            if (confirmed)
            {
                AppServices.AccountRepository.RemoveRange(new[] { acc.Model });
                viewModel?.Accounts.Remove(acc);
                viewModel?.NotifyCounts();
                viewModel?.RebuildGroupList();
                viewModel?.ApplyFilter();
                statusCallback?.Invoke(Shiro.Services.LocalizationService.Format("AccountWorkflow.AccountDeleted", acc.Username));
            }
        }

        public void SetGroupSelected(MainViewModel viewModel, Action<string> statusCallback)
        {
            if (viewModel == null) return;
            var selectedVms = viewModel.GetSelectedAccountViewModels();
            if (selectedVms.Count == 0)
            {
                statusCallback?.Invoke(Shiro.Services.LocalizationService.Get("AccountWorkflow.SelectAccountBeforeGroup"));
                return;
            }

            string currentGroup = selectedVms.Count == 1 && !string.IsNullOrWhiteSpace(selectedVms[0].Group) ? selectedVms[0].Group : "Default";
            string prompt = selectedVms.Count == 1 ? Shiro.Services.LocalizationService.Format("AccountWorkflow.EnterGroupPrompt", selectedVms[0].Username) : Shiro.Services.LocalizationService.Format("AccountWorkflow.BatchGroupPrompt", selectedVms.Count);
            string groupName = AppServices.Dialogs.ShowInputPrompt(prompt, Shiro.Services.LocalizationService.Get("AccountWorkflow.BatchGroupTitle"), currentGroup);

            if (groupName != null && groupName != "/UC")
            {
                groupName = string.IsNullOrWhiteSpace(groupName) ? "Default" : groupName.Trim();
                foreach (var vm in selectedVms)
                {
                    vm.Group = groupName;
                }
                AppServices.AccountRepository.QueueDeferredSave();
                viewModel.RebuildGroupList();
                viewModel.ApplyFilter();
                statusCallback?.Invoke(Shiro.Services.LocalizationService.Format("AccountWorkflow.BatchMovedToGroup", selectedVms.Count, groupName));
            }
        }

        public void SavePlaceIdSelected(MainViewModel viewModel, Action<string> statusCallback)
        {
            if (viewModel == null) return;
            var targets = viewModel.GetSelectedAccounts();
            if (targets.Count == 0)
            {
                statusCallback?.Invoke(Shiro.Services.LocalizationService.Get("AccountWorkflow.SelectAccountBeforeSavePlace"));
                return;
            }

            long parsedPid = AccountLauncherService.ParsePlaceId(viewModel.PlaceId);
            if (parsedPid <= 0)
            {
                statusCallback?.Invoke(Shiro.Services.LocalizationService.Get("AccountWorkflow.EnterValidPlaceBeforeSave"));
                return;
            }

            string pid = parsedPid.ToString(System.Globalization.CultureInfo.InvariantCulture);
            foreach (var a in targets)
            {
                a.SetField("SavedPlaceId", pid);
            }
            AppServices.AccountRepository.QueueDeferredSave();
            statusCallback?.Invoke(Shiro.Services.LocalizationService.Format("AccountWorkflow.PlaceIdSaved", pid, targets.Count));
        }

        public void CopyUserPassSelected(MainViewModel viewModel, Action<string> statusCallback)
        {
            if (viewModel == null) return;
            var targets = viewModel.GetSelectedAccounts();
            if (targets.Count == 0)
            {
                statusCallback?.Invoke(Shiro.Services.LocalizationService.Get("AccountWorkflow.SelectAccountBeforeCopyPass"));
                return;
            }

            var lines = targets.Select(a => $"{a.Username}:{(a.Password ?? string.Empty)}").ToList();
            ClipboardHelper.SetClipboardTextSafe(string.Join(Environment.NewLine, lines));
            statusCallback?.Invoke(Shiro.Services.LocalizationService.Format("AccountWorkflow.BatchCopiedUserPass", lines.Count));
        }

        public void RemoveSelectedAccounts(MainViewModel viewModel, Action<string> statusCallback)
        {
            if (viewModel == null) return;
            var selectedVms = viewModel.GetSelectedAccountViewModels();
            if (selectedVms.Count == 0)
            {
                statusCallback?.Invoke(Shiro.Services.LocalizationService.Get("AccountWorkflow.SelectAccountBeforeDelete"));
                return;
            }

            bool confirmed = AppServices.Dialogs.ShowConfirmation(
                Shiro.Services.LocalizationService.Format("AccountWorkflow.BatchDeletePrompt", selectedVms.Count),
                Shiro.Services.LocalizationService.Get("AccountWorkflow.BatchDeleteTitle"));

            if (confirmed)
            {
                var models = selectedVms.Where(v => v.Model != null).Select(v => v.Model).ToList();
                int count = AppServices.AccountRepository.RemoveRange(models!);
                foreach (var vm in selectedVms)
                {
                    viewModel.Accounts.Remove(vm);
                }
                viewModel.NotifyCounts();
                viewModel.RebuildGroupList();
                viewModel.ApplyFilter();
                statusCallback?.Invoke(Shiro.Services.LocalizationService.Format("AccountWorkflow.BatchAccountsDeleted", count));
            }
        }

        public void ValidateSessionSelected(
            IList<AccountViewModel> list,
            MainViewModel viewModel,
            Action<string> statusCallback)
        {
            if (list == null || list.Count == 0) return;
            var snapshot = list.Where(item => item?.Model != null).ToList();
            if (snapshot.Count == 0) return;

            _ = Task.Run(async () =>
            {
                int checkedCount = 0;
                int total = snapshot.Count;
                try
                {
                    var parallelOptions = new ParallelOptions
                    {
                        MaxDegreeOfParallelism = 12,
                        CancellationToken = CancellationToken.None
                    };

                    await Parallel.ForEachAsync(snapshot, parallelOptions, async (item, ct) =>
                    {
                        try
                        {
                            bool ok = await RobloxRateLimitCoordinator.Instance.ExecuteWithPacingAsync(
                                RateLimitCategory.SessionCheck,
                                () => item.Model.ValidateSessionAsync(ct),
                                minIntervalMs: 0,
                                cancellationToken: ct).ConfigureAwait(false);

                            int c = Interlocked.Increment(ref checkedCount);
                            viewModel?.UpdateAccountValidity(item.Model, ok, refreshFilterAndCounts: false);
                            AppDispatcher.Post(() => viewModel?.NotifyCounts());
                            string itemState = ok ? Shiro.Services.LocalizationService.Get("AccountWorkflow.StatusValid") : (item.Model.IsBannedOrLocked ? item.Model.AccountStatusIssue : Shiro.Services.LocalizationService.Get("KPI_Expired"));
                            statusCallback?.Invoke(Shiro.Services.LocalizationService.Format("AccountWorkflow.CheckingProgressSimple", c, total, item.Username, itemState));
                        }
                        catch (Exception ex)
                        {
                            _logger.Warn($"[OnValidateSessionSelectedRequested] {item.Username}: {ex.Message}");
                            int c = Interlocked.Increment(ref checkedCount);
                        }
                    }).ConfigureAwait(false);
                    await AppServices.AccountRepository.SaveAsync(true).ConfigureAwait(false);
                    AppDispatcher.Post(() =>
                    {
                        viewModel?.NotifyCounts();
                        viewModel?.ApplyFilter();
                    });
                    statusCallback?.Invoke(Shiro.Services.LocalizationService.Format("AccountWorkflow.SessionCheckCompleted", checkedCount));
                }
                catch (Exception ex)
                {
                    _logger.Error($"[OnValidateSessionSelectedRequested] {ex.Message}");
                    statusCallback?.Invoke(Shiro.Services.LocalizationService.Format("AccountWorkflow.SessionCheckError", ex.Message));
                }
            });
        }
    }
}
