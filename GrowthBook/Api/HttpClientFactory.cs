using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Text;

namespace GrowthBook.Api
{
    public class HttpClientFactory : IHttpClientFactory
    {
        private readonly int _requestTimeoutInSeconds;

        public HttpClientFactory(int requestTimeoutInSeconds = 60) => _requestTimeoutInSeconds = requestTimeoutInSeconds;

        /// <inheritdoc/>
        public HttpClient CreateClient(string clientName)
        {
            switch (clientName)
            {
                case ConfiguredClients.DefaultApiClient: return CreateClient(ConfigureAsDefault);
                case ConfiguredClients.ServerSentEventsApiClient: return CreateClient(ConfigureAsServerSentEvents);
                default:
                    throw new ArgumentOutOfRangeException(nameof(clientName), $"Unknown HTTP client '{clientName}' cannot be created");
            };
        }

        private HttpClient ConfigureAsDefault(HttpClient client)
        {
            client.DefaultRequestHeaders.Accept.ParseAdd("application/json");

            return client;
        }

        private HttpClient ConfigureAsServerSentEvents(HttpClient client)
        {
            var acceptHeader = client.DefaultRequestHeaders.Accept;

            acceptHeader.ParseAdd("application/json; q=0.5");
            acceptHeader.ParseAdd("text/event-stream");

            return client;
        }

        protected internal virtual HttpClient CreateClient(Func<HttpClient, HttpClient> configure)
        {
            var client = new HttpClient
            {
                Timeout = TimeSpan.FromSeconds(_requestTimeoutInSeconds)
            };

            return configure(client);
        }
    }
}
