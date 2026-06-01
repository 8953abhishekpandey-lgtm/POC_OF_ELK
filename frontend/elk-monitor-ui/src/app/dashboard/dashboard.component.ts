import { Component, OnInit } from '@angular/core';
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
