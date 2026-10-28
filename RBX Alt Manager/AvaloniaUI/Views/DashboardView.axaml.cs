#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using System.Windows.Input;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.VisualTree;
using RBX_Alt_Manager.Classes;
using RBX_Alt_Manager.Services;
using RBX_Alt_Manager.ViewModels;

namespace RBX_Alt_Manager.AvaloniaUI.Views
{
    public partial class DashboardView : UserControl
    {
        public MainViewModel ViewModel { get; } = new MainViewModel();
        private bool _placeIdHooked;

        public DashboardView()
        {
            InitializeComponent();
            DataContext = ViewModel;
            WireViewModelCallbacks();
            Loaded += (s, e) =>
            {
                LoadDashboardData();
                if (!_placeIdHooked && TxtPlaceId != null)
                {
                    _placeIdHooked = true;
                    TxtPlaceId.LostFocus += (sender, args) =>
                    {
                        if (!string.IsNullOrWhiteSpace(TxtPlaceId.Text))
                        {
                            long parsed = AccountLauncherService.ParsePlaceId(TxtPlaceId.Text);
                            if (parsed > 0)
                            {
                                string pid = parsed.ToString();
                                ViewModel.PlaceId = pid;
                                AccountManager.CurrentPlaceId = pid;
                                AccountManager.General?.Set("SavedPlaceId", pid);
                                AccountManager.QueueDeferredSaveSettings();
                            }
                        }
                    };
                }
            };
        }

        private void WireViewModelCallbacks()
        {
            ViewModel.PropertyChanged += (s, e) =>
            {
                if (e.PropertyName == nameof(MainViewModel.ActiveTab) || e.PropertyName == nameof(MainViewModel.SearchText))
                {
                    AppDispatcher.Post(() =>
                    {
                        try
                        {
                            if (CardsScrollViewer != null) CardsScrollViewer.Offset = new Vector(0, 0);
                            if (TableItemsControl != null && TableItemsControl.ItemCount > 0) TableItemsControl.ScrollIntoView(0);
                        }
                        catch (Exception ex)
                        {
                            Program.Logger?.Debug($"Scroll reset on tab change threw: {ex.Message}");
                        }
                    });
                }
            };

            // Top action callbacks that refresh Dashboard UI state
            ViewModel.OnAddAccountFastLoginRequested = () => OnAddFastLoginClicked(null, null);
            ViewModel.OnAddAccountChromeRequested = () => OnAddChromeClicked(null, null);
            ViewModel.OnAddAccountCookieRequested = () => OnAddCookieClicked(null, null);
            ViewModel.OnBulkImportRequested = () => OnBulkImportClicked(null, null);
            ViewModel.OnProxyManagerRequested = () => OnProxiesClicked(null, null);
            ViewModel.OnNexusRequested = () => OnNexusClicked(null, null);
        }

        public List<Account> GetSelectedAccounts()
        {
            var vmSelected = ViewModel?.GetSelectedAccounts();
            if (vmSelected != null && vmSelected.Count > 0)
                return vmSelected;

            return new List<Account>();
        }

        public void LoadDashboardData(IReadOnlyList<Account>? accountsSnapshot = null)
        {
            try
            {
                if (ViewModel != null && string.IsNullOrWhiteSpace(ViewModel.PlaceId) && !string.IsNullOrWhiteSpace(AccountManager.CurrentPlaceId))
                {
                    ViewModel.PlaceId = AccountManager.CurrentPlaceId;
                }

                var accounts = accountsSnapshot ?? AccountManager.GetAccountsSnapshot();
                ViewModel?.LoadAccounts(accounts);

                if (CmbGroups != null && ViewModel != null && CmbGroups.SelectedItem == null && ViewModel.AvailableGroups.Count > 0)
                {
                    CmbGroups.SelectedItem = ViewModel.SelectedGroup;
                }
            }
            catch (Exception ex)
            {
                Program.Logger?.Error($"[DashboardView.LoadDashboardData] {ex}");
            }
        }

        // ================= TOP ACTION TOOLBAR EVENT HANDLERS =================
        private void OnJoinServerClicked(object sender, RoutedEventArgs e)
        {
            string placeIdStr = TxtPlaceId?.Text?.Trim() ?? string.Empty;
            long placeId = AccountLauncherService.ParsePlaceId(placeIdStr);
            if (placeId <= 0)
            {
                ViewModel.StatusMessage = "⚠️ Vui lòng nhập Place ID hoặc link game hợp lệ trước khi vào Server!";
                return;
            }

            string normalizedPlaceId = placeId.ToString();
            ViewModel.PlaceId = normalizedPlaceId;
            AccountManager.CurrentPlaceId = normalizedPlaceId;
            AccountManager.General?.Set("SavedPlaceId", normalizedPlaceId);
            AccountManager.QueueDeferredSaveSettings();

            var selected = GetSelectedAccounts();
            if (selected == null || selected.Count == 0)
            {
                var snapshot = AccountManager.GetAccountsSnapshot();
                if (snapshot.Count > 0)
                    selected = new List<Account>(1) { snapshot[0] };
            }
            if (selected == null || selected.Count == 0)
            {
                ViewModel.StatusMessage = "⚠️ Vui lòng chọn ít nhất một tài khoản!";
                return;
            }

            string jobId = TxtJobId?.Text?.Trim() ?? string.Empty;
            Task.Run(async () =>
            {
                await AccountLauncherService.JoinServerAsync(selected, placeId, jobId);
            });
        }

