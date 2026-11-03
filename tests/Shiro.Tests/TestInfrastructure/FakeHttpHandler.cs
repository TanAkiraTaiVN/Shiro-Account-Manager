#nullable enable
using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace Shiro.Tests.TestInfrastructure
{
    public sealed class FakeHttpHandler : HttpMessageHandler
    {
        private readonly object _lock = new();
        private readonly Queue<Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>>> _queue = new();
        private readonly Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>>? _fallbackResponder;
        private readonly List<HttpRequestMessage> _requests = new();
        private readonly List<string?> _requestBodies = new();

        public FakeHttpHandler()
        {
        }

        public FakeHttpHandler(Func<HttpRequestMessage, HttpResponseMessage> fallbackResponder)
        {
            if (fallbackResponder == null) throw new ArgumentNullException(nameof(fallbackResponder));
            _fallbackResponder = (req, _) => Task.FromResult(fallbackResponder(req));
        }

        public FakeHttpHandler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> fallbackResponder)
        {
            _fallbackResponder = fallbackResponder ?? throw new ArgumentNullException(nameof(fallbackResponder));
        }

        public int Calls
        {
            get
            {
                lock (_lock)
                {
                    return _requests.Count;
                }
            }
        }

        public IReadOnlyList<HttpRequestMessage> Requests
        {
            get
            {
                lock (_lock)
                {
                    return _requests.ToArray();
                }
            }
        }

        public IReadOnlyList<string?> RequestBodies
        {
            get
            {
                lock (_lock)
                {
                    return _requestBodies.ToArray();
                }
            }
        }

        public FakeHttpHandler Enqueue(HttpStatusCode status, string body = "", params (string Name, string Value)[] headers)
        {
            return Enqueue((_, _) =>
            {
                var response = new HttpResponseMessage(status)
                {
                    Content = new StringContent(body, Encoding.UTF8, "application/json")
                };

                if (headers != null)
                {
                    foreach (var (name, value) in headers)
                    {
                        if (string.Equals(name, "Content-Type", StringComparison.OrdinalIgnoreCase))
                        {
                            response.Content?.Headers.Remove("Content-Type");
                            response.Content?.Headers.TryAddWithoutValidation(name, value);
                        }
                        else if (!response.Headers.TryAddWithoutValidation(name, value))
                        {
                            response.Content?.Headers.TryAddWithoutValidation(name, value);
                        }
                    }
                }

                return Task.FromResult(response);
            });
        }

        public FakeHttpHandler Enqueue(HttpResponseMessage response)
        {
            if (response == null) throw new ArgumentNullException(nameof(response));
            return Enqueue((_, _) => Task.FromResult(response));
        }

        public FakeHttpHandler Enqueue(Func<HttpRequestMessage, HttpResponseMessage> responder)
        {
            if (responder == null) throw new ArgumentNullException(nameof(responder));
            return Enqueue((req, _) => Task.FromResult(responder(req)));
        }

        public FakeHttpHandler Enqueue(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> responder)
        {
            if (responder == null) throw new ArgumentNullException(nameof(responder));
            lock (_lock)
            {
                _queue.Enqueue(responder);
            }
            return this;
        }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();

            string? body = null;
            if (request.Content != null)
            {
                body = await request.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            }

            Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>>? responder = null;

            lock (_lock)
            {
                _requests.Add(request);
                _requestBodies.Add(body);

                if (_queue.Count > 0)
                {
                    responder = _queue.Dequeue();
                }
                else if (_fallbackResponder != null)
                {
                    responder = _fallbackResponder;
                }
            }

            if (responder == null)
            {
                throw new InvalidOperationException($"No queued response for {request.Method} {request.RequestUri}");
            }

            var response = await responder(request, cancellationToken).ConfigureAwait(false);
            response.RequestMessage ??= request;
            return response;
        }
    }
}
