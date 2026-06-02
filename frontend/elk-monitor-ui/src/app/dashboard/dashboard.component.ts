import { Component, OnInit, OnDestroy } from '@angular/core';
import { CommonModule } from '@angular/common';
import { RouterLink } from '@angular/router';
import { forkJoin } from 'rxjs';
import { LogService, DashboardSummary, DashboardCharts, LogEntry } from '../services/log.service';
import { BaseChartDirective } from 'ng2-charts';
import { ChartConfiguration, ChartData, ChartType } from 'chart.js';
import { CHART_THEME } from '../charts/chart-theme';
import {
  Chart,
  ArcElement,
  DoughnutController,
  BarElement,
  BarController,
  CategoryScale,
  LinearScale,
  LineElement,
  LineController,
  PointElement,
  Tooltip,
  Legend,
  Filler
} from 'chart.js';

Chart.register(
  ArcElement, DoughnutController,
  BarElement, BarController,
  CategoryScale, LinearScale,
  LineElement, LineController,
  PointElement,
  Tooltip, Legend, Filler
);

@Component({
  selector: 'app-dashboard',
  standalone: true,
  imports: [CommonModule, RouterLink, BaseChartDirective],
  templateUrl: './dashboard.component.html',
  styleUrls: ['./dashboard.component.scss']
})
export class DashboardComponent implements OnInit {
  summary: DashboardSummary | null = null;
  charts: DashboardCharts | null = null;
  recentLogs: LogEntry[] = [];
  loading = true;
  error: string | null = null;
  lastRefresh = new Date();

  // ── Pipeline Visualizer State ──────────────────────────────────────────────
  selectedPipelineStep = 0;
  isPlayingPipeline = true;
  private pipelineInterval: any;

  pipelineSteps = [
    {
      title: 'Ingestion & Shipping',
      subtitle: 'Filebeat ──► Elasticsearch',
      badge: 'Real-time Ingestion',
      icon: '📦',
      desc: 'Application servers write logs to local files. Filebeat monitors these files and ships them line-by-line into Elasticsearch.',
      details: 'Since stack traces contain multiple lines, Filebeat splits them into independent log entries. Our backend subsequently reconstructs them.',
      location: 'Filebeat Config / Elasticsearch Index',
      locationLink: 'file:///d:/POC_OF_ELK/docker-compose.yml',
      inputLabel: 'Raw Log File (On App Server)',
      inputText: `2026-06-02 16:10:00 [ERROR] Connection timed out\n   at System.Net.Sockets.Socket.Connect()\n   at System.Data.SqlClient.SqlConnection.Open()`,
      outputLabel: 'Elasticsearch Shipped Rows (Line-by-Line)',
      outputText: `Doc 1: { "message": "2026-06-02 16:10:00 [ERROR] Connection timed out" }\nDoc 2: { "message": "   at System.Net.Sockets.Socket.Connect()" }\nDoc 3: { "message": "   at System.Data.SqlClient.SqlConnection.Open()" }`
    },
    {
      title: 'D-1 Fetch & Grouping',
      subtitle: 'C# Backend API Service',
      badge: 'LogService / LogGroupingHelper',
      icon: '⚙️',
      desc: 'The C# API service queries Elasticsearch for the D-1 window (yesterday\'s data) and groups adjacent stack trace lines back into unified errors.',
      details: 'LogGroupingHelper looks at timestamp proximity (within ±5 seconds), log file paths, and server names to merge split rows into single exception statements.',
      location: 'LogGroupingHelper.cs',
      locationLink: 'file:///d:/POC_OF_ELK/backend/ELKMonitor.API/Helpers/LogGroupingHelper.cs',
      inputLabel: 'Elasticsearch Raw Rows',
      inputText: `[\n  { "message": "2026-06-02 16:10:00 [ERROR] Connection timed out" },\n  { "message": "   at System.Net.Sockets.Socket.Connect()" }\n]`,
      outputLabel: 'Grouped In-Memory Log Statement',
      outputText: `{\n  "message": "Connection timed out",\n  "exceptionClass": "SqlException",\n  "stackTrace": "   at System.Net.Sockets.Socket.Connect()\\n   at ..."\n}`
    },
    {
      title: 'Normalization',
      subtitle: 'Python SignatureAgent',
      badge: 'Regex Normalizer',
      icon: '🧪',
      desc: 'The log message is normalized by stripping dynamic parameters (like GUIDs, numbers, timestamps, and quoted values) to produce a template.',
      details: 'This ensures that structurally identical errors can be identified and matched, even if they occurred with different user IDs, IDs, or IP addresses.',
      location: 'SignatureAgent (agents.py)',
      locationLink: 'file:///d:/POC_OF_ELK/categorizer/agents.py#L15-L40',
      inputLabel: 'Reconstructed Raw Error Message',
      inputText: '"Failed login for user admin_user_482 from IP 192.168.1.150"',
      outputLabel: 'Normalized Template & Error Signature Hash',
      outputText: `Normalized: "failed login for user {val} from ip {val}"\nMD5 Signature: "e99a8f4c2e6b7d8c1a0f5e3d7a8b9c2d"`
    },
    {
      title: 'Layered Caching',
      subtitle: 'SQLite & Vector Semantic Cache',
      badge: 'FastAPI Microservice',
      icon: '⚡',
      desc: 'The Python categorizer checks the SQLite cache for exact signature matches first, then checks a local Semantic cache (BGE vector model).',
      details: 'Using BAAI/bge-small-en-v1.5 vector embeddings, it runs a Cosine Similarity search. If a structurally similar error exists with similarity >= 0.88, it reuses the category in <5ms.',
      location: 'CacheAgent / SemanticCacheAgent (agents.py)',
      locationLink: 'file:///d:/POC_OF_ELK/categorizer/agents.py#L43-L210',
      inputLabel: 'New Error Signature Hash',
      inputText: 'Signature: "e99a8f4c2e6b7d8c1a0f5e3d7a8b9c2d"\nText: "timeout connecting to host db_replica_2"',
      outputLabel: 'Semantic Similarity Match (Cosine Score: 0.94)',
      outputText: `Match Found: "timeout connecting to host db_primary"\nResult: Category: "Database", Subcategory: "Connection Failure" [Cached]`
    },
    {
      title: 'GitLab Duo AI',
      subtitle: 'GraphQL & Action Cable Websocket',
      badge: 'Duo Chat Classifier fallback',
      icon: '🦊',
      desc: 'On a cache miss, the system sends the log to GitLab Duo Chat using GraphQL mutations and streams the response back via WebSockets.',
      details: 'The agent executes an aiAction mutation to request categorization, and streams tokens over wss://gitlab.com/cable. Once fullResponse is received, it caches the result.',
      location: 'DuoClassifierAgent (agents.py)',
      locationLink: 'file:///d:/POC_OF_ELK/categorizer/agents.py#L213-L493',
      inputLabel: 'Uncached Log details sent to Duo Chat',
      inputText: 'Prompt: "You are an expert log categorizer. Analyze: Critical memory leak in buffer pool: capacity 8192 exceeded limit..."',
      outputLabel: 'Asynchronous WebSockets JSON Response stream',
      outputText: `Tokens: "{" ... "category": "Infrastructure", "subcategory": "Memory Leak"}"\nResult Saved: SQLite exact cache & Semantic vector cache updated.`
    }
  ];


