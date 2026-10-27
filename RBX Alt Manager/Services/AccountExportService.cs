using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace RBX_Alt_Manager.Services
{
    public enum AccountExportFormat
    {
        UserPassCookie,
        CookieOnly,
        UserPass,
        Csv,
        Json
    }

    /// <summary>
    /// Multi-format account data exporter supporting user:pass:cookie, cookie_only, user:pass, CSV and JSON.
    /// </summary>
    public static class AccountExportService
    {
        public static string ExportAccounts(IEnumerable<Account> accounts, AccountExportFormat format)
        {
            if (accounts == null) return string.Empty;
            var list = accounts.Where(a => a != null).ToList();

            var sb = new StringBuilder();

            switch (format)
            {
                case AccountExportFormat.UserPassCookie:
                    foreach (var a in list)
                    {
                        string pass = a.Password ?? string.Empty;
                        sb.AppendLine($"{a.Username}:{pass}:{a.SecurityToken}");
                    }
                    break;

                case AccountExportFormat.CookieOnly:
                    foreach (var a in list)
                    {
                        if (!string.IsNullOrWhiteSpace(a.SecurityToken))
                            sb.AppendLine(a.SecurityToken);
                    }
                    break;

                case AccountExportFormat.UserPass:
                    foreach (var a in list)
                    {
                        string pass = a.Password ?? string.Empty;
                        sb.AppendLine($"{a.Username}:{pass}");
                    }
                    break;

                case AccountExportFormat.Csv:
                    sb.AppendLine("Username,UserID,Alias,Group,Password,Robux,Proxy,LastUse");
                    foreach (var a in list)
                    {
                        string user = EscapeCsv(a.Username ?? string.Empty);
                        string pass = EscapeCsv(a.Password ?? string.Empty);
                        string alias = EscapeCsv(a.Alias ?? string.Empty);
                        string group = EscapeCsv(a.Group ?? string.Empty);
                        string proxy = EscapeCsv(a.Proxy ?? string.Empty);
                        sb.AppendLine($"{user},{a.UserID},{alias},{group},{pass},{a.RobuxBalance},{proxy},{a.LastUse:s}");
                    }
                    break;

                case AccountExportFormat.Json:
                    var exportModels = list.Select(a => new
                    {
                        Username = a.Username,
                        UserID = a.UserID,
                        Alias = a.Alias,
                        Group = a.Group,
                        Password = a.Password,
                        RobuxBalance = a.RobuxBalance,
                        Proxy = a.Proxy,
                        LastUse = a.LastUse,
                        SecurityToken = a.SecurityToken
                    });
                    return JsonSerializer.Serialize(exportModels, new JsonSerializerOptions
                    {
                        WriteIndented = true
                    });
            }

            return sb.ToString();
        }

        public static async Task ExportToFileAsync(IEnumerable<Account> accounts, AccountExportFormat format, string filePath, CancellationToken cancellationToken = default)
        {
            if (string.IsNullOrWhiteSpace(filePath))
                throw new ArgumentException("File path cannot be empty.", nameof(filePath));

            string content = ExportAccounts(accounts, format);
            string dir = Path.GetDirectoryName(filePath);
            if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
                Directory.CreateDirectory(dir);

            await File.WriteAllTextAsync(filePath, content, Encoding.UTF8, cancellationToken).ConfigureAwait(false);
        }

        private static readonly System.Buffers.SearchValues<char> CsvSpecialChars =
            System.Buffers.SearchValues.Create(",\"\r\n");

        private static string EscapeCsv(string val)
        {
            if (string.IsNullOrEmpty(val)) return "";
            if (val.AsSpan().ContainsAny(CsvSpecialChars))
            {
                return $"\"{val.Replace("\"", "\"\"")}\"";
            }
            return val;
        }
    }
}
