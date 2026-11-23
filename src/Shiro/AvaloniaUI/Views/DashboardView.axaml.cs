#nullable enable
using System;
using System.Collections.Generic;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.VisualTree;
using Shiro.AvaloniaUI.Controls;
using Shiro.Services;
using Shiro.ViewModels;

namespace Shiro.AvaloniaUI.Views
{
    /// <summary>
    /// Thin MVVM view layer for the Accounts Dashboard.
    /// All account, batch, and launch workflows live in <see cref="MainViewModel"/>;
    /// code-behind handles only Avalonia visual-tree gestures, responsive column sizing, scroll resets, and context menu placement.
    /// </summary>
    public partial class DashboardView : UserControl
    {
        public MainViewModel ViewModel { get; } = new MainViewModel();
        private bool _placeIdHooked;

        public DashboardView()
        {
            InitializeComponent();
            DataContext = ViewModel;
            WireViewModelCallbacks();

            if (CardsItemsControl != null)
            {
                CardsItemsControl.ContainerPrepared += (_, e) => SyncCardRowFocusRings(e.Container, isClearing: false);
                CardsItemsControl.ContainerClearing += (_, e) => SyncCardRowFocusRings(e.Container, isClearing: true);
            }

            if (TableItemsControl != null)
            {
                TableItemsControl.AddHandler(PointerPressedEvent, OnTablePointerPressedTunnel, RoutingStrategies.Tunnel);
                TableItemsControl.ContainerPrepared += (_, e) => UpdateTableRowFocusClass(e.Container, isClearing: false);
                TableItemsControl.ContainerClearing += (_, e) => UpdateTableRowFocusClass(e.Container, isClearing: true);
            }

            SizeChanged += (_, e) =>
            {
                if (e.NewSize.Width > 0)
                {
                    ViewModel.AccountsVm.CardColumns = AccountCollectionViewModel.CalculateResponsiveCardColumns(e.NewSize.Width);
                }
            };

            Loaded += (s, e) =>
            {
                if (Bounds.Width > 0)
                {
                    ViewModel.AccountsVm.CardColumns = AccountCollectionViewModel.CalculateResponsiveCardColumns(Bounds.Width);
                }
                LoadDashboardData();
                if (!_placeIdHooked && TxtPlaceId != null)
                {
                    _placeIdHooked = true;
                    TxtPlaceId.LostFocus += (_, _) => ViewModel.LaunchBar.NormalizePlaceIdInput(TxtPlaceId.Text);
                }
            };
        }

        private void WireViewModelCallbacks()
        {
            AccountInspectRequested += (_, avm) => ViewModel.Inspector.Open(avm);

            ViewModel.AccountsVm.AvailableGroups.CollectionChanged += (_, _) =>
            {
                AppDispatcher.Post(() =>
                {
                    if (CmbGroups != null && ViewModel.AccountsVm.AvailableGroups.Count > 0 &&
                        string.IsNullOrEmpty(ViewModel.AccountsVm.SelectedGroup))
                    {
                        ViewModel.AccountsVm.SelectedGroup = ViewModel.AccountsVm.AvailableGroups[0];
                        CmbGroups.SelectedItem = ViewModel.AccountsVm.SelectedGroup;
                    }
                });
            };

            ViewModel.AccountsVm.PropertyChanged += (s, e) =>
            {
                if (e.PropertyName == nameof(AccountCollectionViewModel.ActiveTab) || e.PropertyName == nameof(AccountCollectionViewModel.SearchText))
                {
                    AppDispatcher.Post(() =>
                    {
                        try
                        {
                            if (CardsItemsControl != null && CardsItemsControl.ItemCount > 0) CardsItemsControl.ScrollIntoView(0);
                            if (TableItemsControl != null && TableItemsControl.ItemCount > 0) TableItemsControl.ScrollIntoView(0);
                        }
                        catch (Exception ex)
                        {
                            AppServices.Logger?.Debug($"Scroll reset on tab change threw: {ex.Message}");
                        }
                    });
                }
            };
        }

        internal void FocusListViewportIfNeeded()
        {
            if (ListViewportHost == null)
                return;

            var focused = TopLevel.GetTopLevel(this)?.FocusManager?.GetFocusedElement();
            if (!ReferenceEquals(focused, ListViewportHost))
                ListViewportHost.Focus();
        }

