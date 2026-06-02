using ELKMonitor.API.Agents.CategorizationAgent;
using ELKMonitor.API.Agents.DataFetchingAgent;
using ELKMonitor.API.Agents.SubCategorizationAgent;
using ELKMonitor.API.DTOs;
using ELKMonitor.API.Models;
using ELKMonitor.API.Helpers;

namespace ELKMonitor.API.Services
{
    public interface ILogService
    {
        Task<PagedResult<NormalizedLog>> GetLogsAsync(LogFilterDto filter);
        Task<List<string>> GetApplicationNamesAsync();
        Task<List<string>> GetServerNamesAsync();
        Task<List<string>> GetExceptionTypesAsync();
    }

    /// <summary>
    /// Thin orchestrator that coordinates the 3-agent pipeline for log retrieval.
    ///
    /// Pipeline:
    ///   1. DataFetchingAgent   — fetch raw LogDocument list from Elasticsearch
    ///   2. CategorizationAgent — classify each doc into a Category (A–F)
    ///   3. SubCategorizationAgent — normalize message + generate ErrorSignature
    ///   → Map to NormalizedLog and return paged result
    ///
    /// This class contains NO query building, NO JSON parsing, NO regex rules.
    /// Each of those concerns lives exclusively in its own agent.
    /// </summary>
    public class LogService : ILogService
    {
        private readonly IDataFetchingAgent        _dataAgent;
        private readonly ICategorizationAgent      _categorizationAgent;
        private readonly ISubCategorizationAgent   _subCategorizationAgent;
        private readonly ELKMonitor.API.Dashboard.DashboardWindowConfig _windowConfig;
        private readonly ILogger<LogService>       _logger;

        public LogService(
            IDataFetchingAgent dataAgent,
            ICategorizationAgent categorizationAgent,
            ISubCategorizationAgent subCategorizationAgent,
            ELKMonitor.API.Dashboard.DashboardWindowConfig windowConfig,
            ILogger<LogService> logger)
        {
            _dataAgent              = dataAgent;
            _categorizationAgent    = categorizationAgent;
            _subCategorizationAgent = subCategorizationAgent;
            _windowConfig           = windowConfig;
            _logger                 = logger;
        }

        public async Task<PagedResult<NormalizedLog>> GetLogsAsync(LogFilterDto filter)
        {
            try
            {
                filter.Page     = Math.Max(filter.Page, 1);
                filter.PageSize = Math.Clamp(filter.PageSize, 1, 200);

                var now = DateTime.UtcNow;
                if (filter.DateFrom == null && filter.DateTo == null)
                {
                    filter.DateFrom = now.AddDays(-_windowConfig.WindowDays);
                    filter.DateTo = now;
                }
                else if (filter.DateTo == null)
                {
                    filter.DateTo = now;
                }

                var hasCategoryFilter = !string.IsNullOrWhiteSpace(filter.Category);
                var hasExceptionTypeFilter = !string.IsNullOrWhiteSpace(filter.ExceptionType);

                // ── In-Memory filter & grouping path: time-window walking ───────────
                // Since we group split logs in-memory and match categories/exception types dynamically,
                // we walk backwards through time in batches to fetch, group, and paginate logs.
                const int BatchSize  = 1000;
                const int MaxBatches = 50;        // up to 50,000 docs scanned
                var needed           = filter.Page * filter.PageSize;
                var matched          = new List<NormalizedLog>();
                DateTime? windowEnd  = filter.DateTo;

                for (var batch = 0; batch < MaxBatches; batch++)
                {
                    var batchFilter = CloneFilter(filter, size: BatchSize, dateTo: windowEnd);
                    batchFilter.Category = null;
                    batchFilter.ExceptionType = null;

                    var docs = await _dataAgent.FetchLogsAsync(batchFilter, BatchSize);
                    if (docs.Count == 0) break;

                    // Group split log documents into single cohesive log statements
                    var groupedDocs = Helpers.LogGroupingHelper.GroupLogDocuments(docs);

                    var normalized = await MapToNormalizedLogsBulkAsync(groupedDocs);
                    
                    var hits = normalized.AsEnumerable();
                    if (hasCategoryFilter)
                    {
                        hits = hits.Where(log => MatchesCategory(log, filter.Category!));
                    }
                    if (hasExceptionTypeFilter)
                    {
                        hits = hits.Where(log => MatchesExceptionType(log, filter.ExceptionType!));
                    }
                    
                    matched.AddRange(hits);

                    // Advance window: next batch fetches docs OLDER than the oldest raw doc in this batch
                    var oldestTs = docs
                        .Select(d => d.Timestamp)
                        .Where(t => t.HasValue)
                        .Min();

                    if (!oldestTs.HasValue) break;

                    var nextEnd = oldestTs.Value.AddMilliseconds(-1);
                    if (windowEnd.HasValue && nextEnd >= windowEnd.Value) break;
                    windowEnd = nextEnd;

                    if (matched.Count >= needed * 3 || docs.Count < BatchSize) break;
                }

                // Ensure the list is sorted chronologically descending
                var orderedMatches = matched.OrderByDescending(l => l.Timestamp).ToList();

                var pageItems = orderedMatches
                    .Skip((filter.Page - 1) * filter.PageSize)
                    .Take(filter.PageSize)
                    .ToList();

                return new PagedResult<NormalizedLog>
                {
                    Items    = pageItems,
                    Total    = orderedMatches.Count,
                    Page     = filter.Page,
                    PageSize = filter.PageSize
                };
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error fetching logs");
                throw;
            }
        }

