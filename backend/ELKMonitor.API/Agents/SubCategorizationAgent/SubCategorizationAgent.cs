using ELKMonitor.API.Agents.DataFetchingAgent;
using ELKMonitor.API.Models;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace ELKMonitor.API.Agents.SubCategorizationAgent
{
    /// <summary>
    /// Agent 3 — Sub-Categorization Agent.
    ///
    /// Takes the raw <see cref="LogDocument"/> and the (Category, Subcategory) pair
    /// already assigned by Agent 2, and enriches it into a full <see cref="ErrorClassification"/>.
    ///
    /// Pipeline role:
    ///   DataFetchingAgent → CategorizationAgent → [SubCategorizationAgent]
    ///
    /// Input:  LogDocument + ErrorCategory + subcategoryLabel + applicationName
    /// Output: ErrorClassification (with NormalizedMessage + ErrorSignature)
    ///
    /// Enrichment steps:
    ///   1. NormalizeMessage — strips volatile tokens (GUIDs, timestamps, numbers, quoted values)
    ///      so that the same logical error produces the same normalized text regardless of IDs.
    ///   2. CreateSignature — SHA-256 hash of (category + subcategory + exceptionType + app + normalizedMsg)
    ///      gives a stable, short identifier for grouping recurring errors.
    /// </summary>
    public class SubCategorizationAgent : ISubCategorizationAgent
    {
        // Pre-compiled regex patterns for message normalization (significant perf gain vs inline)
        private static readonly Regex GuidPattern =
            new(@"[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}",
                RegexOptions.IgnoreCase | RegexOptions.Compiled);

        private static readonly Regex TimestampPattern =
            new(@"\b\d{4}-\d{2}-\d{2}[t\s]\d{2}:\d{2}:\d{2}(?:\.\d+)?z?\b",
                RegexOptions.IgnoreCase | RegexOptions.Compiled);

        private static readonly Regex NumberPattern =
            new(@"\b\d+\b", RegexOptions.Compiled);

        private static readonly Regex SingleQuotedPattern =
            new(@"'[^']*'", RegexOptions.Compiled);

        private static readonly Regex DoubleQuotedPattern =
            new(@"""[^""]*""", RegexOptions.Compiled);

        private static readonly Regex WhitespacePattern =
            new(@"\s+", RegexOptions.Compiled);

        /// <inheritdoc/>
        public ErrorClassification Enrich(
            LogDocument doc,
            string category,
            string subcategory,
            string applicationName)
        {
            var message         = doc.Message       ?? string.Empty;
            var exceptionType   = doc.ExceptionClass ?? string.Empty;

            var normalizedMessage = NormalizeMessage(message);
            var errorSignature    = CreateSignature(category, subcategory, normalizedMessage,
                                                    exceptionType, applicationName);

            return new ErrorClassification
            {
                Category         = category,
                Subcategory      = subcategory,
                NormalizedMessage = normalizedMessage,
                ErrorSignature    = errorSignature
            };
        }

        // ── Private enrichment steps ──────────────────────────────────────────

        /// <summary>
        /// Normalize a raw log message by stripping volatile tokens.
        ///
        /// Replacements applied (in order):
        ///   1. GUIDs              → {guid}
        ///   2. ISO-8601 timestamps → {timestamp}
        ///   3. Bare numbers        → {number}
        ///   4. Single-quoted values → '{value}'
        ///   5. Double-quoted values → "{value}"
        ///   6. Collapse whitespace
        ///   7. Truncate to 500 chars
        ///
        /// This makes "User 12345 not found" and "User 67890 not found"
        /// produce the same normalized message — critical for signature grouping.
        /// </summary>
        private static string NormalizeMessage(string message)
        {
            if (string.IsNullOrWhiteSpace(message)) return string.Empty;

            var n = message.ToLowerInvariant();
            n = GuidPattern.Replace(n, "{guid}");
            n = TimestampPattern.Replace(n, "{timestamp}");
            n = NumberPattern.Replace(n, "{number}");
            n = SingleQuotedPattern.Replace(n, "'{value}'");
            n = DoubleQuotedPattern.Replace(n, "\"{value}\"");
            n = WhitespacePattern.Replace(n, " ").Trim();

            return n.Length > 500 ? n[..500] : n;
        }

        /// <summary>
        /// Create a deterministic error signature using SHA-256.
        ///
        /// The signature encodes: category + subcategory + exceptionType + applicationName + normalizedMessage
        /// Result format: "{CategoryCode}-{First16HexCharsOfHash}"
        /// Example:       "B-A3F2C1D4E5B6A7F8"
        ///
        /// Two logs with the same logical root cause will produce the same signature,
        /// enabling reliable grouping and counting in the "Top Errors" dashboard view.
        /// </summary>
        private static string CreateSignature(
            string category,
            string subcategory,
            string normalizedMessage,
            string exceptionType,
            string applicationName)
        {
            var code = GenerateCategoryCode(category);
            var source = string.Join("|",
                code,
                subcategory.Trim().ToLowerInvariant(),
                exceptionType.Trim().ToLowerInvariant(),
                applicationName.Trim().ToLowerInvariant(),
                normalizedMessage);

            var hash = SHA256.HashData(Encoding.UTF8.GetBytes(source));
            return $"{code}-{Convert.ToHexString(hash)[..16]}";
        }

        private static string GenerateCategoryCode(string categoryName)
        {
            if (string.IsNullOrWhiteSpace(categoryName)) return "UNC";
            if (categoryName.Length <= 3) return categoryName.ToUpperInvariant();
            return categoryName[..3].ToUpperInvariant();
        }
    }
}
