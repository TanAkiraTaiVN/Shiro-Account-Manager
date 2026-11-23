#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Shiro.AvaloniaUI.Controls;
using Shiro.AvaloniaUI.Views;
using Shiro.ViewModels;
using Xunit;

namespace Shiro.Tests.Ui
{
    public sealed partial class MarqueeAndKeyboardNavTests
    {
        [AvaloniaFact]
        public void DashboardView_Virtualized200Accounts_MarqueeSelectsUnrealizedRows_AndRecyclingSyncsFocus()
        {
            var accounts = Enumerable.Range(1, 200)
                .Select(i => new Account { UserID = 6000 + i, Username = $"VirtUser_{i:D3}", Valid = true, SecurityToken = "tok" })
                .ToList();

            using var host = CreateMountedDashboard(accounts, width: 1020, height: 640);
            var view = host.View;
            var vm = view.ViewModel;
            vm.AccountsVm.CardColumns = 3;
            Dispatcher.UIThread.RunJobs();

            // Card view scroll-during-drag selects unrealized rows scrolled above viewport
            var cards = view.FindControl<ItemsControl>("CardsItemsControl")!;
            var cardSv = cards.GetVisualDescendants().OfType<ScrollViewer>().First();
            var overlay = view.FindControl<MarqueeSelectionOverlay>("MarqueeOverlay")!;
            Assert.Null(cards.ContainerFromIndex(20));

            Point cardStart = cards.TranslatePoint(new Point(40, 20), host.Window)!.Value;
            Point cardEnd = cards.TranslatePoint(new Point(860, 260), host.Window)!.Value;
            host.Window.MouseDown(cardStart, MouseButton.Left);
            cardSv.Offset = new Vector(0, 2200);
            Dispatcher.UIThread.RunJobs();
            host.Window.MouseMove(cardEnd, RawInputModifiers.LeftMouseButton);
            Dispatcher.UIThread.RunJobs();
            Assert.True(overlay.IsSelecting);
            Assert.Null(cards.ContainerFromIndex(0));
            Assert.True(vm.AccountsVm.FilteredAccounts[0].IsSelected);
            Assert.True(vm.AccountsVm.FilteredAccounts[1].IsSelected);
            Assert.True(vm.AccountsVm.FilteredAccounts[2].IsSelected);
            Assert.True(vm.AccountsVm.SelectedCount >= 45, $"Expected >= 45 card selections across scrolled range, got {vm.AccountsVm.SelectedCount}");
            host.Window.MouseUp(cardEnd, MouseButton.Left);
            vm.AccountsVm.ApplySelectionBatch(Array.Empty<AccountViewModel>(), additive: false);
            Dispatcher.UIThread.RunJobs();

            // Compact table mode: scroll during marquee drag selects unrealized offscreen rows
            vm.AccountsVm.IsCompactView = true;
            Dispatcher.UIThread.RunJobs();
            var table = view.FindControl<ItemsControl>("TableItemsControl")!;
            var sv = table.GetVisualDescendants().OfType<ScrollViewer>().First();
            Point startWin = table.TranslatePoint(new Point(180, 20), host.Window)!.Value;
            Point endWin = table.TranslatePoint(new Point(360, 180), host.Window)!.Value;

            host.Window.MouseDown(startWin, MouseButton.Left);
            sv.Offset = new Vector(0, 1600);
            Dispatcher.UIThread.RunJobs();
            host.Window.MouseMove(endWin, RawInputModifiers.LeftMouseButton);
            Dispatcher.UIThread.RunJobs();
            Assert.True(overlay.IsSelecting);
            Assert.Null(table.ContainerFromIndex(2));
            Assert.True(vm.AccountsVm.FilteredAccounts[2].IsSelected);
            Assert.True(vm.AccountsVm.SelectedCount >= 42, $"Expected >= 42 selected across scrolled range, got {vm.AccountsVm.SelectedCount}");
            host.Window.MouseUp(endWin, MouseButton.Left);

            // Keyboard focus stays accurate with resolved styling after scrolling compact table 100 rows down
            view.SetKeyboardFocusedAccount(vm.AccountsVm.FilteredAccounts[100], bringIntoView: true, index: 100, columns: 1);
            Dispatcher.UIThread.RunJobs();
            var row100 = table.ContainerFromIndex(100)!.GetVisualDescendants().OfType<Border>()
                .First(b => b.Classes.Contains("CompactTableRow"));
            Assert.Contains("kbd-focused", row100.Classes);
            Assert.Equal(new Thickness(1.5), row100.BorderThickness);
            var oldRow2 = table.ContainerFromIndex(2)?.GetVisualDescendants().OfType<Border>()
                .FirstOrDefault(b => b.Classes.Contains("CompactTableRow"));
            if (oldRow2 != null)
            {
                Assert.DoesNotContain("kbd-focused", oldRow2.Classes);
                Assert.Equal(new Thickness(1, 0, 1, 1), oldRow2.BorderThickness);
            }
            Assert.Empty(host.Sink.Errors);
        }

