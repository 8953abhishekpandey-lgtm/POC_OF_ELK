using ELKMonitor.API.Models;

namespace ELKMonitor.API.Agents.CategorizationAgent
{
    /// <summary>
    /// Agent 2 — Categorization Contract.
    ///
    /// Responsibilities:
    ///   • Accept the raw log fields (message, exception type, stack trace, severity)
    ///   • Apply priority-ordered rules or dynamic LLM matching to assign the log to ONE of the 6 error categories (A–F)
    ///   • Return the matched <see cref="ErrorCategory"/> and the matched subcategory label
    ///   • For FATAL logs: query GitLab Duo AI for a code-fix suggestion (on-demand for ERROR)
    /// </summary>
    public class ClassifyLogInput
    {
        public string Message { get; set; } = string.Empty;
        public string ExceptionType { get; set; } = string.Empty;
        public string StackTrace { get; set; } = string.Empty;
        public string Severity { get; set; } = string.Empty;
        public string ApplicationName { get; set; } = string.Empty;
    }

    public interface ICategorizationAgent : IAgent
    {
        /// <summary>
        /// Classify a log entry asynchronously by querying the dynamic microservice.
        /// </summary>
        Task<(string Category, string Subcategory)> ClassifyAsync(
            string message,
            string exceptionType,
            string stackTrace,
            string severity,
            string applicationName = "Unknown");

        /// <summary>
        /// Classify a list of log entries in a single bulk request.
        /// </summary>
        Task<List<(string Category, string Subcategory)>> ClassifyBulkAsync(List<ClassifyLogInput> logs);
    }
}