  // ── Doughnut Chart: Category Distribution ──────────────────────────────────
  doughnutData: ChartData<'doughnut'> = { labels: [], datasets: [] };
  doughnutOptions: ChartConfiguration<'doughnut'>['options'] = {
    responsive: true,
    maintainAspectRatio: false,
    plugins: {
      legend: { position: 'right', labels: { color: CHART_THEME.legend, font: { size: 12 }, padding: 16 } },
      tooltip: {
        callbacks: {
          label: (ctx) => {
            const index = ctx.dataIndex;
            const item = this.charts?.categoryDistribution[index];
            if (!item) return ` ${ctx.label}: ${ctx.formattedValue}%`;
            return ` ${item.category}: ${item.count} Total (${item.errorCount} Errors, ${item.fatalCount} Fatals) - ${item.percentage}%`;
          }
        }
      }
    },
    cutout: '65%'
  };

  // ── Bar Chart: Errors by Application ──────────────────────────────────────
  appBarData: ChartData<'bar'> = { labels: [], datasets: [] };
  appBarOptions: ChartConfiguration['options'] = {
    indexAxis: 'y',
    responsive: true,
    maintainAspectRatio: false,
    plugins: { legend: { labels: { color: '#cbd5e1' } } },
    scales: {
      x: { stacked: true, ticks: { color: CHART_THEME.text }, grid: { color: CHART_THEME.grid } },
      y: { stacked: true, ticks: { color: CHART_THEME.text }, grid: { display: false } }
    }
  };

  // ── Bar Chart: Errors by Server ────────────────────────────────────────────
  serverBarData: ChartData<'bar'> = { labels: [], datasets: [] };
  serverBarOptions: ChartConfiguration['options'] = {
    responsive: true,
    maintainAspectRatio: false,
    plugins: { legend: { labels: { color: '#cbd5e1' } } },
    scales: {
      x: { stacked: true, ticks: { color: CHART_THEME.text }, grid: { color: CHART_THEME.grid } },
      y: { stacked: true, ticks: { color: CHART_THEME.text }, grid: { color: CHART_THEME.grid } }
    }
  };

  // ── Line Chart: Timeline ───────────────────────────────────────────────────
  timelineData: ChartData<'line'> = { labels: [], datasets: [] };
  timelineOptions: ChartConfiguration['options'] = {
    responsive: true,
    maintainAspectRatio: false,
    interaction: { intersect: false, mode: 'index' },
    plugins: { legend: { labels: { color: '#cbd5e1' } } },
    scales: {
      x: { ticks: { color: CHART_THEME.text, maxTicksLimit: 12 }, grid: { color: CHART_THEME.grid } },
      y: { ticks: { color: CHART_THEME.text }, grid: { color: CHART_THEME.grid } }
    }
  };

