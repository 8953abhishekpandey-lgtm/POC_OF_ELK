using ELKMonitor.API.Agents.DataFetchingAgent;
using ELKMonitor.API.Agents.CategorizationAgent;
using ELKMonitor.API.DTOs;
using ELKMonitor.API.Models;
using ELKMonitor.API.Services;
using Microsoft.AspNetCore.Mvc;
using ELKMonitor.API.Helpers;

namespace ELKMonitor.API.Controllers
{
    [ApiController]
    [Route("api/[controller]")]
    [Produces("application/json")]
    public class LogsController : ControllerBase
    {
        private readonly ILogService              _logService;
        private readonly IDataFetchingAgent       _dataAgent;
        private readonly ICategorizationAgent     _categorizationAgent;
        private readonly ILogger<LogsController>  _logger;

        public LogsController(
            ILogService logService,
            IDataFetchingAgent dataAgent,
            ICategorizationAgent categorizationAgent,
            ILogger<LogsController> logger)
        {
            _logService              = logService;
            _dataAgent               = dataAgent;
            _categorizationAgent     = categorizationAgent;
            _logger                  = logger;
        }

        /// <summary>Fetch ERROR and FATAL logs with optional filters.</summary>
        [HttpGet]
        [ProducesResponseType(typeof(PagedResult<LogResponseDto>), StatusCodes.Status200OK)]
        public async Task<IActionResult> GetLogs([FromQuery] LogFilterDto filter)
        {
            try
            {
                _logger.LogInformation("GetLogs called: App={App} Server={Srv} Cat={Cat} Exc={Exc} Search={Search}",
                    filter.ApplicationName, filter.ServerName, filter.Category, filter.ExceptionType, filter.SearchText);
                var result = await _logService.GetLogsAsync(filter);
                var dtos = result.Items.Select(log => new LogResponseDto
                {
                    Id                = log.Id,
                    Timestamp         = log.Timestamp,
                    ServerName        = log.ServerName,
                    ApplicationName   = log.ApplicationName,
                    Severity          = log.Severity,
                    ErrorMessage      = log.ErrorMessage,
                    ExceptionType     = log.ExceptionType,
                    StackTrace        = log.StackTrace,
                    CategoryCode      = log.CategoryCode,
                    Category          = log.Category,
                    CategoryColor     = GetCategoryColor(log.Category),
                    Subcategory       = log.Subcategory,
                    ErrorSignature    = log.ErrorSignature,
                    NormalizedMessage = log.NormalizedMessage,
                    Environment       = log.Environment,
                    Logger            = log.Logger,
                    LogFilePath       = log.LogFilePath,
                    SourceIndex       = log.SourceIndex
                }).ToList();

                return Ok(new PagedResult<LogResponseDto>
                {
                    Items    = dtos,
                    Total    = result.Total,
                    Page     = result.Page,
                    PageSize = result.PageSize
                });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to fetch logs");
                return StatusCode(500, new { error = "Failed to fetch logs", detail = ex.Message });
            }
        }

        /// <summary>Get distinct application names for filter dropdown.</summary>
        [HttpGet("apps")]
        public async Task<IActionResult> GetApplicationNames()
            => Ok(await _logService.GetApplicationNamesAsync());

        /// <summary>Get distinct server names for filter dropdown.</summary>
        [HttpGet("servers")]
        public async Task<IActionResult> GetServerNames()
            => Ok(await _logService.GetServerNamesAsync());

        /// <summary>Get distinct exception types for filter dropdown.</summary>
        [HttpGet("exception-types")]
        public async Task<IActionResult> GetExceptionTypes()
            => Ok(await _logService.GetExceptionTypesAsync());

        /// <summary>
        /// Return sibling log lines from the same Filebeat file within +-5 seconds.
        /// Reconstructs the full multi-line exception that Filebeat split into separate documents.
        /// </summary>
        [HttpGet("context")]
        [ProducesResponseType(typeof(List<LogContextLineDto>), StatusCodes.Status200OK)]
        public async Task<IActionResult> GetLogContext(
            [FromQuery] string? logFilePath,
            [FromQuery] string? serverName,
            [FromQuery] DateTime timestamp)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(logFilePath) && string.IsNullOrWhiteSpace(serverName))
                    return BadRequest(new { error = "Provide at least logFilePath or serverName." });

                var docs = await _dataAgent.FetchContextAsync(
                    logFilePath  ?? string.Empty,
                    serverName   ?? string.Empty,
                    timestamp);

                var lines = docs
                    .Where(d => !string.IsNullOrWhiteSpace(d.Message))
                    .Select(d => new LogContextLineDto
                    {
                        Timestamp = d.Timestamp ?? timestamp,
                        Message   = CleanLogMessage(d.Message!),
                        Level     = d.Level ?? d.Severity ?? "ERROR"
                    })
                    .ToList();

                return Ok(lines);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to fetch log context");
                return StatusCode(500, new { error = "Failed to fetch log context", detail = ex.Message });
            }
        }



        private static string CleanLogMessage(string raw)
        {
            if (string.IsNullOrEmpty(raw)) return raw;
            var dashIdx = raw.IndexOf(" - ");
            if (dashIdx >= 0 && dashIdx < 200)
            {
                var prefix = raw.Substring(0, dashIdx);
                if ((prefix.Contains('[') && prefix.Contains(']')) ||
                    prefix.Contains("ERROR") || prefix.Contains("FATAL") ||
                    prefix.Contains("INFO")  || prefix.Contains("WARN")  || prefix.Contains("DEBUG"))
                {
                    return raw.Substring(dashIdx + 3).TrimStart();
                }
            }
            return raw;
        }

        private static string GetCategoryColor(string category)
        {
            return (category?.ToLowerInvariant()) switch
            {
                "application"    => "#6366f1",
                "database"       => "#ef4444",
                "infrastructure" => "#3b82f6",
                "authentication" => "#a855f7",
                "integration"    => "#f97316",
                "validation"     => "#14b8a6",
                _                => "#6b7280"
            };
        }
    }
}
