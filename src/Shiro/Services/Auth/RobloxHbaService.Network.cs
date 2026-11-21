#nullable enable
using System;
using System.Collections.Generic;
using System.Net.NetworkInformation;
using System.Security.Cryptography;
using System.Text;
using System.Threading;

namespace Shiro.Services
{
    public static partial class RobloxHbaService
    {
        private static readonly AsyncLocal<Func<bool>?> _testIsVpnTunnelActiveOverride = new();
        internal static Func<bool>? TestIsVpnTunnelActiveOverride
        {
            get => _testIsVpnTunnelActiveOverride.Value;
            set => _testIsVpnTunnelActiveOverride.Value = value;
        }

        internal readonly record struct NetworkAdapterSnapshot(
            string Id,
            NetworkInterfaceType InterfaceType,
            bool HasDefaultGateway,
            string[] UnicastAddresses,
            string[] GatewayAddresses);

        public static bool IsVpnOrTunnelInterfaceActive() =>
            TestIsVpnTunnelActiveOverride != null
                ? TestIsVpnTunnelActiveOverride()
                : NetworkSignatureHelper.IsVpnOrTunnelInterfaceActive(_logger);

        public static string GetLocalNetworkSignature() =>
            NetworkSignatureHelper.GetLocalNetworkSignature(_logger);

        public static string GetNetworkSignatureForAccount(Account? account) =>
            GetNetworkSignatureForAccount(account, GetLocalNetworkSignature());

        internal static string GetNetworkSignatureForAccount(Account? account, string localSig)
        {
            if (localSig is "offline" or "unknown")
                return localSig;

            string? normalizedProxy = NetworkSignatureHelper.NormalizeAccountProxyEndpoint(account?.Proxy);
            if (normalizedProxy == null)
                return localSig;

            byte[] hash = SHA256.HashData(Encoding.UTF8.GetBytes($"{localSig}|acctproxy:{normalizedProxy}"));
            return Convert.ToHexStringLower(hash.AsSpan(0, 12));
        }

        internal static string ComputeNetworkSignature(
            IReadOnlyList<NetworkAdapterSnapshot>? adapters,
            string? systemProxyEndpoint = null) =>
            NetworkSignatureHelper.ComputeNetworkSignature(adapters, systemProxyEndpoint);
    }
}
