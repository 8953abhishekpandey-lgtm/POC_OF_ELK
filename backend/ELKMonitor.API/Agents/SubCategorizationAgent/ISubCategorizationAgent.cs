using ELKMonitor.API.Agents.DataFetchingAgent;
using ELKMonitor.API.Models;

namespace ELKMonitor.API.Agents.SubCategorizationAgent
{
    /// <summary>
    /// The result produced by the SubCategorizationAgent.
    /// This is the fully enriched classification record for a single log entry.
    /// </summary>
    public sealed record ErrorClassification
    {
        public string Category               { get; init; } = "Unclassified";
        public string CategoryCode           => GenerateCategoryCode(Category);
        public string CategoryName           => Category;
        public string Subcategory            { get; init; } = "Unclassified";
        public string NormalizedMessage      { get; init; } = string.Empty;
        public string ErrorSignature         { get; init; } = string.Empty;

        private static string GenerateCategoryCode(string categoryName)
        {
            if (string.IsNullOrWhiteSpace(categoryName)) return "UNC";
            if (categoryName.Length <= 3) return categoryName.ToUpperInvariant();
            return categoryName[..3].ToUpperInvariant();
        }
    }

    /// <summary>
    /// Agent 3 — Sub-Categorization Contract.
    ///
    /// Responsibilities:
    ///   • Accept a parsed <see cref="LogDocument"/> and the Category already assigned by Agent 2
    ///   • Normalize the raw message (strip GUIDs, timestamps, numbers)
    ///   • Generate a deterministic ErrorSignature (SHA-256 hash)
    ///   • Return a fully enriched <see cref="ErrorClassification"/>
    ///
    /// This agent does NOT query Elasticsearch or apply category rules.
    /// Those concerns belong to Agents 1 and 2.
    /// </summary>
    public interface ISubCategorizationAgent : IAgent
    {
        /// <summary>
        /// Enrich a log document with subcategory, normalized message, and error signature.
        /// </summary>
        /// <param name="doc">The parsed raw document from Agent 1.</param>
        /// <param name="category">The top-level category assigned by Agent 2.</param>
        /// <param name="subcategory">The subcategory label assigned by Agent 2.</param>
        /// <param name="applicationName">The friendly application name (already resolved).</param>
        /// <returns>A fully populated <see cref="ErrorClassification"/>.</returns>
        ErrorClassification Enrich(
            LogDocument doc,
            string category,
            string subcategory,
            string applicationName);
    }
}
