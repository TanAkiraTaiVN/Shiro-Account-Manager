#nullable enable
using System;
using System.Globalization;
using Avalonia.Data;
using Avalonia.Data.Converters;
using Avalonia.Media;
using Avalonia.Media.Immutable;
using Shiro.Services;
using Shiro.ViewModels.Dialogs;

namespace Shiro.AvaloniaUI.Converters
{
    public class InverseBooleanConverter : IValueConverter
    {
        public static readonly InverseBooleanConverter Instance = new();

        public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        {
            if (value is bool b) return !b;
            return false;
        }

        public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        {
            if (value is bool b) return !b;
            return false;
        }
    }

    public class StringNotNullOrEmptyConverter : IValueConverter
    {
        public static readonly StringNotNullOrEmptyConverter Instance = new();

        public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        {
            return !string.IsNullOrWhiteSpace(value as string);
        }

        public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        {
            throw new NotSupportedException();
        }
    }

    public class EqualityToBooleanConverter : IValueConverter
    {
        public static readonly EqualityToBooleanConverter Instance = new();

        public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        {
            if (value == null && parameter == null) return true;
            if (value == null || parameter == null) return false;
            return string.Equals(value.ToString(), parameter.ToString(), StringComparison.OrdinalIgnoreCase);
        }

        public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        {
            if (value is bool b && b && parameter != null)
                return parameter;
            return BindingOperations.DoNothing;
        }
    }

    public class GroupNameDisplayConverter : IValueConverter
    {
        public static readonly GroupNameDisplayConverter Instance = new();

        public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        {
            if (value is not string s || string.IsNullOrWhiteSpace(s))
                return Loc.Get("Dash_AllGroups");

            if (string.Equals(s, "All Groups", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(s, Shiro.Services.LocalizationService.Get("Dash_AllGroups"), StringComparison.OrdinalIgnoreCase))
            {
                return Loc.Get("Dash_AllGroups");
            }

            if (string.Equals(s, "Default", StringComparison.OrdinalIgnoreCase))
            {
                return LocalizationService.Get("Converters.DefaultGroup");
            }

            return s;
        }

        public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
            => BindingOperations.DoNothing;
    }

    public class StatusKindToBrushConverter : IValueConverter
    {
        public static readonly StatusKindToBrushConverter Instance = new();
        private static readonly IBrush NeutralBrush = new ImmutableSolidColorBrush(Color.Parse("#94A3B8"));
        private static readonly IBrush InfoBrush = Brushes.DeepSkyBlue;
        private static readonly IBrush SuccessBrush = new ImmutableSolidColorBrush(Color.Parse("#22C55E"));
        private static readonly IBrush WarningBrush = Brushes.Orange;
        private static readonly IBrush ErrorBrush = new ImmutableSolidColorBrush(Color.Parse("#EF4444"));

        public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        {
            if (value is StatusKind kind)
            {
                return kind switch
                {
                    StatusKind.Info => InfoBrush,
                    StatusKind.Success => SuccessBrush,
                    StatusKind.Warning => WarningBrush,
                    StatusKind.Error => ErrorBrush,
                    _ => NeutralBrush
                };
            }
            return NeutralBrush;
        }

        public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
            => BindingOperations.DoNothing;
    }

    public class IntToDecimalConverter : IValueConverter
    {
        public static readonly IntToDecimalConverter Instance = new();

        public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        {
            return value is int i ? (decimal?)i : 0m;
        }

        public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        {
            return value is decimal d ? (int)Math.Round(d) : 0;
        }
    }
}
