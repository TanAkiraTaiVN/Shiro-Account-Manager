#nullable enable
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Input;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Shiro.Services;

namespace Shiro.ViewModels
{
    public partial class MainViewModel
    {
        public ObservableCollection<AccountViewModel> Accounts => AccountsVm.Accounts;
        public ObservableCollection<AccountViewModel> FilteredAccounts => AccountsVm.FilteredAccounts;
        public ObservableCollection<AccountRowViewModel> FilteredAccountRows => AccountsVm.FilteredAccountRows;
        public ObservableCollection<string> AvailableGroups => AccountsVm.AvailableGroups;
        public int TotalCount => AccountsVm.TotalCount;
        public int InGameCount => AccountsVm.InGameCount;
        public int ReadyCount => AccountsVm.ReadyCount;
        public int ExpiredCount => AccountsVm.ExpiredCount;
        public bool HasExpiredAccounts => AccountsVm.HasExpiredAccounts;
        public int BannedCount => AccountsVm.BannedCount;
        public bool HasBannedAccounts => AccountsVm.HasBannedAccounts;
        public int PinnedCount => AccountsVm.PinnedCount;
        public int SelectedCount => AccountsVm.SelectedCount;
        public bool HasSelection => AccountsVm.HasSelection;
        public bool? IsAllSelected { get => AccountsVm.IsAllSelected; set => AccountsVm.IsAllSelected = value; }
        public long TotalRobuxCount => AccountsVm.TotalRobuxCount;
        public string TotalRobuxText => AccountsVm.TotalRobuxText;
        public string SearchText { get => AccountsVm.SearchText; set => AccountsVm.SearchText = value; }
        public string SelectedGroup { get => AccountsVm.SelectedGroup; set => AccountsVm.SelectedGroup = value; }
        public AccountFilterTab ActiveTab { get => AccountsVm.ActiveTab; set => AccountsVm.ActiveTab = value; }
        public bool IsFilterAll { get => AccountsVm.IsFilterAll; set => AccountsVm.IsFilterAll = value; }
        public bool IsFilterInGame { get => AccountsVm.IsFilterInGame; set => AccountsVm.IsFilterInGame = value; }
        public bool IsFilterReady { get => AccountsVm.IsFilterReady; set => AccountsVm.IsFilterReady = value; }
        public bool IsFilterExpired { get => AccountsVm.IsFilterExpired; set => AccountsVm.IsFilterExpired = value; }
        public bool IsFilterBanned { get => AccountsVm.IsFilterBanned; set => AccountsVm.IsFilterBanned = value; }
        public bool IsFilterPinned { get => AccountsVm.IsFilterPinned; set => AccountsVm.IsFilterPinned = value; }
        public AccountSortMode SortMode { get => AccountsVm.SortMode; set => AccountsVm.SortMode = value; }
        public bool IsSortNaturalAsc { get => AccountsVm.IsSortNaturalAsc; set => AccountsVm.IsSortNaturalAsc = value; }
        public bool IsSortNaturalDesc { get => AccountsVm.IsSortNaturalDesc; set => AccountsVm.IsSortNaturalDesc = value; }
        public bool IsSortOriginal { get => AccountsVm.IsSortOriginal; set => AccountsVm.IsSortOriginal = value; }
        public bool IsSortStatus { get => AccountsVm.IsSortStatus; set => AccountsVm.IsSortStatus = value; }
        public bool IsSortRobuxDesc { get => AccountsVm.IsSortRobuxDesc; set => AccountsVm.IsSortRobuxDesc = value; }
        public string CurrentSortLabel => AccountsVm.CurrentSortLabel;
        public int CardColumns { get => AccountsVm.CardColumns; set => AccountsVm.CardColumns = value; }
        public UiDensityMode DensityMode { get => AccountsVm.DensityMode; set => AccountsVm.DensityMode = value; }
        public bool IsDensityCompact { get => AccountsVm.IsDensityCompact; set => AccountsVm.IsDensityCompact = value; }
        public bool IsDensityBalanced { get => AccountsVm.IsDensityBalanced; set => AccountsVm.IsDensityBalanced = value; }
        public bool IsDensityComfortable { get => AccountsVm.IsDensityComfortable; set => AccountsVm.IsDensityComfortable = value; }
        public double CardHeight => AccountsVm.CardHeight;
        public double CardRowHeight => AccountsVm.CardRowHeight;
        public double TableRowHeight => AccountsVm.TableRowHeight;
        public double CardBottomSpacing => AccountsVm.CardBottomSpacing;
        public bool IsEmptyNoAccounts => AccountsVm.IsEmptyNoAccounts;
        public bool IsEmptyFiltered => AccountsVm.IsEmptyFiltered;
        public bool IsEmptyState => AccountsVm.IsEmptyState;
        public bool IsCompactView { get => AccountsVm.IsCompactView; set => AccountsVm.IsCompactView = value; }
        public bool IsCardView => AccountsVm.IsCardView;
        public string SubtitleTotalText => AccountsVm.SubtitleTotalText;
        public string SubtitleReadyText => AccountsVm.SubtitleReadyText;
        public string SubtitleSelectedText => AccountsVm.SubtitleSelectedText;
        public string TabAllText => AccountsVm.TabAllText;
        public string TabInGameText => AccountsVm.TabInGameText;
        public string TabReadyText => AccountsVm.TabReadyText;
        public string TabExpiredText => AccountsVm.TabExpiredText;
        public string TabBannedText => AccountsVm.TabBannedText;
        public string TabPinnedText => AccountsVm.TabPinnedText;
        public string DeselectCountText => AccountsVm.DeselectCountText;
        public string FooterSelectedText => AccountsVm.FooterSelectedText;
        public string FooterTotalText => AccountsVm.FooterTotalText;

