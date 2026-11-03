#nullable enable
using System;
using System.Globalization;
using System.Text;

namespace Shiro.Services
{
    public sealed class CommandPaletteCoordinator
    {
        public static CommandPaletteCoordinator Instance { get; } = new();

        public static string NormalizeSearchText(string? input)
        {
            if (string.IsNullOrWhiteSpace(input)) return string.Empty;
            string normalized = input.Normalize(NormalizationForm.FormD);
            var sb = new StringBuilder(normalized.Length);
            foreach (char c in normalized)
            {
                var uc = CharUnicodeInfo.GetUnicodeCategory(c);
                if (uc != UnicodeCategory.NonSpacingMark)
                    sb.Append(char.ToLowerInvariant(c));
            }
            return sb.ToString().Normalize(NormalizationForm.FormC).Replace(Shiro.Services.LocalizationService.Get("CommandPalette.CharDWithStroke"), "d");
        }
    }
}
