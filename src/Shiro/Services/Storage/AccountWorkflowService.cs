#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Shiro.Classes;
using Shiro.ViewModels;

namespace Shiro.Services
{
    public partial class AccountWorkflowService
    {
        [GeneratedRegex(@"_\|WARNING:-[^\r\n""]+")]
        private static partial Regex CookieRegex();

        [GeneratedRegex(@"_\|WARNING:-[^;\r\n""]+")]
        private static partial Regex CookieCleanRegex();

        private static readonly Lazy<AccountWorkflowService> _defaultInstance = new(() => new AccountWorkflowService());
        public static AccountWorkflowService Default => _defaultInstance.Value;

        public async Task CheckAllSessionsAsync(
            MainViewModel viewModel,
            Action<string> statusCallback)
        {
            var list = AppServices.AccountRepository.GetAll();
            if (list == null || list.Count == 0) return;

            statusCallback?.Invoke(Shiro.Services.LocalizationService.Get("Auto_AccountWorkflowS_274"));

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

                    string state = isValid ? Shiro.Services.LocalizationService.Get("Auto_AccountWorkflowS_275") : (acc.IsBannedOrLocked ? acc.AccountStatusIssue : Shiro.Services.LocalizationService.Get("KPI_Expired"));
                    statusCallback?.Invoke(Shiro.Services.LocalizationService.Format("Auto_AccountWorkflowS_276", c, total, acc.Username, state, expiredCount));
                    viewModel?.UpdateAccountValidity(acc, isValid, refreshFilterAndCounts: false);
                    AppDispatcher.Post(() => viewModel?.NotifyCounts());
                }
                catch (Exception ex)
                {
                    Program.Logger?.Warn($"[CheckSessions] {acc.Username}: {ex.Message}");
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
                ? Shiro.Services.LocalizationService.Format("Auto_AccountWorkflowS_277", valid, expired, banned)
                : Shiro.Services.LocalizationService.Format("Auto_AccountWorkflowS_278", total));

            string msg = (expired > 0 || banned > 0)
                ? Shiro.Services.LocalizationService.Get("Auto_AccountWorkflowS_279") +
                  Shiro.Services.LocalizationService.Format("Auto_AccountWorkflowS_280", total) +
                  Shiro.Services.LocalizationService.Format("Auto_AccountWorkflowS_281", valid) +
                  Shiro.Services.LocalizationService.Format("Auto_AccountWorkflowS_282", expired) +
                  Shiro.Services.LocalizationService.Format("Auto_AccountWorkflowS_283", banned) +
                  (banned > 0
                      ? Shiro.Services.LocalizationService.Get("Auto_AccountWorkflowS_284") +
                        string.Join("\n", bannedAccounts.Take(5).Select(a => $"• {a.Username}: {a.AccountStatusIssue}")) +
                        (bannedAccounts.Count > 5 ? Shiro.Services.LocalizationService.Format("Auto_AccountWorkflowS_285", bannedAccounts.Count - 5) : "") + "\n\n"
                      : "") +
                  (expired == total
                      ? Shiro.Services.LocalizationService.Get("Auto_AccountWorkflowS_286") +
                        Shiro.Services.LocalizationService.Get("Auto_AccountWorkflowS_287")
                      : "") +
                  Shiro.Services.LocalizationService.Get("Auto_AccountWorkflowS_288") +
                  Shiro.Services.LocalizationService.Get("Auto_AccountWorkflowS_289")
                : Shiro.Services.LocalizationService.Get("Auto_AccountWorkflowS_279") +
                  Shiro.Services.LocalizationService.Format("Auto_AccountWorkflowS_280", total) +
                  Shiro.Services.LocalizationService.Format("Auto_AccountWorkflowS_281", valid) +
                  Shiro.Services.LocalizationService.Get("Auto_AccountWorkflowS_290") +
                  Shiro.Services.LocalizationService.Get("Auto_AccountWorkflowS_291") +
                  Shiro.Services.LocalizationService.Get("Auto_AccountWorkflowS_292");