        public ICommand AddAccountFastLoginCommand => AccountsVm.AddAccountFastLoginCommand;
        public ICommand AddAccountCookieCommand => AccountsVm.AddAccountCookieCommand;
        public ICommand BulkImportCommand => AccountsVm.BulkImportCommand;
        public ICommand ReLoginWithCookieCommand => AccountsVm.ReLoginWithCookieCommand;
        public ICommand ValidateSessionCommand => AccountsVm.ValidateSessionCommand;
        public ICommand OpenProfileCommand => AccountsVm.OpenProfileCommand;
        public ICommand SetAliasCommand => AccountsVm.SetAliasCommand;
        public ICommand SetGroupCommand => AccountsVm.SetGroupCommand;
        public ICommand SetDescriptionCommand => AccountsVm.SetDescriptionCommand;
        public ICommand TogglePinCommand => AccountsVm.TogglePinCommand;
        public ICommand CopyUsernameCommand => AccountsVm.CopyUsernameCommand;
        public ICommand CopyUserPassCommand => AccountsVm.CopyUserPassCommand;
        public ICommand CopyUserIdCommand => AccountsVm.CopyUserIdCommand;
        public ICommand CopyProfileUrlCommand => AccountsVm.CopyProfileUrlCommand;
        public ICommand CopySecurityTokenCommand => AccountsVm.CopySecurityTokenCommand;
        public ICommand SavePlaceIdToAccountCommand => AccountsVm.SavePlaceIdToAccountCommand;
        public ICommand ViewFieldsCommand => AccountsVm.ViewFieldsCommand;
        public ICommand OpenUtilsCommand => AccountsVm.OpenUtilsCommand;
        public ICommand RemoveAccountCommand => AccountsVm.RemoveAccountCommand;
        public ICommand SetGroupSelectedCommand => AccountsVm.SetGroupSelectedCommand;
        public ICommand SavePlaceIdSelectedCommand => AccountsVm.SavePlaceIdSelectedCommand;
        public ICommand CopyUserPassSelectedCommand => AccountsVm.CopyUserPassSelectedCommand;
        public ICommand RemoveSelectedCommand => AccountsVm.RemoveSelectedCommand;
        public ICommand ReLoginSelectedCommand => AccountsVm.ReLoginSelectedCommand;
        public ICommand ValidateSessionSelectedCommand => AccountsVm.ValidateSessionSelectedCommand;
        public ICommand ClearRobloxClientSessionCommand => AccountsVm.ClearRobloxClientSessionCommand;
        public ICommand SelectAllCommand => AccountsVm.SelectAllCommand;
        public ICommand ClearSelectionCommand => AccountsVm.ClearSelectionCommand;
        public ICommand ClearSearchCommand => AccountsVm.ClearSearchCommand;
        public ICommand CheckSessionsCommand => AccountsVm.CheckSessionsCommand;
        public ICommand ReLoginAllExpiredCommand => AccountsVm.ReLoginAllExpiredCommand;
        public ICommand SetSortModeCommand => AccountsVm.SetSortModeCommand;
        public ICommand RefreshAllRobuxCommand => AccountsVm.RefreshAllRobuxCommand;
        public ICommand CheckRobuxAccountCommand => AccountsVm.CheckRobuxAccountCommand;
        public IAsyncRelayCommand AuditAllAccountsCommand => AccountsVm.AuditAllAccountsCommand;
        public ICommand SelectReadyBatchCommand => AccountsVm.SelectReadyBatchCommand;
        public ICommand InvertSelectionCommand => AccountsVm.InvertSelectionCommand;
        public ICommand SelectByRangePromptCommand => AccountsVm.SelectByRangePromptCommand;
        public ICommand MetricCardInGameCommand => AccountsVm.MetricCardInGameCommand;
        public ICommand MetricCardReadyCommand => AccountsVm.MetricCardReadyCommand;
        public ICommand MetricCardExpiredCommand => AccountsVm.MetricCardExpiredCommand;
        public ICommand MetricCardRobuxCommand => AccountsVm.MetricCardRobuxCommand;
        public ICommand SetDensityModeCommand => AccountsVm.SetDensityModeCommand;
        public ICommand ClearFiltersCommand => AccountsVm.ClearFiltersCommand;

