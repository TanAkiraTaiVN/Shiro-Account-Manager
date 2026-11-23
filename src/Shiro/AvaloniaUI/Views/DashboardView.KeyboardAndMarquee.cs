#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Presenters;
using Avalonia.Input;
using Avalonia.VisualTree;
using Shiro.AvaloniaUI.Controls;
using Shiro.ViewModels;

namespace Shiro.AvaloniaUI.Views
{
    public partial class DashboardView
    {
        private const double CardRowHeight = 148.0;
        private const double CardHeight = 136.0;
        private const double CardHorizontalMargin = 5.0;
        private const double TableRowHeight = 40.0;

        private AccountViewModel? _pendingClickAccount;
        private bool _pendingClickShift;
        private HashSet<AccountViewModel>? _marqueeBaselineSelection;
        private AccountViewModel[]? _lastMarqueeTargets;
        private bool _lastMarqueeAdditive;
        private Point _marqueeAnchorOverlayPoint;
        private Point _marqueeLastOverlayPoint;
        private Vector _marqueeAnchorScrollOffset;

        internal void QueueDeferredCardClick(AccountViewModel account, bool isShiftPressed)
        {
            _pendingClickAccount = account;
            _pendingClickShift = isShiftPressed;
        }

        private void OnListPointerPressed(object? sender, PointerPressedEventArgs e)
        {
            if (e.Handled || MarqueeOverlay == null || ListViewportHost == null)
                return;
            if (!e.GetCurrentPoint(ListViewportHost).Properties.IsLeftButtonPressed)
                return;
            if (e.Source is Visual src && IsInsideInteractiveControl(src))
                return;

            ListViewportHost.Focus();
            Point pos = e.GetPosition(MarqueeOverlay);
            if (!MarqueeOverlay.TryArm(pos, e.Source))
                return;

            _marqueeAnchorOverlayPoint = pos;
            _marqueeLastOverlayPoint = pos;
            _marqueeAnchorScrollOffset = GetActiveListScrollOffset();
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

            Point pos = e.GetPosition(MarqueeOverlay);
            if (!MarqueeOverlay.UpdateDrag(pos, out bool justActivated))
                return;

            if (justActivated)
            {
                _pendingClickAccount = null;
                _pendingClickShift = false;
                if (!ReferenceEquals(e.Pointer.Captured, ListViewportHost))
                    e.Pointer.Capture(ListViewportHost);
            }

            _marqueeLastOverlayPoint = pos;
            e.Handled = true;
            ApplyMarqueeSelection(e.KeyModifiers.HasFlag(KeyModifiers.Control), justActivated);
        }

        private void OnListPointerReleased(object? sender, PointerReleasedEventArgs e)
        {
            bool wasSelecting = MarqueeOverlay?.IsSelecting == true;
            AccountViewModel? pendingAccount = !wasSelecting ? _pendingClickAccount : null;
            bool pendingShift = _pendingClickShift || e.KeyModifiers.HasFlag(KeyModifiers.Shift);
            FinishMarqueeSelection(e.Pointer);

            if (wasSelecting)
            {
                e.Handled = true;
            }
            else if (pendingAccount != null)
            {
                ViewModel.AccountsVm.HandleAccountClickSelection(pendingAccount, pendingShift);
            }
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
            _pendingClickAccount = null;
            _pendingClickShift = false;
        }