            if (expired > 0 || banned > 0)
                AppServices.Dialogs.ShowWarning(msg, Shiro.Services.LocalizationService.Get("Auto_AccountWorkflowS_293"));
            else
                AppServices.Dialogs.ShowInfo(msg, Shiro.Services.LocalizationService.Get("Auto_AccountWorkflowS_293"));
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
                    Shiro.Services.LocalizationService.Format("Auto_AccountWorkflowS_294", acc.Username),
                    Shiro.Services.LocalizationService.Format("Auto_AccountWorkflowS_295", acc.Username),
                    string.Empty)).ConfigureAwait(false);

            if (!string.IsNullOrWhiteSpace(cookie) && cookie != "/UC")
            {
                var match = CookieCleanRegex().Match(cookie);
                string token = Account.SanitizeCookie(match.Success ? match.Value : cookie);

                string previousToken = acc.Model.SecurityToken;
                bool wasValid = acc.Model.Valid;

                acc.Model.SecurityToken = token;
                statusCallback?.Invoke(Shiro.Services.LocalizationService.Format("Auto_AccountWorkflowS_296", acc.Username));
                bool ok = await acc.Model.ValidateSessionAsync().ConfigureAwait(false);

                if (ok)
                {
                    acc.Model.LastUse = DateTime.Now;
                    acc.Model.CSRFToken = null;
                    acc.Model.TokenSet = DateTime.MinValue;
                    acc.Model.LastAuthTicketRateLimited = false;
                    await AppServices.AccountRepository.SaveAsync(true).ConfigureAwait(false);
                    viewModel?.UpdateAccountValidity(acc.Model, true);
                    statusCallback?.Invoke(Shiro.Services.LocalizationService.Format("Auto_AccountWorkflowS_297", acc.Username));
                }
                else
                {
                    if (wasValid && !string.IsNullOrWhiteSpace(previousToken))
                    {
                        acc.Model.SecurityToken = previousToken;
                        acc.Model.Valid = true;
                        viewModel?.UpdateAccountValidity(acc.Model, true);
                        statusCallback?.Invoke(Shiro.Services.LocalizationService.Format("Auto_AccountWorkflowS_298", acc.Username));
                    }
                    else
                    {
                        await AppServices.AccountRepository.SaveAsync(true).ConfigureAwait(false);
                        viewModel?.UpdateAccountValidity(acc.Model, false);
                        statusCallback?.Invoke(Shiro.Services.LocalizationService.Format("Auto_AccountWorkflowS_299", acc.Username));
                    }
                }
            }
        }

        public void SetAlias(AccountViewModel acc, Action<string> statusCallback)
        {
            if (acc?.Model == null) return;
            string current = acc.Model.Alias ?? string.Empty;
            string input = AppServices.Dialogs.ShowInputPrompt(
                Shiro.Services.LocalizationService.Format("Auto_AccountWorkflowS_300", acc.Username),
                Shiro.Services.LocalizationService.Get("Auto_AccountWorkflowS_301"),
                current);

            if (input != null && input != "/UC")
            {
                acc.Model.Alias = string.IsNullOrWhiteSpace(input) ? string.Empty : input.Trim();
                acc.Alias = acc.Model.Alias;
                AppServices.AccountRepository.QueueDeferredSave();
                statusCallback?.Invoke(Shiro.Services.LocalizationService.Format("Auto_AccountWorkflowS_302", acc.Username, acc.Model.Alias));
            }
        }

        public void SetGroup(AccountViewModel acc, MainViewModel viewModel, Action<string> statusCallback)
        {
            if (acc?.Model == null) return;
            string currentGroup = !string.IsNullOrWhiteSpace(acc.Model.Group) ? acc.Model.Group : "Default";
            string groupName = AppServices.Dialogs.ShowInputPrompt(
                Shiro.Services.LocalizationService.Format("Auto_AccountWorkflowS_303", acc.Username),
                Shiro.Services.LocalizationService.Get("Auto_AccountWorkflowS_304"),
                currentGroup);

            if (groupName != null && groupName != "/UC")
            {
                groupName = string.IsNullOrWhiteSpace(groupName) ? "Default" : groupName.Trim();
                acc.Model.Group = groupName;
                acc.Group = groupName;
                AppServices.AccountRepository.QueueDeferredSave();
                viewModel?.RebuildGroupList();
                viewModel?.ApplyFilter();
                statusCallback?.Invoke(Shiro.Services.LocalizationService.Format("Auto_AccountWorkflowS_305", acc.Username, groupName));
            }
        }

        public void SetDescription(AccountViewModel acc, Action<string> statusCallback)
        {
            if (acc?.Model == null) return;
            string current = acc.Model.Description ?? string.Empty;
            string newDesc = AppServices.Dialogs.ShowInputPrompt(
                Shiro.Services.LocalizationService.Format("Auto_AccountWorkflowS_306", acc.Username),
                Shiro.Services.LocalizationService.Get("Auto_AccountWorkflowS_307"),
                current);

            if (newDesc != null && newDesc != "/UC")
            {
                acc.Model.Description = newDesc;
                acc.Description = newDesc;
                AppServices.AccountRepository.QueueDeferredSave();
                statusCallback?.Invoke(Shiro.Services.LocalizationService.Format("Auto_AccountWorkflowS_308", acc.Username));
            }
        }

        public void TogglePin(AccountViewModel acc, MainViewModel viewModel, Action<string> statusCallback)
        {
            if (acc?.Model == null) return;
            acc.Model.Pinned = !acc.Model.Pinned;
            acc.Pinned = acc.Model.Pinned;
            AppServices.AccountRepository.QueueDeferredSave();
            viewModel?.ApplyFilter();
            statusCallback?.Invoke(acc.Pinned ? Shiro.Services.LocalizationService.Format("Auto_AccountWorkflowS_310", acc.Username) : Shiro.Services.LocalizationService.Format("Auto_AccountWorkflowS_309", acc.Username));
        }

        public void CopyUsername(AccountViewModel acc, Action<string> statusCallback)
        {
            if (acc?.Model == null || string.IsNullOrEmpty(acc.Model.Username)) return;
            ClipboardHelper.SetClipboardTextSafe(acc.Model.Username);
            statusCallback?.Invoke(Shiro.Services.LocalizationService.Format("Auto_AccountWorkflowS_311", acc.Model.Username));
        }

        public void CopyUserPass(AccountViewModel acc, Action<string> statusCallback)
        {
            if (acc?.Model != null)
            {
                ClipboardHelper.SetClipboardTextSafe($"{acc.Model.Username}:{(acc.Model.Password ?? string.Empty)}");
                statusCallback?.Invoke(Shiro.Services.LocalizationService.Format("Auto_AccountWorkflowS_312", acc.Model.Username));
            }
        }

        public void CopyUserId(AccountViewModel acc, Action<string> statusCallback)
        {
            if (acc?.Model == null || acc.Model.UserID <= 0) return;
            ClipboardHelper.SetClipboardTextSafe(acc.Model.UserID.ToString());
            statusCallback?.Invoke(Shiro.Services.LocalizationService.Format("Auto_AccountWorkflowS_313", acc.Model.UserID));
        }

        public void CopyProfileUrl(AccountViewModel acc, Action<string> statusCallback)
        {
            if (acc?.Model == null || acc.Model.UserID <= 0) return;
            ClipboardHelper.SetClipboardTextSafe($"https://www.roblox.com/users/{acc.Model.UserID}/profile");
            statusCallback?.Invoke(Shiro.Services.LocalizationService.Format("Auto_AccountWorkflowS_314", acc.Username));
        }

        public void CopySecurityToken(AccountViewModel acc, Action<string> statusCallback)
        {
            if (acc?.Model == null || string.IsNullOrEmpty(acc.Model.SecurityToken)) return;
            ClipboardHelper.SetClipboardTextSafe(acc.Model.SecurityToken);
            statusCallback?.Invoke(Shiro.Services.LocalizationService.Format("Auto_AccountWorkflowS_315", acc.Username));
        }

        public void RemoveAccount(AccountViewModel acc, MainViewModel viewModel, Action<string> statusCallback)
        {
            if (acc?.Model == null) return;
            bool confirmed = AppServices.Dialogs.ShowConfirmation(
                Shiro.Services.LocalizationService.Format("Auto_AccountWorkflowS_316", acc.Username),
                Shiro.Services.LocalizationService.Get("Auto_AccountWorkflowS_317"));

            if (confirmed)
            {
                AppServices.AccountRepository.RemoveRange(new[] { acc.Model });
                viewModel?.Accounts.Remove(acc);
                viewModel?.NotifyCounts();
                viewModel?.RebuildGroupList();
                viewModel?.ApplyFilter();
                statusCallback?.Invoke(Shiro.Services.LocalizationService.Format("Auto_AccountWorkflowS_318", acc.Username));
            }
        }

        public void SetGroupSelected(MainViewModel viewModel, Action<string> statusCallback)
        {
            if (viewModel == null) return;
            var selectedVms = viewModel.GetSelectedAccountViewModels();
            if (selectedVms.Count == 0)
            {
                statusCallback?.Invoke(Shiro.Services.LocalizationService.Get("Auto_AccountWorkflowS_319"));
                return;
            }

            string currentGroup = selectedVms.Count == 1 && !string.IsNullOrWhiteSpace(selectedVms[0].Group) ? selectedVms[0].Group : "Default";
            string prompt = selectedVms.Count == 1 ? Shiro.Services.LocalizationService.Format("Auto_AccountWorkflowS_303", selectedVms[0].Username) : Shiro.Services.LocalizationService.Format("Auto_AccountWorkflowS_320", selectedVms.Count);
            string groupName = AppServices.Dialogs.ShowInputPrompt(prompt, Shiro.Services.LocalizationService.Get("Auto_AccountWorkflowS_321"), currentGroup);

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
                statusCallback?.Invoke(Shiro.Services.LocalizationService.Format("Auto_AccountWorkflowS_322", selectedVms.Count, groupName));
            }
        }

        public void SavePlaceIdSelected(MainViewModel viewModel, Action<string> statusCallback)
        {
            if (viewModel == null) return;
            var targets = viewModel.GetSelectedAccounts();
            if (targets.Count == 0)
            {
                statusCallback?.Invoke(Shiro.Services.LocalizationService.Get("Auto_AccountWorkflowS_323"));
                return;
            }

            long parsedPid = AccountLauncherService.ParsePlaceId(viewModel.PlaceId);
            if (parsedPid <= 0)
            {
                statusCallback?.Invoke(Shiro.Services.LocalizationService.Get("Auto_AccountWorkflowS_324"));
                return;
            }

            string pid = parsedPid.ToString(System.Globalization.CultureInfo.InvariantCulture);
            foreach (var a in targets)
            {
                a.SetField("SavedPlaceId", pid);
            }
            AppServices.AccountRepository.QueueDeferredSave();
            statusCallback?.Invoke(Shiro.Services.LocalizationService.Format("Auto_AccountWorkflowS_325", pid, targets.Count));
        }

        public void CopyUserPassSelected(MainViewModel viewModel, Action<string> statusCallback)
        {
            if (viewModel == null) return;
            var targets = viewModel.GetSelectedAccounts();
            if (targets.Count == 0)
            {
                statusCallback?.Invoke(Shiro.Services.LocalizationService.Get("Auto_AccountWorkflowS_326"));
                return;
            }

            var lines = targets.Select(a => $"{a.Username}:{(a.Password ?? string.Empty)}").ToList();
            ClipboardHelper.SetClipboardTextSafe(string.Join(Environment.NewLine, lines));
            statusCallback?.Invoke(Shiro.Services.LocalizationService.Format("Auto_AccountWorkflowS_327", lines.Count));
        }

        public void RemoveSelectedAccounts(MainViewModel viewModel, Action<string> statusCallback)
        {
            if (viewModel == null) return;
            var selectedVms = viewModel.GetSelectedAccountViewModels();
            if (selectedVms.Count == 0)
            {
                statusCallback?.Invoke(Shiro.Services.LocalizationService.Get("Auto_AccountWorkflowS_328"));
                return;
            }

            bool confirmed = AppServices.Dialogs.ShowConfirmation(
                Shiro.Services.LocalizationService.Format("Auto_AccountWorkflowS_329", selectedVms.Count),
                Shiro.Services.LocalizationService.Get("Auto_AccountWorkflowS_330"));

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
                statusCallback?.Invoke(Shiro.Services.LocalizationService.Format("Auto_AccountWorkflowS_331", count));
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
                            string itemState = ok ? Shiro.Services.LocalizationService.Get("Auto_AccountWorkflowS_275") : (item.Model.IsBannedOrLocked ? item.Model.AccountStatusIssue : Shiro.Services.LocalizationService.Get("KPI_Expired"));
                            statusCallback?.Invoke(Shiro.Services.LocalizationService.Format("Auto_AccountWorkflowS_332", c, total, item.Username, itemState));
                        }
                        catch (Exception ex)
                        {
                            Program.Logger?.Warn($"[OnValidateSessionSelectedRequested] {item.Username}: {ex.Message}");
                            int c = Interlocked.Increment(ref checkedCount);
                        }
                    }).ConfigureAwait(false);
                    await AppServices.AccountRepository.SaveAsync(true).ConfigureAwait(false);
                    AppDispatcher.Post(() =>
                    {
                        viewModel?.NotifyCounts();
                        viewModel?.ApplyFilter();
                    });
                    statusCallback?.Invoke(Shiro.Services.LocalizationService.Format("Auto_AccountWorkflowS_333", checkedCount));
                }
                catch (Exception ex)
                {
                    Program.Logger?.Error($"[OnValidateSessionSelectedRequested] {ex.Message}");
                    statusCallback?.Invoke(Shiro.Services.LocalizationService.Format("Auto_AccountWorkflowS_334", ex.Message));
                }
            });
        }
    }
}