        public string PlaceId { get => LaunchBar.PlaceId; set => LaunchBar.PlaceId = value; }
        public string JobId { get => LaunchBar.JobId; set => LaunchBar.JobId = value; }
        public bool IsShuffleJobId { get => LaunchBar.IsShuffleJobId; set => LaunchBar.IsShuffleJobId = value; }
        public string CurrentGameName => LaunchBar.CurrentGameName;
        public Bitmap? CurrentGameThumbnail => LaunchBar.CurrentGameThumbnail;
        public bool HasCurrentGameInfo => LaunchBar.HasCurrentGameInfo;
        public Color? AdaptiveGameColor => LaunchBar.AdaptiveGameColor;
        public ObservableCollection<RecentGameItemViewModel> RecentGamesList { get => LaunchBar.RecentGamesList; set => LaunchBar.RecentGamesList = value; }
        public bool HasRecentGames => LaunchBar.HasRecentGames;
        public bool IsBatchLaunching => LaunchBar.IsBatchLaunching;
        public string BatchLaunchTitleText => LaunchBar.BatchLaunchTitleText;
        public string BatchLaunchDetailText => LaunchBar.BatchLaunchDetailText;
        public double BatchLaunchProgressPercent => LaunchBar.BatchLaunchProgressPercent;
        public string LaunchHomeButtonText => LaunchBar.LaunchHomeButtonText;
        public string JoinServerButtonText => LaunchBar.JoinServerButtonText;
        public Action<AccountViewModel>? OnLaunchRequested { get => LaunchBar.OnLaunchRequested; set => LaunchBar.OnLaunchRequested = value; }
        public Action<AccountViewModel>? OnOpenHomeRequested { get => LaunchBar.OnOpenHomeRequested; set => LaunchBar.OnOpenHomeRequested = value; }
        public Action<IList<AccountViewModel>>? OnMultiLaunchRequested { get => LaunchBar.OnMultiLaunchRequested; set => LaunchBar.OnMultiLaunchRequested = value; }
        public Action<IList<AccountViewModel>>? OnJoinServerSelectedRequested { get => LaunchBar.OnJoinServerSelectedRequested; set => LaunchBar.OnJoinServerSelectedRequested = value; }
        public Action<AccountViewModel>? OnFastReLoginRequested { get => LaunchBar.OnFastReLoginRequested; set => LaunchBar.OnFastReLoginRequested = value; }
        public ICommand LaunchAccountCommand => LaunchBar.LaunchAccountCommand;
        public ICommand OpenHomeCommand => LaunchBar.OpenHomeCommand;
        public ICommand JoinServerAccountCommand => LaunchBar.JoinServerAccountCommand;
        public ICommand LaunchSelectedCommand => LaunchBar.LaunchSelectedCommand;
        public ICommand JoinServerSelectedCommand => LaunchBar.JoinServerSelectedCommand;
        public ICommand PrimaryCardActionCommand => LaunchBar.PrimaryCardActionCommand;
        public ICommand ViewBanAccountCommand => LaunchBar.ViewBanAccountCommand;
        public ICommand CloseAccountRobloxCommand => LaunchBar.CloseAccountRobloxCommand;
        public ICommand SelectRecentGameCommand => LaunchBar.SelectRecentGameCommand;
        public ICommand CancelBatchLaunchCommand => LaunchBar.CancelBatchLaunchCommand;
        public ICommand OpenCurrentGameServersCommand => LaunchBar.OpenCurrentGameServersCommand;

