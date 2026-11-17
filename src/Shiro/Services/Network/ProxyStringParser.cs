#nullable enable
using System;

namespace Shiro.Services
{
    internal static class ProxyStringParser
    {
        internal static string FormatHostForUri(string host)
            => !string.IsNullOrEmpty(host) && host.Contains(':') && !host.StartsWith('[') ? $"[{host}]" : host;

        internal static string RedactProxy(string? raw)
        {
            if (string.IsNullOrWhiteSpace(raw)) return string.Empty;
            var parsed = ParseProxy(raw);
            if (parsed != null)
                return $"{parsed.Protocol}://{FormatHostForUri(parsed.Host)}:{parsed.Port}";
            return LogRedactor.Redact(raw.Trim());
        }

        internal static ProxyItem? ParseProxy(string? raw)
        {
            if (string.IsNullOrWhiteSpace(raw)) return null;
            ReadOnlySpan<char> trimmedSpan = raw.AsSpan().Trim();
            if (trimmedSpan.IsEmpty || trimmedSpan[0] == '#') return null;

            string trimmed = trimmedSpan.Length == raw.Length ? raw : trimmedSpan.ToString();
            string scheme = "http";
            bool hadScheme = false;
            ReadOnlySpan<char> working = trimmedSpan;

            int schemeIdx = working.IndexOf("://".AsSpan(), StringComparison.Ordinal);
            if (schemeIdx >= 0)
            {
                hadScheme = true;
                scheme = working[..schemeIdx].ToString().ToLowerInvariant();
                working = working[(schemeIdx + 3)..];
            }

            int pathSlash = working.IndexOf('/');
            if (pathSlash >= 0)
                working = working[..pathSlash];

            ReadOnlySpan<char> usernameSpan = ReadOnlySpan<char>.Empty;
            ReadOnlySpan<char> passwordSpan = ReadOnlySpan<char>.Empty;
            ReadOnlySpan<char> hostPortSpan = working;
            bool isUriFormat = hadScheme;

            if (working.Length > 0 && working[0] == '[')
            {
                int closeBracket = working.IndexOf(']');
                if (closeBracket > 1 && closeBracket + 1 < working.Length && working[closeBracket + 1] == ':')
                {
                    ReadOnlySpan<char> ipv6Host = working[1..closeBracket].Trim();
                    ReadOnlySpan<char> afterBracket = working[(closeBracket + 2)..];
                    int firstColon = afterBracket.IndexOf(':');
                    if (firstColon > 0 && int.TryParse(afterBracket[..firstColon].Trim(), out int v6ColonPort) && v6ColonPort is > 0 and <= 65535)
                    {
                        SplitUserPass(afterBracket[(firstColon + 1)..], out usernameSpan, out passwordSpan);
                        return new ProxyItem
                        {
                            RawEntry = trimmed,
                            Protocol = scheme,
                            Host = ipv6Host.ToString(),
                            Port = v6ColonPort,
                            Username = usernameSpan.IsEmpty ? string.Empty : usernameSpan.Trim().ToString(),
                            Password = passwordSpan.IsEmpty ? string.Empty : passwordSpan.Trim().ToString(),
                            IsUriEncoded = hadScheme
                        };
                    }
                }
            }

            if (working.IndexOf('[') < 0)
            {
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
                    passwordSpan = working[colonRanges[3].Start.GetOffset(working.Length)..].Trim();

                    return new ProxyItem
                    {
                        RawEntry = trimmed,
                        Protocol = scheme,
                        Host = hostOnly.ToString(),
                        Port = colonPort,
                        Username = usernameSpan.IsEmpty ? string.Empty : usernameSpan.ToString(),
                        Password = passwordSpan.IsEmpty ? string.Empty : passwordSpan.ToString(),
                        IsUriEncoded = false
                    };
                }
            }

            int atIdx = working.LastIndexOf('@');
            if (atIdx >= 0)
            {
                ReadOnlySpan<char> part1 = working[..atIdx];
                ReadOnlySpan<char> part2 = working[(atIdx + 1)..];
                if (TryExtractHostPort(part2, out _, out _))
                {
                    hostPortSpan = part2;
                    isUriFormat = true;
                    SplitUserPass(part1, out usernameSpan, out passwordSpan);
                }
                else
                {
                    hostPortSpan = part1;
                    SplitUserPass(part2, out usernameSpan, out passwordSpan);
                }
            }

            if (!TryExtractHostPort(hostPortSpan, out ReadOnlySpan<char> hostSpan, out int port))
                return null;

            return new ProxyItem
            {
                RawEntry = trimmed,
                Protocol = scheme,
                Host = hostSpan.ToString(),
                Port = port,
                Username = usernameSpan.IsEmpty ? string.Empty : usernameSpan.ToString(),
                Password = passwordSpan.IsEmpty ? string.Empty : passwordSpan.ToString(),
                IsUriEncoded = isUriFormat
            };
        }

        private static void SplitUserPass(ReadOnlySpan<char> rawCred, out ReadOnlySpan<char> user, out ReadOnlySpan<char> pass)
        {
            int colon = rawCred.IndexOf(':');
            if (colon >= 0)
            {
                user = rawCred[..colon];
                pass = rawCred[(colon + 1)..];
            }
            else
            {
                user = rawCred;
                pass = ReadOnlySpan<char>.Empty;
            }
        }

        private static bool TryExtractHostPort(ReadOnlySpan<char> span, out ReadOnlySpan<char> host, out int port)
        {
            host = ReadOnlySpan<char>.Empty;
            port = 0;
            if (span.Length > 0 && span[0] == '[')
            {
                int close = span.IndexOf(']');
                if (close <= 1 || close + 1 >= span.Length || span[close + 1] != ':')
                    return false;
                if (!int.TryParse(span[(close + 2)..].Trim(), out port) || port is <= 0 or > 65535)
                    return false;
                host = span[1..close].Trim();
                return !host.IsEmpty && !host.IsWhiteSpace();
            }

            int portSep = span.LastIndexOf(':');
            if (portSep <= 0 || !int.TryParse(span[(portSep + 1)..].Trim(), out port) || port is <= 0 or > 65535)
                return false;

            host = span[..portSep].Trim();
            return !host.IsEmpty && !host.IsWhiteSpace() && host.IndexOf(':') < 0;
        }
    }
}
