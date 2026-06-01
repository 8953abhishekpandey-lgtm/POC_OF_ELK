using ELKMonitor.API.DTOs;
using ELKMonitor.API.Dashboard;
using Microsoft.AspNetCore.Mvc;

namespace ELKMonitor.API.Controllers
{
    /// <summary>
    /// API endpoints for dashboard data — summaries, charts, and top errors.
    /// GET /api/dashboard/summary    — metric summary cards
    /// GET /api/dashboard/charts     — all chart data in one call
    /// GET /api/dashboard/categories — category distribution only
    /// GET /api/dashboard/top-errors — most frequent errors
    /// </summary>
    [ApiController]
    [Route("api/[controller]")]
    [Produces("application/json")]
    public class DashboardController : ControllerBase
    {
        private readonly IDashboardService _dashboardService;
        private readonly ILogger<DashboardController> _logger;

        public DashboardController(IDashboardService dashboardService, ILogger<DashboardController> logger)
        {
            _dashboardService = dashboardService;
            _logger = logger;
        }

        /// <summary>
        /// Get dashboard summary cards (total errors, fatals, apps, servers, trends).
        /// </summary>
        [HttpGet("summary")]
        [ProducesResponseType(typeof(DashboardSummaryDto), StatusCodes.Status200OK)]
        public async Task<IActionResult> GetSummary([FromQuery] LogFilterDto? filter)
        {
            try
            {
                var summary = await _dashboardService.GetSummaryAsync(filter);
                return Ok(summary);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to get dashboard summary");
                return StatusCode(500, new { error = "Failed to fetch summary", detail = ex.Message });
            }
        }

        /// <summary>
        /// Get all chart data: category distribution, app errors, server errors, timeline, top errors.
        /// </summary>
        [HttpGet("charts")]
        [ProducesResponseType(typeof(DashboardChartsDto), StatusCodes.Status200OK)]
        public async Task<IActionResult> GetCharts([FromQuery] LogFilterDto? filter)
        {
            try
            {
                var charts = await _dashboardService.GetChartsDataAsync(filter);
                return Ok(charts);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to get dashboard charts");
                return StatusCode(500, new { error = "Failed to fetch chart data", detail = ex.Message });
            }
        }

        /// <summary>
        /// Get error category distribution only.
        /// </summary>
        [HttpGet("categories")]
        [ProducesResponseType(typeof(List<CategoryDistributionDto>), StatusCodes.Status200OK)]
        public async Task<IActionResult> GetCategories([FromQuery] LogFilterDto? filter)
        {
            try
            {
                var charts = await _dashboardService.GetChartsDataAsync(filter);
                return Ok(charts.CategoryDistribution);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to get category distribution");
                return StatusCode(500, new { error = "Failed to fetch categories", detail = ex.Message });
            }
        }

        /// <summary>
        /// Get top most-frequent error messages.
        /// </summary>
        [HttpGet("top-errors")]
        [ProducesResponseType(typeof(List<TopErrorDto>), StatusCodes.Status200OK)]
        public async Task<IActionResult> GetTopErrors([FromQuery] LogFilterDto? filter)
        {
            try
            {
                var charts = await _dashboardService.GetChartsDataAsync(filter);
                return Ok(charts.TopErrors);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to get top errors");
                return StatusCode(500, new { error = "Failed to fetch top errors", detail = ex.Message });
            }
        }

        /// <summary>
        /// Health check — verifies ES connectivity.
        /// </summary>
        [HttpGet("health")]
        public IActionResult Health() => Ok(new { status = "healthy", timestamp = DateTime.UtcNow });
    }
}
