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
  allContextLines: { timestamp: string; message: string; level: string }[] = [];
  filterToExactError = true;
  contextLoading = false;
  contextError: string | null = null;
  showContext = false;

  // Root cause extracted from stack trace context lines
  rootCauseFrame: {
    namespace: string;
    className: string;
    method: string;
    sourceFile: string;
    lineNumber: string;
    fullFrame: string;
  } | null = null;

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
    const now = new Date();
    const yesterday = new Date(now.getTime() - 24 * 60 * 60 * 1000);
    this.filter.dateFrom = this.formatLocalDate(yesterday);

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
    const now = new Date();
    const yesterday = new Date(now.getTime() - 24 * 60 * 60 * 1000);
    this.filter = {
      page: 1,
      pageSize: 25,
      severity: '',
      exceptionType: '',
      dateFrom: this.formatLocalDate(yesterday)
    };
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
    this.showContext = true;
    this.contextLines = [];
    this.allContextLines = [];
    this.contextError = null;
    this.rootCauseFrame = null;
    this.fetchContext();
  }

  closeDetail(): void {
    this.selectedLog = null;
    this.showContext = false;
    this.contextLines = [];
    this.allContextLines = [];
    this.rootCauseFrame = null;
  }

  fetchContext(): void {
    if (!this.selectedLog) return;
    this.showContext = true;
    this.contextLoading = true;
    this.contextError = null;
    this.contextLines = [];
    this.allContextLines = [];

    this.logService.getLogContext(
      this.selectedLog.logFilePath || '',
      this.selectedLog.serverName  || '',
      this.selectedLog.timestamp
    ).subscribe({
      next: lines => {
        // Strip raw exception-header lines ("ExceptionType: message") from
        // context display — they duplicate what is already shown in Error Message.
        // We still pass them into the enrichment scan, just don't display them.
        const displayLines = lines.filter(l => {
          const m = (l.message || '').trim();
          // Suppress lines that are ONLY a "SomeException: some message" header
          // (no file/class/stack context, just the exception class name and message)
          if (/^[A-Za-z0-9\.]+Exception[^\n]{0,300}$/.test(m) && !m.startsWith('at ') && !m.includes('[')) {
            return false;
          }
          return true;
        });
        this.allContextLines = displayLines;
        this.contextLoading = false;
        if (lines.length === 0) {
          this.contextError = 'No adjacent log lines found in the same time window.';
        } else {
          this.enrichSelectedLogFromContext(lines); // scan ALL lines including exception headers
          this.applyContextFilter();
        }
      },
      error: () => {
        this.contextLoading = false;
        this.contextError = 'Failed to load context from server.';
      }
    });
  }

  applyContextFilter(): void {
    if (this.filterToExactError) {
      this.contextLines = this.filterContextLines(this.allContextLines);
    } else {
      this.contextLines = this.allContextLines;
    }
  }

  private filterContextLines(lines: any[]): any[] {
    if (!this.selectedLog || lines.length === 0) return lines;

    const targetMsg = this.selectedLog.errorMessage;
    let clickedIdx = lines.findIndex(l => l.message === targetMsg);
    
    if (clickedIdx === -1) {
      clickedIdx = lines.findIndex(l => targetMsg.includes(l.message));
    }

    if (clickedIdx === -1) return lines;

    const isNewLogHeader = (msg: string) => {
      if (!msg) return false;
      const hasLogPattern = /\d{4}-\d{2}-\d{2}\s+\d{2}:\d{2}:\d{2}/.test(msg) || /\[\d+\]\s+(INFO|ERROR|WARN|FATAL|DEBUG)/i.test(msg);
      const isStackTrace = msg.trim().startsWith('at ') || msg.trim().startsWith('---');
      return hasLogPattern && !isStackTrace;
    };

    let startIdx = clickedIdx;
    while (startIdx > 0) {
      const prevMsg = lines[startIdx].message || '';
      if (isNewLogHeader(prevMsg)) {
        break;
      }
      
      const prevLineMsg = lines[startIdx - 1].message || '';
      if (isNewLogHeader(prevLineMsg)) {
        startIdx--;
        break;
      }
      
      startIdx--;
    }

    let endIdx = clickedIdx;
    while (endIdx < lines.length - 1) {
      const nextMsg = lines[endIdx + 1].message || '';
      if (isNewLogHeader(nextMsg)) {
        break;
      }
      endIdx++;
    }

    return lines.slice(startIdx, endIdx + 1);
  }

  private enrichSelectedLogFromContext(lines: any[]): void {
    if (!this.selectedLog) return;

    // Scan context lines for the actual Exception Type.
    // Filebeat ships each log4net line separately, so exception class info
    // often lives in a sibling doc ("System.ArgumentException: Keyword not supported...").
    let detectedExceptionType = '';

    for (const line of lines) {
      const msg = (line.message || '').trim();
      // Match "Some.Namespace.ExceptionClass: message" or just "SomeException"
      const match = msg.match(/^([A-Za-z0-9\.]+Exception)(?::|\s)/);
      if (match) {
        detectedExceptionType = match[1];
        break;
      }
      // Also match inline "threw Some.ExceptionClass" patterns
      const inlineMatch = msg.match(/([A-Za-z0-9\.]+Exception)/);
      if (inlineMatch && msg.includes('Exception')) {
        detectedExceptionType = inlineMatch[1];
        // Don't break — prefer a line that starts with the exception class
      }
    }

    // Always enrich exception type if we found something from context
    if (detectedExceptionType) {
      this.selectedLog.exceptionType = detectedExceptionType;
    }

    // Reconstruct full message and stack trace from matched context group lines
    const groupLines = this.filterContextLines(lines);
    if (groupLines && groupLines.length > 0) {
      // Reconstruct detailed error message (lines that are NOT stack trace lines)
      const messageLines = groupLines
        .map(l => l.message || '')
        .filter(m => {
          const trimmed = m.trim();
          return !trimmed.startsWith('at ') && !trimmed.startsWith('---') && !trimmed.startsWith('\tat ');
        });

      if (messageLines.length > 0) {
        this.selectedLog.errorMessage = messageLines.join('\n');
      }

      // Reconstruct stack trace lines
      const stackLines = groupLines
        .map(l => l.message || '')
        .filter(m => {
          const trimmed = m.trim();
          return trimmed.startsWith('at ') || trimmed.startsWith('---') || trimmed.startsWith('\tat ');
        });

      if (stackLines.length > 0) {
        this.selectedLog.stackTrace = stackLines.join('\n');
      }
    }

    // ── Root Cause Extraction ──────────────────────────────────────────────
    // Scan context lines for stack frame pattern:
    //   "at Namespace.Class.Method(params) in File.cs:line N"
    // Skip system/framework frames, pick the FIRST user-code frame.
    const SYSTEM_PREFIXES = [
      'System.', 'Microsoft.', 'Newtonsoft.', 'Elastic.',
      'NpgsqlCommand', 'MySql.', 'Oracle.'
    ];

    // Regex: matches "   at Fully.Qualified.Method(params) in /path/to/File.cs:line 296"
    const frameRe = /at\s+([\w\.\+<>]+)\(([^)]*)\)(?:\s+in\s+(.+):line\s+(\d+))?/;

    for (const line of lines) {
      const raw = line.message || '';
      const trimmed = raw.trim();

      // Only look at lines that are stack trace frames
      if (!trimmed.startsWith('at ') && !trimmed.startsWith('   at ')) continue;

      const m = frameRe.exec(trimmed);
      if (!m) continue;

      const fullMethod = m[1]; // e.g. BillingDeterminant.DBContext.ClsDBMasters.GetMasterData
      const filePath   = m[3]; // e.g. C:\Users\...\ClsDBMasters.cs
      const lineNum    = m[4]; // e.g. 296

      // Skip framework/system frames unless there are no user frames at all
      const isSystem = SYSTEM_PREFIXES.some(p => fullMethod.startsWith(p));
      if (isSystem) continue;

      // Parse namespace, class, method from the full method name
      const parts = fullMethod.split('.');
      const method = parts.pop() || fullMethod;
      const className = parts.pop() || '';
      const namespace = parts.join('.');

      // Extract only the file name (not full path)
      let sourceFile = '';
      if (filePath) {
        const slashIdx = Math.max(filePath.lastIndexOf('\\'), filePath.lastIndexOf('/'));
        sourceFile = slashIdx >= 0 ? filePath.substring(slashIdx + 1) : filePath;
      }

      this.rootCauseFrame = {
        namespace,
        className,
        method,
        sourceFile,
        lineNumber: lineNum || '',
        fullFrame: trimmed
      };
      break; // First user-code frame is the root cause
    }
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
