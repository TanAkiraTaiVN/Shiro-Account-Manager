#nullable enable
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace Shiro.Services
{
    public interface IProxyStore
    {
        List<ProxyItem> Load();
        void Save(IEnumerable<ProxyItem> proxies);
        ProxyItem? Parse(string? entry);
        Task TestAllAsync(IEnumerable<ProxyItem> proxies, CancellationToken ct);
    }

    public sealed class ProxyStore : IProxyStore
    {
        public static readonly ProxyStore Default = new();

        public List<ProxyItem> Load() => ProxyManagerService.LoadProxies();

        public void Save(IEnumerable<ProxyItem> proxies) => ProxyManagerService.SaveProxies(proxies);

        public ProxyItem? Parse(string? entry) => ProxyManagerService.ParseProxy(entry ?? string.Empty);

        public Task TestAllAsync(IEnumerable<ProxyItem> proxies, CancellationToken ct)
            => ProxyManagerService.TestAllProxiesAsync(proxies, ct: ct);
    }
}
