#nullable enable
using System;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using LucideAvalonia.Enum;
using Shiro.Classes;

namespace Shiro.ViewModels
{
    public enum CommandPaletteItemType
    {
        Command,
        Navigation,
        Account
    }

    /// <summary>
    /// Represents an actionable item in the Ctrl+K Command Palette:
    /// system commands, quick tools, navigation destinations, and accounts.
    /// </summary>
    public class CommandPaletteItemViewModel : ObservableObject
    {
        private bool _isSelected;
        private string _iconName = "Terminal";
        private LucideIconNames _iconKind = LucideIconNames.Terminal;

        public CommandPaletteItemType ItemType { get; set; } = CommandPaletteItemType.Command;
        public string Title { get; set; } = string.Empty;
        public string Description { get; set; } = string.Empty;
        public string Category { get; set; } = string.Empty;
        public string ActionBadge { get; set; } = "Enter";

        public string IconName
        {
            get => _iconName;
            set
            {
                _iconName = value ?? "Terminal";
                _iconKind = Enum.TryParse<LucideIconNames>(_iconName, ignoreCase: true, out var parsed)
                    ? parsed
                    : LucideIconNames.Terminal;
            }
        }

        public LucideIconNames IconKind
        {
            get => _iconKind;
            set
            {
                _iconKind = value;
                _iconName = value.ToString();
            }
        }

        public string IconColorHex { get; set; } = "#38BDF8";
        public IBrush IconBrush => SolidColorBrush.Parse(string.IsNullOrWhiteSpace(IconColorHex) ? "#38BDF8" : IconColorHex);

        public Geometry? IconGeometry => TryParseGeometry(GetCommandIconPath(_iconName));

        private static Geometry? TryParseGeometry(string pathData)
        {
            try
            {
                return StreamGeometry.Parse(pathData);
            }
            catch (Exception ex)
            {
                Shiro.Services.AppServices.Logger.Debug("[CommandPaletteItemViewModel] TryParseGeometry failed", ex);
                return null;
            }
        }

