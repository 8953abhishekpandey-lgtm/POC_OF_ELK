using ELKMonitor.API.DTOs;
using ELKMonitor.API.Elasticsearch;
using Elastic.Clients.Elasticsearch;
using Elastic.Clients.Elasticsearch.QueryDsl;

namespace ELKMonitor.API.Agents.DataFetchingAgent
{
    /// <summary>
    /// Agent 1 — Data Fetching Agent.
    ///
    /// This is the ONLY place in the codebase that talks to Elasticsearch.
    /// It owns all query construction, pagination, and raw JSON parsing.
    ///
    /// Pipeline role:
    ///   [DataFetchingAgent] → CategorizationAgent → SubCategorizationAgent
    ///
    /// Input:  LogFilterDto (date, app, server, severity, search text)
    /// Output: List&lt;LogDocument&gt; (parsed raw documents, no categories yet)
    /// </summary>
    public class DataFetchingAgent : IDataFetchingAgent
    {
        // ── Known multi-alias field arrays ────────────────────────────────────
        // Different Elasticsearch index templates use different field names
        // for the same logical concept. We try all of them.

        internal static readonly string[] SeverityKeywordFields =
        {
            "level", "level.keyword",
            "severity", "severity.keyword",
            "log.level", "log.level.keyword",
            "fields.level", "fields.level.keyword"
        };

        internal static readonly string[] ApplicationKeywordFields =
        {
            "fields.application", "fields.application.keyword",
            "service.name", "service.name.keyword",
            "application", "application.keyword",
            "app", "app.keyword",
            "app_name", "app_name.keyword",
            "tags.keyword"
        };

        internal static readonly string[] ServerKeywordFields =
        {
            "host.name", "host.name.keyword",
            "hostname", "hostname.keyword",
            "server", "server.keyword",
            "server_name", "server_name.keyword",
            "host_name", "host_name.keyword"
        };

        internal static readonly string[] ExceptionKeywordFields =
        {
            "exception.type", "exception.type.keyword",
            "exception.class", "exception.class.keyword",
            "exception_class", "exception_class.keyword",
            "exception_type", "exception_type.keyword"
        };

        private readonly ElasticsearchClient _esClient;
        private readonly ElasticsearchSettings _settings;
        private readonly ILogger<DataFetchingAgent> _logger;

        public DataFetchingAgent(
            ElasticsearchClient esClient,
            ElasticsearchSettings settings,
            ILogger<DataFetchingAgent> logger)
        {
            _esClient = esClient;
            _settings = settings;
            _logger   = logger;
        }

        // ── Public API ────────────────────────────────────────────────────────

        /// <inheritdoc/>
        public async Task<List<LogDocument>> FetchLogsAsync(LogFilterDto filter, int size = 1000)
        {
            var response = await _esClient.SearchAsync<LogDocument>(s => s
                .Indices(_settings.IndexPattern)
                .From(0)
                .Size(size)
                .Sort(so => so.Field(f => f.Field("@timestamp").Order(SortOrder.Desc)))
                .Query(q => BuildQuery(q, filter)));

            if (!response.IsValidResponse)
            {
                _logger.LogWarning("ES query failed: {Error}",
                    response.ElasticsearchServerError?.Error?.Reason);
                return new List<LogDocument>();
            }

            var docs = new List<LogDocument>();
            foreach (var hit in response.Hits)
            {
                if (hit.Source != null)
                {
                    hit.Source.SourceIndex = hit.Index;
                    docs.Add(hit.Source);
                }
            }
            return docs;
        }

        /// <inheritdoc/>
        public async Task<long> CountAsync(LogFilterDto filter)
        {
            var response = await _esClient.CountAsync<LogDocument>(c => c
                .Indices(_settings.IndexPattern)
                .Query(q => BuildQuery(q, filter)));

            return response.IsValidResponse ? response.Count : 0;
        }

        /// <inheritdoc/>
        public async Task<List<string>> GetDistinctTermsAsync(string[] fields, LogFilterDto? filter = null)
        {
            var response = await _esClient.SearchAsync<LogDocument>(s => s
                .Indices(_settings.IndexPattern)
                .Size(0)
                .Query(q => BuildQuery(q, filter ?? new LogFilterDto { Page = 1, PageSize = 1 }))
                .Aggregations(a =>
                {
                    for (var i = 0; i < fields.Length; i++)
                    {
                        var aggName = $"terms_{i}";
                        var field   = fields[i];
                        a.Add(aggName, agg => agg.Terms(t => t.Field(field).Size(100)));
                    }
                }));

            if (!response.IsValidResponse) return new List<string>();

            var values = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            for (var i = 0; i < fields.Length; i++)
            {
                var agg = response.Aggregations?.GetStringTerms($"terms_{i}");
                if (agg == null) continue;

                foreach (var bucket in agg.Buckets)
                {
                    var value = bucket.Key.ToString();
                    if (!string.IsNullOrWhiteSpace(value) &&
                        !value.Equals("beats_input_codec_plain_applied", StringComparison.OrdinalIgnoreCase))
                    {
                        values.Add(value);
                    }
                }
            }

            return values.OrderBy(x => x).ToList();
        }

        // ── Query Builder (single source of truth, used by both LogService and DashboardService) ──

