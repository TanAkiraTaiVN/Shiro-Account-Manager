#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using RBX_Alt_Manager.Classes;
using RBX_Alt_Manager.ViewModels;

namespace RBX_Alt_Manager.Services
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
            var list = AccountManager.GetAccountsSnapshot();
            if (list == null || list.Count == 0) return;

            statusCallback?.Invoke("🔍 Đang kiểm tra phiên đăng nhập của tất cả tài khoản...");

            int total = list.Count;
            int checkedCount = 0;
            int expiredCount = 0;

            using var concurrencyGate = new SemaphoreSlim(6, 6);
            var tasks = list.Select(async acc =>
            {
                await concurrencyGate.WaitAsync().ConfigureAwait(false);
                try
                {
                    bool isValid = await RobloxRateLimitCoordinator.Instance.ExecuteWithPacingAsync(
                        RateLimitCategory.SessionCheck,
                        () => acc.ValidateSessionAsync(),
                        minIntervalMs: 200).ConfigureAwait(false);

                    int c = Interlocked.Increment(ref checkedCount);
                    if (!isValid) Interlocked.Increment(ref expiredCount);

                    string state = isValid ? "✅ Hợp lệ" : "⚠️ Hết hạn";
                    statusCallback?.Invoke($"🔍 Đang kiểm tra ({c}/{total}): {acc.Username} -> {state} (Hết hạn: {expiredCount})");
                    viewModel?.UpdateAccountValidity(acc, isValid, refreshFilterAndCounts: false);
                }
                catch (Exception ex)
                {
                    Program.Logger?.Warn($"[CheckSessions] {acc.Username}: {ex.Message}");
                    int c = Interlocked.Increment(ref checkedCount);
                    if (!acc.Valid) Interlocked.Increment(ref expiredCount);
                }
                finally
                {
                    concurrencyGate.Release();
                }
            }).ToArray();

            await Task.WhenAll(tasks).ConfigureAwait(false);
            AccountManager.SaveAccounts(true);

            AppDispatcher.Post(() =>
            {
                viewModel?.NotifyCounts();
                viewModel?.ApplyFilter();
            });

            int valid = total - expiredCount;
            statusCallback?.Invoke(expiredCount > 0
                ? $"⚠️ Quét hoàn tất: {valid} sẵn sàng, {expiredCount} hết hạn! Xem tab '🔴 Hết hạn' để Re-login."
                : $"✅ Quét hoàn tất: Toàn bộ {total} tài khoản đều đang hoạt động tốt!");

            string msg = expiredCount > 0
                ? $"🔍 KẾT QUẢ KIỂM TRA PHIÊN (SESSION CHECK):\n\n" +
                  $"• Tổng số tài khoản: {total}\n" +
                  $"• Sẵn sàng hoạt động: {valid}\n" +
                  $"• Hết hạn phiên: {expiredCount}\n\n" +
                  (expiredCount == total
                      ? "⚠️ LƯU Ý ĐẶC BIỆT: Tất cả tài khoản đều báo hết hạn phiên!\n" +
                        "Khả năng cao là địa chỉ IP của bạn vừa thay đổi (do đổi mạng, bật/tắt VPN, hoặc router reset).\n\n"
                      : "") +
                  $"Hệ thống đã tự động gia hạn Cookie xoay (Cookie Rotation) cho các tài khoản hợp lệ.\n" +
                  $"Các tài khoản hết hạn đã được chuyển vào tab '🔴 Hết hạn'."
                : $"🔍 KẾT QUẢ KIỂM TRA PHIÊN (SESSION CHECK):\n\n" +
                  $"• Tổng số tài khoản: {total}\n" +
                  $"• Sẵn sàng hoạt động: {valid}\n" +
                  $"• Hết hạn phiên: 0\n\n" +
                  $"Tất cả tài khoản đều có phiên đăng nhập hợp lệ và đã được tự động cập nhật Cookie xoay!";

            if (expiredCount > 0)
                AppServices.Dialogs.ShowWarning(msg, "Kiểm Tra Phiên Tài Khoản");
            else
                AppServices.Dialogs.ShowInfo(msg, "Kiểm Tra Phiên Tài Khoản");
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
                    $"Nhập Cookie mới (.ROBLOSECURITY) cho {acc.Username}:",
                    $"Cập nhật Cookie cho {acc.Username}",
                    string.Empty)).ConfigureAwait(false);

            if (!string.IsNullOrWhiteSpace(cookie) && cookie != "/UC")
            {
                var match = CookieCleanRegex().Match(cookie);
                string token = Account.SanitizeCookie(match.Success ? match.Value : cookie);

                string previousToken = acc.Model.SecurityToken;
                bool wasValid = acc.Model.Valid;

                acc.Model.SecurityToken = token;
                statusCallback?.Invoke($"🔍 Đang xác thực phiên mới cho {acc.Username}...");
                bool ok = await acc.Model.ValidateSessionAsync().ConfigureAwait(false);

                if (ok)
                {
                    acc.Model.LastUse = DateTime.Now;
                    acc.Model.CSRFToken = null;
                    acc.Model.TokenSet = DateTime.MinValue;
                    acc.Model.LastAuthTicketRateLimited = false;
                    AccountManager.SaveAccounts(true);
                    viewModel?.UpdateAccountValidity(acc.Model, true);
                    statusCallback?.Invoke($"✅ Cập nhật Cookie thành công cho {acc.Username}! Phiên đã sẵn sàng.");
                }
                else
                {
                    if (wasValid && !string.IsNullOrWhiteSpace(previousToken))
                    {
                        acc.Model.SecurityToken = previousToken;
                        acc.Model.Valid = true;
                        viewModel?.UpdateAccountValidity(acc.Model, true);
                        statusCallback?.Invoke($"⚠️ Cookie vừa nhập không hợp lệ cho {acc.Username}. Đã giữ nguyên phiên hợp lệ trước đó.");
                    }
                    else
                    {
                        AccountManager.SaveAccounts(true);
                        viewModel?.UpdateAccountValidity(acc.Model, false);
                        statusCallback?.Invoke($"❌ Cookie vừa nhập không hợp lệ hoặc đã hết hạn cho {acc.Username}.");
                    }
                }
            }
        }

        public void SetAlias(AccountViewModel acc, Action<string> statusCallback)
        {
            if (acc?.Model == null) return;
            string current = acc.Model.Alias ?? string.Empty;
            string input = AppServices.Dialogs.ShowInputPrompt(
                $"Nhập Alias (Tên gợi nhớ) cho {acc.Username}:",
                "Đổi Alias",
                current);

            if (input != null && input != "/UC")
            {
                acc.Model.Alias = string.IsNullOrWhiteSpace(input) ? string.Empty : input.Trim();
                acc.Alias = acc.Model.Alias;
                AccountManager.SaveAccounts();
                statusCallback?.Invoke($"Đã đổi Alias cho {acc.Username} thành: {acc.Model.Alias}");
            }
        }

        public void SetGroup(AccountViewModel acc, MainViewModel viewModel, Action<string> statusCallback)
        {
            if (acc?.Model == null) return;
            string currentGroup = !string.IsNullOrWhiteSpace(acc.Model.Group) ? acc.Model.Group : "Default";
            string groupName = AppServices.Dialogs.ShowInputPrompt(
                $"Nhóm cho {acc.Username}:",
                "Phân Nhóm",
                currentGroup);

            if (groupName != null && groupName != "/UC")
            {
                groupName = string.IsNullOrWhiteSpace(groupName) ? "Default" : groupName.Trim();
                acc.Model.Group = groupName;
                acc.Group = groupName;
                AccountManager.SaveAccounts(true);
                viewModel?.RebuildGroupList();
                viewModel?.ApplyFilter();
                statusCallback?.Invoke($"Đã chuyển {acc.Username} sang nhóm {groupName}.");
            }
        }

        public void SetDescription(AccountViewModel acc, Action<string> statusCallback)
        {
            if (acc?.Model == null) return;
            string current = acc.Model.Description ?? string.Empty;
            string newDesc = AppServices.Dialogs.ShowInputPrompt(
                $"Nhập ghi chú cho {acc.Username}:",
                "Đổi Ghi Chú",
                current);

            if (newDesc != null && newDesc != "/UC")
            {
                acc.Model.Description = newDesc;
                acc.Description = newDesc;
                AccountManager.SaveAccounts();
                statusCallback?.Invoke($"Đã cập nhật ghi chú cho {acc.Username}.");
            }
        }

        public void TogglePin(AccountViewModel acc, MainViewModel viewModel, Action<string> statusCallback)
        {
            if (acc?.Model == null) return;
            acc.Model.Pinned = !acc.Model.Pinned;
            acc.Pinned = acc.Model.Pinned;
            AccountManager.SaveAccounts(true);
            viewModel?.ApplyFilter();
            statusCallback?.Invoke(acc.Pinned ? $"📌 Đã ghim {acc.Username} lên đầu danh sách." : $"Đã bỏ ghim {acc.Username}.");
        }

        public void CopyUsername(AccountViewModel acc, Action<string> statusCallback)
        {
            if (acc?.Model == null || string.IsNullOrEmpty(acc.Model.Username)) return;
            ClipboardHelper.SetClipboardTextSafe(acc.Model.Username);
            statusCallback?.Invoke($"📋 Đã sao chép Username: {acc.Model.Username}");
        }

        public void CopyUserPass(AccountViewModel acc, Action<string> statusCallback)
        {
            if (acc?.Model != null)
            {
                ClipboardHelper.SetClipboardTextSafe($"{acc.Model.Username}:{(acc.Model.Password ?? string.Empty)}");
                statusCallback?.Invoke($"📋 Đã sao chép User:Pass của {acc.Model.Username} vào Clipboard.");
            }
        }

        public void CopyUserId(AccountViewModel acc, Action<string> statusCallback)
        {
            if (acc?.Model == null || acc.Model.UserID <= 0) return;
            ClipboardHelper.SetClipboardTextSafe(acc.Model.UserID.ToString());
            statusCallback?.Invoke($"📋 Đã sao chép User ID: {acc.Model.UserID}");
        }

        public void CopyProfileUrl(AccountViewModel acc, Action<string> statusCallback)
        {
            if (acc?.Model == null || acc.Model.UserID <= 0) return;
            ClipboardHelper.SetClipboardTextSafe($"https://www.roblox.com/users/{acc.Model.UserID}/profile");
            statusCallback?.Invoke($"📋 Đã sao chép liên kết hồ sơ của {acc.Username}");
        }

        public void CopySecurityToken(AccountViewModel acc, Action<string> statusCallback)
        {
            if (acc?.Model == null || string.IsNullOrEmpty(acc.Model.SecurityToken)) return;
            ClipboardHelper.SetClipboardTextSafe(acc.Model.SecurityToken);
            statusCallback?.Invoke($"📋 Đã sao chép Security Token (.ROBLOSECURITY) của {acc.Username}");
        }

        public void RemoveAccount(AccountViewModel acc, MainViewModel viewModel, Action<string> statusCallback)
        {
            if (acc?.Model == null) return;
            bool confirmed = AppServices.Dialogs.ShowConfirmation(
                $"Bạn có chắc chắn muốn xóa tài khoản '{acc.Username}' khỏi Shiro RAM không?",
                "Xác nhận xóa tài khoản");

            if (confirmed)
            {
                AccountManager.RemoveAccountsFromDatabase(new[] { acc.Model });
                viewModel?.Accounts.Remove(acc);
                viewModel?.NotifyCounts();
                viewModel?.RebuildGroupList();
                viewModel?.ApplyFilter();
                statusCallback?.Invoke($"🗑️ Đã xóa tài khoản {acc.Username}.");
            }
        }

        public void SetGroupSelected(MainViewModel viewModel, Action<string> statusCallback)
        {
            if (viewModel == null) return;
            var selectedVms = viewModel.GetSelectedAccountViewModels();
            if (selectedVms.Count == 0)
            {
                statusCallback?.Invoke("⚠️ Vui lòng tick chọn ít nhất một tài khoản trước khi đổi nhóm.");
                return;
            }

            string currentGroup = selectedVms.Count == 1 && !string.IsNullOrWhiteSpace(selectedVms[0].Group) ? selectedVms[0].Group : "Default";
            string prompt = selectedVms.Count == 1 ? $"Nhóm cho {selectedVms[0].Username}:" : $"Nhóm cho {selectedVms.Count} tài khoản đã chọn:";
            string groupName = AppServices.Dialogs.ShowInputPrompt(prompt, "Phân Nhóm Hàng Loạt", currentGroup);

            if (groupName != null && groupName != "/UC")
            {
                groupName = string.IsNullOrWhiteSpace(groupName) ? "Default" : groupName.Trim();
                foreach (var vm in selectedVms)
                {
                    vm.Group = groupName;
                }
                AccountManager.SaveAccounts(true);
                viewModel.RebuildGroupList();
                viewModel.ApplyFilter();
                statusCallback?.Invoke($"Đã chuyển {selectedVms.Count} tài khoản sang nhóm {groupName}.");
            }
        }

        public void SavePlaceIdSelected(MainViewModel viewModel, Action<string> statusCallback)
        {
            if (viewModel == null) return;
            var targets = viewModel.GetSelectedAccounts();
            if (targets.Count == 0)
            {
                statusCallback?.Invoke("⚠️ Vui lòng tick chọn ít nhất một tài khoản để lưu Place ID.");
                return;
            }

            long parsedPid = AccountLauncherService.ParsePlaceId(viewModel.PlaceId);
            if (parsedPid <= 0)
            {
                statusCallback?.Invoke("⚠️ Hãy nhập Place ID hợp lệ vào ô trên trước khi lưu.");
                return;
            }

            string pid = parsedPid.ToString(System.Globalization.CultureInfo.InvariantCulture);
            foreach (var a in targets)
            {
                a.SetField("SavedPlaceId", pid);
            }
            AccountManager.SaveAccounts();
            statusCallback?.Invoke($"💾 Đã lưu Place ID {pid} cho {targets.Count} tài khoản.");
        }

        public void CopyUserPassSelected(MainViewModel viewModel, Action<string> statusCallback)
        {
            if (viewModel == null) return;
            var targets = viewModel.GetSelectedAccounts();
            if (targets.Count == 0)
            {
                statusCallback?.Invoke("⚠️ Vui lòng tick chọn ít nhất một tài khoản để sao chép User:Pass.");
                return;
            }

            var lines = targets.Select(a => $"{a.Username}:{(a.Password ?? string.Empty)}").ToList();
            ClipboardHelper.SetClipboardTextSafe(string.Join(Environment.NewLine, lines));
            statusCallback?.Invoke($"📋 Đã sao chép User:Pass của {lines.Count} tài khoản vào Clipboard.");
        }

        public void RemoveSelectedAccounts(MainViewModel viewModel, Action<string> statusCallback)
        {
            if (viewModel == null) return;
            var selectedVms = viewModel.GetSelectedAccountViewModels();
            if (selectedVms.Count == 0)
            {
                statusCallback?.Invoke("⚠️ Vui lòng tick chọn ít nhất một tài khoản để xóa!");
                return;
            }

            bool confirmed = AppServices.Dialogs.ShowConfirmation(
                $"Bạn có chắc chắn muốn xóa {selectedVms.Count} tài khoản đã chọn khỏi Shiro RAM không?\n\nHành động này không thể hoàn tác!",
                "Xác nhận xóa hàng loạt");

            if (confirmed)
            {
                var models = selectedVms.Where(v => v.Model != null).Select(v => v.Model).ToList();
                int count = AccountManager.RemoveAccountsFromDatabase(models);
                foreach (var vm in selectedVms)
                {
                    viewModel.Accounts.Remove(vm);
                }
                viewModel.NotifyCounts();
                viewModel.RebuildGroupList();
                viewModel.ApplyFilter();
                statusCallback?.Invoke($"🗑️ Đã xóa {count} tài khoản khỏi hệ thống.");
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
                    using var concurrencyGate = new SemaphoreSlim(6, 6);
                    var tasks = snapshot.Select(async item =>
                    {
                        await concurrencyGate.WaitAsync().ConfigureAwait(false);
                        try
                        {
                            bool ok = await RobloxRateLimitCoordinator.Instance.ExecuteWithPacingAsync(
                                RateLimitCategory.SessionCheck,
                                () => item.Model.ValidateSessionAsync(),
                                minIntervalMs: 200).ConfigureAwait(false);

                            int c = Interlocked.Increment(ref checkedCount);
                            viewModel?.UpdateAccountValidity(item.Model, ok, refreshFilterAndCounts: false);
                            statusCallback?.Invoke($"🔍 Đang kiểm tra ({c}/{total}): {item.Username} -> {(ok ? "✅ Hợp lệ" : "⚠️ Hết hạn")}");
                        }
                        finally
                        {
                            concurrencyGate.Release();
                        }
                    }).ToArray();

                    await Task.WhenAll(tasks).ConfigureAwait(false);
                    AccountManager.SaveAccounts(true);
                    AppDispatcher.Post(() =>
                    {
                        viewModel?.NotifyCounts();
                        viewModel?.ApplyFilter();
                    });
                    statusCallback?.Invoke($"✅ Đã hoàn tất kiểm tra phiên cho {checkedCount} tài khoản đã chọn.");
                }
                catch (Exception ex)
                {
                    Program.Logger?.Error($"[OnValidateSessionSelectedRequested] {ex.Message}");
                    statusCallback?.Invoke($"⚠️ Lỗi khi kiểm tra phiên: {ex.Message}");
                }
            });
        }
    }
}