        private static string GetCommandIconPath(string? name) => (name?.Trim().ToLowerInvariant()) switch
        {
            "shield" or "shieldcheck" => "M 12,22 C 12,22 20,18 20,12 L 20,5 L 12,2 L 4,5 L 4,12 C 4,18 12,22 12,22 Z M 9,12 L 11,14 L 15,10",
            "eyeoff" => "M 2,2 L 22,22 M 6.7,6.7 C 4.6,8.1 3,10.2 2,12 C 3.8,15.4 7.5,19 12,19 C 14.1,19 16,18.2 17.6,17 M 10.6,5.2 C 11,5.1 11.5,5 12,5 C 16.5,5 20.2,8.6 22,12 C 21.3,13.3 20.3,14.6 19.1,15.7",
            "zap" => "M 13,2 L 3,14 L 12,14 L 11,22 L 21,10 L 12,10 Z",
            "volume2" => "M 11,5 L 6,9 L 2,9 L 2,15 L 6,15 L 11,19 Z M 15.5,8.5 C 17.5,10.5 17.5,13.5 15.5,15.5 M 19,5 C 23,9 23,15 19,19",
            "volumex" => "M 11,5 L 6,9 L 2,9 L 2,15 L 6,15 L 11,19 Z M 22,9 L 16,15 M 16,9 L 22,15",
            "cpu" => "M 6,4 L 18,4 C 19.1,4 20,4.9 20,6 L 20,18 C 20,19.1 19.1,20 18,20 L 6,20 C 4.9,20 4,19.1 4,18 L 4,6 C 4,4.9 4.9,4 6,4 Z M 9,9 L 15,9 L 15,15 L 9,15 Z M 9,1 L 9,4 M 15,1 L 15,4 M 9,20 L 9,23 M 15,20 L 15,23 M 20,9 L 23,9 M 20,14 L 23,14 M 1,9 L 4,9 M 1,14 L 4,14",
            "brush" => "M 18,3 L 21,6 L 11,16 L 8,13 Z M 8,13 C 5.5,13 4,15 4,17.5 C 4,19 2.5,20 2,20 C 3.5,21.5 6.5,22 8.5,20 C 10,18.5 10.5,16.5 11,16",
            "layoutgrid" or "grid" => "M 3,3 L 10,3 L 10,10 L 3,10 Z M 14,3 L 21,3 L 21,10 L 14,10 Z M 14,14 L 21,14 L 21,21 L 14,21 Z M 3,14 L 10,14 L 10,21 L 3,21 Z",
            "layers" => "M 12,2 L 2,7 L 12,12 L 22,7 Z M 2,12 L 12,17 L 22,12 M 2,17 L 12,22 L 22,17",
            "power" => "M 12,2 L 12,12 M 18.4,6.6 C 21.9,10.1 21.9,15.8 18.4,19.3 C 14.9,22.8 9.1,22.8 5.6,19.3 C 2.1,15.8 2.1,10.1 5.6,6.6",
            "refreshcw" => "M 21,3 L 21,9 L 15,9 M 3,21 L 3,15 L 9,15 M 20.5,9 C 19.1,5 15,2.5 10.5,3.2 C 6.8,3.8 4,6.6 3.2,10.2 M 3.5,15 C 4.9,19 9,21.5 13.5,20.8 C 17.2,20.2 20,17.4 20.8,13.8",
            "key" => "M 21,2 L 19,4 M 11.4,11.6 C 13.5,13.7 13.5,17.2 11.4,19.4 C 9.2,21.5 5.8,21.5 3.6,19.4 C 1.5,17.2 1.5,13.7 3.6,11.6 C 5.8,9.5 9.2,9.5 11.4,11.6 Z M 11.4,11.6 L 21,2 M 16,7 L 19,10",
            "coins" => "M 8,8 A 6,6 0 1 0 20,8 A 6,6 0 1 0 8,8 Z M 4,16 A 6,6 0 1 0 16,16 A 6,6 0 1 0 4,16 Z",
            "checkcheck" or "listchecks" => "M 18,6 L 7,17 L 2,12 M 22,10 L 14.5,17.5 L 13,16",
            "square" => "M 5,3 L 19,3 C 20.1,3 21,3.9 21,5 L 21,19 C 21,20.1 20.1,21 19,21 L 5,21 C 3.9,21 3,20.1 3,19 L 3,5 C 3,3.9 3.9,3 5,3 Z",
            "arrowupdown" => "M 7,3 L 7,21 M 3,7 L 7,3 L 11,7 M 17,21 L 17,3 M 13,17 L 17,21 L 21,17",
            "hash" => "M 4,9 L 20,9 M 4,15 L 20,15 M 10,3 L 8,21 M 16,3 L 14,21",
            "globe" => "M 12,2 A 10,10 0 1 0 12,22 A 10,10 0 1 0 12,2 Z M 2,12 L 22,12 M 12,2 C 14.5,4.7 16,8.3 16,12 C 16,15.7 14.5,19.3 12,22 C 9.5,19.3 8,15.7 8,12 C 8,8.3 9.5,4.7 12,2 Z",
            "gamepad2" => "M 6,11 L 10,11 M 8,9 L 8,13 M 15,12 L 15.01,12 M 18,10 L 18.01,10 M 17.3,5 L 6.7,5 C 4.5,5 2.7,6.6 2.3,8.8 L 1.2,15.8 C 0.9,17.8 2.4,19.5 4.4,19.5 C 5.5,19.5 6.5,19 7.1,18.1 L 9,15.5 L 15,15.5 L 16.9,18.1 C 17.5,19 18.5,19.5 19.6,19.5 C 21.6,19.5 23.1,17.8 22.8,15.8 L 21.7,8.8 C 21.3,6.6 19.5,5 17.3,5 Z",
            "settings" => "M 12,9 A 3,3 0 1 0 12,15 A 3,3 0 1 0 12,9 Z M 19.4,15 L 21,16.5 L 19,20 L 16.9,19.2 C 16.3,19.6 15.7,20 15,20.2 L 14.5,22.4 L 9.5,22.4 L 9,20.2 C 8.3,20 7.7,19.6 7.1,19.2 L 5,20 L 3,16.5 L 4.6,15 C 4.5,14.3 4.5,13.7 4.6,13 L 3,11.5 L 5,8 L 7.1,8.8 C 7.7,8.4 8.3,8 9,7.8 L 9.5,5.6 L 14.5,5.6 L 15,7.8 C 15.7,8 16.3,8.4 16.9,8.8 L 19,8 L 21,11.5 L 19.4,13 C 19.5,13.7 19.5,14.3 19.4,15 Z",
            "user" or "userplus" => "M 20,21 L 20,19 C 20,16.8 18.2,15 16,15 L 8,15 C 5.8,15 4,16.8 4,19 L 4,21 M 12,3 A 4,4 0 1 0 12,11 A 4,4 0 1 0 12,3 Z",
            "network" or "radio" => "M 12,10 A 2,2 0 1 0 12,14 A 2,2 0 1 0 12,10 Z M 16.2,7.8 C 18.6,10.1 18.6,13.9 16.2,16.2 M 7.8,16.2 C 5.4,13.9 5.4,10.1 7.8,7.8 M 19.1,4.9 C 23,8.8 23,15.2 19.1,19.1 M 4.9,19.1 C 1,15.2 1,8.8 4.9,4.9",
            _ => "M 4,17 L 10,11 L 4,5 M 12,19 L 20,19"
        };

