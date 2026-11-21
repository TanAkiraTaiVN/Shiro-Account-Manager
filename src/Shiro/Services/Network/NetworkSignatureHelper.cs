#nullable enable
using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Http;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;

namespace Shiro.Services
{
    /// <summary>
    /// Computes local network adapter/proxy signatures and detects active VPN/Tunnel interfaces
    /// so residential Vietnam sessions are protected during temporary VPN relogin workflows.
    /// </summary>
    internal static class NetworkSignatureHelper
    {
        private static readonly Uri ProxyProbeUri = new("https://auth.roblox.com/");
        private static readonly string[] VpnAdapterMarkers =
        [
            "Proton", "WireGuard", "Wintun", "OpenVPN", "NordLynx", "TAP-Windows",
            "Tailscale", "Mullvad", "ZeroTier", "Cisco", "AnyConnect", "GlobalProtect",
            "Fortinet", "SoftEther", "Radmin", "Hamachi", "WARP", "Cloudflare WARP",
            "VPN", "L2TP", "PPTP", "SSTP", "IKEv2"
        ];

        /// <summary>
        /// Classifies whether an adapter is a VPN or tunnel interface while ignoring loopback,
        /// Windows IPv6 transition pseudo-interfaces, and plain residential PPPoE broadband links.
        /// </summary>
        internal static bool IsVpnOrTunnelAdapter(string? name, string? description, NetworkInterfaceType interfaceType)
        {
            if (interfaceType == NetworkInterfaceType.Loopback ||
                IsTransitionPseudoInterface(name, description, interfaceType))
            {
                return false;
            }

            if (interfaceType == NetworkInterfaceType.Tunnel)
                return true;

            foreach (string marker in VpnAdapterMarkers)
            {
                if ((!string.IsNullOrEmpty(name) && name.Contains(marker, StringComparison.OrdinalIgnoreCase)) ||
                    (!string.IsNullOrEmpty(description) && description.Contains(marker, StringComparison.OrdinalIgnoreCase)))
                {
                    return true;
                }
            }

            return false;
        }