        private void ApplyMarqueeSelection(bool additive, bool forceApply)
        {
            if (MarqueeOverlay == null)
                return;

            Rect effectiveRect = ComputeEffectiveMarqueeRect(MarqueeOverlay.SelectionRect);
            List<AccountViewModel> hit = CollectIntersectingAccounts(effectiveRect);
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

        private Rect ComputeEffectiveMarqueeRect(Rect visualRect)
        {
            Vector curScroll = GetActiveListScrollOffset();
            if (Math.Abs(curScroll.X - _marqueeAnchorScrollOffset.X) <= 0.1 &&
                Math.Abs(curScroll.Y - _marqueeAnchorScrollOffset.Y) <= 0.1)
            {
                return visualRect;
            }

            Point shiftedAnchor = new(
                _marqueeAnchorOverlayPoint.X + (_marqueeAnchorScrollOffset.X - curScroll.X),
                _marqueeAnchorOverlayPoint.Y + (_marqueeAnchorScrollOffset.Y - curScroll.Y));
            return MarqueeSelectionOverlay.ComputeNormalizedRect(shiftedAnchor, _marqueeLastOverlayPoint);
        }

        private Vector GetActiveListScrollOffset()
        {
            ItemsControl? active = ViewModel.AccountsVm.IsCardView ? CardsItemsControl : TableItemsControl;
            return active?.GetVisualDescendants().OfType<ScrollViewer>().FirstOrDefault()?.Offset ?? default;
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

        internal List<AccountViewModel> CollectIntersectingAccounts(Rect selRect)
        {
            var hits = new List<AccountViewModel>();
            if (MarqueeOverlay == null || selRect.Width <= 0.0 || selRect.Height <= 0.0)
                return hits;

            var seen = new HashSet<AccountViewModel>(ReferenceEqualityComparer.Instance);
            if (ViewModel.AccountsVm.IsCardView && CardsItemsControl != null)
                CollectCardViewIntersections(selRect, hits, seen);
            else if (TableItemsControl != null)
                CollectTableViewIntersections(selRect, hits, seen);

            return hits;
        }

        private void CollectCardViewIntersections(Rect selRect, List<AccountViewModel> hits, HashSet<AccountViewModel> seen)
        {
            if (CardsItemsControl == null) return;
            var rows = ViewModel.AccountsVm.FilteredAccountRows;
            if (!ResolveContentViewport(CardsItemsControl, 14.0, out Point contentOrigin, out double rowWidth, out Rect clipRect))
                return;

            Rect clippedSel = selRect.Intersect(clipRect);
            if (clippedSel.Width <= 0.0 || clippedSel.Height <= 0.0)
                return;

            foreach (var card in CardsItemsControl.GetRealizedContainers()
                .Where(c => c.IsEffectivelyVisible)
                .SelectMany(c => c.GetVisualDescendants().OfType<AccountCardView>()))
            {
                if (card.IsEffectivelyVisible && card.DataContext is AccountViewModel avm &&
                    HitsOverlay(card.FindControl<Border>("CardRoot") ?? (Control)card, clippedSel, clipRect) && seen.Add(avm))
                {
                    hits.Add(avm);
                }
            }

            if (rows.Count == 0)
                return;

            double contentTopY = contentOrigin.Y;
            int firstRow = Math.Max(0, (int)Math.Floor((clippedSel.Top - contentTopY) / CardRowHeight));
            int lastRow = Math.Min(rows.Count - 1, (int)Math.Floor((clippedSel.Bottom - contentTopY) / CardRowHeight));
            for (int r = firstRow; r <= lastRow; r++)
            {
                if (CardsItemsControl.ContainerFromIndex(r) is { IsEffectivelyVisible: true })
                    continue;
                double cardTop = contentTopY + r * CardRowHeight;
                if (cardTop + CardHeight <= clippedSel.Top || cardTop >= clippedSel.Bottom)
                    continue;

                AccountRowViewModel rowVm = rows[r];
                int cols = Math.Max(1, rowVm.ColumnCount);
                double slotWidth = rowWidth / cols;
                for (int c = 0; c < cols; c++)
                {
                    AccountViewModel? item = c switch { 0 => rowVm.Item1, 1 => rowVm.Item2, 2 => rowVm.Item3, 3 => rowVm.Item4, _ => null };
                    if (item == null) continue;
                    Rect cardRect = new Rect(contentOrigin.X + c * slotWidth + CardHorizontalMargin, cardTop, Math.Max(0.0, slotWidth - CardHorizontalMargin * 2.0), CardHeight).Intersect(clipRect);
                    if (MarqueeSelectionOverlay.Intersects(clippedSel, cardRect) && seen.Add(item))
                        hits.Add(item);
                }
            }
        }

        private void CollectTableViewIntersections(Rect selRect, List<AccountViewModel> hits, HashSet<AccountViewModel> seen)
        {
            if (TableItemsControl == null) return;
            var items = ViewModel.AccountsVm.FilteredAccounts;
            if (!ResolveContentViewport(TableItemsControl, 12.0, out Point contentOrigin, out double rowWidth, out Rect clipRect))
                return;

            Rect clippedSel = selRect.Intersect(clipRect);
            if (clippedSel.Width <= 0.0 || clippedSel.Height <= 0.0)
                return;

            foreach (var row in TableItemsControl.GetRealizedContainers())
            {
                if (row.IsEffectivelyVisible && row.DataContext is AccountViewModel avm && HitsOverlay(row, clippedSel, clipRect) && seen.Add(avm))
                    hits.Add(avm);
            }

            if (items.Count == 0)
                return;

            double contentTopY = contentOrigin.Y;
            int firstRow = Math.Max(0, (int)Math.Floor((clippedSel.Top - contentTopY) / TableRowHeight));
            int lastRow = Math.Min(items.Count - 1, (int)Math.Floor((clippedSel.Bottom - contentTopY) / TableRowHeight));
            for (int r = firstRow; r <= lastRow; r++)
            {
                if (TableItemsControl.ContainerFromIndex(r) is { IsEffectivelyVisible: true })
                    continue;
                Rect rowRect = new Rect(contentOrigin.X, contentTopY + r * TableRowHeight, rowWidth, TableRowHeight).Intersect(clipRect);
                if (MarqueeSelectionOverlay.Intersects(clippedSel, rowRect) && seen.Add(items[r]))
                    hits.Add(items[r]);
            }
        }

        private bool HitsOverlay(Control target, Rect selRect, Rect clipRect)
        {
            if (MarqueeOverlay == null || target.Bounds.Width <= 0.0 || target.Bounds.Height <= 0.0)
                return false;
            Point? origin = target.TranslatePoint(default, MarqueeOverlay);
            if (!origin.HasValue)
                return false;
            Rect clippedItem = new Rect(origin.Value, target.Bounds.Size).Intersect(clipRect);
            return MarqueeSelectionOverlay.Intersects(selRect, clippedItem);
        }

        private bool ResolveContentViewport(
            ItemsControl ic,
            double insetRight,
            out Point contentOrigin,
            out double contentWidth,
            out Rect clipRect)
        {
            contentOrigin = default;
            contentWidth = 0.0;
            clipRect = default;
            if (MarqueeOverlay == null)
                return false;

            var sv = ic.GetVisualDescendants().OfType<ScrollViewer>().FirstOrDefault();
            var scp = sv?.GetVisualDescendants().OfType<Avalonia.Controls.Presenters.ScrollContentPresenter>().FirstOrDefault();
            var presenter = ic.GetVisualDescendants().OfType<ItemsPresenter>().FirstOrDefault();
            Vector scrollOffset = sv?.Offset ?? default;

            Point? vpOrigin = ((Control?)scp ?? ic).TranslatePoint(default, MarqueeOverlay) ?? ic.TranslatePoint(default, MarqueeOverlay);
            if (presenter != null && presenter.TranslatePoint(default, MarqueeOverlay) is { } pOrigin)
            {
                bool usesLogicalScroll = presenter.Panel is Avalonia.Controls.Primitives.ILogicalScrollable { IsLogicalScrollEnabled: true };
                contentOrigin = usesLogicalScroll ? new Point(pOrigin.X, pOrigin.Y - scrollOffset.Y) : pOrigin;
                contentWidth = presenter.Bounds.Width > 0.0 ? presenter.Bounds.Width : Math.Max(0.0, ic.Bounds.Width - insetRight);
            }
            else if (vpOrigin is { } icOrigin)
            {
                contentOrigin = new Point(icOrigin.X, icOrigin.Y - scrollOffset.Y);
                contentWidth = sv?.Viewport.Width > 0.0 ? sv.Viewport.Width : Math.Max(0.0, ic.Bounds.Width - insetRight);
            }
            else
            {
                return false;
            }

            if (contentWidth <= 0.0)
                return false;

            double vpTop = vpOrigin?.Y ?? contentOrigin.Y;
            double vpHeight = sv?.Viewport.Height > 0.0
                ? sv.Viewport.Height
                : (ic.Bounds.Height > 0.0 ? ic.Bounds.Height : MarqueeOverlay.Bounds.Height);
            double downDelta = MarqueeOverlay.IsSelecting ? Math.Max(0.0, scrollOffset.Y - _marqueeAnchorScrollOffset.Y) : 0.0;
            double upDelta = MarqueeOverlay.IsSelecting ? Math.Max(0.0, _marqueeAnchorScrollOffset.Y - scrollOffset.Y) : 0.0;
            double allowedTop = vpTop - downDelta;
            double allowedBottom = vpTop + vpHeight + upDelta;
            clipRect = new Rect(contentOrigin.X, allowedTop, contentWidth, Math.Max(0.0, allowedBottom - allowedTop));
            return clipRect.Height > 0.0;
        }
    }
}