        private void OnTablePointerPressedTunnel(object? sender, PointerPressedEventArgs e)
        {
            if (!e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
                return;

            if (e.Source is Visual v)
            {
                var checkBox = v as CheckBox ?? v.FindAncestorOfType<CheckBox>();
                if (checkBox?.DataContext is AccountViewModel avm)
                {
                    SetKeyboardFocusedAccount(avm);
                    FocusListViewportIfNeeded();
                    ViewModel.AccountsVm.HandleAccountClickSelection(avm, e.KeyModifiers.HasFlag(KeyModifiers.Shift));
                    e.Handled = true;
                }
            }
        }

        public List<Account> GetSelectedAccounts()
        {
            return ViewModel?.AccountsVm.GetSelectedAccounts() ?? new List<Account>();
        }

        internal void RequestInspectAccount(AccountViewModel? avm)
        {
            if (avm != null) AccountInspectRequested?.Invoke(this, avm);
        }

        public void LoadDashboardData(IReadOnlyList<Account>? accountsSnapshot = null)
        {
            try
            {
                if (ViewModel != null &&
                    string.IsNullOrWhiteSpace(ViewModel.LaunchBar.PlaceId) &&
                    AppServices.ServerBrowserState is not ServerBrowserStateService { IsPlaceIdExplicitlyCleared: true } &&
                    !string.IsNullOrWhiteSpace(AppServices.ServerBrowserState.CurrentPlaceId))
                {
                    ViewModel.LaunchBar.PlaceId = AppServices.ServerBrowserState.CurrentPlaceId;
                }

                string? prevGroup = ViewModel?.AccountsVm.SelectedGroup;
                var accounts = accountsSnapshot ?? AppServices.AccountRepository.GetAll();
                ViewModel?.AccountsVm.LoadAccounts(accounts);

                if (CmbGroups != null && ViewModel != null && ViewModel.AccountsVm.AvailableGroups.Count > 0)
                {
                    if (string.IsNullOrEmpty(ViewModel.AccountsVm.SelectedGroup))
                    {
                        ViewModel.AccountsVm.SelectedGroup = !string.IsNullOrEmpty(prevGroup) &&
                            ViewModel.AccountsVm.AvailableGroups.Contains(prevGroup)
                            ? prevGroup
                            : ViewModel.AccountsVm.AvailableGroups[0];
                    }
                    if (CmbGroups.SelectedItem == null)
                        CmbGroups.SelectedItem = ViewModel.AccountsVm.SelectedGroup;
                }
            }
            catch (Exception ex)
            {
                AppServices.Logger?.Error($"[DashboardView.LoadDashboardData] {ex}");
            }
        }

        private void OnCardPointerPressed(object sender, PointerPressedEventArgs e)
        {
            if (sender is Control ctrl)
                HandleCardPointerPressed(ctrl, ctrl.DataContext as AccountViewModel, e);
        }

        internal void HandleCardPointerPressed(Control anchor, AccountViewModel? avm, PointerPressedEventArgs e)
        {
            if (avm == null)
                return;

            var props = e.GetCurrentPoint(this).Properties;
            if (props.IsRightButtonPressed)
            {
                ShowAccountContextMenu(anchor, avm);
                e.Handled = true;
                return;
            }

            if (!props.IsLeftButtonPressed)
                return;

            if (e.Source is Visual visual && IsInsideInteractiveControl(visual))
                return;

            SetKeyboardFocusedAccount(avm);
            ListViewportHost?.Focus();
            QueueDeferredCardClick(avm, e.KeyModifiers.HasFlag(KeyModifiers.Shift));
        }

        private long _lastRowDoubleTapTimestamp;

        internal static bool IsInsideInteractiveControl(Visual? visual)
            => MarqueeSelectionOverlay.IsInteractiveControl(visual);

        internal static bool ShouldTriggerDoubleTap(object? source, ref long lastTimestamp, long debounceMs = 500)
        {
            if (source is Visual visual && IsInsideInteractiveControl(visual))
                return false;

            long now = System.Diagnostics.Stopwatch.GetTimestamp();
            long prev = lastTimestamp;
            long minTicks = (System.Diagnostics.Stopwatch.Frequency * debounceMs) / 1000L;
            if (prev != 0 && (now - prev) < minTicks)
                return false;

            lastTimestamp = now;
            return true;
        }

        private void OnCardDoubleTapped(object sender, TappedEventArgs e)
        {
            if (!ShouldTriggerDoubleTap(e.Source, ref _lastRowDoubleTapTimestamp))
            {
                if (e.Source is not Visual v || !IsInsideInteractiveControl(v))
                    e.Handled = true;
                return;
            }

            e.Handled = true;
            ExecuteMenuCommand(sender, ViewModel.LaunchBar.LaunchAccountCommand);
        }

        private void OnCardLaunchClicked(object sender, RoutedEventArgs e)
            => ExecuteMenuCommand(sender, ViewModel.LaunchBar.PrimaryCardActionCommand);

        private void OnCardFocusWindowClicked(object sender, RoutedEventArgs e)
            => ExecuteMenuCommand(sender, ViewModel.ToolsStatus.FocusAccountWindowCommand);

        private void OnCardCloseClientClicked(object sender, RoutedEventArgs e)
            => ExecuteMenuCommand(sender, ViewModel.LaunchBar.CloseAccountRobloxCommand);

        private void OnCardViewBanClicked(object sender, RoutedEventArgs e)
            => ExecuteMenuCommand(sender, ViewModel.LaunchBar.ViewBanAccountCommand);

        private void OnTableRowJoinClicked(object sender, RoutedEventArgs e)
            => ExecuteMenuCommand(sender, ViewModel.LaunchBar.JoinServerAccountCommand);

        internal void ShowAccountContextMenu(Control anchor, AccountViewModel avm)
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
                AppServices.Logger?.Error($"[ShowAccountContextMenu] {ex}");
            }
        }
    }
}
