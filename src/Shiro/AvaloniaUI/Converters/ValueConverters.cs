#nullable enable
using System;
using System.Globalization;
using Avalonia.Data;
using Avalonia.Data.Converters;
using Shiro.Services;

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

    public class CountToVisibilityConverter : IValueConverter
    {
        public static readonly CountToVisibilityConverter Instance = new();

        public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        {
            if (value is int count) return count > 0;
            if (value is long l) return l > 0;
            return false;
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
                return LocalizationService.Get("Auto_ValueConverters_033");
            }

            return s;
        }

        public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
            => BindingOperations.DoNothing;
    }
}