        /// <summary>
        /// Builds the Elasticsearch bool/filter query from a <see cref="LogFilterDto"/>.
        /// This replaces the previously duplicated BuildQuery methods in LogService and DashboardService.
        /// </summary>
        internal static void BuildQuery(QueryDescriptor<LogDocument> q, LogFilterDto filter)
        {
            var filters = new List<Action<QueryDescriptor<LogDocument>>>
            {
                // Always filter to ERROR + FATAL (or specific severity if given)
                fq => fq.Bool(b => b
                    .Should(BuildSeverityClauses(filter.Severity).ToArray())
                    .MinimumShouldMatch(1))
            };

            if (filter.DateFrom.HasValue || filter.DateTo.HasValue)
            {
                filters.Add(fq => fq.Range(r => r.Date(dr =>
                {
                    dr.Field("@timestamp");
                    if (filter.DateFrom.HasValue)
                    {
                        dr.Gte(filter.DateFrom.Value.ToString("o"));
                    }
                    if (filter.DateTo.HasValue)
                    {
                        dr.Lte(filter.DateTo.Value.ToString("o"));
                    }
                })));
            }

            if (!string.IsNullOrWhiteSpace(filter.ApplicationName))
            {
                filters.Add(fq => fq.Bool(b => b
                    .Should(BuildExactOrTextClauses(ApplicationKeywordFields, filter.ApplicationName!).ToArray())
                    .MinimumShouldMatch(1)));
            }

            if (!string.IsNullOrWhiteSpace(filter.ServerName))
            {
                filters.Add(fq => fq.Bool(b => b
                    .Should(BuildExactOrTextClauses(ServerKeywordFields, filter.ServerName!).ToArray())
                    .MinimumShouldMatch(1)));
            }

            if (!string.IsNullOrWhiteSpace(filter.SearchText))
            {
                var search = filter.SearchText.Trim();
                var queryTerm = search.Contains('*') ? search : $"*{search}*";

                filters.Add(fq => fq.QueryString(qs => qs
                    .Fields(new[] { 
                        "message", "exception.type", "exception.stacktrace", "stack_trace",
                        "tags", "tags.keyword",
                        "fields.application", "fields.application.keyword",
                        "service.name", "service.name.keyword",
                        "application", "application.keyword",
                        "app", "app.keyword",
                        "app_name", "app_name.keyword",
                        "host.name", "host.name.keyword",
                        "hostname", "hostname.keyword",
                        "server", "server.keyword",
                        "server_name", "server_name.keyword",
                        "host_name", "host_name.keyword"
                    })
                    .Query(queryTerm)
                    .AnalyzeWildcard(true)));
            }

            q.Bool(b => b.Filter(filters.ToArray()));
        }

        private static List<Action<QueryDescriptor<LogDocument>>> BuildSeverityClauses(string? severity)
        {
            var normalized = severity?.Trim().ToUpperInvariant();
            var requested  = normalized is "ERROR" or "FATAL"
                ? new[] { normalized }
                : new[] { "ERROR", "FATAL" };

            var clauses = new List<Action<QueryDescriptor<LogDocument>>>();
            foreach (var value in requested)
            {
                foreach (var field in SeverityKeywordFields)
                    clauses.Add(q => q.Term(t => t.Field(field).Value(value)));

                clauses.Add(q => q.Match(m => m.Field("message").Query(value)));
            }

            return clauses;
        }

        private static List<Action<QueryDescriptor<LogDocument>>> BuildExactOrTextClauses(
            string[] keywordFields, string value)
        {
            var clauses = new List<Action<QueryDescriptor<LogDocument>>>();
            foreach (var field in keywordFields)
            {
                clauses.Add(q => q.Term(t => t.Field(field).Value(value)));

                if (field.EndsWith(".keyword", StringComparison.OrdinalIgnoreCase))
                    clauses.Add(q => q.Match(m => m.Field(field[..^8]).Query(value)));
            }

            return clauses;
        }

        /// <inheritdoc/>
        public async Task<List<LogDocument>> FetchContextAsync(
            string logFilePath,
            string serverName,
            DateTime timestamp,
            int windowSeconds = 5)
        {
            var from = timestamp.AddSeconds(-windowSeconds).ToString("o");
            var to   = timestamp.AddSeconds(+windowSeconds).ToString("o");

            var response = await _esClient.SearchAsync<LogDocument>(s => s
                .Indices(_settings.IndexPattern)
                .Size(50)
                .Sort(so => so.Field(f => f.Field("@timestamp").Order(SortOrder.Asc)))
                .Query(q => q.Bool(b =>
                {
                    var filters = new List<Action<QueryDescriptor<LogDocument>>>
                    {
                        // Time window
                        fq => fq.Range(r => r.Date(dr => dr
                            .Field("@timestamp")
                            .Gte(from)
                            .Lte(to)))
                    };

                    // Match by log file path (Filebeat convention)
                    if (!string.IsNullOrWhiteSpace(logFilePath))
                    {
                        filters.Add(fq => fq.Bool(inner => inner
                            .Should(
                                sq => sq.Term(t => t.Field("log.file.path.keyword").Value(logFilePath)),
                                sq => sq.Match(m => m.Field("log.file.path").Query(logFilePath))
                            )
                            .MinimumShouldMatch(1)));
                    }

                    // Match by server name when file path is absent
                    if (!string.IsNullOrWhiteSpace(serverName))
                    {
                        filters.Add(fq => fq.Bool(inner => inner
                            .Should(BuildExactOrTextClauses(ServerKeywordFields, serverName).ToArray())
                            .MinimumShouldMatch(1)));
                    }

                    b.Filter(filters.ToArray());
                })));

            if (!response.IsValidResponse)
            {
                _logger.LogWarning("Context fetch failed: {Error}",
                    response.ElasticsearchServerError?.Error?.Reason);
                return new List<LogDocument>();
            }

            var docs = new List<LogDocument>();
            foreach (var hit in response.Hits)
            {
                if (hit.Source != null)
                {
                    hit.Source.SourceIndex = hit.Index;
                    docs.Add(hit.Source);
                }
            }
            return docs;
        }
    }
}
