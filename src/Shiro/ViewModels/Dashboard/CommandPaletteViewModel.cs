#nullable enable
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Windows.Input;
using Shiro.Services;

namespace Shiro.ViewModels
{
    public enum CommandPaletteScope
    {
        All = 0,
        Accounts = 1,
        Commands = 2
    }

    /// <summary>
    /// Manages Ctrl+K Command Palette 2.0 search, scope filters, MRU recent history,
    /// live preview selection state, and command/account execution.
    /// </summary>
    public sealed class CommandPaletteViewModel : ObservableObject
    {
        public const int MaxRecentCommandHistory = 5;

        private readonly MainViewModel _host;
        private readonly ObservableCollection<CommandPaletteItemViewModel> _commandPaletteItems = new();
        private readonly ObservableCollection<AccountViewModel> _commandPaletteResults = new();
        private readonly List<string> _recentCommandIds = new();

        private bool _isCommandPaletteOpen;
        private string _commandPaletteSearchText = string.Empty;
        private CommandPaletteScope _activeScope = CommandPaletteScope.All;
        private int _selectedCommandPaletteIndex = -1;
        private CommandPaletteItemViewModel? _selectedCommandPaletteItem;

        public CommandPaletteViewModel(MainViewModel host)
        {
            _host = host ?? throw new ArgumentNullException(nameof(host));
            SetScopeCommand = new RelayCommand(ExecuteSetScope);
        }

        public ICommand SetScopeCommand { get; }
        public ObservableCollection<CommandPaletteItemViewModel> CommandPaletteItems => _commandPaletteItems;
        public ObservableCollection<AccountViewModel> CommandPaletteResults => _commandPaletteResults;
        public IReadOnlyList<string> RecentCommandIds => _recentCommandIds.AsReadOnly();
        public bool HasRecentCommands => _recentCommandIds.Count > 0;
        public CommandPaletteItemViewModel? SelectedCommandPaletteItem => _selectedCommandPaletteItem;
        public AccountViewModel? SelectedCommandPaletteAccount => _selectedCommandPaletteItem?.Account;
        public bool HasPreviewItem => _selectedCommandPaletteItem != null;
        public bool IsEmptyResults => _commandPaletteItems.Count == 0;

        public bool IsScopeAll => _activeScope == CommandPaletteScope.All;
        public bool IsScopeAccounts => _activeScope == CommandPaletteScope.Accounts;
        public bool IsScopeCommands => _activeScope == CommandPaletteScope.Commands;

        public CommandPaletteScope ActiveScope
        {
            get => _activeScope;
            set
            {
                CommandPaletteScope normalized = Enum.IsDefined(value) ? value : CommandPaletteScope.All;
                if (SetProperty(ref _activeScope, normalized))
                {
                    OnPropertyChanged(nameof(IsScopeAll));
                    OnPropertyChanged(nameof(IsScopeAccounts));
                    OnPropertyChanged(nameof(IsScopeCommands));
                    UpdateCommandPaletteResults();
                }
            }
        }

        public bool IsCommandPaletteOpen
        {
            get => _isCommandPaletteOpen;
            set
            {
                if (SetProperty(ref _isCommandPaletteOpen, value) && value)
                {
                    if (!string.IsNullOrEmpty(_commandPaletteSearchText))
                        CommandPaletteSearchText = string.Empty;
                    else
                        UpdateCommandPaletteResults();
                }
            }
        }

        public string CommandPaletteSearchText
        {
            get => _commandPaletteSearchText;
            set
            {
                if (SetProperty(ref _commandPaletteSearchText, value))
                    UpdateCommandPaletteResults();
            }
        }

        public void UpdateCommandPaletteResults()
        {
            CommandPaletteCatalog.PopulateResults(
                _host,
                _commandPaletteSearchText.Trim(),
                _host.AccountsVm.Accounts,
                _commandPaletteItems,
                _commandPaletteResults,
                _activeScope,
                _recentCommandIds);
            SetSelectedCommandPaletteIndex(0);
            OnPropertyChanged(nameof(IsEmptyResults));
        }

