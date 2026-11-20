#nullable enable
using System;
using System.Linq;
using System.Windows.Input;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.VisualTree;
using Shiro.Services;
using Shiro.ViewModels;

namespace Shiro.AvaloniaUI.Views
{
    public partial class DashboardView
    {
        // ================= CONTEXT MENU ACTIONS =================
        private AccountViewModel? GetItemFromMenu(object? sender)
        {
            if (sender is MenuItem mi)
            {
                if (mi.DataContext is AccountViewModel avm)
                    return avm;
                if (mi.Parent is ContextMenu pCm && pCm.DataContext is AccountViewModel pAvm)
                    return pAvm;
                if (mi.Parent is ContextMenu pCmTarget && pCmTarget.PlacementTarget is Control pt1 && pt1.DataContext is AccountViewModel pt1Avm)
                    return pt1Avm;
                if (Resources.TryGetResource("AccountSharedContextMenu", null, out var res) && res is ContextMenu cm)
                {
                    if (cm.DataContext is AccountViewModel cDavm) return cDavm;
                    if (cm.PlacementTarget is Control pt && pt.DataContext is AccountViewModel ptAvm) return ptAvm;
                }
                if (mi.FindAncestorOfType<ContextMenu>() is ContextMenu fcm && fcm.DataContext is AccountViewModel cavm)
                    return cavm;
                if (mi.FindAncestorOfType<Button>() is Button btn && btn.DataContext is AccountViewModel bavm)
                    return bavm;
                if (mi.FindAncestorOfType<Border>() is Border b && b.DataContext is AccountViewModel bbavm)
                    return bbavm;
            }
            else if (sender is Control c && c.DataContext is AccountViewModel cavm)
            {
                return cavm;
            }
            return ViewModel?.FilteredAccounts?.FirstOrDefault(a => a.IsSelected) ?? ViewModel?.FilteredAccounts?.FirstOrDefault();
        }

        private void ExecuteMenuCommand(object? sender, ICommand? command)
        {
            var avm = GetItemFromMenu(sender);
            if (avm != null && command != null)
                command.Execute(avm);
        }

        private void OnMenuLaunchHome(object sender, RoutedEventArgs e)
            => ExecuteMenuCommand(sender, ViewModel.LaunchAccountCommand);

        private void OnMenuJoinServer(object sender, RoutedEventArgs e)
            => ExecuteMenuCommand(sender, ViewModel.JoinServerAccountCommand);

        private void OnMenuFocusAccountWindow(object sender, RoutedEventArgs e)
            => ExecuteMenuCommand(sender, ViewModel.FocusAccountWindowCommand);

        private void OnMenuReLoginFast(object sender, RoutedEventArgs e)
        {
            var avm = GetItemFromMenu(sender);
            if (avm?.Model != null)
                AppServices.Dialogs.OpenFastLoginDialog(avm.Model);
        }

        private void OnMenuValidateSession(object sender, RoutedEventArgs e)
            => ExecuteMenuCommand(sender, ViewModel.ValidateSessionCommand);

        private void OnMenuOpenModerationPage(object sender, RoutedEventArgs e)
            => OnCardViewBanClicked(sender, e);

        private void OnMenuCheckRobux(object sender, RoutedEventArgs e)
            => ExecuteMenuCommand(sender, ViewModel.CheckRobuxAccountCommand);

        private void OnMenuChangeGroup(object sender, RoutedEventArgs e)
            => ExecuteMenuCommand(sender, ViewModel.SetGroupCommand);

        private void OnMenuChangeAlias(object sender, RoutedEventArgs e)
            => ExecuteMenuCommand(sender, ViewModel.SetAliasCommand);

        private void OnMenuChangeDescription(object sender, RoutedEventArgs e)
            => ExecuteMenuCommand(sender, ViewModel.SetDescriptionCommand);

        private void OnMenuCopyCookie(object sender, RoutedEventArgs e)
            => ExecuteMenuCommand(sender, ViewModel.CopySecurityTokenCommand);

        private void OnMenuCopyUserPass(object sender, RoutedEventArgs e)
            => ExecuteMenuCommand(sender, ViewModel.CopyUserPassCommand);

        private void OnMenuCopyUserId(object sender, RoutedEventArgs e)
            => ExecuteMenuCommand(sender, ViewModel.CopyUserIdCommand);

        private void OnMenuCopyUsername(object sender, RoutedEventArgs e)
            => ExecuteMenuCommand(sender, ViewModel.CopyUsernameCommand);

        private void OnMenuCopyProfileUrl(object sender, RoutedEventArgs e)
            => ExecuteMenuCommand(sender, ViewModel.CopyProfileUrlCommand);

        private void OnMenuAccountFields(object sender, RoutedEventArgs e)
            => ExecuteMenuCommand(sender, ViewModel.ViewFieldsCommand);

        private void OnMenuAccountUtils(object sender, RoutedEventArgs e)
            => ExecuteMenuCommand(sender, ViewModel.OpenUtilsCommand);

        private void OnMenuCloseAccountRoblox(object sender, RoutedEventArgs e)
            => ExecuteMenuCommand(sender, ViewModel.CloseAccountRobloxCommand);

        private void OnMenuDeleteAccount(object sender, RoutedEventArgs e)
            => ExecuteMenuCommand(sender, ViewModel.RemoveAccountCommand);
    }
}
