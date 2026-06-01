namespace ELKMonitor.API.Agents
{
    /// <summary>
    /// Marker interface for all pipeline agents.
    /// Each agent owns a single, well-defined stage of the log-processing pipeline:
    ///   1. DataFetchingAgent   — queries Elasticsearch and parses raw JSON
    ///   2. CategorizationAgent — maps a log to a top-level category (A–F)
    ///   3. SubCategorizationAgent — assigns subcategory, normalizes message, creates signature
    /// </summary>
    public interface IAgent { }
}
