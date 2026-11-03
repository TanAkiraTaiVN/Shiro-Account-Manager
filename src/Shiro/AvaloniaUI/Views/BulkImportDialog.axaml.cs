#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Threading;
using Shiro.Classes;
using Shiro.Services;

namespace Shiro.AvaloniaUI.Views
{
    public partial class BulkImportDialog : Window
    {
        private CancellationTokenSource? _cts;
        public int ImportedCount { get; private set; }

        public BulkImportDialog()
        {
            InitializeComponent();
        }

        private void OnClearClicked(object sender, RoutedEventArgs e)
        {
            TxtImportData.Text = string.Empty;
            TxtStatus.Text = Shiro.Services.LocalizationService.Get("Auto_BulkImportDialog_065");
            TxtStatus.Foreground = Brushes.Gray;
        }

        private void OnCloseClicked(object sender, RoutedEventArgs e)
        {
            SafeExecuteHelper.Try(() => _cts?.Cancel());
            Close(ImportedCount > 0);
        }

        protected override void OnClosing(WindowClosingEventArgs e)
        {
            SafeExecuteHelper.Try(() => _cts?.Cancel());
            base.OnClosing(e);
        }

        private async void OnStartImportClicked(object sender, RoutedEventArgs e)
        {
            string? raw = TxtImportData.Text;
            if (string.IsNullOrWhiteSpace(raw))
            {
                TxtStatus.Text = Shiro.Services.LocalizationService.Get("Auto_BulkImportDialog_066");
                TxtStatus.Foreground = Brushes.OrangeRed;
                return;
            }

            var lines = raw.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

            if (lines.Length == 0)
            {
                TxtStatus.Text = Shiro.Services.LocalizationService.Get("Auto_BulkImportDialog_067");
                TxtStatus.Foreground = Brushes.OrangeRed;
                return;
            }

            BtnStartImport.IsEnabled = false;
            BtnClear.IsEnabled = false;
            PrgProgress.IsVisible = true;
            PrgProgress.Maximum = lines.Length;
            PrgProgress.Value = 0;

            _cts = new CancellationTokenSource();
            var token = _cts.Token;

            int success = 0;
            int failed = 0;

            var cookieLines = new List<(string cookie, string? password)>();
            var userPassLines = new List<(string username, string password)>();

            foreach (var line in lines)
            {
                var (cookie, password) = ParseAccountLine(line);
                if (!string.IsNullOrEmpty(cookie))
                {
                    cookieLines.Add((cookie, password));
                }
                else
                {
                    // Check if line is username:password
                    ReadOnlySpan<char> lineSpan = line.AsSpan();
                    int sep = lineSpan.IndexOf(':');
                    if (sep <= 0) sep = lineSpan.IndexOf('|');
                    if (sep <= 0) sep = lineSpan.IndexOf('\t');

                    if (sep > 0 && sep < lineSpan.Length - 1)
                    {
                        ReadOnlySpan<char> uSpan = lineSpan.Slice(0, sep).Trim();
                        ReadOnlySpan<char> pSpan = lineSpan.Slice(sep + 1).Trim();
                        if (!uSpan.IsEmpty && !pSpan.IsEmpty)
                        {
                            userPassLines.Add((uSpan.ToString(), pSpan.ToString()));
                            continue;
                        }
                    }
                    failed++;
                }
            }

            try
            {
                // 1. Process Cookie-based entries (bounded parallel async import)
                if (cookieLines.Count > 0)
                {
                    using var cookieGate = new SemaphoreSlim(4, 4);
                    var cookieTasks = cookieLines.Select(async item =>
                    {
                        if (token.IsCancellationRequested) return;
                        try
                        {
                            await cookieGate.WaitAsync(token).ConfigureAwait(true);
                        }
                        catch (OperationCanceledException)
                        {
                            return;
                        }

                        try
                        {
                            if (token.IsCancellationRequested) return;
                            var acc = await AppServices.AccountRepository.AddOrUpdateAsync(item.cookie, item.password ?? string.Empty, saveImmediate: false, cancellationToken: token).ConfigureAwait(true);
                            int newSuccess = acc != null && acc.Valid
                                ? Interlocked.Increment(ref success)
                                : Volatile.Read(ref success);
                            int newFailed = acc == null || !acc.Valid
                                ? Interlocked.Increment(ref failed)
                                : Volatile.Read(ref failed);

                            int current = newSuccess + newFailed;
                            PrgProgress.Value = current;
                            TxtStatus.Text = Shiro.Services.LocalizationService.Format("Auto_BulkImportDialog_068", current, lines.Length);
                        }
                        catch (OperationCanceledException) { /* Cancelled */ }
                        finally
                        {
                            SafeExecuteHelper.Try(() => cookieGate.Release());
                            /* Handled by SafeExecuteHelper */
                            /* Handled by SafeExecuteHelper */
                        }
                    }).ToList();

                    await Task.WhenAll(cookieTasks).ConfigureAwait(true);
                }

                // 2. Process username:password entries (persist credentials immediately + launch In-App Batch Login)
                var queuedForFastLogin = new List<Account>();
                if (userPassLines.Count > 0 && !token.IsCancellationRequested)
                {
                    var existingByUsername = new Dictionary<string, Account>(StringComparer.OrdinalIgnoreCase);
                    foreach (var acc in AppServices.AccountRepository.GetAll())
                    {
                        if (acc != null && !string.IsNullOrEmpty(acc.Username))
                            existingByUsername.TryAdd(acc.Username, acc);
                    }

                    int upIndex = 0;
                    foreach (var up in userPassLines)
                    {
                        if (token.IsCancellationRequested) break;
                        upIndex++;

                        TxtStatus.Text = Shiro.Services.LocalizationService.Format("Auto_BulkImportDialog_069", up.username, upIndex, userPassLines.Count);

                        if (!existingByUsername.TryGetValue(up.username, out Account? targetAcc))
                        {
                            long resolvedUserId = 0;
                            try
                            {
                                var (okId, uid, _) = await AppServices.AccountRepository.LookupUserIdAsync(up.username, token).ConfigureAwait(true);
                                if (okId && uid > 0)
                                    resolvedUserId = uid;
                            }
                            catch (Exception ex)
                            {
                                Program.Logger?.Debug($"Resolve userId for {up.username} failed: {ex.Message}");
                            }

                            targetAcc = new Account
                            {
                                Username = up.username,
                                Password = up.password,
                                UserID = resolvedUserId,
                                Valid = false,
                                Group = "Default",
                                LastUse = DateTime.Now
                            };

                            AppServices.AccountRepository.AddThreadSafe(targetAcc);
                            existingByUsername[up.username] = targetAcc;
                        }
                        else
                        {
                            targetAcc.Password = up.password;
                        }

                        if (!targetAcc.Valid || string.IsNullOrWhiteSpace(targetAcc.SecurityToken))
                        {
                            queuedForFastLogin.Add(targetAcc);
                        }

                        int current = Interlocked.Increment(ref success) + Volatile.Read(ref failed);
                        PrgProgress.Value = current;
                    }
                }

                if (success > 0)
                {
                    try { AppServices.AccountRepository.Save(true); }
                    catch (Exception ex) { Program.Logger?.Warn($"SaveAccounts after bulk import failed: {ex.Message}"); }
                    MainWindow.Instance?.DashboardView?.LoadDashboardData();
                    AppSettings.SetDashboardStatus(Shiro.Services.LocalizationService.Format("Auto_BulkImportDialog_070", success));
                }

                ImportedCount = success;
                PrgProgress.IsVisible = false;
                BtnStartImport.IsEnabled = true;
                BtnClear.IsEnabled = true;

                if (queuedForFastLogin.Count > 0 && !token.IsCancellationRequested)
                {
                    TxtStatus.Text = Shiro.Services.LocalizationService.Format("Auto_BulkImportDialog_071", success, queuedForFastLogin.Count);
                    TxtStatus.Foreground = Brushes.LightGreen;

                    if (queuedForFastLogin.Count == 1)
                        AppServices.Dialogs.OpenFastLoginDialog(queuedForFastLogin[0]);
                    else
                        AppServices.Dialogs.OpenFastLoginDialogBatch(queuedForFastLogin);
                }
                else if (token.IsCancellationRequested)
                {
                    TxtStatus.Text = Shiro.Services.LocalizationService.Format("Auto_BulkImportDialog_072", success);
                    TxtStatus.Foreground = Brushes.Orange;
                }
                else
                {
                    TxtStatus.Text = Shiro.Services.LocalizationService.Format("Auto_BulkImportDialog_073", success, failed);
                    TxtStatus.Foreground = success > 0 ? Brushes.LightGreen : Brushes.OrangeRed;
                }
            }
            catch (Exception ex)
            {
                TxtStatus.Text = Shiro.Services.LocalizationService.Format("Proxy_Dead", ex.Message);
                TxtStatus.Foreground = Brushes.OrangeRed;
                BtnStartImport.IsEnabled = true;
                BtnClear.IsEnabled = true;
                PrgProgress.IsVisible = false;
            }
            finally
            {
                _cts?.Dispose();
                _cts = null;
            }
        }

