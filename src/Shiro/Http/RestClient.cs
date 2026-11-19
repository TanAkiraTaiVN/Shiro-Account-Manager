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
using Shiro.Services;

namespace Shiro.Http
{
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

        internal static bool IsBodyMethod(Method method) =>
            method is Method.Post or Method.Put or Method.Patch;

        private static void AppendQueryList(StringBuilder sb, List<KeyValuePair<string, string>> parameters)
        {
            foreach (var param in parameters)
            {
                if (sb.Length > 0)
                    sb.Append('&');
                sb.Append(Uri.EscapeDataString(param.Key))
                  .Append('=')
                  .Append(Uri.EscapeDataString(param.Value));
            }
        }

        internal static Uri BuildRequestUri(RestRequest request, string? baseUrl)
        {
            string requestUri = request.Resource ?? string.Empty;
            Uri finalUri;

            if (Uri.TryCreate(requestUri, UriKind.Absolute, out Uri? absolute) && absolute != null)
            {
                finalUri = absolute;
            }
            else
            {
                string baseStr = string.IsNullOrWhiteSpace(baseUrl)
                    ? "https://www.roblox.com/"
                    : baseUrl.TrimEnd('/') + "/";
                requestUri = requestUri.TrimStart('/');
                finalUri = new Uri(new Uri(baseStr), requestUri);
            }

            bool includeLegacyGetParams = !IsBodyMethod(request.Method) && request.Parameters.Count > 0;
            if (request.QueryParameters.Count > 0 || includeLegacyGetParams)
            {
                var ub = new UriBuilder(finalUri);
                var sb = new StringBuilder();
                if (!string.IsNullOrEmpty(ub.Query))
                    sb.Append(ub.Query.AsSpan().TrimStart('?'));

                if (request.QueryParameters.Count > 0)
                    AppendQueryList(sb, request.QueryParameters);
                if (includeLegacyGetParams)
                    AppendQueryList(sb, request.Parameters);

                ub.Query = sb.ToString();
                finalUri = ub.Uri;
            }

            return finalUri;
        }

        internal HttpRequestMessage CreateHttpRequestMessage(RestRequest request)
        {
            Uri finalUri = BuildRequestUri(request, BaseUrl);
            bool isBodyMethod = IsBodyMethod(request.Method);

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
                    if (request.JsonBody is string rawJson)
                    {
                        message.Content = new StringContent(rawJson, Encoding.UTF8, "application/json");
                    }
                    else if (request.JsonBody is JsonElement je)
                    {
                        message.Content = new StringContent(je.GetRawText(), Encoding.UTF8, "application/json");
                    }
                    else
                    {
                        byte[] utf8Bytes = FastJson.SerializeToUtf8Bytes(request.JsonBody);
                        var byteContent = new ByteArrayContent(utf8Bytes);
                        byteContent.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("application/json")
                        {
                            CharSet = "utf-8"
                        };
                        message.Content = byteContent;
                    }
                }
                else if (specifiedContentType != null && specifiedContentType.Contains("x-www-form-urlencoded", StringComparison.OrdinalIgnoreCase))
                {
                    message.Content = new FormUrlEncodedContent(request.Parameters);
                }
                else if (request.StringBody != null)
                {
                    string mediaType = request.ContentType ?? specifiedContentType ?? "application/json";
                    int semi = mediaType.IndexOf(';');
                    if (semi > 0) mediaType = mediaType.Substring(0, semi).Trim();
                    message.Content = new StringContent(request.StringBody, Encoding.UTF8, mediaType);
                }
                else if (request.Parameters.Count > 0)
                {
                    message.Content = new FormUrlEncodedContent(request.Parameters);
                }
            }

            return message;
        }

        internal static string? SerializeRequestBodyForSigning(RestRequest request)
        {
            if (request == null) return null;
            if (request.JsonBody != null)
            {
                if (request.JsonBody is string rawJson) return rawJson;
                if (request.JsonBody is JsonElement je) return je.GetRawText();
                return FastJson.Serialize(request.JsonBody);
            }

            bool isForm = request.Headers.TryGetValue("Content-Type", out var ct) &&
                          ct.Contains("x-www-form-urlencoded", StringComparison.OrdinalIgnoreCase);
            if (!isForm && request.StringBody != null)
                return request.StringBody;

            if (request.Parameters.Count > 0)
            {
                return string.Join("&", request.Parameters.Select(p =>
                    $"{Uri.EscapeDataString(p.Key).Replace("%20", "+")}={Uri.EscapeDataString(p.Value).Replace("%20", "+")}"));
            }
            return null;
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
        public Task<(bool Success, T? Data, HttpStatusCode StatusCode, string? ErrorMessage)> ExecuteJsonAsync<T>(
            RestRequest request,
            JsonTypeInfo<T> jsonTypeInfo,
            CancellationToken cancellationToken = default) =>
            ExecuteJsonAsync(request, jsonTypeInfo, null, cancellationToken);

        public async Task<(bool Success, T? Data, HttpStatusCode StatusCode, string? ErrorMessage)> ExecuteJsonAsync<T>(
            RestRequest request,
            JsonTypeInfo<T> jsonTypeInfo,
            HttpClient? clientOverride,
            CancellationToken cancellationToken = default)
        {
            if (request == null) throw new ArgumentNullException(nameof(request));
            if (jsonTypeInfo == null) throw new ArgumentNullException(nameof(jsonTypeInfo));

            HttpClient activeClient = clientOverride ?? _httpClient;
            using var message = CreateHttpRequestMessage(request);
            try
            {
                using var httpResponse = await activeClient.SendAsync(message, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
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

            if (FastJson.TryDeserialize<T>(raw.Content, out var data))
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
