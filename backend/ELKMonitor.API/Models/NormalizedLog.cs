namespace ELKMonitor.API.Models
{
    /// <summary>
    /// Unified log model — all raw ES formats are normalized into this structure.
    /// </summary>
    public class NormalizedLog
    {
        public string Id { get; set; } = string.Empty;
        public DateTime Timestamp { get; set; }
        public string ServerName { get; set; } = string.Empty;
        public string ApplicationName { get; set; } = string.Empty;
        public string Severity { get; set; } = string.Empty;
        public string ErrorMessage { get; set; } = string.Empty;
        public string ExceptionType { get; set; } = string.Empty;
        public string StackTrace { get; set; } = string.Empty;
        public string Category { get; set; } = "Unclassified";
        public string CategoryCode { get; set; } = string.Empty;
        public string Subcategory { get; set; } = string.Empty;
        public string ErrorSignature { get; set; } = string.Empty;
        public string NormalizedMessage { get; set; } = string.Empty;
        public string Environment { get; set; } = string.Empty;
        public string Thread { get; set; } = string.Empty;
        public string Logger { get; set; } = string.Empty;
        /// <summary>Filebeat log.file.path — used to query sibling lines for context.</summary>
        public string LogFilePath { get; set; } = string.Empty;
        /// <summary>The Elasticsearch index name where the log is stored.</summary>
        public string SourceIndex { get; set; } = string.Empty;
        public Dictionary<string, string> AdditionalFields { get; set; } = new();
    }
}