        public string StatusMessage { get => ToolsStatus.StatusMessage; set => ToolsStatus.StatusMessage = value; }
        public string FooterReadyStatusText => ToolsStatus.FooterReadyStatusText;
        public int RunningRobloxProcessCount { get => ToolsStatus.RunningRobloxProcessCount; set => ToolsStatus.RunningRobloxProcessCount = value; }
        public bool HasRunningRobloxProcesses => ToolsStatus.HasRunningRobloxProcesses;
        public string CloseRobloxButtonText => ToolsStatus.CloseRobloxButtonText;
        public bool IsPotatoMode { get => ToolsStatus.IsPotatoMode; set => ToolsStatus.IsPotatoMode = value; }
        public bool IsAntiAfk { get => ToolsStatus.IsAntiAfk; set => ToolsStatus.IsAntiAfk = value; }
        public bool AreWindowsHidden { get => ToolsStatus.AreWindowsHidden; set => ToolsStatus.AreWindowsHidden = value; }
        public bool IsSmartMuteActive => ToolsStatus.IsSmartMuteActive;
        public bool IsStreamerModeActive => ToolsStatus.IsStreamerModeActive;
        public bool IsStreamerModeEnabled { get => ToolsStatus.IsStreamerModeEnabled; set => ToolsStatus.IsStreamerModeEnabled = value; }
        public char JobIdMaskChar => ToolsStatus.JobIdMaskChar;
        public int ActiveToolsCount => ToolsStatus.ActiveToolsCount;
        public bool HasActiveTools => ToolsStatus.HasActiveTools;
        public ICommand AntiLagBoosterCommand => ToolsStatus.AntiLagBoosterCommand;
        public ICommand ToggleWindowsCommand => ToolsStatus.ToggleWindowsCommand;
        public ICommand ToggleAntiAfkCommand => ToolsStatus.ToggleAntiAfkCommand;
        public ICommand TogglePotatoModeCommand => ToolsStatus.TogglePotatoModeCommand;
        public ICommand ToggleStreamerModeCommand => ToolsStatus.ToggleStreamerModeCommand;
        public ICommand ToggleSmartMuteCommand => ToolsStatus.ToggleSmartMuteCommand;
        public ICommand FocusAccountWindowCommand => ToolsStatus.FocusAccountWindowCommand;
        public ICommand ArrangeRobloxWindowsCommand => ToolsStatus.ArrangeRobloxWindowsCommand;
        public ICommand OpenProxyManagerCommand => ToolsStatus.OpenProxyManagerCommand;
        public ICommand CleanSystemTrayCommand => ToolsStatus.CleanSystemTrayCommand;
        public ICommand KillAllClientsCommand => ToolsStatus.KillAllClientsCommand;
        public ICommand RefreshPresenceCommand => ToolsStatus.RefreshPresenceCommand;

        public ObservableCollection<CommandPaletteItemViewModel> CommandPaletteItems => CommandPalette.CommandPaletteItems;
        public ObservableCollection<AccountViewModel> CommandPaletteResults => CommandPalette.CommandPaletteResults;
        public CommandPaletteItemViewModel? SelectedCommandPaletteItem => CommandPalette.SelectedCommandPaletteItem;
        public AccountViewModel? SelectedCommandPaletteAccount => CommandPalette.SelectedCommandPaletteAccount;
        public bool IsCommandPaletteOpen { get => CommandPalette.IsCommandPaletteOpen; set => CommandPalette.IsCommandPaletteOpen = value; }
        public string CommandPaletteSearchText { get => CommandPalette.CommandPaletteSearchText; set => CommandPalette.CommandPaletteSearchText = value; }
        public CommandPaletteScope ActiveScope { get => CommandPalette.ActiveScope; set => CommandPalette.ActiveScope = value; }
        public bool HasPreviewItem => CommandPalette.HasPreviewItem;
        public IReadOnlyList<string> RecentCommandIds => CommandPalette.RecentCommandIds;
        public ICommand SetScopeCommand => CommandPalette.SetScopeCommand;

