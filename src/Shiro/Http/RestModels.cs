#nullable enable
using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Net;

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
        public List<KeyValuePair<string, string>> QueryParameters { get; } = new();
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
                QueryParameters.Add(new KeyValuePair<string, string>(name, value ?? string.Empty));
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
}
