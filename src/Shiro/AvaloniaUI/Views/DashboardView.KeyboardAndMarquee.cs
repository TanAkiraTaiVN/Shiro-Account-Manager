#nullable enable
using System;
using System.Collections.Generic;
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
        private HashSet<AccountViewModel>? _marqueeBaselineSelection;
        private AccountViewModel[]? _lastMarqueeTargets;
        private bool _lastMarqueeAdditive;

        internal AccountViewModel? FocusedAccount => ResolveFocusedAccount(out _);
        internal int FocusedAccountIndex { get { ResolveFocusedAccount(out int idx); return idx; } }

        internal bool IsKeyboardFocusedAccount(AccountViewModel? account)
            => account != null && ReferenceEquals(_focusedAccount, account);

        private void OnListPointerPressed(object? sender, PointerPressedEventArgs e)
        {
            if (e.Handled || MarqueeOverlay == null || ListViewportHost == null)
                return;
            if (!e.GetCurrentPoint(ListViewportHost).Properties.IsLeftButtonPressed)
                return;
            if (e.Source is Visual src && IsInsideInteractiveControl(src))
                return;

            ListViewportHost.Focus();
            if (!MarqueeOverlay.TryArm(e.GetPosition(MarqueeOverlay), e.Source))
                return;

            _marqueeBaselineSelection = new HashSet<AccountViewModel>(
                ViewModel.AccountsVm.GetSelectedAccountViewModels(),
                ReferenceEqualityComparer.Instance);
            _lastMarqueeTargets = null;
        }

        private void OnListPointerMoved(object? sender, PointerEventArgs e)
        {
            if (MarqueeOverlay == null || ListViewportHost == null)
                return;
            if (!e.GetCurrentPoint(ListViewportHost).Properties.IsLeftButtonPressed)
            {
                if (MarqueeOverlay.IsSelecting)
                    FinishMarqueeSelection(e.Pointer);
                return;
            }

            if (!MarqueeOverlay.UpdateDrag(e.GetPosition(MarqueeOverlay), out bool justActivated))
                return;

            if (justActivated && !ReferenceEquals(e.Pointer.Captured, ListViewportHost))
                e.Pointer.Capture(ListViewportHost);

            e.Handled = true;
            ApplyMarqueeSelection(e.KeyModifiers.HasFlag(KeyModifiers.Control), justActivated);
        }

        private void OnListPointerReleased(object? sender, PointerReleasedEventArgs e)
        {
            bool wasSelecting = MarqueeOverlay?.IsSelecting == true;
            FinishMarqueeSelection(e.Pointer);
            if (wasSelecting)
                e.Handled = true;
        }

        private void OnListPointerCaptureLost(object? sender, PointerCaptureLostEventArgs e)
            => FinishMarqueeSelection(null);

        private void FinishMarqueeSelection(IPointer? pointer)
        {
            if (pointer != null && ListViewportHost != null && ReferenceEquals(pointer.Captured, ListViewportHost))
                pointer.Capture(null);

            MarqueeOverlay?.CompleteSelection();
            _marqueeBaselineSelection = null;
            _lastMarqueeTargets = null;
        }

        private void ApplyMarqueeSelection(bool additive, bool forceApply)
        {
            if (MarqueeOverlay == null)
                return;

            List<AccountViewModel> hit = CollectIntersectingAccounts(MarqueeOverlay.SelectionRect);
            AccountViewModel[] targets;
            if (additive && _marqueeBaselineSelection is { Count: > 0 })
            {
                var merged = new HashSet<AccountViewModel>(_marqueeBaselineSelection, ReferenceEqualityComparer.Instance);
                foreach (var item in hit)
                    merged.Add(item);
                targets = merged.ToArray();
            }
            else
            {
                targets = hit.ToArray();
            }

            if (!forceApply && _lastMarqueeAdditive == additive && AreSameTargets(_lastMarqueeTargets, targets))
                return;

            _lastMarqueeAdditive = additive;
            _lastMarqueeTargets = targets;
            ViewModel.AccountsVm.ApplySelectionBatch(targets, additive: false);
        }

        private static bool AreSameTargets(AccountViewModel[]? prev, AccountViewModel[] next)
        {
            if (prev == null || prev.Length != next.Length)
                return false;
            for (int i = 0; i < prev.Length; i++)
            {
                if (!ReferenceEquals(prev[i], next[i]))
                    return false;
            }
            return true;
        }

        private List<AccountViewModel> CollectIntersectingAccounts(Rect selRect)
        {
            var hits = new List<AccountViewModel>();
            if (MarqueeOverlay == null || selRect.Width <= 0.0 || selRect.Height <= 0.0)
                return hits;

            bool HitsOverlay(Control target)
            {
                if (target.Bounds.Width <= 0.0 || target.Bounds.Height <= 0.0)
                    return false;
                Point? origin = target.TranslatePoint(default, MarqueeOverlay);
                return origin.HasValue && MarqueeSelectionOverlay.Intersects(selRect, new Rect(origin.Value, target.Bounds.Size));
            }

            if (ViewModel.AccountsVm.IsCardView && CardsItemsControl != null)
            {
                foreach (var card in CardsItemsControl.GetRealizedContainers()
                    .Where(c => c.IsEffectivelyVisible)
                    .SelectMany(c => c.GetVisualDescendants().OfType<AccountCardView>()))
                {
                    if (card.IsEffectivelyVisible && card.DataContext is AccountViewModel avm &&
                        HitsOverlay(card.FindControl<Border>("CardRoot") ?? (Control)card))
                    {
                        hits.Add(avm);
                    }
                }
            }
            else if (TableItemsControl != null)
            {
                foreach (var row in TableItemsControl.GetRealizedContainers())
                {
                    if (row.IsEffectivelyVisible && row.DataContext is AccountViewModel avm && HitsOverlay(row))
                        hits.Add(avm);
                }
            }

            return hits;
        }

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
                    if (!focused.IsLaunching)
                        ViewModel.LaunchBar.LaunchAccountCommand.Execute(focused);
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
                {
                    if (Match(cur))
                        return true;
                }

                for (StyledElement? cur = visual; cur != null; cur = cur.Parent)
                {
                    if (Match(cur))
                        return true;
                }

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

        private void SetKeyboardFocusedAccount(AccountViewModel? account, bool bringIntoView = false, int index = -1, int columns = 1)
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
                    UpdateTableRowFocusClass(container);
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

        private void UpdateTableRowFocusClass(Control? container)
        {
            if (container == null)
                return;
            bool focused = _focusedAccount != null && ReferenceEquals(container.DataContext, _focusedAccount);
            var border = container as Border ?? container.GetVisualDescendants().OfType<Border>().FirstOrDefault();
            border?.Classes.Set("TableRowKbdFocus", focused);
        }
    }
}
