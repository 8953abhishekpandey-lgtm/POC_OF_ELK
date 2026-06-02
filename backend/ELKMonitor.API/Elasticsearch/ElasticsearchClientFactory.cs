using Elastic.Clients.Elasticsearch;
using Elastic.Transport;

namespace ELKMonitor.API.Elasticsearch
{
    /// <summary>
    /// Factory that creates and configures the ElasticsearchClient singleton.
    /// Supports basic auth, API key, and Cloud ID connection modes.
    /// </summary>
    public static class ElasticsearchClientFactory
    {
        public static ElasticsearchClient Create(ElasticsearchSettings settings)
        {
            ElasticsearchClientSettings clientSettings;

            // Cloud ID mode (Elastic Cloud)
            if (!string.IsNullOrWhiteSpace(settings.CloudId))
            {
                if (!string.IsNullOrWhiteSpace(settings.ApiKey))
                {
                    clientSettings = new ElasticsearchClientSettings(
                        new CloudNodePool(settings.CloudId, new ApiKey(settings.ApiKey)));
                }
                else
                {
                    clientSettings = new ElasticsearchClientSettings(
                        new CloudNodePool(settings.CloudId,
                            new BasicAuthentication(settings.Username, settings.Password)));
                }
            }
            // API Key mode (self-hosted)
            else if (!string.IsNullOrWhiteSpace(settings.ApiKey))
            {
                var uri = new Uri(settings.Url);
                clientSettings = new ElasticsearchClientSettings(uri)
                    .Authentication(new ApiKey(settings.ApiKey));
            }
            // Basic auth mode (default for local / Docker)
            else
            {
                var uri = new Uri(settings.Url);
                clientSettings = new ElasticsearchClientSettings(uri)
                    .Authentication(new BasicAuthentication(settings.Username, settings.Password));
            }

            // Common settings
            clientSettings
                .RequestTimeout(TimeSpan.FromSeconds(settings.RequestTimeoutSeconds))
                .EnableDebugMode()
                .PrettyJson()
                .OnRequestCompleted(apiCallDetails =>
                {
                    if (apiCallDetails.RequestBodyInBytes != null)
                    {
                        var requestBody = System.Text.Encoding.UTF8.GetString(apiCallDetails.RequestBodyInBytes);
                        Console.WriteLine($"Elasticsearch Request JSON:\n{requestBody}");
                    }
                    if (apiCallDetails.ResponseBodyInBytes != null)
                    {
                        var responseBody = System.Text.Encoding.UTF8.GetString(apiCallDetails.ResponseBodyInBytes);
                        if (responseBody.Length > 1000)
                            responseBody = responseBody.Substring(0, 1000) + "...[TRUNCATED]";
                        Console.WriteLine($"Elasticsearch Response JSON:\n{responseBody}");
                    }
                });

            // Disable SSL verification for local dev (never use in production)
            if (!settings.VerifySsl)
            {
                clientSettings.ServerCertificateValidationCallback(
                    (sender, cert, chain, errors) => true);
            }

            return new ElasticsearchClient(clientSettings);
        }
    }
}