        private void OnLaunchHomeClicked(object sender, RoutedEventArgs e)
        {
            var selected = GetSelectedAccounts();
            if (selected != null && selected.Count > 0)
            {
                AccountManager.SelectedAccounts = selected;
            }
            AccountLauncherService.MultiLaunchHome();
        }

        private void OnAddFastLoginClicked(object? sender = null, RoutedEventArgs? e = null)
        {
            AppServices.Dialogs.OpenFastLoginDialog(null);
        }

        private void OnAddChromeClicked(object? sender = null, RoutedEventArgs? e = null)
        {
            Task.Run(async () =>
            {
                try
                {
                    await BrowserService.Default.SignInAsync();
                    AppDispatcher.Post(() => LoadDashboardData());
                }
                catch (Exception ex)
                {
                    Program.Logger?.Error($"[OnAddChromeClicked] {ex}");
                }
            });
        }

        private void OnAddCookieClicked(object? sender = null, RoutedEventArgs? e = null)
        {
            string cookie = AppServices.Dialogs.ShowInputPrompt("Dán chuỗi Cookie .ROBLOSECURITY vào ô bên dưới:", "Nhập Cookie");
            if (!string.IsNullOrWhiteSpace(cookie) && cookie != "/UC")
            {
                _ = Task.Run(async () =>
                {
                    var acc = await AccountManager.AddAccountAsync(cookie.Trim()).ConfigureAwait(false);
                    if (acc != null)
                    {
                        AppDispatcher.Post(() =>
                        {
                            LoadDashboardData();
                            ViewModel.StatusMessage = $"✅ Đã thêm tài khoản {acc.Username} qua Cookie thành công!";
                        });
                    }
                    else
                    {
                        AppDispatcher.Post(() =>
                        {
                            ViewModel.StatusMessage = "❌ Cookie không hợp lệ hoặc đã hết hạn.";
                        });
                    }
                });
            }
        }

        private void OnBulkImportClicked(object? sender = null, RoutedEventArgs? e = null)
        {
            AppServices.Dialogs.OpenBulkImportDialog();
        }

        private void OnProxiesClicked(object? sender = null, RoutedEventArgs? e = null)
        {
            AppServices.Dialogs.OpenProxyManagerDialog();
        }

        private void OnNexusClicked(object? sender = null, RoutedEventArgs? e = null)
        {
            AppServices.Dialogs.OpenNexusWindow();
        }

        // ================= BATCH ACTIONS =================
        private void OnBatchCheckSessionsClicked(object sender, RoutedEventArgs e)
            => ViewModel.CheckSessionsCommand.Execute(null);

        private void OnBatchReLoginExpiredClicked(object sender, RoutedEventArgs e)
            => ViewModel.ReLoginAllExpiredCommand.Execute(null);

        private void OnBatchValidateSelectedClicked(object sender, RoutedEventArgs e)
            => ViewModel.ValidateSessionSelectedCommand.Execute(null);

        private void OnBatchReLoginSelectedClicked(object sender, RoutedEventArgs e)
            => ViewModel.ReLoginSelectedCommand.Execute(null);

        private void OnBatchSetGroupSelectedClicked(object sender, RoutedEventArgs e)
            => ViewModel.SetGroupSelectedCommand.Execute(null);

        private void OnBatchSavePlaceIdSelectedClicked(object sender, RoutedEventArgs e)
            => ViewModel.SavePlaceIdSelectedCommand.Execute(null);

        private void OnBatchCopyUserPassSelectedClicked(object sender, RoutedEventArgs e)
            => ViewModel.CopyUserPassSelectedCommand.Execute(null);

        private void OnBatchRemoveSelectedClicked(object sender, RoutedEventArgs e)
            => ViewModel.RemoveSelectedCommand.Execute(null);

        private void OnClearRobloxClientSessionClicked(object sender, RoutedEventArgs e)
        {
            bool ok = RobloxProcessService.ClearRobloxClientSession();
            if (ok)
            {
                AccountManager.Instance?.SetDashboardStatus("✅ Đã xóa sạch session đăng nhập của Roblox Client.");
            }
            else
            {
                AccountManager.Instance?.SetDashboardStatus("⚠️ Không thể xóa session Roblox Client (có thể Roblox đang chạy).");
            }
        }