        public Task<List<string>> GetApplicationNamesAsync()
            => _dataAgent.GetDistinctTermsAsync(DataFetchingAgent.ApplicationKeywordFields);

        public Task<List<string>> GetServerNamesAsync()
            => _dataAgent.GetDistinctTermsAsync(DataFetchingAgent.ServerKeywordFields);

        public async Task<List<string>> GetExceptionTypesAsync()
        {
            var filter = new LogFilterDto { Page = 1, PageSize = 1000 };
            var docs = await _dataAgent.FetchLogsAsync(filter, 1000);
            
            // Group documents so exception classes are extracted from reconstructed fields
            var groupedDocs = Helpers.LogGroupingHelper.GroupLogDocuments(docs);
            
            var exceptionTypes = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            
            foreach (var doc in groupedDocs)
            {
                if (!string.IsNullOrEmpty(doc.ExceptionClass))
                {
                    exceptionTypes.Add(doc.ExceptionClass);
                }
            }
            
            var defaultTypes = new[]
            {
                "System.ArgumentException",
                "System.Data.SqlClient.SqlException",
                "System.NullReferenceException",
                "System.UnauthorizedAccessException",
                "System.TimeoutException",
                "System.WebException",
                "System.IO.FileNotFoundException",
                "Microsoft.EntityFrameworkCore.DbUpdateException"
            };
            
            foreach (var type in defaultTypes)
            {
                exceptionTypes.Add(type);
            }
            
            return exceptionTypes.OrderBy(x => x).ToList();
        }

        // ── Private helpers ───────────────────────────────────────────────────

