#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;

namespace Shiro.Services
{
    public static partial class RobloxHbaService
    {
        private static readonly Uri ProxyProbeUri = new("https://auth.roblox.com/");

        internal readonly record struct NetworkAdapterSnapshot(
            string Id,
            NetworkInterfaceType InterfaceType,
            bool HasDefaultGateway,
            string[] UnicastAddresses,
            string[] GatewayAddresses);

        /// <summary>
        /// Computes a fast, offline SHA-256 fingerprint of active non-loopback network interfaces,
        /// gateways, virtual VPN/WireGuard/WARP adapters (including gatewayless split-tunnel adapters),
        /// and active system proxy routing.
        /// </summary>
        public static string GetLocalNetworkSignature()
        {
            try
            {
                var snapshots = new List<NetworkAdapterSnapshot>(8);
                foreach (NetworkInterface nic in NetworkInterface.GetAllNetworkInterfaces())
                {
                    if (nic.OperationalStatus != OperationalStatus.Up ||
                        nic.NetworkInterfaceType == NetworkInterfaceType.Loopback)
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
                        _logger.Debug($"[RobloxHbaService] GetIPProperties skipped for '{nic.Name}': {ex.Message}");
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
                            // Ignore APIPA 169.254.0.0/16 and 0.0.0.0
                            if ((bytes[0] == 169 && bytes[1] == 254) || (bytes[0] == 0 && bytes[1] == 0 && bytes[2] == 0 && bytes[3] == 0))
                                continue;
                            unicasts.Add(addr.ToString());
                        }
                        else if (addr.AddressFamily == AddressFamily.InterNetworkV6)
                        {
                            if (addr.IsIPv6LinkLocal || addr.IsIPv6Multicast)
                                continue;
                            unicasts.Add(addr.ToString());
                        }
                    }

                    if (unicasts.Count > 0)
                    {
                        snapshots.Add(new NetworkAdapterSnapshot(
                            nic.Id ?? nic.Name ?? string.Empty,
                            nic.NetworkInterfaceType,
                            gateways.Count > 0,
                            unicasts.ToArray(),
                            gateways.ToArray()));
                    }
                }

                string? proxyEndpoint = TryResolveSystemProxySignature();
                return ComputeNetworkSignature(snapshots, proxyEndpoint);
            }
            catch (Exception ex)
            {
                _logger.Debug($"[RobloxHbaService] GetLocalNetworkSignature failed: {ex.Message}");
                return "unknown";
            }
        }

        internal static string ComputeNetworkSignature(
            IReadOnlyList<NetworkAdapterSnapshot>? adapters,
            string? systemProxyEndpoint = null)
        {
            var parts = new List<string>(12);

            if (adapters != null)
            {
                foreach (NetworkAdapterSnapshot nic in adapters)
                {
                    if (nic.UnicastAddresses == null || nic.UnicastAddresses.Length == 0)
                        continue;

                    foreach (string rawIp in nic.UnicastAddresses)
                    {
                        if (string.IsNullOrWhiteSpace(rawIp))
                            continue;

                        string ip = rawIp.Trim();
                        if (ip.StartsWith("127.", StringComparison.Ordinal) ||
                            ip.StartsWith("169.254.", StringComparison.Ordinal) ||
                            ip.Equals("0.0.0.0", StringComparison.Ordinal) ||
                            ip.Equals("::1", StringComparison.Ordinal) ||
                            ip.StartsWith("fe80:", StringComparison.OrdinalIgnoreCase))
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

        private static string? TryResolveSystemProxySignature()
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
                _logger.Debug($"[RobloxHbaService] System proxy probe skipped: {ex.Message}");
                return null;
            }
        }
    }
}
