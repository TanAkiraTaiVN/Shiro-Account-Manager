#nullable enable
using System;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.VisualTree;
using Shiro.AvaloniaUI.Controls;
using Shiro.Services;
using Shiro.ViewModels;

namespace Shiro.AvaloniaUI.Views
{
    public partial class DashboardView
    {
        public event EventHandler<AccountViewModel>? AccountInspectRequested;

        private AccountViewModel? _focusedAccount;

        internal AccountViewModel? FocusedAccount => ResolveFocusedAccount(out _);
        internal int FocusedAccountIndex { get { ResolveFocusedAccount(out int idx); return idx; } }

        internal bool IsKeyboardFocusedAccount(AccountViewModel? account)
            => account != null && ReferenceEquals(_focusedAccount, account);

        protected override void OnKeyDown(KeyEventArgs e)
        {
            base.OnKeyDown(e);
            if (e.Handled)
                return;

            if (e.Key == Key.Escape && MarqueeOverlay is { IsSelecting: true })
            {
                if (_marqueeBaselineSelection != null)
                    ViewModel.AccountsVm.ApplySelectionBatch(_marqueeBaselineSelection.ToArray(), additive: false);
                FinishMarqueeSelection(null);
                e.Handled = true;
                return;
            }

            if (ShouldIgnoreKeyboardNavigation(e))
                return;

            var filtered = ViewModel.AccountsVm.FilteredAccounts;
            if (filtered.Count == 0)
                return;

            if (e.KeyModifiers == KeyModifiers.Control && e.Key == Key.A)
            {
                ViewModel.AccountsVm.ApplySelectionBatch(filtered.ToArray(), additive: false);
                e.Handled = true;
                return;
            }

            if (e.Key is Key.Up or Key.Down or Key.Left or Key.Right)
            {
                if (e.KeyModifiers != KeyModifiers.None)
                    return;

                ResolveFocusedAccount(out int curIdx);
                int cols = ResolveGridColumnCount();
                int nextIdx = StepGridIndex(curIdx, e.Key, cols, filtered.Count);
                if (nextIdx >= 0 && nextIdx < filtered.Count)
                {
                    SetKeyboardFocusedAccount(filtered[nextIdx], bringIntoView: true, nextIdx, cols);
                    e.Handled = true;
                }
                return;
            }

            AccountViewModel? focused = ResolveFocusedAccount(out int focusedIdx);
            if (e.Key == Key.Space)
            {
                focused ??= filtered[0];
                SetKeyboardFocusedAccount(focused, bringIntoView: false, focusedIdx >= 0 ? focusedIdx : 0);
                ViewModel.AccountsVm.HandleAccountClickSelection(focused, e.KeyModifiers.HasFlag(KeyModifiers.Shift));
                e.Handled = true;
            }
            else if (e.Key == Key.Enter)
            {
                focused ??= filtered.FirstOrDefault(a => a.IsSelected) ?? filtered[0];
                if (e.KeyModifiers.HasFlag(KeyModifiers.Control))
                {
                    if (!focused.IsLaunching && ViewModel.LaunchBar.LaunchAccountCommand is { } launchCmd && launchCmd.CanExecute(focused))
                        launchCmd.Execute(focused);
                    e.Handled = true;
                }
                else if (e.KeyModifiers == KeyModifiers.None)
                {
                    SetKeyboardFocusedAccount(focused, bringIntoView: false, focusedIdx >= 0 ? focusedIdx : 0);
                    AccountInspectRequested?.Invoke(this, focused);
                    e.Handled = true;
                }
            }
        }

        private bool ShouldIgnoreKeyboardNavigation(KeyEventArgs e)
        {
            if (ViewModel.CommandPalette.IsCommandPaletteOpen || ViewModel.IsNotificationCenterOpen || CmbGroups?.IsDropDownOpen == true)
                return true;

            object? focused = TopLevel.GetTopLevel(this)?.FocusManager?.GetFocusedElement();
            static bool IsInputOrCombo(Visual? visual)
            {
                static bool Match(object? node) => node is TextBox or ComboBox or ComboBoxItem;
                for (Visual? cur = visual; cur != null; cur = cur.GetVisualParent())
                    if (Match(cur)) return true;
                for (StyledElement? cur = visual; cur != null; cur = cur.Parent)
                    if (Match(cur)) return true;
                return false;
            }

            return IsInputOrCombo(e.Source as Visual) || IsInputOrCombo(focused as Visual);
        }

