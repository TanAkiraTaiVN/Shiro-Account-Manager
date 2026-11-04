#nullable enable
using System;
using Avalonia.Media;
using LucideAvalonia.Enum;
using Shiro.Classes;
using Shiro.ViewModels;
using Xunit;

namespace Shiro.Tests
{
    public class CommandPaletteItemViewModelTests
    {
        [Fact]
        public void Defaults_MatchExpectedValues()
        {
            var vm = new CommandPaletteItemViewModel();

            Assert.Equal(CommandPaletteItemType.Command, vm.ItemType);
            Assert.Equal("Terminal", vm.IconName);
            Assert.Equal(LucideIconNames.Terminal, vm.IconKind);
            Assert.Equal("Enter", vm.ActionBadge);
            Assert.Equal("#38BDF8", vm.IconColorHex);
            Assert.True(vm.IsCommand);
            Assert.False(vm.IsAccount);
            Assert.False(vm.IsSelected);
            Assert.Equal("?", vm.Initials);
            Assert.Equal(string.Empty, vm.ShownUsername);
            Assert.Equal(string.Empty, vm.Alias);
            Assert.Equal(string.Empty, vm.StatusText);
            Assert.Null(vm.StatusBadgeForeground);
            Assert.NotNull(vm.IconBrush);
            Assert.Null(vm.IconGeometry);
        }

        [Fact]
        public void IconName_And_IconKind_StaySynchronized()
        {
            var vm = new CommandPaletteItemViewModel();

            vm.IconName = "Cpu";
            Assert.Equal("Cpu", vm.IconName);
            Assert.Equal(LucideIconNames.Cpu, vm.IconKind);

            vm.IconKind = LucideIconNames.Settings;
            Assert.Equal("Settings", vm.IconName);
            Assert.Equal(LucideIconNames.Settings, vm.IconKind);

            // Null input falls back to Terminal
            vm.IconName = null!;
            Assert.Equal("Terminal", vm.IconName);
            Assert.Equal(LucideIconNames.Terminal, vm.IconKind);

            // Unknown enum falls back to Terminal
            vm.IconName = "NotARealIconKindName123";
            Assert.Equal("NotARealIconKindName123", vm.IconName);
            Assert.Equal(LucideIconNames.Terminal, vm.IconKind);
        }

        [Theory]
        [InlineData("shield")]
        [InlineData("shieldcheck")]
        [InlineData("eyeoff")]
        [InlineData("zap")]
        [InlineData("volume2")]
        [InlineData("volumex")]
        [InlineData("cpu")]
        [InlineData("brush")]
        [InlineData("layoutgrid")]
        [InlineData("grid")]
        [InlineData("layers")]
        [InlineData("power")]
        [InlineData("refreshcw")]
        [InlineData("key")]
        [InlineData("coins")]
        [InlineData("checkcheck")]
        [InlineData("listchecks")]
        [InlineData("square")]
        [InlineData("arrowupdown")]
        [InlineData("hash")]
        [InlineData("globe")]
        [InlineData("gamepad2")]
        [InlineData("settings")]
        [InlineData("user")]
        [InlineData("userplus")]
        [InlineData("network")]
        [InlineData("radio")]
        [InlineData("custom_fallback")]
        public void IconGeometry_ParsesKnownIconPathVariants(string iconName)
        {
            var vm = new CommandPaletteItemViewModel { IconName = iconName };
            Assert.Equal(iconName, vm.IconName);
            Assert.Null(vm.IconGeometry);
        }

        [Fact]
        public void IconColorHex_EmptyOrWhitespace_FallsBackToDefaultHex()
        {
            var vm = new CommandPaletteItemViewModel { IconColorHex = "   " };
            var brush = vm.IconBrush as ISolidColorBrush;

            Assert.NotNull(brush);
            Assert.Equal(Color.Parse("#38BDF8"), brush!.Color);
        }

        [Fact]
        public void Selection_UpdatesVisualBrushes_And_SyncsAccountPaletteFlag()
        {
            var accountVm = new AccountViewModel(new Account { Username = "TestPalUser" });
            var vm = new CommandPaletteItemViewModel
            {
                ItemType = CommandPaletteItemType.Account,
                Account = accountVm
            };

            var unselectedBg = (ISolidColorBrush)vm.RowBackground;
            var unselectedBorder = (ISolidColorBrush)vm.RowBorder;
            Assert.Equal(Color.Parse("#14141E"), unselectedBg.Color);
            Assert.Equal(Color.Parse("#20202E"), unselectedBorder.Color);
            Assert.False(accountVm.IsCommandPaletteSelected);

            vm.IsSelected = true;

            var selectedBg = (ISolidColorBrush)vm.RowBackground;
            var selectedBorder = (ISolidColorBrush)vm.RowBorder;
            Assert.Equal(Color.Parse("#252538"), selectedBg.Color);
            Assert.Equal(Color.Parse("#6366F1"), selectedBorder.Color);
            Assert.True(accountVm.IsCommandPaletteSelected);
        }

        [Theory]
        [InlineData(CommandPaletteItemType.Command, "#1A2436", "#38BDF8")]
        [InlineData(CommandPaletteItemType.Navigation, "#231E38", "#C084FC")]
        [InlineData(CommandPaletteItemType.Account, "#192920", "#4ADE80")]
        public void CategoryBrushes_MatchExpectedColorsPerType(
            CommandPaletteItemType itemType,
            string expectedBgHex,
            string expectedFgHex)
        {
            var vm = new CommandPaletteItemViewModel { ItemType = itemType };

            var bg = (ISolidColorBrush)vm.CategoryBackground;
            var fg = (ISolidColorBrush)vm.CategoryForeground;

            Assert.Equal(Color.Parse(expectedBgHex), bg.Color);
            Assert.Equal(Color.Parse(expectedFgHex), fg.Color);
        }

        [Fact]
        public void AccountBinding_BindsPropertiesAndActionsCorrectly()
        {
            var rawAccount = new Account
            {
                Username = "PlayerAlpha",
                Alias = "AlphaPrime",
                SecurityToken = "token"
            };
            var accountVm = new AccountViewModel(rawAccount);

            bool actionExecuted = false;
            var vm = new CommandPaletteItemViewModel
            {
                ItemType = CommandPaletteItemType.Account,
                Title = "PaletteFallbackTitle",
                Account = accountVm,
                AccountAction = forceLaunch => { actionExecuted = true; }
            };

            Assert.True(vm.IsAccount);
            Assert.False(vm.IsCommand);
            Assert.Equal("PlayerAlpha", vm.ShownUsername);
            Assert.Equal("AlphaPrime", vm.Alias);
            Assert.Equal("P", vm.Initials);

            vm.AccountAction?.Invoke(true);
            Assert.True(actionExecuted);
        }

        [Fact]
        public void CommandAction_ExecutesActionCallback()
        {
            bool commandRan = false;
            var vm = new CommandPaletteItemViewModel
            {
                ItemType = CommandPaletteItemType.Command,
                CommandAction = mainVm => { commandRan = true; }
            };

            vm.CommandAction?.Invoke(null!);
            Assert.True(commandRan);
        }
    }
}
