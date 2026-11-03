#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Shiro.Services
{
    public sealed class DatacenterLocationItem
    {
        [JsonPropertyName("city")]
        public string City { get; set; } = string.Empty;

        [JsonPropertyName("region")]
        public string Region { get; set; } = string.Empty;

        [JsonPropertyName("country")]
        public string Country { get; set; } = string.Empty;

        [JsonPropertyName("latLong")]
        public List<string>? LatLong { get; set; }
    }

    public sealed class DatacenterEntryItem
    {
        [JsonPropertyName("id")]
        public int Id { get; set; }

        [JsonPropertyName("dataCenterIds")]
        public List<int>? DataCenterIds { get; set; }

        [JsonPropertyName("robloxIps")]
        public List<string>? RobloxIps { get; set; }

        [JsonPropertyName("location")]
        public DatacenterLocationItem? Location { get; set; }
    }

    public sealed class DatacenterResolutionResult
    {
        public int Id { get; init; }
        public string City { get; init; } = string.Empty;
        public string Region { get; init; } = string.Empty;
        public string CountryCode { get; init; } = string.Empty;
        public string FlagEmoji { get; init; } = string.Empty;
        public string FormattedRegion { get; init; } = string.Empty;
        public string? SamplePingIp { get; init; }
        public double? Latitude { get; init; }
        public double? Longitude { get; init; }
    }

    /// <summary>
    /// High-performance offline resolver for Roblox global datacenters, subnets, and country flags.
    /// Eliminates slow external HTTP GeoIP queries and prevents rate-limiting when browsing game servers.
    /// </summary>
    public sealed class RobloxDatacenterService
    {
        private static readonly Lazy<RobloxDatacenterService> _instance = new(() => new RobloxDatacenterService(AppServices.Logger));
        public static RobloxDatacenterService Default => _instance.Value;

        private readonly IAppLogger _logger;
        private readonly Dictionary<int, DatacenterEntryItem> _byDataCenterId = new();
        private readonly Dictionary<string, DatacenterEntryItem> _byExactIp = new(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, DatacenterEntryItem> _bySubnet24 = new(StringComparer.OrdinalIgnoreCase);
        private readonly List<DatacenterEntryItem> _allEntries = new();
        private readonly bool _isLoaded;

        public bool IsLoaded => _isLoaded;
        public int TotalDatacenters => _allEntries.Count;

        public RobloxDatacenterService() : this(AppServices.Logger) { }

        public RobloxDatacenterService(IAppLogger logger)
        {
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
            try
            {
                string? json = LoadDatacentersJson();
                if (!string.IsNullOrWhiteSpace(json))
                {
                    var options = new JsonSerializerOptions
                    {
                        PropertyNameCaseInsensitive = true
                    };
                    List<DatacenterEntryItem>? entries = JsonSerializer.Deserialize<List<DatacenterEntryItem>>(json, options);
                    if (entries != null)
                    {
                        foreach (DatacenterEntryItem entry in entries)
                        {
                            _allEntries.Add(entry);

                            if (entry.DataCenterIds != null)
                            {
                                foreach (int dcId in entry.DataCenterIds)
                                {
                                    _byDataCenterId[dcId] = entry;
                                }
                            }

                            if (entry.RobloxIps != null)
                            {
                                foreach (string ip in entry.RobloxIps)
                                {
                                    if (string.IsNullOrWhiteSpace(ip)) continue;
                                    string trimmed = ip.Trim();
                                    _byExactIp[trimmed] = entry;

                                    string subnet24 = GetSubnet24(trimmed);
                                    if (!string.IsNullOrEmpty(subnet24))
                                    {
                                        _bySubnet24[subnet24] = entry;
                                    }
                                }
                            }
                        }
                        _isLoaded = _allEntries.Count > 0;
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.Debug($"[RobloxDatacenterService] Failed to load datacenters: {ex.Message}");
            }
        }

        private static string? LoadDatacentersJson()
        {
            // 1. Try file system
            string fileOnDisk = Path.Combine(AppContext.BaseDirectory, "Resources", "Data", "datacenters.json");
            if (File.Exists(fileOnDisk))
            {
                return File.ReadAllText(fileOnDisk);
            }

            // 2. Try embedded resource
            Assembly asm = typeof(RobloxDatacenterService).Assembly;
            string resourceName = "Shiro.Resources.Data.datacenters.json";
            using Stream? stream = asm.GetManifestResourceStream(resourceName);
            if (stream != null)
            {
                using var reader = new StreamReader(stream);
                return reader.ReadToEnd();
            }

            return null;
        }

        private static string GetSubnet24(string ip)
        {
            int lastDot = ip.LastIndexOf('.');
            return lastDot > 0 ? ip.Substring(0, lastDot) : string.Empty;
        }

        /// <summary>
        /// Converts an ISO 3166-1 alpha-2 country code (e.g. "SG", "US", "DE") to Unicode Flag Emoji.
        /// </summary>
        public static string GetCountryFlagEmoji(string? countryCode)
        {
            if (string.IsNullOrWhiteSpace(countryCode) || countryCode.Length != 2)
                return string.Empty;

            string code = countryCode.ToUpperInvariant();
            if (code[0] < 'A' || code[0] > 'Z' || code[1] < 'A' || code[1] > 'Z')
                return string.Empty;

            int first = 0x1F1E6 + (code[0] - 'A');
            int second = 0x1F1E6 + (code[1] - 'A');
            return char.ConvertFromUtf32(first) + char.ConvertFromUtf32(second);
        }

        /// <summary>
        /// Attempts to resolve datacenter metadata and country flag from a target IP address or Roblox Datacenter ID.
        /// </summary>
        public bool TryResolve(string? ip, int? dataCenterId, out DatacenterResolutionResult? resolution)
        {
            resolution = null;
            DatacenterEntryItem? entry = null;

            // 1. Check Datacenter ID
            if (dataCenterId.HasValue && dataCenterId.Value > 0)
            {
                _byDataCenterId.TryGetValue(dataCenterId.Value, out entry);
            }

            // 2. Check IP exact match
            if (entry == null && !string.IsNullOrWhiteSpace(ip))
            {
                string trimmed = ip.Trim();
                _byExactIp.TryGetValue(trimmed, out entry);

                // 3. Check /24 subnet prefix
                if (entry == null)
                {
                    string subnet = GetSubnet24(trimmed);
                    if (!string.IsNullOrEmpty(subnet))
                    {
                        _bySubnet24.TryGetValue(subnet, out entry);
                    }
                }
            }

            if (entry == null || entry.Location == null)
                return false;

            string city = entry.Location.City;
            string country = entry.Location.Country;
            string flag = GetCountryFlagEmoji(country);
            string formatted = !string.IsNullOrEmpty(city) && !string.IsNullOrEmpty(country)
                ? $"{city}, {country}"
                : (!string.IsNullOrEmpty(country) ? country : city);

            double? lat = null;
            double? lon = null;
            if (entry.Location.LatLong != null && entry.Location.LatLong.Count >= 2)
            {
                if (double.TryParse(entry.Location.LatLong[0], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out double parsedLat))
                    lat = parsedLat;
                if (double.TryParse(entry.Location.LatLong[1], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out double parsedLon))
                    lon = parsedLon;
            }

            resolution = new DatacenterResolutionResult
            {
                Id = entry.Id,
                City = city,
                Region = entry.Location.Region,
                CountryCode = country,
                FlagEmoji = flag,
                FormattedRegion = formatted,
                SamplePingIp = entry.RobloxIps?.FirstOrDefault(i => !string.IsNullOrWhiteSpace(i)),
                Latitude = lat,
                Longitude = lon
            };

            return true;
        }
    }
}
