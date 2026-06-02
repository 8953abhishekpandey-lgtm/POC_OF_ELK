namespace ELKMonitor.API.Dashboard
{
    /// <summary>
    /// Configuration class holding the default window (in days) to fetch logs for the dashboard.
    /// Default is 1 day (D-1).
    /// </summary>
    public class DashboardWindowConfig
    {
        public int WindowDays { get; set; } = 1;
    }
}
