using ELKMonitor.API.Models;

namespace ELKMonitor.API.DTOs
{
    public class LogFilterDto
    {
        public string? ServerName { get; set; }
        public string? ApplicationName { get; set; }
        public DateTime? DateFrom { get; set; }
        public DateTime? DateTo { get; set; }
        public string? Severity { get; set; }  // ERROR, FATAL, or null for both
        public string? Category { get; set; }
        public string? ExceptionType { get; set; }
        public string? SearchText { get; set; }
        public int Page { get; set; } = 1;
        public int PageSize { get; set; } = 50;
    }

    public class LogResponseDto
    {
        public string Id { get; set; } = string.Empty;
        public DateTime Timestamp { get; set; }
        public string ServerName { get; set; } = string.Empty;
        public string ApplicationName { get; set; } = string.Empty;
        public string Severity { get; set; } = string.Empty;
        public string ErrorMessage { get; set; } = string.Empty;
        public string ExceptionType { get; set; } = string.Empty;
        public string StackTrace { get; set; } = string.Empty;
        public string CategoryCode { get; set; } = string.Empty;
        public string Category { get; set; } = string.Empty;
        public string CategoryColor { get; set; } = string.Empty;
        public string Subcategory { get; set; } = string.Empty;
        public string ErrorSignature { get; set; } = string.Empty;
        public string NormalizedMessage { get; set; } = string.Empty;
        public string Environment { get; set; } = string.Empty;
        public string Logger { get; set; } = string.Empty;
        /// <summary>Filebeat log.file.path — used to request adjacent context lines.</summary>
        public string LogFilePath { get; set; } = string.Empty;
    }

    /// <summary>One sibling log line returned by the context endpoint.</summary>
    public class LogContextLineDto
    {
        public DateTime Timestamp { get; set; }
        public string Message { get; set; } = string.Empty;
        public string Level { get; set; } = string.Empty;
    }

    public class PagedResult<T>
    {
        public List<T> Items { get; set; } = new();
        public long Total { get; set; }
        public int Page { get; set; }
        public int PageSize { get; set; }
        public int TotalPages => (int)Math.Ceiling((double)Total / PageSize);
    }

    public class DashboardSummaryDto
    {
        public long TotalErrors { get; set; }
        public long TotalFatals { get; set; }
        public long TotalLogs => TotalErrors + TotalFatals;
        public int TotalApplications { get; set; }
        public int TotalServers { get; set; }
        public long ErrorsLast24Hours { get; set; }
        public long ErrorsLast7Days { get; set; }
        public string TrendDirection { get; set; } = "stable"; // up, down, stable
    }

    public class CategoryDistributionDto
    {
        public string CategoryCode { get; set; } = string.Empty;
        public string Category { get; set; } = string.Empty;
        public long Count { get; set; }
        public long ErrorCount { get; set; }
        public long FatalCount { get; set; }
        public string Color { get; set; } = string.Empty;
        public double Percentage { get; set; }
    }

    public class ApplicationErrorCountDto
    {
        public string ApplicationName { get; set; } = string.Empty;
        public long ErrorCount { get; set; }
        public long FatalCount { get; set; }
        public long Total => ErrorCount + FatalCount;
    }

    public class ServerErrorCountDto
    {
        public string ServerName { get; set; } = string.Empty;
        public long ErrorCount { get; set; }
        public long FatalCount { get; set; }
        public long Total => ErrorCount + FatalCount;
    }

    public class TopErrorDto
    {
        public string ErrorSignature { get; set; } = string.Empty;
        public string ErrorMessage { get; set; } = string.Empty;
        public string ExceptionType { get; set; } = string.Empty;
        public string ApplicationName { get; set; } = string.Empty;
        public long Count { get; set; }
        public string CategoryCode { get; set; } = string.Empty;
        public string Category { get; set; } = string.Empty;
        public string Subcategory { get; set; } = string.Empty;
        public DateTime FirstOccurrence { get; set; }
        public DateTime LastOccurrence { get; set; }
        public long Last24HoursCount { get; set; }
        public long Previous24HoursCount { get; set; }
        public long D1Delta { get; set; }
        public double DurationMinutes { get; set; }
    }

    public class TimelineDataPointDto
    {
        public DateTime Time { get; set; }
        public long ErrorCount { get; set; }
        public long FatalCount { get; set; }
    }

    public class DashboardChartsDto
    {
        public List<CategoryDistributionDto> CategoryDistribution { get; set; } = new();
        public List<ApplicationErrorCountDto> ErrorsByApplication { get; set; } = new();
        public List<ServerErrorCountDto> ErrorsByServer { get; set; } = new();
        public List<TimelineDataPointDto> Timeline { get; set; } = new();
        public List<TopErrorDto> TopErrors { get; set; } = new();
    }
}
