using System.Text.Json;
using System.Text.Json.Serialization;

namespace ELKMonitor.API.Agents.DataFetchingAgent
{
    /// <summary>
    /// Represents a raw Elasticsearch log document after parsing from the index.
    /// All fields are nullable — the flexible JSON converter handles the many
    /// different field-name conventions used across log shippers (Filebeat, Logstash, etc.).
    /// </summary>
    [JsonConverter(typeof(LogDocumentConverter))]
    public class LogDocument
    {
        public DateTime? Timestamp { get; set; }
        public string? Message { get; set; }
        public string? Level { get; set; }
        public string? Severity { get; set; }
        public string? Logger { get; set; }
        public string? Thread { get; set; }
        public string? ServerName { get; set; }
        public string? AppName { get; set; }
        public string? EnvName { get; set; }
        public string? ExceptionClass { get; set; }
        public string? StackTrace { get; set; }
        /// <summary>Original log.file.path from Filebeat — used for context fetching.</summary>
        public string? LogFilePath { get; set; }
    }

    /// <summary>
    /// Custom JSON converter for <see cref="LogDocument"/>.
    ///
    /// Why a custom converter?
    ///   Different log shippers write the same logical field under different names:
    ///   - Level  → "level", "severity", "log_level", "loglevel"
    ///   - Server → "host.name", "hostname", "server", "server_name"
    ///   - App    → "tags[]", "fields.application", "service.name", "app"
    ///
    /// This converter tries all known aliases and falls back gracefully.
    /// It also extracts thread, logger, exception class, and stack trace from
    /// the raw message string when structured fields are missing.
    /// </summary>
    public class LogDocumentConverter : JsonConverter<LogDocument>
    {
        public override LogDocument Read(
            ref Utf8JsonReader reader,
            Type typeToConvert,
            JsonSerializerOptions options)
        {
            if (reader.TokenType != JsonTokenType.StartObject)
                return new LogDocument();

            using var jsonDoc = JsonDocument.ParseValue(ref reader);
            var r = jsonDoc.RootElement;
            var doc = new LogDocument();

            // ── Timestamp ─────────────────────────────────────────────────────
            doc.Timestamp = GetStr(r, "@timestamp", "timestamp", "date") is string ts
                && DateTime.TryParse(ts, out var dt) ? dt : null;

            // ── Message ───────────────────────────────────────────────────────
            doc.Message = GetStr(r, "message", "msg", "log_message");

            // ── Level: try structured fields first, then sniff from message ──
            doc.Level = GetStr(r, "level", "severity", "log_level", "loglevel");
            if (string.IsNullOrEmpty(doc.Level) && !string.IsNullOrEmpty(doc.Message))
            {
                if (doc.Message.Contains(" FATAL ", StringComparison.OrdinalIgnoreCase))       doc.Level = "FATAL";
                else if (doc.Message.Contains(" ERROR ", StringComparison.OrdinalIgnoreCase)
                      || doc.Message.Contains("Exception:", StringComparison.OrdinalIgnoreCase)) doc.Level = "ERROR";
                else if (doc.Message.Contains(" WARN ", StringComparison.OrdinalIgnoreCase)
                      || doc.Message.Contains(" WARNING ", StringComparison.OrdinalIgnoreCase)) doc.Level = "WARN";
                else if (doc.Message.Contains(" INFO ", StringComparison.OrdinalIgnoreCase))    doc.Level = "INFO";
                else if (doc.Message.Contains(" DEBUG ", StringComparison.OrdinalIgnoreCase))   doc.Level = "DEBUG";
                else doc.Level = "ERROR";
            }
            else if (string.IsNullOrEmpty(doc.Level))
            {
                doc.Level = "ERROR";
            }

            // ── Thread & Logger: parse from message brackets when not structured ──
            if (!string.IsNullOrEmpty(doc.Message))
            {
                var openBracket  = doc.Message.IndexOf('[');
                var closeBracket = doc.Message.IndexOf(']');
                if (openBracket >= 0 && closeBracket > openBracket)
                {
                    doc.Thread = doc.Message.Substring(openBracket + 1, closeBracket - openBracket - 1);

                    var levelIndex = doc.Message.IndexOf(" " + doc.Level + " ", StringComparison.OrdinalIgnoreCase);
                    if (levelIndex < 0)
                    {
                        foreach (var lvl in new[] { "INFO", "ERROR", "FATAL", "WARN", "DEBUG" })
                        {
                            levelIndex = doc.Message.IndexOf(" " + lvl + " ", StringComparison.OrdinalIgnoreCase);
                            if (levelIndex >= 0) break;
                        }
                    }

                    if (levelIndex >= 0)
                    {
                        var afterLevel = doc.Message.Substring(levelIndex).TrimStart();
                        var spaceIndex = afterLevel.IndexOf(' ');
                        if (spaceIndex >= 0)
                        {
                            var loggerPart = afterLevel.Substring(spaceIndex).TrimStart();
                            var dashIndex  = loggerPart.IndexOf(" - ");
                            if (dashIndex >= 0)
                                doc.Logger = loggerPart.Substring(0, dashIndex).Trim();
                        }
                    }
                }

                // ── Exception class: scan for "SomeException:" lines ──────────
                if (doc.Message.Contains("Exception:", StringComparison.OrdinalIgnoreCase))
                {
                    var lines = doc.Message.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);
                    foreach (var line in lines)
                    {
                        if (!line.Contains("Exception:", StringComparison.OrdinalIgnoreCase)) continue;
                        var excIdx = line.IndexOf("Exception:", StringComparison.OrdinalIgnoreCase);
                        var start  = line.LastIndexOf(' ', excIdx);
                        if (start < 0) start = 0;
                        doc.ExceptionClass = line.Substring(start, excIdx + 9 - start).Trim();
                        break;
                    }
                }

                if (doc.Message.Contains("   at "))
                    doc.StackTrace = doc.Message;
            }

