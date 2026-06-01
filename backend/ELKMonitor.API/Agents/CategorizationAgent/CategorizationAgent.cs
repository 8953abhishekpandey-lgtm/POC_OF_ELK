using ELKMonitor.API.Models;
using System.Net.Http.Json;
using Microsoft.Extensions.Logging;

namespace ELKMonitor.API.Agents.CategorizationAgent
{
    /// <summary>
    /// Agent 2 — Categorization Agent.
    ///
    /// Periodically queries the dynamic microservice (GitLab Duo) to classify each log entry.
    /// If the microservice is offline, it falls back to Unclassified.
    /// </summary>
    public class CategorizationAgent : ICategorizationAgent
    {
        private readonly HttpClient _httpClient;
        private readonly ILogger<CategorizationAgent> _logger;

        public CategorizationAgent(IHttpClientFactory httpClientFactory, ILogger<CategorizationAgent> logger)
        {
            _httpClient = httpClientFactory.CreateClient("CategorizerClient");
            _logger = logger;
        }

        /// <inheritdoc/>
        public async Task<(string Category, string Subcategory)> ClassifyAsync(
            string message,
            string exceptionType,
            string stackTrace,
            string severity,
            string applicationName = "Unknown")
        {
            try
            {
                var payload = new
                {
                    message,
                    exception_type = exceptionType,
                    stack_trace = stackTrace,
                    severity,
                    application_name = applicationName
                };

                var response = await _httpClient.PostAsJsonAsync("classify", payload);
                if (response.IsSuccessStatusCode)
                {
                    var result = await response.Content.ReadFromJsonAsync<ClassifyResponse>();
                    if (result != null && !string.IsNullOrWhiteSpace(result.Category))
                    {
                        return (result.Category, result.Subcategory);
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Dynamic GitLab Duo classification failed. Falling back to Unclassified.");
            }

            return ("Unclassified", "Unclassified");
        }

        /// <inheritdoc/>
        public async Task<List<(string Category, string Subcategory)>> ClassifyBulkAsync(List<ClassifyLogInput> logs)
        {
            var fallback = logs.Select(_ => ("Unclassified", "Unclassified")).ToList();
            if (logs == null || logs.Count == 0) return fallback;

            try
            {
                var payload = logs.Select(l => new
                {
                    message = l.Message,
                    exception_type = l.ExceptionType,
                    stack_trace = l.StackTrace,
                    severity = l.Severity,
                    application_name = l.ApplicationName
                }).ToList();

                var response = await _httpClient.PostAsJsonAsync("classify/bulk", payload);
                if (response.IsSuccessStatusCode)
                {
                    var result = await response.Content.ReadFromJsonAsync<List<ClassifyResponse>>();
                    if (result != null && result.Count == logs.Count)
                    {
                        return result.Select(r => (r.Category, r.Subcategory)).ToList();
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Dynamic bulk classification failed. Falling back to Unclassified.");
            }

            return fallback;
        }

        private class ClassifyResponse
        {
            public string Category { get; set; } = string.Empty;
            public string Subcategory { get; set; } = string.Empty;
            public bool Cached { get; set; }
        }
    }
}
