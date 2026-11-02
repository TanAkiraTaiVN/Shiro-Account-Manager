#nullable enable
using System;
using System.Collections.Generic;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.VisualTree;
using Shiro.Classes;
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

            if (TableItemsControl != null)
            {
                TableItemsControl.AddHandler(PointerPressedEvent, OnTablePointerPressedTunnel, RoutingStrategies.Tunnel);
            }

            SizeChanged += (_, e) =>
            {
                if (e.NewSize.Width > 0)
                {
                    ViewModel.CardColumns = MainViewModel.CalculateResponsiveCardColumns(e.NewSize.Width);
                }
            };

            Loaded += (s, e) =>
            {
                if (Bounds.Width > 0)
                {
                    ViewModel.CardColumns = MainViewModel.CalculateResponsiveCardColumns(Bounds.Width);
                }
                LoadDashboardData();
                if (!_placeIdHooked && TxtPlaceId != null)
                {
                    _placeIdHooked = true;
                    TxtPlaceId.LostFocus += (_, _) => ViewModel.NormalizePlaceIdInput(TxtPlaceId.Text);
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
                            if (CardsItemsControl != null && CardsItemsControl.ItemCount > 0) CardsItemsControl.ScrollIntoView(0);
                            if (TableItemsControl != null && TableItemsControl.ItemCount > 0) TableItemsControl.ScrollIntoView(0);
                        }
                        catch (Exception ex)
                        {
                            Program.Logger?.Debug($"Scroll reset on tab change threw: {ex.Message}");
                        }
                    });
                }
            };
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
                    ViewModel.HandleAccountClickSelection(avm, e.KeyModifiers.HasFlag(KeyModifiers.Shift));
                    e.Handled = true;
                }
            }
        }

        public List<Account> GetSelectedAccounts()
        {
            return ViewModel?.GetSelectedAccounts() ?? new List<Account>();
        }

        public void LoadDashboardData(IReadOnlyList<Account>? accountsSnapshot = null)
        {
            try
            {
                if (ViewModel != null && string.IsNullOrWhiteSpace(ViewModel.PlaceId) && !string.IsNullOrWhiteSpace(AppSettings.CurrentPlaceId))
                {
                    ViewModel.PlaceId = AppSettings.CurrentPlaceId;
                }

                var accounts = accountsSnapshot ?? AppServices.AccountRepository.GetAll();
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

            ViewModel.HandleAccountClickSelection(avm, e.KeyModifiers.HasFlag(KeyModifiers.Shift));
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

        private void OnCardDoubleTapped(object sender, TappedEventArgs e)
            => ExecuteMenuCommand(sender, ViewModel.LaunchAccountCommand);

        private void OnCardLaunchClicked(object sender, RoutedEventArgs e)
            => ExecuteMenuCommand(sender, ViewModel.PrimaryCardActionCommand);

        private void OnCardFocusWindowClicked(object sender, RoutedEventArgs e)
            => ExecuteMenuCommand(sender, ViewModel.FocusAccountWindowCommand);

        private void OnCardCloseClientClicked(object sender, RoutedEventArgs e)
            => ExecuteMenuCommand(sender, ViewModel.CloseAccountRobloxCommand);

        private void OnCardViewBanClicked(object sender, RoutedEventArgs e)
            => ExecuteMenuCommand(sender, ViewModel.ViewBanAccountCommand);

        private void OnTableRowJoinClicked(object sender, RoutedEventArgs e)
            => ExecuteMenuCommand(sender, ViewModel.JoinServerAccountCommand);

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