        // ================= CARD & ROW INTERACTIONS =================
        private void OnCardPointerPressed(object sender, PointerPressedEventArgs e)
        {
            var props = e.GetCurrentPoint(this).Properties;
            if (props.IsRightButtonPressed)
            {
                if (sender is Control ctrl && ctrl.DataContext is AccountViewModel avm)
                {
                    ShowAccountContextMenu(ctrl, avm);
                    e.Handled = true;
                }
                return;
            }

            if (!props.IsLeftButtonPressed)
                return;

            if (e.Source is Visual visual && IsInsideInteractiveControl(visual))
                return;

            if (sender is Control ctrl2 && ctrl2.DataContext is AccountViewModel avm2)
            {
                avm2.IsSelected = !avm2.IsSelected;
            }
        }

        private static bool IsInsideInteractiveControl(Visual visual)
        {
            if (visual == null) return false;
            if (visual is Button || visual is CheckBox || visual is TextBox || visual is ComboBox)
                return true;
            return visual.FindAncestorOfType<Button>() != null
                || visual.FindAncestorOfType<CheckBox>() != null
                || visual.FindAncestorOfType<TextBox>() != null
                || visual.FindAncestorOfType<ComboBox>() != null;
        }

        private void LaunchAppHomeForSender(object? sender)
        {
            var avm = GetItemFromMenu(sender);
            if (avm?.Model != null)
            {
                Task.Run(async () => await AccountLauncherService.LaunchAppHomeAsync(avm.Model));
            }
        }

        private void JoinSingleAccountServer(AccountViewModel? avm)
        {
            if (avm?.Model == null) return;
            long placeId = AccountLauncherService.ParsePlaceId(ViewModel?.PlaceId?.Trim() ?? string.Empty);
            if (placeId > 0)
            {
                Task.Run(async () => await AccountLauncherService.JoinServerAsync(new[] { avm.Model }, placeId, ViewModel?.JobId?.Trim() ?? string.Empty));
            }
            else
            {
                if (ViewModel != null)
                {
                    ViewModel.StatusMessage = "⚠️ Vui lòng nhập Place ID hoặc link game hợp lệ trước khi vào Server!";
                }
            }
        }

        private void CloseRobloxForAccount(AccountViewModel? avm)
        {
            if (avm?.Model == null) return;
            var model = avm.Model;
            string username = avm.Username;
            _ = Task.Run(() =>
            {
                bool closed = RobloxProcessService.CloseAccountRobloxProcess(model);
                AppDispatcher.Post(() =>
                {
                    if (closed)
                        ToastService.ShowSuccess($"Đã đóng Roblox cho {username}.");
                    else
                        ToastService.ShowInfo($"Tài khoản {username} không có tiến trình Roblox nào đang chạy.");
                    ViewModel?.RefreshLiveProcessStatus();
                });
            });
        }

        private void OnCardDoubleTapped(object sender, TappedEventArgs e)
            => LaunchAppHomeForSender(sender);

        private void OnCardLaunchClicked(object sender, RoutedEventArgs e)
        {
            var avm = GetItemFromMenu(sender);
            if (avm?.Model != null)
            {
                if (avm.IsExpired || avm.IsBannedOrLocked)
                {
                    AppServices.Dialogs.OpenFastLoginDialog(avm.Model);
                    return;
                }
                LaunchAppHomeForSender(sender);
            }
        }

        private void OnTableRowJoinClicked(object sender, RoutedEventArgs e)
            => JoinSingleAccountServer(GetItemFromMenu(sender));

        private void OnCardCloseRobloxClicked(object sender, RoutedEventArgs e)
            => CloseRobloxForAccount(GetItemFromMenu(sender));

        private void OnCardMoreOptionsClicked(object sender, RoutedEventArgs e)
        {
            try
            {
                if (sender is Button btn)
                {
                    if (btn.Flyout != null)
                    {
                        btn.Flyout.ShowAt(btn);
                        return;
                    }

                    var border = btn.FindAncestorOfType<Border>();
                    if (border?.ContextMenu != null)
                    {
                        border.ContextMenu.Open(border);
                    }
                }
            }
            catch (Exception ex)
            {
                Program.Logger?.Error($"[OnCardMoreOptionsClicked] {ex}");
            }
        }

        private void ShowAccountContextMenu(Control anchor, AccountViewModel avm)
        {
            if (anchor == null || avm == null) return;
            try
            {
                if (Resources.TryGetResource("AccountSharedContextMenu", null, out var res) && res is ContextMenu cm)
                {
                    cm.Close();
                    cm.PlacementTarget = anchor;
                    cm.DataContext = avm;
                    cm.Open(anchor);
                }
            }
            catch (Exception ex)
            {
                Program.Logger?.Error($"[ShowAccountContextMenu] {ex}");
            }
        }

        private void OnMoreButtonClicked(object sender, RoutedEventArgs e)
        {
            if (sender is Control control && control.DataContext is AccountViewModel avm)
            {
                ShowAccountContextMenu(control, avm);
            }
        }
    }
}
