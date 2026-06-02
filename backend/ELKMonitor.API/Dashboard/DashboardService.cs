using ELKMonitor.API.Agents.CategorizationAgent;
using ELKMonitor.API.Agents.DataFetchingAgent;
using ELKMonitor.API.Agents.SubCategorizationAgent;
using ELKMonitor.API.DTOs;
using ELKMonitor.API.Models;

namespace ELKMonitor.API.Dashboard
{
    public interface IDashboardService
    {
        Task<DashboardSummaryDto>  GetSummaryAsync(LogFilterDto?  filter = null);
        Task<DashboardChartsDto>   GetChartsDataAsync(LogFilterDto? filter = null);
    }

    /// <summary>
    /// Thin orchestrator that coordinates the 3-agent pipeline for dashboard data.
    ///
    /// Pipeline:
    ///   1. DataFetchingAgent   — count queries and bulk sample fetch from ES
    ///   2. CategorizationAgent — classify each sampled doc into Category (A–F)
    ///   3. SubCategorizationAgent — normalize + signature for top-errors grouping
    ///   → Aggregate into summary cards and chart DTOs
    ///
    /// This class contains NO query building, NO regex rules, NO JSON parsing.
    /// Zero code is duplicated from LogService — both share the same agents.
    /// </summary>
    public class DashboardService : IDashboardService
    {
        private const int SampleSize = 5000;

        private readonly IDataFetchingAgent       _dataAgent;
        private readonly ICategorizationAgent     _categorizationAgent;
        private readonly ISubCategorizationAgent  _subCategorizationAgent;
        private readonly DashboardWindowConfig    _windowConfig;
        private readonly ILogger<DashboardService> _logger;

        public DashboardService(
            IDataFetchingAgent dataAgent,
            ICategorizationAgent categorizationAgent,
            ISubCategorizationAgent subCategorizationAgent,
            DashboardWindowConfig windowConfig,
            ILogger<DashboardService> logger)
        {
            _dataAgent              = dataAgent;
            _categorizationAgent    = categorizationAgent;
            _subCategorizationAgent = subCategorizationAgent;
            _windowConfig           = windowConfig;
            _logger                 = logger;
        }