        public AccountViewModel? TryGetAccountViewModel(Account? account) => AccountsVm.TryGetAccountViewModel(account);
        internal void RebuildLookupIndex() => AccountsVm.RebuildLookupIndex();
        internal void RemoveAccountViewModels(params AccountViewModel[] toRemove) => AccountsVm.RemoveAccountViewModels(toRemove);
        public void LoadAccounts(IEnumerable<Account> sourceAccounts) => AccountsVm.LoadAccounts(sourceAccounts);
        public void RebuildGroupList(bool applyFilterOnReset = true) => AccountsVm.RebuildGroupList(applyFilterOnReset);
        public void ApplyFilter() => AccountsVm.ApplyFilter();
        public void RefreshAccount(Account? account) => AccountsVm.RefreshAccount(account);
        public void UpdateAccountValidity(Account account, bool isValid, bool refreshFilterAndCounts = true) => AccountsVm.UpdateAccountValidity(account, isValid, refreshFilterAndCounts);
        public void NotifyCounts() => AccountsVm.NotifyCounts();
        public List<Account> GetSelectedAccounts() => AccountsVm.GetSelectedAccounts();
        public List<AccountViewModel> GetSelectedAccountViewModels() => AccountsVm.GetSelectedAccountViewModels();
        public Task<IReadOnlyDictionary<Account, AccountSessionAuditResult>> AuditAccountSessionsAsync(CancellationToken ct = default) => AccountsVm.AuditAccountSessionsAsync(ct);
        public void HandleAccountClickSelection(AccountViewModel? target, bool isShiftPressed, bool? explicitTargetState = null) => AccountsVm.HandleAccountClickSelection(target, isShiftPressed, explicitTargetState);
        public void ApplySelectionBatch(IReadOnlyList<AccountViewModel>? targets, bool additive) => AccountsVm.ApplySelectionBatch(targets, additive);
        public int SelectFirstReadyAccounts(int count) => AccountsVm.SelectFirstReadyAccounts(count);
        public int InvertFilteredSelection() => AccountsVm.InvertFilteredSelection();
        public int SelectRangeExpression(string? expression) => AccountsVm.SelectRangeExpression(expression);
        public static int CalculateResponsiveCardColumns(double availableWidth) => AccountCollectionViewModel.CalculateResponsiveCardColumns(availableWidth);
        public void SetKnownGameTitle(long placeId, string? title) => LaunchBar.SetKnownGameTitle(placeId, title);
        public void ScheduleCurrentGameLookup(string? rawPlaceId) => LaunchBar.ScheduleCurrentGameLookup(rawPlaceId);
        public void NormalizePlaceIdInput(string? rawInput = null) => LaunchBar.NormalizePlaceIdInput(rawInput);
        public void ReloadRecentGames() => LaunchBar.ReloadRecentGames();
        internal void UpdateBatchLaunchBanner(LaunchProgress progress) => LaunchBar.UpdateBatchLaunchBanner(progress);
        public void RefreshStateFromSettings() => ToolsStatus.RefreshStateFromSettings();
        public void NotifyToolStatesChanged() => ToolsStatus.NotifyToolStatesChanged();
        public void RefreshLiveProcessStatus() => ToolsStatus.RefreshLiveProcessStatus();
        public void UpdateCommandPaletteResults() => CommandPalette.UpdateCommandPaletteResults();
        public void SetSelectedCommandPaletteIndex(int index) => CommandPalette.SetSelectedCommandPaletteIndex(index);
        public void MoveCommandPaletteSelection(int delta) => CommandPalette.MoveCommandPaletteSelection(delta);
        public bool ExecuteCommandPaletteSelection(bool joinCurrentServer = false) => CommandPalette.ExecuteCommandPaletteSelection(joinCurrentServer);
        public static string NormalizeSearchText(string? input) => CommandPaletteViewModel.NormalizeSearchText(input);

        IReadOnlyList<IAccountWorkflowItem> IAccountWorkflowHost.GetSelectedWorkflowItems() => ((IAccountWorkflowHost)AccountsVm).GetSelectedWorkflowItems();
        IReadOnlyList<Account> IAccountWorkflowHost.GetSelectedAccounts() => ((IAccountWorkflowHost)AccountsVm).GetSelectedAccounts();
        void IAccountWorkflowHost.RebuildLookupIndex() => ((IAccountWorkflowHost)AccountsVm).RebuildLookupIndex();
        void IAccountWorkflowHost.RemoveWorkflowItems(params IAccountWorkflowItem[] toRemove) => ((IAccountWorkflowHost)AccountsVm).RemoveWorkflowItems(toRemove);
    }
}