        public static (string? cookie, string? password) ParseAccountLine(string? line)
        {
            if (string.IsNullOrWhiteSpace(line))
                return (null, null);

            line = line.Trim();

            // Check if the line is pipe-delimited or colon-delimited.
            // Note: Roblox cookies contain "_|WARNING", which has a '|' and a ':'.
            // A format like user|pass|cookie has at least two '|' characters.
            // A format like user:pass:cookie has at least two ':' characters.
            int pipeCount = 0;
            int colonCount = 0;
            for (int i = 0; i < line.Length; i++)
            {
                if (line[i] == '|') pipeCount++;
                else if (line[i] == ':') colonCount++;
            }

            string[]? parts = null;
            if (pipeCount >= 2)
            {
                parts = line.Split(new[] { '|' }, 3);
            }
            else if (colonCount >= 2)
            {
                // In user:pass:cookie, split at most into 3 parts so colons inside the cookie are preserved
                parts = line.Split(new[] { ':' }, 3);
            }

            if (parts != null && parts.Length == 3)
            {
                string candidateCookie = parts[2].Trim();
                if (candidateCookie.Contains("_|WARNING") || candidateCookie.Length > 80)
                {
                    string pass = parts[1].Trim();
                    string? cleaned = CleanCookie(candidateCookie);
                    if (!string.IsNullOrEmpty(cleaned))
                        return (cleaned, pass);
                }
            }

            string? singleCookie = CleanCookie(line);
            return (!string.IsNullOrEmpty(singleCookie) ? singleCookie : null, null);
        }

        public static string? CleanCookie(string? line)
        {
            if (string.IsNullOrWhiteSpace(line))
                return null;

            // If it contains _|WARNING, extract starting at _|WARNING
            int warningIdx = line.IndexOf("_|WARNING", StringComparison.Ordinal);
            if (warningIdx >= 0)
            {
                return Account.SanitizeCookie(line.Substring(warningIdx));
            }

            // Otherwise, sanitize the whole line (e.g. if cookie without _|WARNING header or .ROBLOSECURITY= prefix)
            string sanitized = Account.SanitizeCookie(line);
            if (!string.IsNullOrEmpty(sanitized) && sanitized.Length > 80)
            {
                return sanitized;
            }

            return null;
        }
    }
}
