#nullable enable
using System;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Threading;
using Shiro.Services;

namespace Shiro.AvaloniaUI.Views
{
    public partial class MessageBoxDialog : Window
    {
        private MessageBoxResult _result = MessageBoxResult.None;

        public MessageBoxDialog()
        {
            InitializeComponent();
            WindowBackdropService.ApplyDarkAndBackdrop(this);
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
                    dialog.IconBadge.Background = new SolidColorBrush(Color.Parse("#102131"));
                    dialog.IconBadge.BorderBrush = new SolidColorBrush(Color.Parse("#1E3E5C"));
                    break;
                case MessageBoxIcon.Warning:
                    dialog.IconLucide.Icon = LucideAvalonia.Enum.LucideIconNames.TriangleAlert;
                    dialog.IconLucide.StrokeBrush = new SolidColorBrush(Color.Parse("#FBBF24"));
                    dialog.IconBadge.Background = new SolidColorBrush(Color.Parse("#261D0E"));
                    dialog.IconBadge.BorderBrush = new SolidColorBrush(Color.Parse("#593C14"));
                    break;
                case MessageBoxIcon.Error:
                    dialog.IconLucide.Icon = LucideAvalonia.Enum.LucideIconNames.CircleX;
                    dialog.IconLucide.StrokeBrush = new SolidColorBrush(Color.Parse("#F87171"));
                    dialog.IconBadge.Background = new SolidColorBrush(Color.Parse("#271217"));
                    dialog.IconBadge.BorderBrush = new SolidColorBrush(Color.Parse("#541D28"));
                    break;
                case MessageBoxIcon.Question:
                    dialog.IconLucide.Icon = LucideAvalonia.Enum.LucideIconNames.CircleQuestionMark;
                    dialog.IconLucide.StrokeBrush = new SolidColorBrush(Color.Parse("#A78BFA"));
                    dialog.IconBadge.Background = new SolidColorBrush(Color.Parse("#1A152E"));
                    dialog.IconBadge.BorderBrush = new SolidColorBrush(Color.Parse("#372A63"));
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
            if (StoragePaths.IsRunningUnderUnitTests())
                return MessageBoxResult.None;

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

        private void BtnCloseHeader_Click(object sender, RoutedEventArgs e)
        {
            Close();
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
    }
}