  constructor(private logService: LogService) {}

  ngOnInit(): void {
    this.loadData();
    this.startPipelineAutoplay();
  }

  ngOnDestroy(): void {
    this.stopPipelineAutoplay();
  }

  startPipelineAutoplay(): void {
    this.stopPipelineAutoplay();
    this.pipelineInterval = setInterval(() => {
      if (this.isPlayingPipeline) {
        this.selectedPipelineStep = (this.selectedPipelineStep + 1) % this.pipelineSteps.length;
      }
    }, 4000); // cycle every 4 seconds
  }

  stopPipelineAutoplay(): void {
    if (this.pipelineInterval) {
      clearInterval(this.pipelineInterval);
      this.pipelineInterval = null;
    }
  }

  togglePipelinePlay(): void {
    this.isPlayingPipeline = !this.isPlayingPipeline;
    if (this.isPlayingPipeline) {
      this.startPipelineAutoplay();
    } else {
      this.stopPipelineAutoplay();
    }
  }

  selectStep(idx: number): void {
    this.selectedPipelineStep = idx;
    // Pause autoplay when user manually interacts/selects a step so they can read it
    this.isPlayingPipeline = false;
    this.stopPipelineAutoplay();
  }

  loadData(): void {
    this.loading = true;
    this.error = null;

    forkJoin({
      summary: this.logService.getDashboardSummary(),
      charts: this.logService.getDashboardCharts(),
      recent: this.logService.getLogs({ page: 1, pageSize: 8 })
    }).subscribe({
      next: ({ summary, charts, recent }) => {
        this.summary = summary;
        this.charts = charts;
        this.recentLogs = recent.items;
        this.buildCharts(charts);
        this.loading = false;
        this.lastRefresh = new Date();
      },
      error: () => {
        this.error = 'Failed to load dashboard data. Is the backend running and connected to Elasticsearch?';
        this.loading = false;
      }
    });
  }

  private buildCharts(charts: DashboardCharts): void {
    // Doughnut
    this.doughnutData = {
      labels: charts.categoryDistribution.map(c => c.category),
      datasets: [{
        data: charts.categoryDistribution.map(c => c.percentage),
        backgroundColor: charts.categoryDistribution.map(c => c.color),
        borderColor: 'transparent',
        hoverOffset: 8
      }]
    };

    // App bar
    const appLabels = charts.errorsByApplication.map(a => a.applicationName);
    this.appBarData = {
      labels: appLabels,
      datasets: [
        {
          label: 'ERROR',
          data: charts.errorsByApplication.map(a => a.errorCount),
          backgroundColor: 'rgba(239,68,68,0.8)',
          borderRadius: 4
        },
        {
          label: 'FATAL',
          data: charts.errorsByApplication.map(a => a.fatalCount),
          backgroundColor: 'rgba(168,85,247,0.8)',
          borderRadius: 4
        }
      ]
    };

    // Server bar
    this.serverBarData = {
      labels: charts.errorsByServer.map(s => s.serverName),
      datasets: [
        {
          label: 'ERROR',
          data: charts.errorsByServer.map(s => s.errorCount),
          backgroundColor: 'rgba(59,130,246,0.8)',
          borderRadius: 4
        },
        {
          label: 'FATAL',
          data: charts.errorsByServer.map(s => s.fatalCount),
          backgroundColor: 'rgba(234,179,8,0.8)',
          borderRadius: 4
        }
      ]
    };

    // Timeline
    const labels = charts.timeline.map(t => {
      const d = new Date(t.time);
      return `${d.getMonth()+1}/${d.getDate()} ${d.getHours()}:00`;
    });
    this.timelineData = {
      labels,
      datasets: [
        {
          label: 'ERROR',
          data: charts.timeline.map(t => t.errorCount),
          borderColor: CHART_THEME.error,
          backgroundColor: 'rgba(239,68,68,0.1)',
          fill: true,
          tension: 0.4,
          pointRadius: 3
        },
        {
          label: 'FATAL',
          data: charts.timeline.map(t => t.fatalCount),
          borderColor: CHART_THEME.fatal,
          backgroundColor: 'rgba(168,85,247,0.1)',
          fill: true,
          tension: 0.4,
          pointRadius: 3
        }
      ]
    };
  }

  getTrendIcon(): string {
    if (!this.summary) return '—';
    return this.summary.trendDirection === 'up' ? '↑' :
           this.summary.trendDirection === 'down' ? '↓' : '→';
  }

  getTrendClass(): string {
    if (!this.summary) return '';
    return this.summary.trendDirection === 'up' ? 'trend-up' :
           this.summary.trendDirection === 'down' ? 'trend-down' : 'trend-stable';
  }

  getSeverityClass(severity: string): string {
    return severity === 'FATAL' ? 'badge-fatal' : 'badge-error';
  }
}