        internal int ResolveGridColumnCount()
        {
            if (ViewModel.AccountsVm.IsCompactView)
                return 1;

            var panel = CardsItemsControl?.GetVisualDescendants()
                .OfType<UniformRowPanel>()
                .FirstOrDefault(p => p.IsVisible && p.Columns > 0);
            if (panel != null)
                return panel.Columns;

            if (ViewModel.AccountsVm.FilteredAccountRows.Count > 0)
                return Math.Clamp(ViewModel.AccountsVm.FilteredAccountRows[0].ColumnCount, 1, 4);

            double width = CardsItemsControl?.Bounds.Width > 0 ? CardsItemsControl.Bounds.Width : Bounds.Width;
            return ViewModel.AccountsVm.CardColumns > 0
                ? Math.Clamp(ViewModel.AccountsVm.CardColumns, 2, 4)
                : AccountCollectionViewModel.CalculateResponsiveCardColumns(width);
        }

        internal static int StepGridIndex(int currentIndex, Key key, int columnCount, int itemCount)
        {
            if (itemCount <= 0)
                return -1;

            int cols = Math.Max(1, columnCount);
            if (currentIndex < 0)
                return 0;

            int idx = Math.Clamp(currentIndex, 0, itemCount - 1);
            int lastIdx = itemCount - 1;
            return key switch
            {
                Key.Left => cols > 1 ? Math.Max(0, idx - 1) : idx,
                Key.Right => cols > 1 ? Math.Min(lastIdx, idx + 1) : idx,
                Key.Up => idx - cols >= 0 ? idx - cols : idx,
                Key.Down => (idx / cols) < (lastIdx / cols) ? Math.Min(lastIdx, idx + cols) : idx,
                _ => idx
            };
        }

        private AccountViewModel? ResolveFocusedAccount(out int index)
        {
            index = _focusedAccount != null ? ViewModel.AccountsVm.FilteredAccounts.IndexOf(_focusedAccount) : -1;
            if (index < 0)
                _focusedAccount = null;
            return _focusedAccount;
        }

        internal void SetKeyboardFocusedAccount(AccountViewModel? account, bool bringIntoView = false, int index = -1, int columns = 1)
        {
            _focusedAccount = account;
            if (CardsItemsControl != null)
            {
                foreach (var card in CardsItemsControl.GetVisualDescendants().OfType<AccountCardView>())
                    card.SetKeyboardFocused(_focusedAccount != null && ReferenceEquals(card.DataContext, _focusedAccount));
            }
            if (TableItemsControl != null)
            {
                foreach (var container in TableItemsControl.GetRealizedContainers())
                    UpdateTableRowFocusClass(container, isClearing: false);
            }

            if (!bringIntoView || index < 0)
                return;

            try
            {
                if (ViewModel.AccountsVm.IsCardView && CardsItemsControl != null)
                {
                    int rowIdx = index / Math.Max(1, columns);
                    if (rowIdx < CardsItemsControl.ItemCount)
                        CardsItemsControl.ScrollIntoView(rowIdx);
                    CardsItemsControl.GetVisualDescendants().OfType<AccountCardView>()
                        .FirstOrDefault(c => ReferenceEquals(c.DataContext, _focusedAccount))?.BringIntoView();
                }
                else if (TableItemsControl != null && index < TableItemsControl.ItemCount)
                {
                    TableItemsControl.ScrollIntoView(index);
                    TableItemsControl.ContainerFromIndex(index)?.BringIntoView();
                }
            }
            catch (Exception ex)
            {
                AppServices.Logger?.Debug($"[DashboardView.BringAccountIntoView] {ex.Message}");
            }
        }

        private void OnTableRowDataContextChanged(object? sender, EventArgs e)
        {
            if (sender is Border border)
                border.Classes.Set("kbd-focused", IsKeyboardFocusedAccount(border.DataContext as AccountViewModel));
        }

        private void SyncCardRowFocusRings(Control? container, bool isClearing)
        {
            if (container == null)
                return;
            foreach (var card in container.GetVisualDescendants().OfType<AccountCardView>())
            {
                if (isClearing) card.SetKeyboardFocused(false);
                else card.SyncKeyboardFocusRing();
            }
        }

        private void UpdateTableRowFocusClass(Control? container, bool isClearing)
        {
            if (container == null)
                return;
            var border = container as Border ?? container.GetVisualDescendants().OfType<Border>()
                .FirstOrDefault(b => b.Classes.Contains("CompactTableRow"));
            if (border == null)
                return;
            bool focused = !isClearing && IsKeyboardFocusedAccount(border.DataContext as AccountViewModel);
            border.Classes.Set("kbd-focused", focused);
        }
    }
}