        [AvaloniaFact]
        public void DashboardView_CompactHeaderDrag_DoesNotSelectScrolledAboveRows()
        {
            var accounts = Enumerable.Range(1, 200)
                .Select(i => new Account { UserID = 7000 + i, Username = $"HeaderGuard_{i:D3}", Valid = true, SecurityToken = "tok" })
                .ToList();

            using var host = CreateMountedDashboard(accounts, width: 1020, height: 640);
            var view = host.View;
            var vm = view.ViewModel;
            vm.AccountsVm.IsCompactView = true;
            Dispatcher.UIThread.RunJobs();

            var table = view.FindControl<ItemsControl>("TableItemsControl")!;
            var sv = table.GetVisualDescendants().OfType<ScrollViewer>().First();
            var overlay = view.FindControl<MarqueeSelectionOverlay>("MarqueeOverlay")!;
            var tableHostGrid = Assert.IsType<Grid>(table.Parent);
            var headerBorder = tableHostGrid.Children.OfType<Border>().First(b => Grid.GetRow(b) == 0);

            // Scroll so VirtualizingStackPanel cache retains rows visually above the TableItemsControl viewport
            sv.Offset = new Vector(0, 400);
            Dispatcher.UIThread.RunJobs();

            double headerYBottom = Math.Max(14.0, headerBorder.Bounds.Height - 4.0);
            Point headerStart = headerBorder.TranslatePoint(new Point(40, 8), host.Window)!.Value;
            Point headerEnd = headerBorder.TranslatePoint(new Point(320, headerYBottom), host.Window)!.Value;

            host.Window.MouseDown(headerStart, MouseButton.Left);
            host.Window.MouseMove(headerEnd, RawInputModifiers.LeftMouseButton);
            Dispatcher.UIThread.RunJobs();
            Assert.True(overlay.IsSelecting);
            Assert.Equal(0, vm.AccountsVm.SelectedCount);
            host.Window.MouseUp(headerEnd, MouseButton.Left);
            Assert.Equal(0, vm.AccountsVm.SelectedCount);

            // Dragging inside the visible table viewport below the header still selects rows normally
            Point bodyStart = table.TranslatePoint(new Point(180, 10), host.Window)!.Value;
            Point bodyEnd = table.TranslatePoint(new Point(360, 120), host.Window)!.Value;
            host.Window.MouseDown(bodyStart, MouseButton.Left);
            host.Window.MouseMove(bodyEnd, RawInputModifiers.LeftMouseButton);
            Dispatcher.UIThread.RunJobs();
            Assert.True(overlay.IsSelecting);
            Assert.True(vm.AccountsVm.SelectedCount >= 2, $"Expected >= 2 selected rows in visible table body, got {vm.AccountsVm.SelectedCount}");
            host.Window.MouseUp(bodyEnd, MouseButton.Left);
            Assert.Empty(host.Sink.Errors);
        }
    }
}
