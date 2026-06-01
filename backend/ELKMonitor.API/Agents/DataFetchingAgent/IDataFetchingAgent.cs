using ELKMonitor.API.DTOs;

namespace ELKMonitor.API.Agents.DataFetchingAgent
{
    /// <summary>
    /// Agent 1 — Data Fetching Contract.
    ///
    /// Responsibilities:
    ///   • Query Elasticsearch with filters (date range, severity, app, server, search text)
    ///   • Parse raw JSON hits into strongly-typed <see cref="LogDocument"/> objects
    ///   • Count matching documents (for totals / trend calculations)
    ///   • Return distinct term lists for filter dropdowns
    ///
    /// This agent knows NOTHING about categories, subcategories, or business logic.
    /// It is purely concerned with fetching and parsing raw data from ES.
    /// </summary>
    public interface IDataFetchingAgent : IAgent
    {
        /// <summary>Fetch up to <paramref name="size"/> documents matching <paramref name="filter"/>.</summary>
        Task<List<LogDocument>> FetchLogsAsync(LogFilterDto filter, int size = 1000);

        /// <summary>Count documents matching <paramref name="filter"/> (fast ES count query).</summary>
        Task<long> CountAsync(LogFilterDto filter);

        /// <summary>Return distinct values for all provided <paramref name="keywordFields"/>, optionally matching <paramref name="filter"/>.</summary>
        Task<List<string>> GetDistinctTermsAsync(string[] keywordFields, LogFilterDto? filter = null);

        /// <summary>
        /// Fetch all documents from the same log file within a ±<paramref name="windowSeconds"/> time window.
        /// Used to reconstruct multi-line exceptions that Filebeat split into separate documents.
        /// </summary>
        Task<List<LogDocument>> FetchContextAsync(string logFilePath, string serverName, DateTime timestamp, int windowSeconds = 5);
    }
}