        public static bool IsVpnOrTunnelInterfaceActive(IAppLogger? logger)
        {
            try
            {
                foreach (NetworkInterface nic in NetworkInterface.GetAllNetworkInterfaces())
                {
                    if (nic.OperationalStatus != OperationalStatus.Up ||
                        !IsVpnOrTunnelAdapter(nic.Name, nic.Description, nic.NetworkInterfaceType))
                    {
                        continue;
                    }

                    IPInterfaceProperties props = nic.GetIPProperties();
                    foreach (UnicastIPAddressInformation uni in props.UnicastAddresses)
                    {
                        IPAddress? addr = uni?.Address;
                        if (addr == null || IPAddress.IsLoopback(addr))
                            continue;

                        if (addr.AddressFamily == AddressFamily.InterNetwork)
                        {
                            byte[] b = addr.GetAddressBytes();
                            if (!(b[0] == 169 && b[1] == 254) && !(b[0] == 0 && b[1] == 0 && b[2] == 0 && b[3] == 0))
                                return true;
                        }
                        else if (addr.AddressFamily == AddressFamily.InterNetworkV6 &&
                                 !addr.IsIPv6LinkLocal &&
                                 !addr.IsIPv6Multicast &&
                                 !addr.Equals(IPAddress.IPv6None))
                        {
                            return true;
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                logger?.Debug($"[NetworkSignatureHelper] IsVpnOrTunnelInterfaceActive failed: {ex.Message}");
            }

            return false;
        }

        public static string GetLocalNetworkSignature(IAppLogger? logger)
        {
            try
            {
                var snapshots = new List<RobloxHbaService.NetworkAdapterSnapshot>(8);
                foreach (NetworkInterface nic in NetworkInterface.GetAllNetworkInterfaces())
                {
                    if (nic.OperationalStatus != OperationalStatus.Up ||
                        nic.NetworkInterfaceType == NetworkInterfaceType.Loopback ||
                        IsTransitionPseudoInterface(nic.Name, nic.Description, nic.NetworkInterfaceType))
                    {
                        continue;
                    }

                    IPInterfaceProperties props;
                    try
                    {
                        props = nic.GetIPProperties();
                    }
                    catch (Exception ex)
                    {
                        logger?.Debug($"[NetworkSignatureHelper] GetIPProperties skipped for '{nic.Name}': {ex.Message}");
                        continue;
                    }

                    var gateways = new List<string>(2);
                    foreach (GatewayIPAddressInformation gw in props.GatewayAddresses)
                    {
                        if (gw?.Address == null)
                            continue;
                        string gwStr = gw.Address.ToString();
                        if (!string.IsNullOrWhiteSpace(gwStr) &&
                            !gwStr.Equals("0.0.0.0", StringComparison.Ordinal) &&
                            !gwStr.Equals("::", StringComparison.Ordinal))
                        {
                            gateways.Add(gwStr);
                        }
                    }

                    var unicasts = new List<string>(4);
                    foreach (UnicastIPAddressInformation uni in props.UnicastAddresses)
                    {
                        IPAddress? addr = uni?.Address;
                        if (addr == null || IPAddress.IsLoopback(addr))
                            continue;

                        if (addr.AddressFamily == AddressFamily.InterNetwork)
                        {
                            byte[] bytes = addr.GetAddressBytes();
                            if ((bytes[0] == 169 && bytes[1] == 254) || (bytes[0] == 0 && bytes[1] == 0 && bytes[2] == 0 && bytes[3] == 0))
                                continue;
                            unicasts.Add(addr.ToString());
                        }
                        else if (addr.AddressFamily == AddressFamily.InterNetworkV6)
                        {
                            if (addr.IsIPv6LinkLocal || addr.IsIPv6Multicast || IsTransitionTunnelIpv6(addr))
                                continue;
                            unicasts.Add(addr.ToString());
                        }
                    }

                    if (unicasts.Count > 0)
                    {
                        snapshots.Add(new RobloxHbaService.NetworkAdapterSnapshot(
                            nic.Id ?? nic.Name ?? string.Empty,
                            nic.NetworkInterfaceType,
                            gateways.Count > 0,
                            unicasts.ToArray(),
                            gateways.ToArray()));
                    }
                }

                string? proxyEndpoint = TryResolveSystemProxySignature(logger);
                return ComputeNetworkSignature(snapshots, proxyEndpoint);
            }
            catch (Exception ex)
            {
                logger?.Debug($"[NetworkSignatureHelper] GetLocalNetworkSignature failed: {ex.Message}");
                return "unknown";
            }
        }

        public static string ComputeNetworkSignature(
            IReadOnlyList<RobloxHbaService.NetworkAdapterSnapshot>? adapters,
            string? systemProxyEndpoint = null)
        {
            var parts = new List<string>(12);

            if (adapters != null)
            {
                foreach (RobloxHbaService.NetworkAdapterSnapshot nic in adapters)
                {
                    if (IsTransitionPseudoInterface(nic.Id, null, nic.InterfaceType) ||
                        nic.UnicastAddresses == null ||
                        nic.UnicastAddresses.Length == 0)
                    {
                        continue;
                    }

                    foreach (string rawIp in nic.UnicastAddresses)
                    {
                        if (string.IsNullOrWhiteSpace(rawIp))
                            continue;

                        string ip = rawIp.Trim();
                        if (ip.StartsWith("127.", StringComparison.Ordinal) ||
                            ip.StartsWith("169.254.", StringComparison.Ordinal) ||
                            ip.Equals("0.0.0.0", StringComparison.Ordinal) ||
                            ip.Equals("::1", StringComparison.Ordinal) ||
                            ip.StartsWith("fe80:", StringComparison.OrdinalIgnoreCase) ||
                            (IPAddress.TryParse(ip, out IPAddress? parsedIp) && IsTransitionTunnelIpv6(parsedIp)))
                        {
                            continue;
                        }

                        parts.Add($"ip:{nic.Id}:{ip}");
                    }

                    if (nic.GatewayAddresses != null)
                    {
                        foreach (string rawGw in nic.GatewayAddresses)
                        {
                            if (!string.IsNullOrWhiteSpace(rawGw) &&
                                !rawGw.Equals("0.0.0.0", StringComparison.Ordinal) &&
                                !rawGw.Equals("::", StringComparison.Ordinal))
                            {
                                parts.Add($"gw:{nic.Id}:{rawGw.Trim()}");
                            }
                        }
                    }
                }
            }

            if (!string.IsNullOrWhiteSpace(systemProxyEndpoint))
            {
                parts.Add($"proxy:{systemProxyEndpoint.Trim()}");
            }

            if (parts.Count == 0)
                return "offline";

            parts.Sort(StringComparer.Ordinal);
            string joined = string.Join("|", parts);
            byte[] hash = SHA256.HashData(Encoding.UTF8.GetBytes(joined));
            return Convert.ToHexStringLower(hash.AsSpan(0, 12));
        }

        private static bool IsTransitionPseudoInterface(string? nameOrId, string? description, NetworkInterfaceType interfaceType)
        {
            if (interfaceType != NetworkInterfaceType.Tunnel)
                return false;

            return ContainsTransitionToken(nameOrId) || ContainsTransitionToken(description);
        }

        private static bool ContainsTransitionToken(string? value)
        {
            if (string.IsNullOrEmpty(value))
                return false;

            return value.Contains("Teredo", StringComparison.OrdinalIgnoreCase) ||
                value.Contains("6to4", StringComparison.OrdinalIgnoreCase) ||
                value.Contains("ISATAP", StringComparison.OrdinalIgnoreCase) ||
                value.Contains("IPHTTPS", StringComparison.OrdinalIgnoreCase);
        }

        private static bool IsTransitionTunnelIpv6(IPAddress address)
        {
            if (address.AddressFamily != AddressFamily.InterNetworkV6)
                return false;

            byte[] b = address.GetAddressBytes();
            if (b.Length != 16)
                return false;

            // Teredo (2001::/32) and 6to4 (2002::/16)
            if (b[0] == 0x20 && b[1] == 0x01 && b[2] == 0x00 && b[3] == 0x00)
                return true;
            if (b[0] == 0x20 && b[1] == 0x02)
                return true;

            // ISATAP interface identifier: 0000:5efe or 0200:5efe
            return (b[8] == 0x00 || b[8] == 0x02) && b[9] == 0x00 && b[10] == 0x5e && b[11] == 0xfe;
        }

        internal static string? NormalizeAccountProxyEndpoint(string? rawProxy)
        {
            ProxyItem? parsed = ProxyManagerService.ParseProxy(rawProxy);
            if (parsed == null)
                return null;

            string host = ProxyManagerService.FormatHostForUri(parsed.Host).ToLowerInvariant();
            return $"{parsed.Protocol.ToLowerInvariant()}://{host}:{parsed.Port}";
        }

        private static string? TryResolveSystemProxySignature(IAppLogger? logger)
        {
            try
            {
                IWebProxy? defaultProxy = HttpClient.DefaultProxy;
                if (defaultProxy == null || defaultProxy.IsBypassed(ProxyProbeUri))
                    return null;

                Uri? proxyUri = defaultProxy.GetProxy(ProxyProbeUri);
                if (proxyUri == null ||
                    string.Equals(proxyUri.Host, ProxyProbeUri.Host, StringComparison.OrdinalIgnoreCase))
                {
                    return null;
                }

                return $"{proxyUri.Scheme}://{proxyUri.Host}:{proxyUri.Port}";
            }
            catch (Exception ex)
            {
                logger?.Debug($"[NetworkSignatureHelper] System proxy probe skipped: {ex.Message}");
                return null;
            }
        }
    }
}
