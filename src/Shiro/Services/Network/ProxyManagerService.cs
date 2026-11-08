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
        private static IAppLogger _logger = AppServices.Logger;
        public static IAppLogger Logger
        {
            get => _logger;
            set => _logger = value ?? throw new ArgumentNullException(nameof(value));
        }

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
            ReadOnlySpan<char> trimmedSpan = raw.AsSpan().Trim();
            if (trimmedSpan.IsEmpty || trimmedSpan[0] == '#') return null;

            string trimmed = trimmedSpan.Length == raw.Length ? raw : trimmedSpan.ToString();
            string scheme = "http";
            ReadOnlySpan<char> working = trimmedSpan;

            int schemeIdx = working.IndexOf("://".AsSpan(), StringComparison.Ordinal);
            if (schemeIdx >= 0)
            {
                scheme = working[..schemeIdx].ToString().ToLowerInvariant();
                working = working[(schemeIdx + 3)..];
            }

            ReadOnlySpan<char> usernameSpan = ReadOnlySpan<char>.Empty;
            ReadOnlySpan<char> passwordSpan = ReadOnlySpan<char>.Empty;
            ReadOnlySpan<char> hostPortSpan = working;

            // 1. Check ip:port:user:pass format first (supports '@' and ':' inside password)
            Span<Range> colonRanges = stackalloc Range[5];
            int colonCount = working.Split(colonRanges, ':');
            if (colonCount >= 4 &&
                working[colonRanges[0]].IndexOf('@') < 0 &&
                int.TryParse(working[colonRanges[1]].Trim(), out int colonPort) &&
                colonPort is > 0 and <= 65535)
            {
                ReadOnlySpan<char> hostOnly = working[colonRanges[0]].Trim();
                if (hostOnly.IsEmpty || hostOnly.IsWhiteSpace())
                    return null;

                usernameSpan = working[colonRanges[2]].Trim();
                int passStart = colonRanges[3].Start.GetOffset(working.Length);
                passwordSpan = working[passStart..].Trim();

                return new ProxyItem
                {
                    RawEntry = trimmed,
                    Protocol = scheme,
                    Host = hostOnly.ToString(),
                    Port = colonPort,
                    Username = usernameSpan.IsEmpty ? string.Empty : usernameSpan.ToString(),
                    Password = passwordSpan.IsEmpty ? string.Empty : passwordSpan.ToString()
                };
            }

            // 2. Handle user:pass@host:port or host:port@user:pass
            int atIdx = working.LastIndexOf('@');
            if (atIdx >= 0)
            {
                ReadOnlySpan<char> part1 = working[..atIdx];
                ReadOnlySpan<char> part2 = working[(atIdx + 1)..];

                int part2LastColon = part2.LastIndexOf(':');
                if (part2LastColon >= 0 && int.TryParse(part2[(part2LastColon + 1)..], out _))
                {
                    // user:pass@host:port
                    hostPortSpan = part2;
                    int colon = part1.IndexOf(':');
                    if (colon >= 0)
                    {
                        usernameSpan = part1[..colon];
                        passwordSpan = part1[(colon + 1)..];
                    }
                    else
                    {
                        usernameSpan = part1;
                    }
                }
                else
                {
                    // host:port@user:pass
                    hostPortSpan = part1;
                    int colon = part2.IndexOf(':');
                    if (colon >= 0)
                    {
                        usernameSpan = part2[..colon];
                        passwordSpan = part2[(colon + 1)..];
                    }
                    else
                    {
                        usernameSpan = part2;
                    }
                }
            }

            // Now extract host and port (explicit port is required)
            int portSep = hostPortSpan.LastIndexOf(':');
            if (portSep <= 0 || !int.TryParse(hostPortSpan[(portSep + 1)..].Trim(), out int port) || port is <= 0 or > 65535)
                return null;

            ReadOnlySpan<char> hostSpan = hostPortSpan[..portSep].Trim();
            if (hostSpan.IsEmpty || hostSpan.IsWhiteSpace())
                return null;

            return new ProxyItem
            {
                RawEntry = trimmed,
                Protocol = scheme,
                Host = hostSpan.ToString(),
                Port = port,
                Username = usernameSpan.IsEmpty ? string.Empty : usernameSpan.ToString(),
                Password = passwordSpan.IsEmpty ? string.Empty : passwordSpan.ToString()
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
                _logger.Error($"[ProxyManagerService] Failed to load proxies: {ex.Message}");
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
                _logger.Error($"[ProxyManagerService] Failed to save proxies: {ex.Message}");
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
            catch (Exception ex)
            {
                AppServices.Logger.Debug("[ProxyManagerService] PingProxyAsync failed", ex);
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

            await Parallel.ForEachAsync(
                snapshot,
                new ParallelOptions { MaxDegreeOfParallelism = Math.Max(1, maxConcurrency), CancellationToken = ct },
                async (proxy, token) =>
                {
                    await TestProxyLatencyAsync(proxy, timeoutMs, token).ConfigureAwait(false);
                    progress?.Report(proxy);
                }).ConfigureAwait(false);
        }
    }
}
