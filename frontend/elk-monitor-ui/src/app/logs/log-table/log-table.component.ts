import { Component, OnInit } from '@angular/core';
import { CommonModule } from '@angular/common';
import { FormsModule } from '@angular/forms';
import { RouterLink } from '@angular/router';
import { LogService, LogEntry, LogFilter, PagedResult } from '../../services/log.service';
import { ERROR_CATEGORY_OPTIONS } from '../../shared/error-categories';

@Component({
  selector: 'app-log-table',
  standalone: true,
  imports: [CommonModule, FormsModule, RouterLink],
  templateUrl: './log-table.component.html',
  styleUrls: ['./log-table.component.scss']
})
export class LogTableComponent implements OnInit {
  logs: LogEntry[] = [];
  pagedResult: PagedResult<LogEntry> | null = null;
  loading = true;
  error: string | null = null;
  selectedLog: LogEntry | null = null;

  // Context viewer state
  contextLines: { timestamp: string; message: string; level: string }[] = [];
  contextLoading = false;
  contextError: string | null = null;
  showContext = false;

  filter: LogFilter = {
    page: 1,
    pageSize: 25,
    severity: '',
    exceptionType: ''
  };

  apps: string[] = [];
  servers: string[] = [];
  exceptionTypes: string[] = [];

  severities = ['', 'ERROR', 'FATAL'];
  categories: readonly string[] = ERROR_CATEGORY_OPTIONS;

  constructor(private logService: LogService) {}

  ngOnInit(): void {
    this.loadFilterOptions();
    this.loadLogs();
  }

  loadFilterOptions(): void {
    this.logService.getApplicationNames().subscribe(apps => this.apps = apps);
    this.logService.getServerNames().subscribe(servers => this.servers = servers);
    this.logService.getExceptionTypes().subscribe(types => this.exceptionTypes = types);
    this.logService.getDashboardCharts().subscribe(charts => {
      if (charts && charts.categoryDistribution) {
        const dynamicCats = charts.categoryDistribution.map(c => c.category);
        const uniqueCats = Array.from(new Set([...ERROR_CATEGORY_OPTIONS, ...dynamicCats])).filter(Boolean);
        this.categories = ['', ...uniqueCats];
      }
    });
  }

  loadLogs(): void {
    this.loading = true;
    this.error = null;
    this.logService.getLogs(this.filter).subscribe({
      next: result => {
        this.pagedResult = result;
        this.logs = result.items;
        this.loading = false;
      },
      error: err => {
        this.error = 'Failed to load logs from backend.';
        this.loading = false;
      }
    });
  }

  applyFilters(): void {
    this.filter.page = 1;
    this.loadLogs();
  }

  resetFilters(): void {
    this.filter = { page: 1, pageSize: 25, severity: '', exceptionType: '' };
    this.loadLogs();
  }

  setTimeRange(hoursOrDays: string): void {
    const now = new Date();
    let fromDate: Date;

    if (hoursOrDays === '1h') {
      fromDate = new Date(now.getTime() - 60 * 60 * 1000);
    } else if (hoursOrDays === '10h') {
      fromDate = new Date(now.getTime() - 10 * 60 * 60 * 1000);
    } else if (hoursOrDays === '24h') {
      fromDate = new Date(now.getTime() - 24 * 60 * 60 * 1000);
    } else if (hoursOrDays === '2d') {
      fromDate = new Date(now.getTime() - 2 * 24 * 60 * 60 * 1000);
    } else if (hoursOrDays === '7d') {
      fromDate = new Date(now.getTime() - 7 * 24 * 60 * 60 * 1000);
    } else {
      return;
    }

    this.filter.dateFrom = this.formatLocalDate(fromDate);
    this.filter.dateTo = this.formatLocalDate(now);
    this.applyFilters();
  }

  private formatLocalDate(date: Date): string {
    const pad = (num: number) => num.toString().padStart(2, '0');
    const yyyy = date.getFullYear();
    const MM = pad(date.getMonth() + 1);
    const dd = pad(date.getDate());
    const hh = pad(date.getHours());
    const mm = pad(date.getMinutes());
    return `${yyyy}-${MM}-${dd}T${hh}:${mm}`;
  }

  goToPage(page: number): void {
    if (this.pagedResult && page >= 1 && page <= this.pagedResult.totalPages) {
      this.filter.page = page;
      this.loadLogs();
    }
  }

  openDetail(log: LogEntry): void {
    this.selectedLog = log;
    this.showContext = false;
    this.contextLines = [];
    this.contextError = null;
  }

  closeDetail(): void {
    this.selectedLog = null;
    this.showContext = false;
    this.contextLines = [];
  }

  fetchContext(): void {
    if (!this.selectedLog) return;
    this.showContext = true;
    this.contextLoading = true;
    this.contextError = null;
    this.contextLines = [];

    this.logService.getLogContext(
      this.selectedLog.logFilePath || '',
      this.selectedLog.serverName  || '',
      this.selectedLog.timestamp
    ).subscribe({
      next: lines => {
        this.contextLines = lines;
        this.contextLoading = false;
        if (lines.length === 0) {
          this.contextError = 'No adjacent log lines found in the same time window.';
        }
      },
      error: () => {
        this.contextLoading = false;
        this.contextError = 'Failed to load context from server.';
      }
    });
  }

  toFriendlyAppName(rawName: string): string {
    if (!rawName) return '';
    const mappings: { [key: string]: string } = {
      'dil_app_registerdata_log': 'DIL Register Data',
      'mdmdashboard_log': 'MDM Dashboard',
      'bdm_app_log': 'BDM App',
      'dil_app_snapshotdata_log': 'DIL Snapshot Data',
      'dil_app_eventdata_log': 'DIL Event Data',
      'dil_app_intervaldata_log': 'DIL Interval Data'
    };
    if (mappings[rawName]) return mappings[rawName];
    return rawName
      .replace(/_log$/i, '')
      .replace(/_/g, ' ')
      .replace(/\b\w/g, c => c.toUpperCase());
  }

  getSeverityClass(severity: string): string {
    return severity === 'FATAL' ? 'badge-fatal' : 'badge-error';
  }

  getPages(): number[] {
    if (!this.pagedResult) return [];
    const total = this.pagedResult.totalPages;
    const current = this.filter.page ?? 1;
    const pages = [];
    const start = Math.max(1, current - 2);
    const end = Math.min(total, current + 2);
    for (let i = start; i <= end; i++) pages.push(i);
    return pages;
  }
}
