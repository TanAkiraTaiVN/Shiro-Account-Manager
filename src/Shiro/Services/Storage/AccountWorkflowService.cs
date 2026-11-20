#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace Shiro.Services
{
    public partial class AccountWorkflowService
    {
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
            IAccountWorkflowHost? viewModel,
            Action<string>? statusCallback)
        {
            var list = AppServices.AccountRepository.GetAll();
            if (list == null || list.Count == 0) return;

            statusCallback?.Invoke(LocalizationService.Get("AccountWorkflow.CheckingAllSessions"));

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

                    string state = isValid ? LocalizationService.Get("AccountWorkflow.StatusValid") : (acc.IsBannedOrLocked ? acc.AccountStatusIssue : LocalizationService.Get("KPI_Expired"));
                    statusCallback?.Invoke(LocalizationService.Format("AccountWorkflow.CheckingProgressWithExpired", c, total, StreamerModeService.GetDisplayUsername(acc.Username), state, expiredCount));
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
                viewModel?.RebuildLookupIndex();
                viewModel?.NotifyCounts();
                viewModel?.ApplyFilter();
            });

            statusCallback?.Invoke((expired > 0 || banned > 0)
                ? LocalizationService.Format("AccountWorkflow.ScanCompletedWithBans", valid, expired, banned)
                : LocalizationService.Format("AccountWorkflow.ScanCompletedAllHealthy", total));

            string msg = (expired > 0 || banned > 0)
                ? LocalizationService.Get("AccountWorkflow.SessionCheckResultHeader") +
                  LocalizationService.Format("AccountWorkflow.TotalAccountsSummary", total) +
                  LocalizationService.Format("AccountWorkflow.ReadyAccountsSummary", valid) +
                  LocalizationService.Format("AccountWorkflow.ExpiredSessionsSummary", expired) +
                  LocalizationService.Format("AccountWorkflow.BannedAccountsSummary", banned) +
                  (banned > 0
                      ? LocalizationService.Get("AccountWorkflow.BannedAccountsListHeader") +
                        string.Join("\n", bannedAccounts.Take(5).Select(a => $"• {StreamerModeService.GetDisplayUsername(a.Username)}: {a.AccountStatusIssue}")) +
                        (bannedAccounts.Count > 5 ? LocalizationService.Format("AccountWorkflow.MoreAccountsTruncated", bannedAccounts.Count - 5) : "") + "\n\n"
                      : "") +
                  (expired == total
                      ? LocalizationService.Get("AccountWorkflow.SpecialNoteAllExpired") +
                        LocalizationService.Get("AccountWorkflow.IpChangedWarning")
                      : "") +
                  LocalizationService.Get("AccountWorkflow.CookieRotationAutoRenewed") +
                  LocalizationService.Get("AccountWorkflow.CategorizedTabsNotification")
                : LocalizationService.Get("AccountWorkflow.SessionCheckResultHeader") +
                  LocalizationService.Format("AccountWorkflow.TotalAccountsSummary", total) +
                  LocalizationService.Format("AccountWorkflow.ReadyAccountsSummary", valid) +
                  LocalizationService.Get("AccountWorkflow.ZeroExpiredSessions") +
                  LocalizationService.Get("AccountWorkflow.ZeroBannedAccounts") +
                  LocalizationService.Get("AccountWorkflow.AllAccountsValidAndRotated");

            if (expired > 0 || banned > 0)
                AppServices.Dialogs.ShowWarning(msg, LocalizationService.Get("AccountWorkflow.SessionCheckDialogTitle"));
            else
                AppServices.Dialogs.ShowInfo(msg, LocalizationService.Get("AccountWorkflow.SessionCheckDialogTitle"));
        }

        public async Task ReLoginWithCookieAsync(
            IAccountWorkflowItem? accountVm,
            IAccountWorkflowHost? viewModel,
            Action<string>? statusCallback)
        {
            if (accountVm?.Model == null) return;
            var acc = accountVm;

            string? cookie = await AppDispatcher.InvokeAsync(() =>
                AppServices.Dialogs.ShowInputPrompt(
                    LocalizationService.Format("AccountWorkflow.EnterNewCookiePrompt", acc.ShownUsername),
                    LocalizationService.Format("AccountWorkflow.UpdateCookieDialogTitle", acc.ShownUsername),
                    string.Empty)).ConfigureAwait(false);

            if (!string.IsNullOrWhiteSpace(cookie) && cookie != "/UC")
            {
                var match = CookieCleanRegex().Match(cookie);
                string token = Account.SanitizeCookie(match.Success ? match.Value : cookie);

                string previousToken = acc.Model.SecurityToken;
                bool wasValid = acc.Model.Valid;
                string previousIssue = acc.Model.AccountStatusIssue;
                long previousUserId = acc.Model.UserID;
                string previousUsername = acc.Model.Username;

                acc.Model.SecurityToken = token;
                statusCallback?.Invoke(LocalizationService.Format("AccountWorkflow.ValidatingNewSession", acc.ShownUsername));
                bool ok = await acc.Model.ValidateSessionAsync().ConfigureAwait(false);

                if (ok)
                {
                    acc.Model.LastUse = DateTime.Now;
                    acc.Model.CSRFToken = null;
                    acc.Model.TokenSet = DateTime.MinValue;
                    acc.Model.LastAuthTicketRateLimited = false;
                    await AppServices.AccountRepository.SaveAsync(true).ConfigureAwait(false);
                    viewModel?.RebuildLookupIndex();
                    viewModel?.UpdateAccountValidity(acc.Model, true);
                    AppDispatcher.Post(() => acc.NotifyAllPropertiesChanged());
                    statusCallback?.Invoke(LocalizationService.Format("AccountWorkflow.UpdateCookieSuccess", acc.ShownUsername));
                }
                else
                {
                    acc.Model.SecurityToken = previousToken;
                    acc.Model.UserID = previousUserId;
                    acc.Model.Username = previousUsername;
                    acc.Model.AccountStatusIssue = previousIssue;

                    if (wasValid && !string.IsNullOrWhiteSpace(previousToken))
                    {
                        acc.Model.Valid = true;
                        viewModel?.UpdateAccountValidity(acc.Model, true);
                        statusCallback?.Invoke(LocalizationService.Format("AccountWorkflow.InvalidCookieRetainedOld", acc.ShownUsername));
                    }
                    else
                    {
                        acc.Model.Valid = false;
                        viewModel?.UpdateAccountValidity(acc.Model, false);
                        statusCallback?.Invoke(LocalizationService.Format("AccountWorkflow.InvalidOrExpiredCookie", acc.ShownUsername));
                    }
                }
            }
        }

        public void SetAlias(IAccountWorkflowItem? acc, Action<string>? statusCallback)
        {
            if (acc?.Model == null) return;
            string current = acc.Model.Alias ?? string.Empty;
            string input = AppServices.Dialogs.ShowInputPrompt(
                LocalizationService.Format("AccountWorkflow.EnterAliasPrompt", acc.ShownUsername),
                LocalizationService.Get("AccountWorkflow.ChangeAliasTitle"),
                current);

            if (input != null && input != "/UC")
            {
                string nextAlias = string.IsNullOrWhiteSpace(input) ? string.Empty : input.Trim();
                acc.Alias = nextAlias;
                AppServices.AccountRepository.QueueDeferredSave();
                statusCallback?.Invoke(LocalizationService.Format("AccountWorkflow.AliasChanged", acc.ShownUsername, acc.ShownAlias));
            }
        }

        public void SetGroup(IAccountWorkflowItem? acc, IAccountWorkflowHost? viewModel, Action<string>? statusCallback)
        {
            if (acc?.Model == null) return;
            string currentGroup = !string.IsNullOrWhiteSpace(acc.Model.Group) ? acc.Model.Group : "Default";
            string groupName = AppServices.Dialogs.ShowInputPrompt(
                LocalizationService.Format("AccountWorkflow.EnterGroupPrompt", acc.ShownUsername),
                LocalizationService.Get("AccountWorkflow.AssignGroupTitle"),
                currentGroup);

            if (groupName != null && groupName != "/UC")
            {
                groupName = string.IsNullOrWhiteSpace(groupName) ? "Default" : groupName.Trim();
                acc.Group = groupName;
                AppServices.AccountRepository.QueueDeferredSave();
                viewModel?.RebuildGroupList();
                viewModel?.ApplyFilter();
                statusCallback?.Invoke(LocalizationService.Format("AccountWorkflow.MovedToGroup", acc.ShownUsername, groupName));
            }
        }

        public void SetDescription(IAccountWorkflowItem? acc, Action<string>? statusCallback)
        {
            if (acc?.Model == null) return;
            string current = acc.Model.Description ?? string.Empty;
            string newDesc = AppServices.Dialogs.ShowInputPrompt(
                LocalizationService.Format("AccountWorkflow.EnterNotesPrompt", acc.ShownUsername),
                LocalizationService.Get("AccountWorkflow.ChangeNotesTitle"),
                current);

            if (newDesc != null && newDesc != "/UC")
            {
                acc.Description = newDesc;
                AppServices.AccountRepository.QueueDeferredSave();
                statusCallback?.Invoke(LocalizationService.Format("AccountWorkflow.NotesUpdated", acc.ShownUsername));
            }
        }

        public void TogglePin(IAccountWorkflowItem? acc, IAccountWorkflowHost? viewModel, Action<string>? statusCallback)
        {
            if (acc?.Model == null) return;
            acc.Pinned = !acc.Pinned;
            AppServices.AccountRepository.QueueDeferredSave();
            viewModel?.NotifyCounts();
            viewModel?.ApplyFilter();
            statusCallback?.Invoke(acc.Pinned ? LocalizationService.Format("AccountWorkflow.PinnedAccount", acc.ShownUsername) : LocalizationService.Format("AccountWorkflow.UnpinnedAccount", acc.ShownUsername));
        }

        public void CopyUsername(IAccountWorkflowItem? acc, Action<string>? statusCallback)
        {
            if (acc?.Model == null || string.IsNullOrEmpty(acc.Model.Username)) return;
            ClipboardHelper.SetClipboardTextSafe(acc.Model.Username);
            statusCallback?.Invoke(LocalizationService.Format("AccountWorkflow.CopiedUsername", acc.ShownUsername));
        }

        public void CopyUserPass(IAccountWorkflowItem? acc, Action<string>? statusCallback)
        {
            if (acc?.Model != null)
            {
                ClipboardHelper.SetClipboardTextSafe($"{acc.Model.Username}:{(acc.Model.Password ?? string.Empty)}");
                statusCallback?.Invoke(LocalizationService.Format("AccountWorkflow.CopiedUserPass", acc.ShownUsername));
            }
        }

        public void CopyUserId(IAccountWorkflowItem? acc, Action<string>? statusCallback)
        {
            if (acc?.Model == null || acc.Model.UserID <= 0) return;
            ClipboardHelper.SetClipboardTextSafe(acc.Model.UserID.ToString());
            statusCallback?.Invoke(LocalizationService.Format("AccountWorkflow.CopiedUserId", acc.ShownUserId));
        }

        public void CopyProfileUrl(IAccountWorkflowItem? acc, Action<string>? statusCallback)
        {
            if (acc?.Model == null || acc.Model.UserID <= 0) return;
            ClipboardHelper.SetClipboardTextSafe($"https://www.roblox.com/users/{acc.Model.UserID}/profile");
            statusCallback?.Invoke(LocalizationService.Format("AccountWorkflow.CopiedProfileLink", acc.ShownUsername));
        }

        public void CopySecurityToken(IAccountWorkflowItem? acc, Action<string>? statusCallback)
        {
            if (acc?.Model == null || string.IsNullOrEmpty(acc.Model.SecurityToken)) return;
            ClipboardHelper.SetClipboardTextSafe(acc.Model.SecurityToken);
            statusCallback?.Invoke(LocalizationService.Format("AccountWorkflow.CopiedSecurityToken", acc.ShownUsername));
        }

        public void RemoveAccount(IAccountWorkflowItem? acc, IAccountWorkflowHost? viewModel, Action<string>? statusCallback)
        {
            if (acc?.Model == null) return;
            bool confirmed = AppServices.Dialogs.ShowConfirmation(
                LocalizationService.Format("AccountWorkflow.DeleteAccountPrompt", acc.ShownUsername),
                LocalizationService.Get("AccountWorkflow.ConfirmDeleteTitle"));

            if (confirmed)
            {
                AppServices.AccountRepository.RemoveRange(new[] { acc.Model });
                viewModel?.RemoveWorkflowItems(acc);
                viewModel?.NotifyCounts();
                viewModel?.RebuildGroupList();
                viewModel?.ApplyFilter();
                statusCallback?.Invoke(LocalizationService.Format("AccountWorkflow.AccountDeleted", acc.ShownUsername));
            }
        }

        public void SetGroupSelected(IAccountWorkflowHost? viewModel, Action<string>? statusCallback)
        {
            if (viewModel == null) return;
            var selectedVms = viewModel.GetSelectedWorkflowItems();
            if (selectedVms.Count == 0)
            {
                statusCallback?.Invoke(LocalizationService.Get("AccountWorkflow.SelectAccountBeforeGroup"));
                return;
            }

            string currentGroup = selectedVms.Count == 1 && !string.IsNullOrWhiteSpace(selectedVms[0].Group) ? selectedVms[0].Group : "Default";
            string prompt = selectedVms.Count == 1 ? LocalizationService.Format("AccountWorkflow.EnterGroupPrompt", selectedVms[0].ShownUsername) : LocalizationService.Format("AccountWorkflow.BatchGroupPrompt", selectedVms.Count);
            string groupName = AppServices.Dialogs.ShowInputPrompt(prompt, LocalizationService.Get("AccountWorkflow.BatchGroupTitle"), currentGroup);

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
                statusCallback?.Invoke(LocalizationService.Format("AccountWorkflow.BatchMovedToGroup", selectedVms.Count, groupName));
            }
        }

        public void SavePlaceIdSelected(IAccountWorkflowHost? viewModel, Action<string>? statusCallback)
        {
            if (viewModel == null) return;
            var targets = viewModel.GetSelectedAccounts();
            if (targets.Count == 0)
            {
                statusCallback?.Invoke(LocalizationService.Get("AccountWorkflow.SelectAccountBeforeSavePlace"));
                return;
            }

            long parsedPid = AccountLauncherService.ParsePlaceId(viewModel.PlaceId);
            if (parsedPid <= 0)
            {
                statusCallback?.Invoke(LocalizationService.Get("AccountWorkflow.EnterValidPlaceBeforeSave"));
                return;
            }

            string pid = parsedPid.ToString(System.Globalization.CultureInfo.InvariantCulture);
            foreach (var a in targets)
            {
                a.SetField("SavedPlaceId", pid);
            }
            AppServices.AccountRepository.QueueDeferredSave();
            statusCallback?.Invoke(LocalizationService.Format("AccountWorkflow.PlaceIdSaved", pid, targets.Count));
        }

        public void CopyUserPassSelected(IAccountWorkflowHost? viewModel, Action<string>? statusCallback)
        {
            if (viewModel == null) return;
            var targets = viewModel.GetSelectedAccounts();
            if (targets.Count == 0)
            {
                statusCallback?.Invoke(LocalizationService.Get("AccountWorkflow.SelectAccountBeforeCopyPass"));
                return;
            }

            var lines = targets.Select(a => $"{a.Username}:{(a.Password ?? string.Empty)}").ToList();
            ClipboardHelper.SetClipboardTextSafe(string.Join(Environment.NewLine, lines));
            statusCallback?.Invoke(LocalizationService.Format("AccountWorkflow.BatchCopiedUserPass", lines.Count));
        }

        public void RemoveSelectedAccounts(IAccountWorkflowHost? viewModel, Action<string>? statusCallback)
        {
            if (viewModel == null) return;
            var selectedVms = viewModel.GetSelectedWorkflowItems();
            if (selectedVms.Count == 0)
            {
                statusCallback?.Invoke(LocalizationService.Get("AccountWorkflow.SelectAccountBeforeDelete"));
                return;
            }

            bool confirmed = AppServices.Dialogs.ShowConfirmation(
                LocalizationService.Format("AccountWorkflow.BatchDeletePrompt", selectedVms.Count),
                LocalizationService.Get("AccountWorkflow.BatchDeleteTitle"));

            if (confirmed)
            {
                var models = selectedVms.Where(v => v.Model != null).Select(v => v.Model).ToList();
                int count = AppServices.AccountRepository.RemoveRange(models!);
                viewModel.RemoveWorkflowItems(selectedVms.ToArray());
                viewModel.NotifyCounts();
                viewModel.RebuildGroupList();
                viewModel.ApplyFilter();
                statusCallback?.Invoke(LocalizationService.Format("AccountWorkflow.BatchAccountsDeleted", count));
            }
        }

        public void ValidateSessionSelected(
            IEnumerable<IAccountWorkflowItem>? list,
            IAccountWorkflowHost? viewModel,
            Action<string>? statusCallback)
        {
            if (list == null) return;
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
                            string itemState = ok ? LocalizationService.Get("AccountWorkflow.StatusValid") : (item.Model.IsBannedOrLocked ? item.Model.AccountStatusIssue : LocalizationService.Get("KPI_Expired"));
                            statusCallback?.Invoke(LocalizationService.Format("AccountWorkflow.CheckingProgressSimple", c, total, item.ShownUsername, itemState));
                        }
                        catch (Exception ex)
                        {
                            _logger.Warn($"[ValidateSessionSelected] {item.Username}: {ex.Message}");
                            Interlocked.Increment(ref checkedCount);
                        }
                    }).ConfigureAwait(false);
                    await AppServices.AccountRepository.SaveAsync(true).ConfigureAwait(false);
                    AppDispatcher.Post(() =>
                    {
                        viewModel?.RebuildLookupIndex();
                        viewModel?.NotifyCounts();
                        viewModel?.ApplyFilter();
                    });
                    statusCallback?.Invoke(LocalizationService.Format("AccountWorkflow.SessionCheckCompleted", checkedCount));
                }
                catch (Exception ex)
                {
                    _logger.Error($"[ValidateSessionSelected] {ex.Message}");
                    statusCallback?.Invoke(LocalizationService.Format("AccountWorkflow.SessionCheckError", ex.Message));
                }
            });
        }
    }
}