            // ── Fallback structured fields for Thread / Logger ──────────────
            doc.Logger ??= GetStr(r, "logger", "logger_name", "source_context");
            doc.Thread ??= GetStr(r, "thread", "thread_id", "thread_name");

            // ── Server name ───────────────────────────────────────────────────
            if (r.TryGetProperty("host", out var host) && host.TryGetProperty("name", out var hn))
                doc.ServerName = hn.GetString();
            else
                doc.ServerName = GetStr(r, "hostname", "server", "server_name", "host_name");

            // ── App name: tags array → tags string → file path → fields → service ──
            if (r.TryGetProperty("tags", out var tagsEl) && tagsEl.ValueKind == JsonValueKind.Array)
            {
                foreach (var tag in tagsEl.EnumerateArray())
                {
                    var tagStr = tag.GetString();
                    if (!string.IsNullOrEmpty(tagStr) && tagStr != "beats_input_codec_plain_applied")
                    {
                        doc.AppName = tagStr;
                        break;
                    }
                }
            }
            else if (r.TryGetProperty("tags", out var tagStringEl) && tagStringEl.ValueKind == JsonValueKind.String)
            {
                var tagStr = tagStringEl.GetString();
                if (!string.IsNullOrWhiteSpace(tagStr) &&
                    !tagStr.Equals("beats_input_codec_plain_applied", StringComparison.OrdinalIgnoreCase))
                    doc.AppName = tagStr;
            }

            // Try to infer app name from log.file.path (Filebeat convention)
            string? filePath = null;
            if (r.TryGetProperty("log", out var logProp)
             && logProp.TryGetProperty("file", out var fileProp)
             && fileProp.TryGetProperty("path", out var pathProp))
            {
                filePath = pathProp.GetString();
            }

            // Store the raw file path so the context endpoint can fetch sibling lines
            doc.LogFilePath = filePath;

            if (string.IsNullOrEmpty(doc.AppName) && !string.IsNullOrEmpty(filePath))
            {
                if      (filePath.Contains("\\BDM\\",          StringComparison.OrdinalIgnoreCase)) doc.AppName = "bdm_app_log";
                else if (filePath.Contains("\\MDMDashboard\\", StringComparison.OrdinalIgnoreCase)) doc.AppName = "mdmdashboard_log";
                else if (filePath.Contains("\\DIL\\",          StringComparison.OrdinalIgnoreCase))
                {
                    if      (filePath.Contains("REGISTER_DATA",  StringComparison.OrdinalIgnoreCase)) doc.AppName = "dil_app_registerdata_log";
                    else if (filePath.Contains("SNAPSHOT_DATA",  StringComparison.OrdinalIgnoreCase)) doc.AppName = "dil_app_snapshotdata_log";
                    else if (filePath.Contains("EVENT_DATA",     StringComparison.OrdinalIgnoreCase)) doc.AppName = "dil_app_eventdata_log";
                    else if (filePath.Contains("INTERVAL_DATA",  StringComparison.OrdinalIgnoreCase)) doc.AppName = "dil_app_intervaldata_log";
                    else doc.AppName = "dil_app_log";
                }
            }

            if (string.IsNullOrEmpty(doc.AppName))
            {
                if (r.TryGetProperty("fields", out var flds) && flds.TryGetProperty("application", out var appF))
                    doc.AppName = appF.GetString();
                else if (r.TryGetProperty("service", out var svc) && svc.TryGetProperty("name", out var svcN))
                    doc.AppName = svcN.GetString();
                else
                    doc.AppName = GetStr(r, "app", "application", "app_name");
            }

            // ── Environment ───────────────────────────────────────────────────
            if (r.TryGetProperty("fields", out var flds2) && flds2.TryGetProperty("environment", out var envF))
                doc.EnvName = envF.GetString();
            else
                doc.EnvName = GetStr(r, "environment", "env") ?? "Unknown";

            // ── Exception & StackTrace (structured fallbacks) ─────────────────
            if (string.IsNullOrEmpty(doc.ExceptionClass))
            {
                if (r.TryGetProperty("exception", out var exc))
                {
                    doc.ExceptionClass = GetStr(exc, "type", "class", "exception_type");
                    doc.StackTrace     = GetStr(exc, "stacktrace", "stack_trace", "trace");
                }
                else
                {
                    doc.ExceptionClass = GetStr(r, "exception_class", "exception_type");
                    doc.StackTrace     = GetStr(r, "stack_trace", "stacktrace");
                }
            }

            return doc;
        }

        private static string? GetStr(JsonElement el, params string[] keys)
        {
            foreach (var key in keys)
                if (el.TryGetProperty(key, out var p) && p.ValueKind == JsonValueKind.String)
                    return p.GetString();
            return null;
        }

        public override void Write(Utf8JsonWriter writer, LogDocument value, JsonSerializerOptions options)
            => JsonSerializer.Serialize(writer, value, options);
    }
}
