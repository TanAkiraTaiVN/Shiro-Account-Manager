#nullable enable
using System;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;

namespace Shiro.AvaloniaUI.Views
{
    public partial class InputDialog : Window
    {
        private string _value = "/UC";

        public InputDialog()
        {
            InitializeComponent();
        }

        public static Window? GetActiveWindow()
        {
            if (Avalonia.Application.Current?.ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
            {
                foreach (var win in desktop.Windows)
                {
                    if (win.IsActive) return win;
                }
                return desktop.MainWindow;
            }
            return null;
        }

        private static InputDialog CreateDialogInstance(string text, string caption, string defaultText, bool big)
        {
            var dialog = new InputDialog();
            dialog.Title = caption ?? "Shiro";
            dialog.TxtTitle.Text = caption ?? "Shiro";
            dialog.TxtPrompt.Text = text ?? string.Empty;
            dialog.TxtInput.Text = defaultText ?? string.Empty;

            if (caption != null && (caption.Contains("mật khẩu", StringComparison.OrdinalIgnoreCase) || caption.Contains("password", StringComparison.OrdinalIgnoreCase)))
            {
                dialog.TxtInput.PasswordChar = '•';
            }

            if (big)
            {
                dialog.TxtInput.AcceptsReturn = true;
                dialog.TxtInput.Height = 120;
                dialog.TxtInput.TextWrapping = Avalonia.Media.TextWrapping.Wrap;
            }

            return dialog;
        }

        public static async Task<string> PromptAsync(string text, string caption = "Shiro", string defaultText = "", bool big = false, Window? owner = null)
        {
            owner ??= GetActiveWindow();
            var dialog = CreateDialogInstance(text, caption, defaultText, big);

            if (owner != null)
            {
                await dialog.ShowDialog(owner);
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

        public static string Prompt(string text, string caption = "Shiro", string defaultText = "", bool big = false, Window? owner = null)
        {
            if (Dispatcher.UIThread.CheckAccess())
            {
                owner ??= GetActiveWindow();
                var dialog = CreateDialogInstance(text, caption, defaultText, big);

                var frame = new DispatcherFrame();
                dialog.Closed += (s, e) => frame.Continue = false;

                if (owner != null)
                    _ = dialog.ShowDialog(owner);
                else
                    dialog.Show();

                Dispatcher.UIThread.PushFrame(frame);
                return dialog._value;
            }
            else
            {
                return Dispatcher.UIThread.InvokeAsync(() => PromptAsync(text, caption, defaultText, big, owner)).GetAwaiter().GetResult();
            }
        }

        private void BtnOK_Click(object sender, RoutedEventArgs e)
        {
            _value = TxtInput.Text ?? string.Empty;
            Close();
        }

        private void BtnCancel_Click(object sender, RoutedEventArgs e)
        {
            _value = "/UC";
            Close();
        }

        private void TxtInput_KeyDown(object sender, KeyEventArgs e)
        {
            if (!TxtInput.AcceptsReturn && e.Key == Key.Enter)
            {
                _value = TxtInput.Text ?? string.Empty;
                Close();
            }
            else if (e.Key == Key.Escape)
            {
                _value = "/UC";
                Close();
            }
        }
    }
}