        public bool IsCommand => ItemType != CommandPaletteItemType.Account;
        public bool IsAccount => ItemType == CommandPaletteItemType.Account;

        // Account-specific bindings
        public AccountViewModel? Account { get; set; }
        public Bitmap? AvatarImage => Account?.AvatarImage;
        public string Initials => Account?.Initials ?? "?";
        public string ShownUsername => Account?.ShownUsername ?? Title;
        public string Alias => Account?.Alias ?? string.Empty;
        public string StatusText => Account?.StatusText ?? string.Empty;
        public IBrush? StatusBadgeForeground => Account?.StatusBadgeForeground;

        public Action<MainViewModel>? CommandAction { get; set; }
        public Action<bool>? AccountAction { get; set; }

        public bool IsSelected
        {
            get => _isSelected;
            set
            {
                if (SetProperty(ref _isSelected, value))
                {
                    OnPropertyChanged(nameof(RowBackground));
                    OnPropertyChanged(nameof(RowBorder));
                    if (Account != null)
                    {
                        Account.IsCommandPaletteSelected = value;
                    }
                }
            }
        }

        public IBrush RowBackground => IsSelected
            ? SolidColorBrush.Parse("#252538")
            : SolidColorBrush.Parse("#14141E");

        public IBrush RowBorder => IsSelected
            ? SolidColorBrush.Parse("#6366F1")
            : SolidColorBrush.Parse("#20202E");

        public IBrush CategoryBackground => ItemType switch
        {
            CommandPaletteItemType.Command => SolidColorBrush.Parse("#1A2436"),
            CommandPaletteItemType.Navigation => SolidColorBrush.Parse("#231E38"),
            _ => SolidColorBrush.Parse("#192920")
        };

        public IBrush CategoryForeground => ItemType switch
        {
            CommandPaletteItemType.Command => SolidColorBrush.Parse("#38BDF8"),
            CommandPaletteItemType.Navigation => SolidColorBrush.Parse("#C084FC"),
            _ => SolidColorBrush.Parse("#4ADE80")
        };
    }
}
