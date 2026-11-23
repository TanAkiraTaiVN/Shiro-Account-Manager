#nullable enable
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.VisualTree;
using Shiro.ViewModels;

namespace Shiro.AvaloniaUI.Views.Panels
{
    /// <summary>
    /// Command Palette 2.0 modal overlay: 2-column SkiaGlassSurface with scope filter chips,
    /// MRU recent commands, live preview pane, and exposed SearchTextBox for MainWindow routing.
    /// </summary>
    public partial class CommandPaletteOverlayPanel : UserControl
    {
        public CommandPaletteOverlayPanel()
        {
            InitializeComponent();
        }

        public TextBox? SearchTextBox => TxtCommandPalette;

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
