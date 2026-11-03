#nullable enable
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net.Sockets;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Media;

namespace Shiro.Services
{
    public class ProxyItem : INotifyPropertyChanged
    {
        private static readonly IBrush BrushTesting = new SolidColorBrush(Color.Parse("#F59E0B"));
        private static readonly IBrush BrushUntested = new SolidColorBrush(Color.Parse("#71717A"));
        private static readonly IBrush BrushFailed = new SolidColorBrush(Color.Parse("#EF4444"));
        private static readonly IBrush BrushFast = new SolidColorBrush(Color.Parse("#22C55E"));
        private static readonly IBrush BrushMedium = new SolidColorBrush(Color.Parse("#EAB308"));
        private static readonly IBrush BrushSlow = new SolidColorBrush(Color.Parse("#F97316"));

        private string _rawEntry = string.Empty;
        private string _host = string.Empty;
        private int _port;
        private string _username = string.Empty;
        private string _password = string.Empty;
        private string _protocol = "http";
        private long _latencyMs = -1;
        private bool _isTesting;

        public event PropertyChangedEventHandler? PropertyChanged;

        private void OnPropertyChanged([CallerMemberName] string? name = null)
            => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));

        public string RawEntry
        {
            get => _rawEntry;
            set { _rawEntry = value; OnPropertyChanged(); }
        }

        public string Host
        {
            get => _host;
            set { _host = value; OnPropertyChanged(); OnPropertyChanged(nameof(AddressDisplay)); }
        }

        public int Port
        {
            get => _port;
            set { _port = value; OnPropertyChanged(); OnPropertyChanged(nameof(AddressDisplay)); }
        }

        public string AddressDisplay => $"{Host}:{Port}";

        public string Username
        {
            get => _username;
            set { _username = value; OnPropertyChanged(); OnPropertyChanged(nameof(AuthDisplay)); }
        }

        public string Password
        {
            get => _password;
            set { _password = value; OnPropertyChanged(); OnPropertyChanged(nameof(AuthDisplay)); }
        }

        public string AuthDisplay => string.IsNullOrEmpty(Username) ? Shiro.Services.LocalizationService.Get("ProxyManager.NoAuthentication") : $"{Username}:***";

        public string Protocol
        {
            get => _protocol;
            set { _protocol = value; OnPropertyChanged(); OnPropertyChanged(nameof(ProtocolUpper)); }
        }

        public string ProtocolUpper => (Protocol ?? "http").ToUpperInvariant();

        public long LatencyMs
        {
            get => _latencyMs;
            set
            {
                if (_latencyMs == value) return;
                _latencyMs = value;
                OnPropertyChanged();
                OnPropertyChanged(nameof(IsOnline));
                OnPropertyChanged(nameof(IsTested));
                OnPropertyChanged(nameof(StatusText));
                OnPropertyChanged(nameof(StatusColor));
                OnPropertyChanged(nameof(StatusBrush));
            }
        }

        public bool IsTesting
        {
            get => _isTesting;
            set
            {
                if (_isTesting == value) return;
                _isTesting = value;
                OnPropertyChanged();
                OnPropertyChanged(nameof(StatusText));
                OnPropertyChanged(nameof(StatusColor));
                OnPropertyChanged(nameof(StatusBrush));
            }
        }

        public bool IsOnline => LatencyMs >= 0;
        public bool IsTested => LatencyMs != -1;

        public string StatusText
        {
            get
            {
                if (IsTesting) return Shiro.Services.LocalizationService.Get("ProxyManager.TestingStatus");
                if (LatencyMs == -1) return Shiro.Services.LocalizationService.Get("ProxyManager.NotTestedStatus");
                if (LatencyMs == -2) return Shiro.Services.LocalizationService.Get("ProxyManager.TimeoutOrErrorStatus");
                return $"⚡ {LatencyMs} ms";
            }
        }

        public string StatusColor
        {
            get
            {
                if (IsTesting) return "#F59E0B";
                if (LatencyMs == -1) return "#71717A";
                if (LatencyMs == -2) return "#EF4444";
                if (LatencyMs < 150) return "#22C55E";
                if (LatencyMs < 400) return "#EAB308";
                return "#F97316";
            }
        }

        public IBrush StatusBrush
        {
            get
            {
                if (IsTesting) return BrushTesting;
                if (LatencyMs == -1) return BrushUntested;
                if (LatencyMs == -2) return BrushFailed;
                if (LatencyMs < 150) return BrushFast;
                if (LatencyMs < 400) return BrushMedium;
                return BrushSlow;
            }
        }
    }

    public static class ProxyManagerService
    {
        private static readonly System.Threading.Lock CacheLock = new();
        private static IReadOnlyList<string>? _cachedRawProxies;
        private static string? _cachedFilePath;
        private static long _lastCacheTicks;
        private const long CacheTtlMs = 15_000;

        public static string ProxiesFilePath => StoragePaths.GetUserFile("proxies.txt");

        public static void InvalidateCache()
        {
            lock (CacheLock)
            {
                _cachedRawProxies = null;
                _cachedFilePath = null;
                _lastCacheTicks = 0;
            }
        }

        public static IReadOnlyList<string> GetCachedProxyStrings()
        {
            string path = ProxiesFilePath;
            long now = Environment.TickCount64;

            lock (CacheLock)
            {
                if (_cachedRawProxies != null &&
                    string.Equals(_cachedFilePath, path, StringComparison.OrdinalIgnoreCase) &&
                    now - _lastCacheTicks < CacheTtlMs)
                {
                    return _cachedRawProxies;
                }
            }

            var loaded = LoadProxies();
            return loaded.Select(p => p.RawEntry).ToList();
        }

        public static ProxyItem? ParseProxy(string? raw)
        {
            if (string.IsNullOrWhiteSpace(raw)) return null;
            string trimmed = raw.Trim();
            if (trimmed.StartsWith("#")) return null;

            string scheme = "http";
            string working = trimmed;

            int schemeIdx = working.IndexOf("://", StringComparison.Ordinal);
            if (schemeIdx >= 0)
            {
                scheme = working.Substring(0, schemeIdx).ToLowerInvariant();
                working = working.Substring(schemeIdx + 3);
            }

            string username = string.Empty;
            string password = string.Empty;

            // 1. Check ip:port:user:pass format first (supports '@' and ':' inside password)
            var colons = working.Split(':');
            if (colons.Length >= 4 &&
                !colons[0].Contains('@') &&
                int.TryParse(colons[1].Trim(), out int colonPort) &&
                colonPort is > 0 and <= 65535)
            {
                working = $"{colons[0].Trim()}:{colonPort}";
                username = colons[2].Trim();
                password = string.Join(":", colons.Skip(3)).Trim();
            }
            else
            {
                // 2. Handle user:pass@host:port or host:port@user:pass
                int atIdx = working.LastIndexOf('@');
                if (atIdx >= 0)
                {
                    string part1 = working.Substring(0, atIdx);
                    string part2 = working.Substring(atIdx + 1);

                    if (part2.Contains(':') && int.TryParse(part2.Substring(part2.LastIndexOf(':') + 1), out _))
                    {
                        // user:pass@host:port
                        working = part2;
                        int colon = part1.IndexOf(':');
                        if (colon >= 0)
                        {
                            username = part1.Substring(0, colon);
                            password = part1.Substring(colon + 1);
                        }
                        else
                        {
                            username = part1;
                        }
                    }
                    else
                    {
                        // host:port@user:pass
                        working = part1;
                        int colon = part2.IndexOf(':');
                        if (colon >= 0)
                        {
                            username = part2.Substring(0, colon);
                            password = part2.Substring(colon + 1);
                        }
                        else
                        {
                            username = part2;
                        }
                    }
                }
            }

            // Now extract host and port (explicit port is required)
            int portSep = working.LastIndexOf(':');
            if (portSep <= 0 || !int.TryParse(working.Substring(portSep + 1).Trim(), out int port) || port is <= 0 or > 65535)
                return null;

            string host = working.Substring(0, portSep).Trim();
            if (string.IsNullOrWhiteSpace(host))
                return null;

            return new ProxyItem
            {
                RawEntry = trimmed,
                Protocol = scheme,
                Host = host,
                Port = port,
                Username = username,
                Password = password
            };
        }

        public static List<ProxyItem> LoadProxies()
        {
            var list = new List<ProxyItem>();
            string path = ProxiesFilePath;
            if (!File.Exists(path))
            {
                lock (CacheLock)
                {
                    _cachedRawProxies = Array.Empty<string>();
                    _cachedFilePath = path;
                    _lastCacheTicks = Environment.TickCount64;
                }
                return list;
            }

            try
            {
                var lines = File.ReadAllLines(path);
                foreach (var line in lines)
                {
                    var item = ParseProxy(line);
                    if (item != null)
                        list.Add(item);
                }
            }
            catch (Exception ex)
            {
                Program.Logger?.Error($"[ProxyManagerService] Failed to load proxies: {ex.Message}");
            }

            lock (CacheLock)
            {
                _cachedRawProxies = list.Select(p => p.RawEntry).ToArray();
                _cachedFilePath = path;
                _lastCacheTicks = Environment.TickCount64;
            }

            return list;
        }

        public static void SaveProxies(IEnumerable<ProxyItem> proxies)
        {
            try
            {
                string path = ProxiesFilePath;
                var lines = (proxies ?? Enumerable.Empty<ProxyItem>())
                    .Where(p => p != null && !string.IsNullOrWhiteSpace(p.RawEntry))
                    .Select(p => p.RawEntry.Trim())
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .ToList();

                StoragePaths.AtomicWriteText(path, string.Join(Environment.NewLine, lines));

                lock (CacheLock)
                {
                    _cachedRawProxies = lines.ToArray();
                    _cachedFilePath = path;
                    _lastCacheTicks = Environment.TickCount64;
                }

                var accounts = AppServices.AccountRepository.GetAll();
                if (accounts.Count > 0)
                {
                    if (lines.Count > 0)
                    {
                        ProxyBindingService.DistributeProxiesSticky(accounts, lines);
                    }
                    else
                    {
                        foreach (var account in accounts)
                        {
                            if (account != null)
                                account.Proxy = null;
                        }
                    }
                }

                ProxyBindingService.PruneClients(lines);
            }
            catch (Exception ex)
            {
                Program.Logger?.Error($"[ProxyManagerService] Failed to save proxies: {ex.Message}");
            }
        }

        public static async Task TestProxyLatencyAsync(ProxyItem proxy, int timeoutMs = 3000, CancellationToken ct = default)
        {
            if (proxy == null) return;

            AppDispatcher.Post(() => proxy.IsTesting = true);
            long measuredLatency = -2;

            try
            {
                using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                cts.CancelAfter(timeoutMs);

                using var tcp = new TcpClient();
                var sw = Stopwatch.StartNew();
                await tcp.ConnectAsync(proxy.Host, proxy.Port, cts.Token).ConfigureAwait(false);
                sw.Stop();

                long tcpMs = sw.ElapsedMilliseconds;

                if (!string.IsNullOrEmpty(proxy.Username) &&
                    (string.Equals(proxy.Protocol, "http", StringComparison.OrdinalIgnoreCase) ||
                     string.Equals(proxy.Protocol, "https", StringComparison.OrdinalIgnoreCase)))
                {
                    var (success, httpLatency, _) = await ProxyBindingService.TestProxyAsync(proxy.RawEntry, cts.Token).ConfigureAwait(false);
                    measuredLatency = success ? Math.Max(tcpMs, (long)httpLatency.TotalMilliseconds) : -2;
                }
                else
                {
                    measuredLatency = tcpMs;
                }
            }
            catch
            {
                measuredLatency = -2; // Failed or timeout
            }
            finally
            {
                long finalLatency = measuredLatency;
                AppDispatcher.Post(() =>
                {
                    proxy.LatencyMs = finalLatency;
                    proxy.IsTesting = false;
                });
            }
        }

        public static async Task TestAllProxiesAsync(IEnumerable<ProxyItem> proxies, int maxConcurrency = 6, int timeoutMs = 3000, IProgress<ProxyItem>? progress = null, CancellationToken ct = default)
        {
            if (proxies == null) return;
            var snapshot = proxies.Where(p => p != null).ToList();
            if (snapshot.Count == 0) return;

            using var semaphore = new SemaphoreSlim(maxConcurrency, maxConcurrency);
            var tasks = snapshot.Select(async proxy =>
            {
                await semaphore.WaitAsync(ct).ConfigureAwait(false);
                try
                {
                    await TestProxyLatencyAsync(proxy, timeoutMs, ct).ConfigureAwait(false);
                    progress?.Report(proxy);
                }
                finally
                {
                    semaphore.Release();
                }
            });

            await Task.WhenAll(tasks).ConfigureAwait(false);
        }
    }
}
