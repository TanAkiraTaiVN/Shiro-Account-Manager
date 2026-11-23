#nullable enable
using System;
using System.ComponentModel;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Shiro.ViewModels;

namespace Shiro.AvaloniaUI.Views.Panels
{
    /// <summary>
    /// Command Palette 2.0 modal overlay: 2-column SkiaGlassSurface with scope filter chips,
    /// MRU recent commands, live preview pane, scroll tracking, and SearchTextBox routing.
    /// </summary>
    public partial class CommandPaletteOverlayPanel : UserControl
    {
        private CommandPaletteViewModel? _subscribedVm;

        public CommandPaletteOverlayPanel()
        {
            InitializeComponent();
        }

        public TextBox? SearchTextBox => TxtCommandPalette;

        protected override void OnDataContextChanged(EventArgs e)
        {
            base.OnDataContextChanged(e);
            SyncViewModelSubscription();
        }

        protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
        {
            base.OnAttachedToVisualTree(e);
            SyncViewModelSubscription();
        }

        protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
        {
            DetachViewModel();
            base.OnDetachedFromVisualTree(e);
        }

        private void SyncViewModelSubscription()
        {
            var nextVm = DataContext as CommandPaletteViewModel;
            if (ReferenceEquals(_subscribedVm, nextVm))
                return;

            DetachViewModel();
            if (nextVm != null)
            {
                _subscribedVm = nextVm;
                _subscribedVm.PropertyChanged += OnViewModelPropertyChanged;
            }
        }

        private void DetachViewModel()
        {
            if (_subscribedVm != null)
            {
                _subscribedVm.PropertyChanged -= OnViewModelPropertyChanged;
                _subscribedVm = null;
            }
        }

        private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
        {
            if (e.PropertyName == nameof(CommandPaletteViewModel.SelectedCommandPaletteItem))
            {
                ScrollSelectedItemIntoView();
            }
            else if (e.PropertyName == nameof(CommandPaletteViewModel.ActiveScope))
            {
                EnsureSearchBoxFocus();
            }
        }

        private void EnsureSearchBoxFocus()
        {
            if (TxtCommandPalette is { IsEffectivelyVisible: true, IsFocused: false })
                TxtCommandPalette.Focus();
        }

        private void ScrollSelectedItemIntoView()
        {
            if (_subscribedVm?.SelectedCommandPaletteItem is not { } selected ||
                CommandPaletteItemsControl is not { } itemsControl)
            {
                return;
            }

            int index = _subscribedVm.CommandPaletteItems.IndexOf(selected);
            if (index < 0)
                return;

            Control? container = itemsControl.ContainerFromIndex(index) ?? itemsControl.ContainerFromItem(selected);
            if (container != null && container.Bounds.Height > 0.0)
            {
                container.BringIntoView();
                return;
            }

            Dispatcher.UIThread.Post(() =>
            {
                if (_subscribedVm?.SelectedCommandPaletteItem is not { } current ||
                    CommandPaletteItemsControl is not { } ic)
                {
                    return;
                }

                int currentIdx = _subscribedVm.CommandPaletteItems.IndexOf(current);
                if (currentIdx < 0)
                    return;

                Control? realized = ic.ContainerFromIndex(currentIdx) ?? ic.ContainerFromItem(current);
                realized?.BringIntoView();
            }, DispatcherPriority.Render);
        }

        private void OnCloseCommandPaletteClicked(object? sender, RoutedEventArgs e)
        {
            if (DataContext is CommandPaletteViewModel vm)
                vm.IsCommandPaletteOpen = false;
        }

        private void OnCommandPaletteRowPointerPressed(object? sender, PointerPressedEventArgs e)
        {
            if (e.Source is Visual v && (v is Button || v.FindAncestorOfType<Button>() != null))
                return;

            if (sender is Control ctrl &&
                ctrl.DataContext is CommandPaletteItemViewModel item &&
                DataContext is CommandPaletteViewModel vm)
            {
                int idx = vm.CommandPaletteItems.IndexOf(item);
                if (idx >= 0)
                    vm.SetSelectedCommandPaletteIndex(idx);

                if (e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
                {
                    bool joinServer = e.KeyModifiers.HasFlag(KeyModifiers.Shift) ||
                                      e.KeyModifiers.HasFlag(KeyModifiers.Control);
                    vm.ExecuteCommandPaletteSelection(joinServer);
                    e.Handled = true;
                }
            }
        }
    }
}