        public void SetSelectedCommandPaletteIndex(int index)
        {
            if (_commandPaletteItems.Count == 0)
            {
                if (_selectedCommandPaletteItem != null)
                    _selectedCommandPaletteItem.IsSelected = false;
                _selectedCommandPaletteItem = null;
                _selectedCommandPaletteIndex = -1;
                OnPropertyChanged(nameof(SelectedCommandPaletteItem));
                OnPropertyChanged(nameof(SelectedCommandPaletteAccount));
                OnPropertyChanged(nameof(HasPreviewItem));
                return;
            }

            int count = _commandPaletteItems.Count;
            int normalized = ((index % count) + count) % count;

            if (_selectedCommandPaletteItem != null && !ReferenceEquals(_selectedCommandPaletteItem, _commandPaletteItems[normalized]))
                _selectedCommandPaletteItem.IsSelected = false;

            _selectedCommandPaletteIndex = normalized;
            _selectedCommandPaletteItem = _commandPaletteItems[normalized];
            _selectedCommandPaletteItem.IsSelected = true;

            OnPropertyChanged(nameof(SelectedCommandPaletteItem));
            OnPropertyChanged(nameof(SelectedCommandPaletteAccount));
            OnPropertyChanged(nameof(HasPreviewItem));
        }

        public void MoveCommandPaletteSelection(int delta)
        {
            if (_commandPaletteItems.Count == 0) return;
            int baseIdx = _selectedCommandPaletteIndex < 0 ? 0 : _selectedCommandPaletteIndex;
            SetSelectedCommandPaletteIndex(baseIdx + delta);
        }

        public bool ExecuteCommandPaletteSelection(bool joinCurrentServer = false)
        {
            var target = _selectedCommandPaletteItem ?? _commandPaletteItems.FirstOrDefault();
            if (target == null) return false;

            IsCommandPaletteOpen = false;
            if (target.IsAccount)
            {
                target.AccountAction?.Invoke(joinCurrentServer);
                return true;
            }

            if (target.CommandAction != null)
            {
                RecordRecentCommand(target.CommandId);
                target.CommandAction.Invoke(_host);
                return true;
            }

            return false;
        }

        internal void NotifyLanguageChanged()
        {
            if (_isCommandPaletteOpen)
            {
                UpdateCommandPaletteResults();
                return;
            }

            foreach (var item in _commandPaletteItems)
                item.NotifyPreviewLanguageChanged();
        }

        private void ExecuteSetScope(object? parameter)
        {
            if (parameter is CommandPaletteScope scope)
            {
                ActiveScope = scope;
                return;
            }

            if (parameter is string str && Enum.TryParse<CommandPaletteScope>(str, ignoreCase: true, out var parsed))
                ActiveScope = parsed;
        }

        private void RecordRecentCommand(string? commandId)
        {
            if (string.IsNullOrWhiteSpace(commandId))
                return;

            int existingIdx = _recentCommandIds.FindIndex(id => string.Equals(id, commandId, StringComparison.Ordinal));
            if (existingIdx >= 0)
                _recentCommandIds.RemoveAt(existingIdx);

            _recentCommandIds.Insert(0, commandId);
            if (_recentCommandIds.Count > MaxRecentCommandHistory)
                _recentCommandIds.RemoveRange(MaxRecentCommandHistory, _recentCommandIds.Count - MaxRecentCommandHistory);

            OnPropertyChanged(nameof(RecentCommandIds));
            OnPropertyChanged(nameof(HasRecentCommands));
        }

        public static string NormalizeSearchText(string? input) => CommandPaletteCoordinator.NormalizeSearchText(input);
        internal static IReadOnlyList<CommandPaletteCatalog.BuiltinCommandDef> GetBuiltinCommands() =>
            CommandPaletteCatalog.GetBuiltinCommands();
    }
}
