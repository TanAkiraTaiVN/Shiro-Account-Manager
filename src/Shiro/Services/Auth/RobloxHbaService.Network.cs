#nullable enable
using System;
using System.Collections.Generic;
using System.Net.NetworkInformation;

namespace Shiro.Services
{
    public static partial class RobloxHbaService
    {
        internal static Func<bool>? TestIsVpnTunnelActiveOverride;

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

        internal static string ComputeNetworkSignature(
            IReadOnlyList<NetworkAdapterSnapshot>? adapters,
            string? systemProxyEndpoint = null) =>
            NetworkSignatureHelper.ComputeNetworkSignature(adapters, systemProxyEndpoint);
    }
}
