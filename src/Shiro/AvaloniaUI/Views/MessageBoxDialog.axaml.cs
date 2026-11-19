#nullable enable
using System;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Threading;
using Shiro.Services;

namespace Shiro.AvaloniaUI.Views
{
    public enum MessageBoxButtons
    {
        OK,
        OKCancel,
        YesNo,
        YesNoCancel
    }

    public enum MessageBoxIcon
    {
        None,
        Information,
        Warning,
        Error,
        Question
    }

    public enum MessageBoxResult
    {
        None,
        OK,
        Cancel,
        Yes,
        No
    }

    public partial class MessageBoxDialog : Window
    {
        private MessageBoxResult _result = MessageBoxResult.None;

        public MessageBoxDialog()
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

        private static MessageBoxDialog CreateDialogInstance(
            string message,
            string title,
            MessageBoxButtons buttons,
            MessageBoxIcon icon)
        {
            var dialog = new MessageBoxDialog();
            dialog.Title = title;
            dialog.TxtTitle.Text = title;
            dialog.TxtMessage.Text = message ?? string.Empty;

            // Configure Icon
            switch (icon)
            {
                case MessageBoxIcon.Information:
                    dialog.IconLucide.Icon = LucideAvalonia.Enum.LucideIconNames.Info;
                    dialog.IconLucide.StrokeBrush = new SolidColorBrush(Color.Parse("#38BDF8"));
                    dialog.IconBadge.Background = new SolidColorBrush(Color.Parse("#172554"));
                    break;
                case MessageBoxIcon.Warning:
                    dialog.IconLucide.Icon = LucideAvalonia.Enum.LucideIconNames.TriangleAlert;
                    dialog.IconLucide.StrokeBrush = new SolidColorBrush(Color.Parse("#F59E0B"));
                    dialog.IconBadge.Background = new SolidColorBrush(Color.Parse("#451A03"));
                    break;
                case MessageBoxIcon.Error:
                    dialog.IconLucide.Icon = LucideAvalonia.Enum.LucideIconNames.CircleX;
                    dialog.IconLucide.StrokeBrush = new SolidColorBrush(Color.Parse("#EF4444"));
                    dialog.IconBadge.Background = new SolidColorBrush(Color.Parse("#450A0A"));
                    break;
                case MessageBoxIcon.Question:
                    dialog.IconLucide.Icon = LucideAvalonia.Enum.LucideIconNames.CircleQuestionMark;
                    dialog.IconLucide.StrokeBrush = new SolidColorBrush(Color.Parse("#A855F7"));
                    dialog.IconBadge.Background = new SolidColorBrush(Color.Parse("#3B0764"));
                    break;
                default:
                    dialog.IconBadge.IsVisible = false;
                    break;
            }

            // Configure Buttons
            switch (buttons)
            {
                case MessageBoxButtons.OK:
                    dialog.BtnOK.IsVisible = true;
                    dialog.BtnCancel.IsVisible = false;
                    dialog.BtnYes.IsVisible = false;
                    dialog.BtnNo.IsVisible = false;
                    break;
                case MessageBoxButtons.OKCancel:
                    dialog.BtnOK.IsVisible = true;
                    dialog.BtnCancel.IsVisible = true;
                    dialog.BtnYes.IsVisible = false;
                    dialog.BtnNo.IsVisible = false;
                    break;
                case MessageBoxButtons.YesNo:
                    dialog.BtnOK.IsVisible = false;
                    dialog.BtnCancel.IsVisible = false;
                    dialog.BtnYes.IsVisible = true;
                    dialog.BtnNo.IsVisible = true;
                    break;
                case MessageBoxButtons.YesNoCancel:
                    dialog.BtnOK.IsVisible = false;
                    dialog.BtnCancel.IsVisible = true;
                    dialog.BtnYes.IsVisible = true;
                    dialog.BtnNo.IsVisible = true;
                    break;
            }

            return dialog;
        }

        public static bool IsAvaloniaUiReady()
        {
            try
            {
                return Avalonia.Application.Current?.ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop
                    && desktop.MainWindow != null;
            }
            catch (Exception ex)
            {
                Shiro.Services.AppServices.Logger.Debug("[MessageBoxDialog] IsAvaloniaUiReady check failed", ex);
                return false;
            }
        }