        public async Task<DashboardSummaryDto> GetSummaryAsync(LogFilterDto? filter = null)
        {
            try
            {
                var now = DateTime.UtcNow;
                filter ??= new LogFilterDto();
                if (filter.DateFrom == null && filter.DateTo == null)
                {
                    filter.DateFrom = now.AddDays(-_windowConfig.WindowDays);
                    filter.DateTo = now;
                }
                else if (filter.DateTo == null)
                {
                    filter.DateTo = now;
                }

                var f   = Eff(filter);

                // Fire all ES count queries in parallel
                var totalErrorsTask = _dataAgent.CountAsync(Eff(f, severity: "ERROR"));
                var totalFatalsTask = _dataAgent.CountAsync(Eff(f, severity: "FATAL"));
                var last24Task      = _dataAgent.CountAsync(Eff(f, dateFrom: now.AddDays(-1),  dateTo: now));
                var last2DaysTask   = _dataAgent.CountAsync(Eff(f, dateFrom: now.AddDays(-2),  dateTo: now));
                var sampleTask      = FetchNormalizedAsync(f, SampleSize);

                var distinctAppsTask = string.IsNullOrWhiteSpace(f.Category)
                    ? _dataAgent.GetDistinctTermsAsync(DataFetchingAgent.ApplicationKeywordFields, f)
                    : Task.FromResult(new List<string>());

                var distinctServersTask = string.IsNullOrWhiteSpace(f.Category)
                    ? _dataAgent.GetDistinctTermsAsync(DataFetchingAgent.ServerKeywordFields, f)
                    : Task.FromResult(new List<string>());

                await Task.WhenAll(totalErrorsTask, totalFatalsTask,
                                   last24Task, last2DaysTask, sampleTask,
                                   distinctAppsTask, distinctServersTask);

                var sample = ApplyCategoryFilter(await sampleTask, f.Category);
                var last24 = await last24Task;
                var last2Days = await last2DaysTask;
                var prev24 = Math.Max(0, last2Days - last24);

                // If a category is requested, recalculate counts from the in-memory sample
                if (!string.IsNullOrWhiteSpace(f.Category))
                {
                    var last24From = now.AddDays(-1);
                    var last2DaysFrom = now.AddDays(-2);

                    totalErrorsTask = Task.FromResult(sample.LongCount(l => l.Severity.Equals("ERROR", StringComparison.OrdinalIgnoreCase)));
                    totalFatalsTask = Task.FromResult(sample.LongCount(l => l.Severity.Equals("FATAL", StringComparison.OrdinalIgnoreCase)));
                    last24Task      = Task.FromResult(sample.LongCount(l => l.Timestamp >= last24From && l.Timestamp <= now));
                    last2DaysTask   = Task.FromResult(sample.LongCount(l => l.Timestamp >= last2DaysFrom && l.Timestamp <= now));
                    
                    last24 = await last24Task;
                    last2Days = await last2DaysTask;
                    prev24 = Math.Max(0, last2Days - last24);
                }

                return new DashboardSummaryDto
                {
                    TotalErrors       = await totalErrorsTask,
                    TotalFatals       = await totalFatalsTask,
                    TotalApplications = string.IsNullOrWhiteSpace(f.Category)
                        ? (await distinctAppsTask)
                            .Select(ToFriendlyApplicationName)
                            .Where(v => !string.IsNullOrWhiteSpace(v) && v != "Unknown")
                            .Distinct(StringComparer.OrdinalIgnoreCase)
                            .Count()
                        : sample
                            .Select(l => l.ApplicationName)
                            .Where(v => !string.IsNullOrWhiteSpace(v) && v != "Unknown")
                            .Distinct(StringComparer.OrdinalIgnoreCase)
                            .Count(),
                    TotalServers = string.IsNullOrWhiteSpace(f.Category)
                        ? (await distinctServersTask)
                            .Where(v => !string.IsNullOrWhiteSpace(v) && v != "Unknown")
                            .Distinct(StringComparer.OrdinalIgnoreCase)
                            .Count()
                        : sample
                            .Select(l => l.ServerName)
                            .Where(v => !string.IsNullOrWhiteSpace(v) && v != "Unknown")
                            .Distinct(StringComparer.OrdinalIgnoreCase)
                            .Count(),
                    ErrorsLast24Hours = last24,
                    ErrorsLast7Days   = last2Days,
                    TrendDirection    = GetTrendDirection(last24, prev24)
                };
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error computing dashboard summary");
                throw;
            }
        }

        public async Task<DashboardChartsDto> GetChartsDataAsync(LogFilterDto? filter = null)
        {
            try
            {
                var now = DateTime.UtcNow;
                filter ??= new LogFilterDto();
                if (filter.DateFrom == null && filter.DateTo == null)
                {
                    filter.DateFrom = now.AddDays(-_windowConfig.WindowDays);
                    filter.DateTo = now;
                }
                else if (filter.DateTo == null)
                {
                    filter.DateTo = now;
                }

                var f    = Eff(filter);
                var logs = ApplyCategoryFilter(await FetchNormalizedAsync(f, SampleSize), f.Category);

                return new DashboardChartsDto
                {
                    CategoryDistribution = BuildCategoryDistribution(logs),
                    ErrorsByApplication  = BuildApplicationDistribution(logs),
                    ErrorsByServer       = BuildServerDistribution(logs),
                    Timeline             = BuildTimeline(logs),
                    TopErrors            = BuildTopErrors(logs)
                };
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error computing dashboard charts");
                throw;
            }
        }

        // ── Private: agent pipeline invocation ───────────────────────────────

