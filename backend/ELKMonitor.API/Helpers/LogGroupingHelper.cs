using ELKMonitor.API.Agents.DataFetchingAgent;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;

namespace ELKMonitor.API.Helpers
{
    public static class LogGroupingHelper
    {
        /// <summary>
        /// Groups split log lines (like stack traces or multiline messages) back into single cohesive LogDocuments.
        /// </summary>
        public static List<LogDocument> GroupLogDocuments(List<LogDocument> rawDocs)
        {
            if (rawDocs == null || rawDocs.Count == 0) return new List<LogDocument>();

            // Sort by LogFilePath, ServerName, then Timestamp ascending
            var sorted = rawDocs
                .OrderBy(d => d.LogFilePath ?? string.Empty)
                .ThenBy(d => d.ServerName ?? string.Empty)
                .ThenBy(d => d.Timestamp ?? DateTime.MinValue)
                .ToList();

            var grouped = new List<LogDocument>();
            LogDocument? currentHeader = null;

            foreach (var doc in sorted)
            {
                if (currentHeader == null)
                {
                    currentHeader = CloneDoc(doc);
                    grouped.Add(currentHeader);
                    continue;
                }

                // Check if this document belongs to the same log statement (source log file and server)
                bool isSameSource = (doc.LogFilePath == currentHeader.LogFilePath) 
                                 && (doc.ServerName == currentHeader.ServerName);
                
                // If it is the same source and occurs within 3 seconds of the header
                bool isCloseInTime = doc.Timestamp.HasValue && currentHeader.Timestamp.HasValue 
                                  && Math.Abs((doc.Timestamp.Value - currentHeader.Timestamp.Value).TotalSeconds) <= 3;

                // Check if this line is a continuation (not starting a new log header pattern)
                bool isContinuation = !IsLogHeader(doc.Message);

                if (isSameSource && isCloseInTime && isContinuation)
                {
                    var msg = doc.Message ?? string.Empty;
                    
                    // If it matches a stack trace frame pattern, append to stack trace field
                    if (IsStackTraceLine(msg))
                    {
                        currentHeader.StackTrace = string.IsNullOrEmpty(currentHeader.StackTrace) 
                            ? msg 
                            : currentHeader.StackTrace + "\n" + msg;
                    }
                    else
                    {
                        currentHeader.Message = string.IsNullOrEmpty(currentHeader.Message)
                            ? msg
                            : currentHeader.Message + "\n" + msg;
                    }

                    // Keep FATAL level if any fragmented line is flagged as FATAL
                    if (doc.Level == "FATAL" || doc.Severity == "FATAL")
                    {
                        currentHeader.Level = "FATAL";
                        currentHeader.Severity = "FATAL";
                    }
                }
                else
                {
                    // Start a new grouped log statement
                    currentHeader = CloneDoc(doc);
                    grouped.Add(currentHeader);
                }
            }

            // Return grouped documents ordered descending by timestamp (newest first for display)
            return grouped.OrderByDescending(d => d.Timestamp ?? DateTime.MinValue).ToList();
        }

        public static bool IsStackTraceLine(string? message)
        {
            if (string.IsNullOrEmpty(message)) return false;
            var trimmed = message.TrimStart();
            return trimmed.StartsWith("at ", StringComparison.OrdinalIgnoreCase)
                || trimmed.StartsWith("---", StringComparison.OrdinalIgnoreCase)
                || trimmed.StartsWith("...", StringComparison.OrdinalIgnoreCase)
                || trimmed.StartsWith("caused by:", StringComparison.OrdinalIgnoreCase)
                || trimmed.StartsWith("causado por:", StringComparison.OrdinalIgnoreCase)
                || trimmed.StartsWith("System.", StringComparison.OrdinalIgnoreCase)
                || trimmed.StartsWith("Microsoft.", StringComparison.OrdinalIgnoreCase);
        }

        public static bool IsLogHeader(string? message)
        {
            if (string.IsNullOrEmpty(message)) return false;
            var trimmed = message.Trim();
            
            // Matches yyyy-MM-dd HH:mm:ss timestamp pattern
            bool hasTimestamp = Regex.IsMatch(trimmed, @"^\d{4}-\d{2}-\d{2}\s+\d{2}:\d{2}:\d{2}");
            // Matches [thread] SEVERITY pattern
            bool hasThreadAndLevel = Regex.IsMatch(trimmed, @"\[\d+\]\s+(INFO|ERROR|WARN|FATAL|DEBUG)", RegexOptions.IgnoreCase);
            
            return (hasTimestamp || hasThreadAndLevel) && !IsStackTraceLine(message);
        }

        private static LogDocument CloneDoc(LogDocument doc)
        {
            return new LogDocument
            {
                Timestamp = doc.Timestamp,
                Message = doc.Message,
                Level = doc.Level,
                Severity = doc.Severity,
                Logger = doc.Logger,
                Thread = doc.Thread,
                ServerName = doc.ServerName,
                AppName = doc.AppName,
                EnvName = doc.EnvName,
                ExceptionClass = doc.ExceptionClass,
                StackTrace = doc.StackTrace,
                LogFilePath = doc.LogFilePath,
                SourceIndex = doc.SourceIndex
            };
        }
    }
}
