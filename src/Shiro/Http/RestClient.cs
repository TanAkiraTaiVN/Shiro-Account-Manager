#nullable enable
using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using System.Threading;
using System.Threading.Tasks;
using Shiro.Classes;

namespace Shiro.Http
{
    public enum Method
    {
        Get,
        Post,
        Put,
        Delete,
        Head,
        Options,
        Patch
    }

    public sealed class HeaderParameter
    {
        public string Name { get; set; }
        public object? Value { get; set; }

        public HeaderParameter(string name, object? value)
        {
            Name = name;
            Value = value;
        }

        public override string ToString() => $"{Name}: {Value}";
    }

    public sealed class RestResponseCookie
    {
        public string Name { get; set; }
        public string Value { get; set; }

        public RestResponseCookie(string name, string value)
        {
            Name = name;
            Value = value;
        }

        public override string ToString() => $"{Name}={Value}";
    }

    public sealed class RestResponseCookies : IReadOnlyList<RestResponseCookie>
    {
        private readonly List<RestResponseCookie> _cookies = new();

        public int Count => _cookies.Count;

        public RestResponseCookie this[int index] => _cookies[index];

        public RestResponseCookie? this[string name]
        {
            get
            {
                if (string.IsNullOrWhiteSpace(name)) return null;
                return _cookies.FirstOrDefault(c => string.Equals(c.Name, name, StringComparison.OrdinalIgnoreCase));
            }
        }

        internal void Add(string name, string value)
        {
            _cookies.Add(new RestResponseCookie(name, value));
        }

        public IEnumerator<RestResponseCookie> GetEnumerator() => _cookies.GetEnumerator();
        IEnumerator IEnumerable.GetEnumerator() => _cookies.GetEnumerator();
    }

    public sealed class RestRequest
    {
        public string Resource { get; set; }
        public Method Method { get; set; }
        public Dictionary<string, string> Headers { get; } = new(StringComparer.OrdinalIgnoreCase);
        public List<KeyValuePair<string, string>> Parameters { get; } = new();
        public object? JsonBody { get; private set; }
        public string? StringBody { get; private set; }
        public string? ContentType { get; set; }

        public RestRequest(string resource = "", Method method = Method.Get)
        {
            Resource = resource ?? string.Empty;
            Method = method;
        }

        public RestRequest AddHeader(string name, string value)
        {
            if (!string.IsNullOrWhiteSpace(name))
                Headers[name] = value ?? string.Empty;
            return this;
        }

        public RestRequest AddCookie(string name, string value, string path = "/", string domain = ".roblox.com")
        {
            if (string.IsNullOrWhiteSpace(name)) return this;
            string cookieStr = $"{name}={value}";
            if (Headers.TryGetValue("Cookie", out string? existing))
                Headers["Cookie"] = $"{existing}; {cookieStr}";
            else
                Headers["Cookie"] = cookieStr;
            return this;
        }

        public RestRequest AddQueryParameter(string name, string value)
        {
            if (!string.IsNullOrWhiteSpace(name))
                Parameters.Add(new KeyValuePair<string, string>(name, value ?? string.Empty));
            return this;
        }

        public RestRequest AddParameter(string name, string value)
        {
            if (!string.IsNullOrWhiteSpace(name))
                Parameters.Add(new KeyValuePair<string, string>(name, value ?? string.Empty));
            return this;
        }

        public RestRequest AddJsonBody(object obj)
        {
            JsonBody = obj;
            return this;
        }

        public RestRequest AddStringBody(string body, string contentType = "application/json")
        {
            StringBody = body;
            ContentType = contentType;
            return this;
        }
    }

    public sealed class RestResponse
    {
        public HttpStatusCode StatusCode { get; set; }
        public string? StatusDescription { get; set; }
        public string? Content { get; set; }
        public bool IsSuccessful => (int)StatusCode >= 200 && (int)StatusCode <= 299;
        public string? ErrorMessage { get; set; }
        public Exception? ErrorException { get; set; }
        public List<HeaderParameter> Headers { get; } = new();
        public RestResponseCookies Cookies { get; } = new();
    }

    /// <summary>
    /// Modern zero-dependency HTTP client layer replacing legacy RestSharp.
    /// Built on high-performance .NET 10 SocketsHttpHandler with native Brotli/GZip compression.
    /// </summary>
    public sealed class RestClient : IDisposable
    {
        private readonly HttpClient _httpClient;
        private readonly bool _ownsClient;
        public string? BaseUrl { get; }
        public string? UserAgent { get; }

