#nullable enable
using System;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Shiro.Services;

namespace Shiro.AvaloniaUI.Views
{
    public partial class InputDialog : Window
    {
        private string _value = "/UC";

        public InputDialog()
        {
            InitializeComponent();
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

        private static InputDialog CreateDialogInstance(string text, string caption, string defaultText, bool big, bool isSecret = false)
        {
            var dialog = new InputDialog();
            dialog.Title = caption ?? "Shiro";
            dialog.TxtTitle.Text = caption ?? "Shiro";
            dialog.TxtPrompt.Text = text ?? string.Empty;
            dialog.TxtInput.Text = defaultText ?? string.Empty;

            if (ShouldMaskInput(caption, isSecret))
            {
                dialog.TxtInput.PasswordChar = '*';
            }

            if (big)
            {
                dialog.TxtInput.AcceptsReturn = true;
                dialog.TxtInput.Height = 120;
                dialog.TxtInput.TextWrapping = Avalonia.Media.TextWrapping.Wrap;
            }

            return dialog;
        }

        public static async Task<string> PromptAsync(string text, string caption = "Shiro", string defaultText = "", bool big = false, Window? owner = null, bool isSecret = false)
        {
            if (!MessageBoxDialog.IsAvaloniaUiReady())
                return "/UC";

            if (!Dispatcher.UIThread.CheckAccess())
                return await Dispatcher.UIThread.InvokeAsync(() => PromptAsync(text, caption, defaultText, big, owner, isSecret));

            if (!IsValidDialogOwner(owner))
                owner = GetActiveWindow();

            var dialog = CreateDialogInstance(text, caption, defaultText, big, isSecret);

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

        /// <summary>
        /// Synchronous prompt dialog. Prefer <see cref="PromptAsync"/> to prevent nested message pump reentrancy.
        /// </summary>
        public static string Prompt(string text, string caption = "Shiro", string defaultText = "", bool big = false, Window? owner = null, bool isSecret = false)
        {
            if (!MessageBoxDialog.IsAvaloniaUiReady() && !IsValidDialogOwner(owner))
                return "/UC";

            if (Dispatcher.UIThread.CheckAccess())
            {
                if (!IsValidDialogOwner(owner))
                    owner = GetActiveWindow();

                var dialog = CreateDialogInstance(text, caption, defaultText, big, isSecret);

                var frame = new DispatcherFrame();
                dialog.Closed += (s, e) => frame.Continue = false;

                if (IsValidDialogOwner(owner))
                {
                    _ = dialog.ShowDialog(owner!).ContinueWith(
                        t =>
                        {
                            if (t.IsFaulted || t.IsCanceled)
                                Dispatcher.UIThread.Post(() => frame.Continue = false);
                        },
                        System.Threading.CancellationToken.None,
                        TaskContinuationOptions.ExecuteSynchronously,
                        TaskScheduler.Default);
                }
                else
                {
                    dialog.Show();
                }

                Dispatcher.UIThread.PushFrame(frame);
                return dialog._value;
            }
            else
            {
                string result = "/UC";
                using var done = new System.Threading.ManualResetEventSlim(false);
                Dispatcher.UIThread.Post(async () =>
                {
                    try
                    {
                        result = await PromptAsync(text, caption, defaultText, big, owner, isSecret);
                    }
                    catch (Exception ex)
                    {
                        AppServices.Logger?.Debug($"[InputDialog.Prompt] {ex.Message}");
                    }
                    finally
                    {
                        done.Set();
                    }
                });
                done.Wait(TimeSpan.FromMinutes(10));
                return result;
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