        private async Task<List<NormalizedLog>> FetchNormalizedAsync(LogFilterDto filter, int size)
        {
            var docs = await _dataAgent.FetchLogsAsync(filter, size);
            if (docs == null || docs.Count == 0) return new List<NormalizedLog>();

            var groupedDocs = Helpers.LogGroupingHelper.GroupLogDocuments(docs);

            var inputs = groupedDocs.Select(doc => {
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
            for (int i = 0; i < groupedDocs.Count; i++)
            {
                var doc = groupedDocs[i];
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
                    Thread            = doc.Thread  ?? string.Empty
                });
            }

            return normalizedLogs;
        }

        // ── Private: chart aggregation ────────────────────────────────────────

        private static List<CategoryDistributionDto> BuildCategoryDistribution(List<NormalizedLog> logs)
        {
            var grouped = logs
                .GroupBy(l => l.Category)
                .Select(g => new
                {
                    Category = g.Key,
                    Count = (long)g.Count(),
                    ErrorCount = g.LongCount(l => l.Severity.Equals("ERROR", StringComparison.OrdinalIgnoreCase)),
                    FatalCount = g.LongCount(l => l.Severity.Equals("FATAL", StringComparison.OrdinalIgnoreCase))
                })
                .OrderByDescending(x => x.Count)
                .ToList();

            var total = grouped.Sum(x => x.Count);
            var colors = new[] { "#6366f1", "#ef4444", "#3b82f6", "#a855f7", "#f97316", "#14b8a6", "#eab308", "#6b7280" };

            return grouped.Select((x, index) => new CategoryDistributionDto
            {
                CategoryCode = GenerateCategoryCode(x.Category),
                Category     = x.Category,
                Count        = x.Count,
                ErrorCount   = x.ErrorCount,
                FatalCount   = x.FatalCount,
                Color        = colors[index % colors.Length],
                Percentage   = total > 0 ? Math.Round((double)x.Count / total * 100, 1) : 0
            }).ToList();
        }

        private static string GenerateCategoryCode(string categoryName)
        {
            if (string.IsNullOrWhiteSpace(categoryName)) return "UNC";
            if (categoryName.Length <= 3) return categoryName.ToUpperInvariant();
            return categoryName[..3].ToUpperInvariant();
        }

        private static List<ApplicationErrorCountDto> BuildApplicationDistribution(List<NormalizedLog> logs)
            => logs
                .GroupBy(l => l.ApplicationName)
                .Select(g => new ApplicationErrorCountDto
                {
                    ApplicationName = g.Key,
                    ErrorCount      = g.LongCount(l => l.Severity.Equals("ERROR", StringComparison.OrdinalIgnoreCase)),
                    FatalCount      = g.LongCount(l => l.Severity.Equals("FATAL", StringComparison.OrdinalIgnoreCase))
                })
                .OrderByDescending(x => x.Total)
                .Take(20)
                .ToList();

        private static List<ServerErrorCountDto> BuildServerDistribution(List<NormalizedLog> logs)
            => logs
                .GroupBy(l => l.ServerName)
                .Select(g => new ServerErrorCountDto
                {
                    ServerName = g.Key,
                    ErrorCount = g.LongCount(l => l.Severity.Equals("ERROR", StringComparison.OrdinalIgnoreCase)),
                    FatalCount = g.LongCount(l => l.Severity.Equals("FATAL", StringComparison.OrdinalIgnoreCase))
                })
                .OrderByDescending(x => x.Total)
                .Take(20)
                .ToList();

        private static List<TimelineDataPointDto> BuildTimeline(List<NormalizedLog> logs)
            => logs
                .GroupBy(l => new DateTime(l.Timestamp.Year, l.Timestamp.Month, l.Timestamp.Day,
                                           l.Timestamp.Hour, 0, 0, DateTimeKind.Utc))
                .OrderBy(g => g.Key)
                .Select(g => new TimelineDataPointDto
                {
                    Time       = g.Key,
                    ErrorCount = g.LongCount(l => l.Severity.Equals("ERROR", StringComparison.OrdinalIgnoreCase)),
                    FatalCount = g.LongCount(l => l.Severity.Equals("FATAL", StringComparison.OrdinalIgnoreCase))
                })
                .ToList();