        public RestClient(string? baseUrl = null, string? userAgent = null)
        {
            if (!string.IsNullOrWhiteSpace(baseUrl))
                BaseUrl = baseUrl.TrimEnd('/') + "/";

            UserAgent = userAgent;
            string clientPoolName = string.IsNullOrEmpty(userAgent) ? "default" : $"ua_{userAgent}";
            _httpClient = Services.SharedHttpClient.Get(clientPoolName);
            _ownsClient = false;
        }

        public RestClient(HttpClient client, string? baseUrl = null, string? userAgent = null)
        {
            _httpClient = client ?? Services.SharedHttpClient.Get();
            if (!string.IsNullOrWhiteSpace(baseUrl))
                BaseUrl = baseUrl.TrimEnd('/') + "/";
            UserAgent = userAgent;
            _ownsClient = false;
        }

        private HttpRequestMessage CreateHttpRequestMessage(RestRequest request)
        {
            string requestUri = request.Resource ?? string.Empty;
            Uri finalUri;

            if (Uri.TryCreate(requestUri, UriKind.Absolute, out Uri? absolute) && absolute != null)
            {
                finalUri = absolute;
            }
            else
            {
                string baseStr = BaseUrl ?? "https://www.roblox.com/";
                requestUri = requestUri.TrimStart('/');
                finalUri = new Uri(new Uri(baseStr), requestUri);
            }

            bool isBodyMethod = request.Method is Method.Post or Method.Put or Method.Patch;
            if (!isBodyMethod && request.Parameters.Count > 0)
            {
                var ub = new UriBuilder(finalUri);
                var sb = new StringBuilder();
                if (!string.IsNullOrEmpty(ub.Query))
                    sb.Append(ub.Query.AsSpan().TrimStart('?'));

                foreach (var param in request.Parameters)
                {
                    if (sb.Length > 0)
                        sb.Append('&');
                    sb.Append(Uri.EscapeDataString(param.Key))
                      .Append('=')
                      .Append(Uri.EscapeDataString(param.Value));
                }
                ub.Query = sb.ToString();
                finalUri = ub.Uri;
            }

            HttpMethod httpMethod = request.Method switch
            {
                Method.Get => HttpMethod.Get,
                Method.Post => HttpMethod.Post,
                Method.Put => HttpMethod.Put,
                Method.Delete => HttpMethod.Delete,
                Method.Head => HttpMethod.Head,
                Method.Options => HttpMethod.Options,
                Method.Patch => HttpMethod.Patch,
                _ => HttpMethod.Get
            };

            var message = new HttpRequestMessage(httpMethod, finalUri);

            if (!string.IsNullOrEmpty(UserAgent))
            {
                message.Headers.Remove("User-Agent");
                message.Headers.TryAddWithoutValidation("User-Agent", UserAgent);
            }

            string? specifiedContentType = null;
            foreach (var kvp in request.Headers)
            {
                if (kvp.Key.Equals("Content-Type", StringComparison.OrdinalIgnoreCase))
                {
                    specifiedContentType = kvp.Value;
                    continue;
                }
                if (kvp.Key.Equals("User-Agent", StringComparison.OrdinalIgnoreCase) && !string.IsNullOrEmpty(UserAgent))
                {
                    continue;
                }
                message.Headers.TryAddWithoutValidation(kvp.Key, kvp.Value);
            }

            if (isBodyMethod)
            {
                if (request.JsonBody != null)
                {
                    string json;
                    if (request.JsonBody is string rawJson)
                        json = rawJson;
                    else if (request.JsonBody is JsonElement je)
                        json = je.GetRawText();
                    else
                        json = Utilities.FastJson.Serialize(request.JsonBody);
                    message.Content = new StringContent(json, Encoding.UTF8, "application/json");
                }
                else if (specifiedContentType != null && specifiedContentType.Contains("x-www-form-urlencoded", StringComparison.OrdinalIgnoreCase))
                {
                    message.Content = new FormUrlEncodedContent(request.Parameters);
                }
                else if (request.StringBody != null)
                {
                    message.Content = new StringContent(request.StringBody, Encoding.UTF8, request.ContentType ?? specifiedContentType ?? "application/json");
                }
                else if (request.Parameters.Count > 0)
                {
                    message.Content = new FormUrlEncodedContent(request.Parameters);
                }
            }

            return message;
        }

        public Task<RestResponse> ExecuteAsync(RestRequest request, CancellationToken cancellationToken = default) =>
            ExecuteAsync(request, null, cancellationToken);

