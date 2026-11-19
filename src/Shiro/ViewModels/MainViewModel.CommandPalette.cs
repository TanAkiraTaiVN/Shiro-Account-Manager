#nullable enable
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using Shiro.Services;

namespace Shiro.ViewModels
{
    public partial class MainViewModel
    {
        private bool _isCommandPaletteOpen;
        private string _commandPaletteSearchText = string.Empty;
        private int _selectedCommandPaletteIndex = -1;
        private CommandPaletteItemViewModel? _selectedCommandPaletteItem;

        private readonly ObservableCollection<CommandPaletteItemViewModel> _commandPaletteItems = new();
        public ObservableCollection<CommandPaletteItemViewModel> CommandPaletteItems => _commandPaletteItems;

        private readonly ObservableCollection<AccountViewModel> _commandPaletteResults = new();
        public ObservableCollection<AccountViewModel> CommandPaletteResults => _commandPaletteResults;

        public CommandPaletteItemViewModel? SelectedCommandPaletteItem => _selectedCommandPaletteItem;
        public AccountViewModel? SelectedCommandPaletteAccount => _selectedCommandPaletteItem?.Account;

        public bool IsCommandPaletteOpen
        {
            get => _isCommandPaletteOpen;
            set
            {
                if (SetProperty(ref _isCommandPaletteOpen, value) && value)
                {
                    CommandPaletteSearchText = string.Empty;
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
                this,
                _commandPaletteSearchText.Trim(),
                _accounts,
                _commandPaletteItems,
                _commandPaletteResults);
            SetSelectedCommandPaletteIndex(0);
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
                target.CommandAction.Invoke(this);
                return true;
            }

            return false;
        }

        public static string NormalizeSearchText(string? input) => CommandPaletteCoordinator.NormalizeSearchText(input);

        private static IReadOnlyList<CommandPaletteCatalog.BuiltinCommandDef> GetBuiltinCommands() =>
            CommandPaletteCatalog.GetBuiltinCommands();
    }
}