        private static List<TopErrorDto> BuildTopErrors(List<NormalizedLog> logs)
            => logs
                .Where(l => !string.IsNullOrWhiteSpace(l.ErrorSignature))
                .GroupBy(l => l.ErrorSignature)
                .Select(g =>
                {
                    var latest  = g.OrderByDescending(l => l.Timestamp).First();
                    var first   = g.OrderBy(l => l.Timestamp).First();
                    var now     = DateTime.UtcNow;
                    var last24  = g.LongCount(l => l.Timestamp >= now.AddDays(-1) && l.Timestamp <= now);
                    var prev24  = g.LongCount(l => l.Timestamp >= now.AddDays(-2) && l.Timestamp < now.AddDays(-1));

                    return new TopErrorDto
                    {
                        ErrorSignature       = g.Key,
                        ErrorMessage         = latest.ErrorMessage.Length > 200
                                               ? latest.ErrorMessage[..200] + "..."
                                               : latest.ErrorMessage,
                        ExceptionType        = latest.ExceptionType,
                        ApplicationName      = latest.ApplicationName,
                        Count                = g.LongCount(),
                        CategoryCode         = latest.CategoryCode,
                        Category             = latest.Category,
                        Subcategory          = latest.Subcategory,
                        FirstOccurrence      = first.Timestamp,
                        LastOccurrence       = latest.Timestamp,
                        Last24HoursCount     = last24,
                        Previous24HoursCount = prev24,
                        D1Delta              = last24 - prev24,
                        DurationMinutes      = Math.Round((latest.Timestamp - first.Timestamp).TotalMinutes, 1)
                    };
                })
                .OrderByDescending(x => x.Count)
                .ThenByDescending(x => x.LastOccurrence)
                .Take(10)
                .ToList();

        // ── Private: filter helpers ───────────────────────────────────────────

        private static List<NormalizedLog> ApplyCategoryFilter(List<NormalizedLog> logs, string? category)
            => string.IsNullOrWhiteSpace(category) ? logs
             : logs.Where(l => MatchesCategory(l, category)).ToList();

        private static bool MatchesCategory(NormalizedLog log, string category)
        {
            var expected = Norm(category);
            return Norm(log.CategoryCode).Equals(expected, StringComparison.OrdinalIgnoreCase)
                || Norm(log.Category).Equals(expected, StringComparison.OrdinalIgnoreCase);
        }

        private static string Norm(string v)
            => v.Replace(" ", "", StringComparison.OrdinalIgnoreCase)
               .Replace("-", "", StringComparison.OrdinalIgnoreCase)
               .Trim();

        /// <summary>Create an effective filter copy, defaulting nulls.</summary>
        private static LogFilterDto Eff(
            LogFilterDto? filter,
            string?   severity = null,
            DateTime? dateFrom = null,
            DateTime? dateTo   = null)
            => new()
            {
                ServerName      = filter?.ServerName,
                ApplicationName = filter?.ApplicationName,
                DateFrom        = dateFrom ?? filter?.DateFrom,
                DateTo          = dateTo   ?? filter?.DateTo,
                Severity        = severity ?? filter?.Severity,
                Category        = filter?.Category,
                SearchText      = filter?.SearchText,
                Page            = 1,
                PageSize        = SampleSize
            };

        private static string GetTrendDirection(long current, long previous)
        {
            if (previous == 0) return current > 0 ? "up" : "stable";
            return current > previous * 1.1 ? "up"
                 : current < previous * 0.9 ? "down"
                 : "stable";
        }

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