        public async Task<RestResponse> ExecuteAsync(RestRequest request, HttpClient? clientOverride, CancellationToken cancellationToken = default)
        {
            if (request == null)
                throw new ArgumentNullException(nameof(request));

            HttpClient activeClient = clientOverride ?? _httpClient;
            using var message = CreateHttpRequestMessage(request);
            var restResponse = new RestResponse();

            try
            {
                using var httpResponse = await activeClient.SendAsync(message, HttpCompletionOption.ResponseContentRead, cancellationToken).ConfigureAwait(false);
                restResponse.StatusCode = httpResponse.StatusCode;
                restResponse.StatusDescription = httpResponse.ReasonPhrase ?? httpResponse.StatusCode.ToString();

                foreach (var header in httpResponse.Headers)
                {
                    foreach (var val in header.Value)
                        restResponse.Headers.Add(new HeaderParameter(header.Key, val));
                }

                if (httpResponse.Content?.Headers != null)
                {
                    foreach (var header in httpResponse.Content.Headers)
                    {
                        foreach (var val in header.Value)
                            restResponse.Headers.Add(new HeaderParameter(header.Key, val));
                    }
                }

                IEnumerable<string>? cookieHeaders = null;
                if (!httpResponse.Headers.TryGetValues("Set-Cookie", out cookieHeaders))
                {
                    try
                    {
                        if (httpResponse.Headers.NonValidated.TryGetValues("Set-Cookie", out var nonVal))
                            cookieHeaders = nonVal;
                    }
                    catch (Exception ex)
                    {
                        Program.Logger?.Debug($"Failed non-validated Set-Cookie header extraction: {ex.Message}");
                    }
                }

                if (cookieHeaders != null)
                {
                    foreach (string cookieHeader in cookieHeaders)
                    {
                        if (string.IsNullOrWhiteSpace(cookieHeader)) continue;
                        ReadOnlySpan<char> headerSpan = cookieHeader.AsSpan();
                        int semiIdx = headerSpan.IndexOf(';');
                        ReadOnlySpan<char> firstPart = semiIdx >= 0 ? headerSpan[..semiIdx] : headerSpan;
                        int eqIdx = firstPart.IndexOf('=');
                        if (eqIdx > 0)
                        {
                            string cName = firstPart[..eqIdx].Trim().ToString();
                            string cVal = firstPart[(eqIdx + 1)..].Trim().ToString();
                            restResponse.Cookies.Add(cName, cVal);
                        }
                    }
                }

                restResponse.Content = httpResponse.Content != null
                    ? await httpResponse.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false)
                    : string.Empty;
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                restResponse.StatusCode = 0;
                restResponse.ErrorMessage = ex.Message;
                restResponse.ErrorException = ex;
            }

            return restResponse;
        }

        /// <summary>
        /// Streams the HTTP response body directly into <see cref="JsonSerializer.DeserializeAsync{TValue}(System.IO.Stream,JsonTypeInfo{TValue},CancellationToken)"/>
        /// using source-generated metadata, eliminating intermediate UTF-16 string allocations on hot polling paths.
        /// </summary>
        public async Task<(bool Success, T? Data, HttpStatusCode StatusCode, string? ErrorMessage)> ExecuteJsonAsync<T>(
            RestRequest request,
            JsonTypeInfo<T> jsonTypeInfo,
            CancellationToken cancellationToken = default)
        {
            if (request == null) throw new ArgumentNullException(nameof(request));
            if (jsonTypeInfo == null) throw new ArgumentNullException(nameof(jsonTypeInfo));

            using var message = CreateHttpRequestMessage(request);
            try
            {
                using var httpResponse = await _httpClient.SendAsync(message, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
                if (!httpResponse.IsSuccessStatusCode)
                {
                    return (false, default, httpResponse.StatusCode, httpResponse.ReasonPhrase);
                }

                if (httpResponse.Content == null)
                {
                    return (false, default, httpResponse.StatusCode, "Empty response content.");
                }

                await using var stream = await httpResponse.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
                T? data = await JsonSerializer.DeserializeAsync(stream, jsonTypeInfo, cancellationToken).ConfigureAwait(false);
                return (data != null, data, httpResponse.StatusCode, null);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                return (false, default, 0, ex.Message);
            }
        }

        /// <summary>
        /// Executes the request and automatically deserializes the response JSON into <typeparamref name="T"/>.
        /// </summary>
        public async Task<(bool Success, T? Data, RestResponse Raw)> ExecuteAsync<T>(RestRequest request, CancellationToken cancellationToken = default)
        {
            var raw = await ExecuteAsync(request, cancellationToken).ConfigureAwait(false);
            if (!raw.IsSuccessful || string.IsNullOrWhiteSpace(raw.Content))
                return (false, default, raw);

            if (Utilities.FastJson.TryDeserialize<T>(raw.Content, out var data))
                return (true, data, raw);

            return (false, default, raw);
        }

        public void Dispose()
        {
            if (_ownsClient)
                _httpClient?.Dispose();
        }
    }
}