        private static MessageBoxResult ShowNativeFallback(
            string message,
            string title,
            MessageBoxButtons buttons,
            MessageBoxIcon icon)
        {
            if (OperatingSystem.IsWindows())
            {
                uint uType = 0;
                if (buttons == MessageBoxButtons.YesNo) uType |= 0x00000004;
                else if (buttons == MessageBoxButtons.OKCancel) uType |= 0x00000001;
                else if (buttons == MessageBoxButtons.YesNoCancel) uType |= 0x00000003;

                if (icon == MessageBoxIcon.Warning) uType |= 0x00000030;
                else if (icon == MessageBoxIcon.Error) uType |= 0x00000010;
                else if (icon == MessageBoxIcon.Question) uType |= 0x00000020;
                else if (icon == MessageBoxIcon.Information) uType |= 0x00000040;

                int res = Win32NativeHelper.MessageBoxW(IntPtr.Zero, message ?? string.Empty, title ?? "Shiro", uType);
                return res switch
                {
                    6 => MessageBoxResult.Yes,
                    7 => MessageBoxResult.No,
                    1 => MessageBoxResult.OK,
                    2 => MessageBoxResult.Cancel,
                    _ => MessageBoxResult.OK
                };
            }
            return MessageBoxResult.OK;
        }

        public static async Task<MessageBoxResult> ShowAsync(
            string message,
            string title = "Shiro",
            MessageBoxButtons buttons = MessageBoxButtons.OK,
            MessageBoxIcon icon = MessageBoxIcon.Information,
            Window? owner = null)
        {
            if (!IsAvaloniaUiReady() && !IsValidDialogOwner(owner))
            {
                return ShowNativeFallback(message, title, buttons, icon);
            }

            if (!Dispatcher.UIThread.CheckAccess())
            {
                return await Dispatcher.UIThread.InvokeAsync(() => ShowAsync(message, title, buttons, icon, owner));
            }

            if (!IsValidDialogOwner(owner))
                owner = GetActiveWindow();

            var dialog = CreateDialogInstance(message, title, buttons, icon);

            if (IsValidDialogOwner(owner))
            {
                await dialog.ShowDialog(owner!);
            }
            else
            {
                var tcs = new TaskCompletionSource<MessageBoxResult>();
                dialog.Closed += (s, e) => tcs.TrySetResult(dialog._result);
                dialog.Show();
                return await tcs.Task;
            }

            return dialog._result;
        }

        public static MessageBoxResult Show(
            string message,
            string title = "Shiro",
            MessageBoxButtons buttons = MessageBoxButtons.OK,
            MessageBoxIcon icon = MessageBoxIcon.Information,
            Window? owner = null)
        {
            if (!IsAvaloniaUiReady() && !IsValidDialogOwner(owner))
            {
                return ShowNativeFallback(message, title, buttons, icon);
            }

            if (Dispatcher.UIThread.CheckAccess())
            {
                if (!IsValidDialogOwner(owner))
                    owner = GetActiveWindow();

                var dialog = CreateDialogInstance(message, title, buttons, icon);

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
                return dialog._result;
            }
            else
            {
                MessageBoxResult result = MessageBoxResult.None;
                using var done = new System.Threading.ManualResetEventSlim(false);
                Dispatcher.UIThread.Post(async () =>
                {
                    try
                    {
                        result = await ShowAsync(message, title, buttons, icon, owner);
                    }
                    catch (Exception ex)
                    {
                        AppServices.Logger?.Debug($"[MessageBoxDialog.Show] {ex.Message}");
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
            _result = MessageBoxResult.OK;
            Close();
        }

        private void BtnCancel_Click(object sender, RoutedEventArgs e)
        {
            _result = MessageBoxResult.Cancel;
            Close();
        }

        private void BtnYes_Click(object sender, RoutedEventArgs e)
        {
            _result = MessageBoxResult.Yes;
            Close();
        }

        private void BtnNo_Click(object sender, RoutedEventArgs e)
        {
            _result = MessageBoxResult.No;
            Close();
        }
    }
}
