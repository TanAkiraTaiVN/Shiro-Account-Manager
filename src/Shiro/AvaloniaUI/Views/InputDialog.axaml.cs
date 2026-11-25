#nullable enable
using System;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Threading;
using LucideAvalonia.Enum;
using Shiro.Services;

namespace Shiro.AvaloniaUI.Views
{
    public partial class InputDialog : Window
    {
        private const char SecretMaskChar = '•';
        private string _value = "/UC";
        private bool _isSecret;
        private bool _isPasswordRevealed;
        private bool _requireConfirmation;

        public InputDialog()
        {
            InitializeComponent();
            WindowBackdropService.ApplyDarkAndBackdrop(this);
            Opened += InputDialog_Opened;
        }

        private void InputDialog_Opened(object? sender, EventArgs e)
        {
            TxtInput.Focus();
            if (!string.IsNullOrEmpty(TxtInput.Text))
            {
                TxtInput.CaretIndex = TxtInput.Text.Length;
            }
        }

        internal static bool IsValidDialogOwner(Window? window)
        {
            return window is { IsVisible: true };
        }

        public static Window? GetActiveWindow()
        {
            if (Dispatcher.UIThread != null && !Dispatcher.UIThread.CheckAccess())
                return null;
            if (Avalonia.Application.Current?.ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
            {
                foreach (var win in desktop.Windows)
                {
                    if (win.IsActive && win.IsVisible) return win;
                }
                return IsValidDialogOwner(desktop.MainWindow) ? desktop.MainWindow : null;
            }
            return null;
        }

        internal static bool ShouldMaskInput(string? caption, bool isSecret)
        {
            if (isSecret)
                return true;

            if (string.IsNullOrEmpty(caption))
                return false;

            string localizedKeyword = LocalizationService.Get("InputDialog.PasswordKeyword");
            return (!string.IsNullOrEmpty(localizedKeyword) && caption.Contains(localizedKeyword, StringComparison.OrdinalIgnoreCase))
                || caption.Contains("password", StringComparison.OrdinalIgnoreCase);
        }

        internal static InputDialog CreateDialogInstance(
            string text,
            string caption,
            string defaultText,
            bool big,
            bool isSecret = false,
            bool requireConfirmation = false)
        {
            var dialog = new InputDialog();
            dialog.Title = caption ?? "Shiro";
            dialog.TxtTitle.Text = caption ?? "Shiro";
            dialog.TxtPrompt.Text = text ?? string.Empty;
            dialog.TxtInput.Text = defaultText ?? string.Empty;

            if (ShouldMaskInput(caption, isSecret) || requireConfirmation)
            {
                dialog._isSecret = true;
                dialog.TxtInput.PasswordChar = SecretMaskChar;
                dialog.BtnToggleReveal.IsVisible = true;
                dialog.TxtInput.PlaceholderText = LocalizationService.Get("InputDialog_PasswordPlaceholder");
                dialog.IconLucide.Icon = LucideIconNames.Lock;
                ApplySecretIconTheme(dialog);

                if (requireConfirmation)
                {
                    dialog._requireConfirmation = true;
                    dialog.PnlConfirmPassword.IsVisible = true;
                    dialog.TxtConfirmInput.PasswordChar = SecretMaskChar;
                }
            }

            if (big)
            {
                dialog.TxtInput.AcceptsReturn = true;
                dialog.TxtInput.Height = 120;
                dialog.TxtInput.TextWrapping = TextWrapping.Wrap;
            }

            return dialog;
        }

        private static void ApplySecretIconTheme(InputDialog dialog)
        {
            if (dialog.TryFindResource("AccentSoftFillBrush", dialog.ActualThemeVariant, out object? bgObj) && bgObj is IBrush bg)
                dialog.IconBadge.Background = bg;
            if (dialog.TryFindResource("AccentBorderBrush", dialog.ActualThemeVariant, out object? borderObj) && borderObj is IBrush border)
                dialog.IconBadge.BorderBrush = border;
            if (dialog.TryFindResource("AccentBrush", dialog.ActualThemeVariant, out object? accentObj) && accentObj is IBrush accent)
                dialog.IconLucide.StrokeBrush = accent;
        }

        public static async Task<string> PromptAsync(
            string text,
            string caption = "Shiro",
            string defaultText = "",
            bool big = false,
            Window? owner = null,
            bool isSecret = false,
            bool requireConfirmation = false)
        {
            if (!MessageBoxDialog.IsAvaloniaUiReady() && !IsValidDialogOwner(owner))
                return "/UC";

            if (!Dispatcher.UIThread.CheckAccess())
                return await Dispatcher.UIThread.InvokeAsync(() => PromptAsync(text, caption, defaultText, big, owner, isSecret, requireConfirmation));

            if (!IsValidDialogOwner(owner))
                owner = GetActiveWindow();

            var dialog = CreateDialogInstance(text, caption, defaultText, big, isSecret, requireConfirmation);

            if (IsValidDialogOwner(owner))
            {
                await dialog.ShowDialog(owner!);
            }
            else
            {
                var tcs = new TaskCompletionSource<string>();
                dialog.Closed += (s, e) => tcs.TrySetResult(dialog._value);
                dialog.Show();
                await tcs.Task;
            }

            return dialog._value;
        }

        private void TryCommitInput()
        {
            string input = TxtInput.Text ?? string.Empty;
            if (_requireConfirmation)
            {
                string confirm = TxtConfirmInput.Text ?? string.Empty;
                if (!string.Equals(input, confirm, StringComparison.Ordinal))
                {
                    TxtValidationError.Text = LocalizationService.Get("InputDialog_PasswordMismatch");
                    TxtValidationError.IsVisible = true;
                    TxtConfirmInput.Focus();
                    return;
                }
            }

            _value = input;
            Close();
        }

        private void BtnOK_Click(object sender, RoutedEventArgs e) => TryCommitInput();

        private void BtnCancel_Click(object sender, RoutedEventArgs e)
        {
            _value = "/UC";
            Close();
        }

        private void BtnToggleReveal_Click(object sender, RoutedEventArgs e)
        {
            if (!_isSecret) return;
            _isPasswordRevealed = !_isPasswordRevealed;
            char mask = _isPasswordRevealed ? '\0' : SecretMaskChar;
            TxtInput.PasswordChar = mask;
            if (_requireConfirmation)
                TxtConfirmInput.PasswordChar = mask;
            IconToggleReveal.Icon = _isPasswordRevealed ? LucideIconNames.EyeOff : LucideIconNames.Eye;
        }

        private void TxtPasswordInput_TextChanged(object? sender, TextChangedEventArgs e)
        {
            if (TxtValidationError.IsVisible)
                TxtValidationError.IsVisible = false;
        }

        private void Header_PointerPressed(object? sender, PointerPressedEventArgs e)
        {
            for (StyledElement? cur = e.Source as StyledElement; cur != null; cur = cur.Parent)
            {
                if (cur is Button)
                    return;
            }

            if (e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
                BeginMoveDrag(e);
        }

        private void TxtInput_KeyDown(object sender, KeyEventArgs e)
        {
            if (!TxtInput.AcceptsReturn && e.Key == Key.Enter)
            {
                if (_requireConfirmation && string.IsNullOrEmpty(TxtConfirmInput.Text))
                {
                    TxtConfirmInput.Focus();
                    e.Handled = true;
                    return;
                }
                TryCommitInput();
            }
            else if (e.Key == Key.Escape)
            {
                _value = "/UC";
                Close();
            }
        }

        private void TxtConfirmInput_KeyDown(object? sender, KeyEventArgs e)
        {
            if (e.Key == Key.Enter)
            {
                TryCommitInput();
            }
            else if (e.Key == Key.Escape)
            {
                _value = "/UC";
                Close();
            }
        }
    }
}