        /// <summary>
        /// Map a list of raw LogDocuments through the dynamic categorizer in bulk.
        /// </summary>
        private async Task<List<NormalizedLog>> MapToNormalizedLogsBulkAsync(List<LogDocument> docs)
        {
            if (docs == null || docs.Count == 0) return new List<NormalizedLog>();

            var inputs = docs.Select(doc => {
                var appName = ToFriendlyApplicationName(doc.AppName ?? "Unknown");
                return new ClassifyLogInput
                {
                    Message = doc.Message ?? string.Empty,
                    ExceptionType = doc.ExceptionClass ?? string.Empty,
                    StackTrace = doc.StackTrace ?? string.Empty,
                    Severity = (doc.Level ?? doc.Severity ?? "ERROR").ToUpperInvariant(),
                    ApplicationName = appName
                };
            }).ToList();

            var classifications = await _categorizationAgent.ClassifyBulkAsync(inputs);

            var normalizedLogs = new List<NormalizedLog>();
            for (int i = 0; i < docs.Count; i++)
            {
                var doc = docs[i];
                var input = inputs[i];
                var (category, subcategory) = classifications[i];

                var classification = _subCategorizationAgent.Enrich(doc, category, subcategory, input.ApplicationName);

                normalizedLogs.Add(new NormalizedLog
                {
                    Id                = Guid.NewGuid().ToString(),
                    Timestamp         = doc.Timestamp ?? DateTime.UtcNow,
                    ServerName        = doc.ServerName ?? "Unknown",
                    ApplicationName   = input.ApplicationName,
                    Severity          = input.Severity,
                    ErrorMessage      = input.Message,
                    ExceptionType     = input.ExceptionType,
                    StackTrace        = input.StackTrace,
                    Category          = category,
                    CategoryCode      = classification.CategoryCode,
                    Subcategory       = classification.Subcategory,
                    ErrorSignature    = classification.ErrorSignature,
                    NormalizedMessage = classification.NormalizedMessage,
                    Environment       = doc.EnvName ?? "Unknown",
                    Logger            = doc.Logger  ?? string.Empty,
                    Thread            = doc.Thread  ?? string.Empty,
                    LogFilePath       = doc.LogFilePath ?? string.Empty,
                    SourceIndex       = doc.SourceIndex ?? string.Empty
                });
            }

            return normalizedLogs;
        }

        private static bool MatchesCategory(NormalizedLog log, string category)
        {
            var expected = Normalize(category);
            return Normalize(log.CategoryCode).Equals(expected, StringComparison.OrdinalIgnoreCase)
                || Normalize(log.Category).Equals(expected, StringComparison.OrdinalIgnoreCase);
        }

        private static bool MatchesExceptionType(NormalizedLog log, string exceptionType)
        {
            if (string.IsNullOrWhiteSpace(exceptionType)) return true;
            return log.ExceptionType.Equals(exceptionType, StringComparison.OrdinalIgnoreCase);
        }

        private static string Normalize(string value)
            => value.Replace(" ", "", StringComparison.OrdinalIgnoreCase)
                    .Replace("-", "", StringComparison.OrdinalIgnoreCase)
                    .Trim();

        private static LogFilterDto CloneFilter(
            LogFilterDto filter,
            int?      size    = null,
            DateTime? dateTo  = null)
            => new()
            {
                ServerName      = filter.ServerName,
                ApplicationName = filter.ApplicationName,
                DateFrom        = filter.DateFrom,
                DateTo          = dateTo ?? filter.DateTo,
                Severity        = filter.Severity,
                Category        = filter.Category,
                ExceptionType   = filter.ExceptionType,
                SearchText      = filter.SearchText,
                Page            = 1,
                PageSize        = size ?? filter.PageSize
            };

        /// <summary>Maps raw index-based app names to human-friendly display names.</summary>
        private static string ToFriendlyApplicationName(string rawAppName)
            => rawAppName switch
            {
                "dil_app_registerdata_log" => "DIL Register Data",
                "mdmdashboard_log"         => "MDM Dashboard",
                "bdm_app_log"              => "BDM App",
                "dil_app_snapshotdata_log" => "DIL Snapshot Data",
                "dil_app_eventdata_log"    => "DIL Event Data",
                "dil_app_intervaldata_log" => "DIL Interval Data",
                _ => System.Globalization.CultureInfo.InvariantCulture.TextInfo.ToTitleCase(
                         rawAppName.Replace("_log", "", StringComparison.OrdinalIgnoreCase)
                                   .Replace("_", " "))
            };
    }
}
