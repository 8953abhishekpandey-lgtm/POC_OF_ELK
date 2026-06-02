import { Injectable } from '@angular/core';
import { HttpClient, HttpParams } from '@angular/common/http';
import { Observable } from 'rxjs';
import { environment } from '../../environments/environment';

export interface LogFilter {
  serverName?: string;
  applicationName?: string;
  dateFrom?: string;
  dateTo?: string;
  severity?: string;
  category?: string;
  exceptionType?: string;
  searchText?: string;
  page?: number;
  pageSize?: number;
}

export interface LogEntry {
  id: string;
  timestamp: string;
  serverName: string;
  applicationName: string;
  severity: string;
  errorMessage: string;
  exceptionType: string;
  stackTrace: string;
  categoryCode: string;
  category: string;
  categoryColor: string;
  subcategory: string;
  errorSignature: string;
  normalizedMessage: string;
  environment: string;
  logger: string;
  logFilePath: string;
  sourceIndex?: string;
}

export interface PagedResult<T> {
  items: T[];
  total: number;
  page: number;
  pageSize: number;
  totalPages: number;
}

export interface DashboardSummary {
  totalErrors: number;
  totalFatals: number;
  totalLogs: number;
  totalApplications: number;
  totalServers: number;
  errorsLast24Hours: number;
  errorsLast7Days: number;
  trendDirection: 'up' | 'down' | 'stable';
}

export interface CategoryDistribution {
  categoryCode: string;
  category: string;
  count: number;
  errorCount: number;
  fatalCount: number;
  color: string;
  percentage: number;
}

export interface ApplicationErrorCount {
  applicationName: string;
  errorCount: number;
  fatalCount: number;
  total: number;
}

export interface ServerErrorCount {
  serverName: string;
  errorCount: number;
  fatalCount: number;
  total: number;
}

export interface TopError {
  errorSignature: string;
  errorMessage: string;
  exceptionType: string;
  applicationName: string;
  count: number;
  categoryCode: string;
  category: string;
  subcategory: string;
  firstOccurrence: string;
  lastOccurrence: string;
  last24HoursCount: number;
  previous24HoursCount: number;
  d1Delta: number;
  durationMinutes: number;
}

export interface TimelineDataPoint {
  time: string;
  errorCount: number;
  fatalCount: number;
}

export interface DashboardCharts {
  categoryDistribution: CategoryDistribution[];
  errorsByApplication: ApplicationErrorCount[];
  errorsByServer: ServerErrorCount[];
  timeline: TimelineDataPoint[];
  topErrors: TopError[];
}

@Injectable({ providedIn: 'root' })
export class LogService {
  private readonly api = environment.apiUrl;

  constructor(private http: HttpClient) {}

  getLogs(filter: LogFilter = {}): Observable<PagedResult<LogEntry>> {
    let params = new HttpParams();
    if (filter.serverName) params = params.set('serverName', filter.serverName);
    if (filter.applicationName) params = params.set('applicationName', filter.applicationName);
    if (filter.dateFrom) {
      const d = new Date(filter.dateFrom);
      if (!isNaN(d.getTime())) {
        params = params.set('dateFrom', d.toISOString());
      }
    }
    if (filter.dateTo) {
      const d = new Date(filter.dateTo);
      if (!isNaN(d.getTime())) {
        params = params.set('dateTo', d.toISOString());
      }
    }
    if (filter.severity) params = params.set('severity', filter.severity);
    if (filter.category) params = params.set('category', filter.category);
    if (filter.exceptionType) params = params.set('exceptionType', filter.exceptionType);
    if (filter.searchText) params = params.set('searchText', filter.searchText);
    if (filter.page) params = params.set('page', filter.page.toString());
    if (filter.pageSize) params = params.set('pageSize', filter.pageSize.toString());
    return this.http.get<PagedResult<LogEntry>>(`${this.api}/logs`, { params });
  }

  getApplicationNames(): Observable<string[]> {
    return this.http.get<string[]>(`${this.api}/logs/apps`);
  }

  getServerNames(): Observable<string[]> {
    return this.http.get<string[]>(`${this.api}/logs/servers`);
  }

  getExceptionTypes(): Observable<string[]> {
    return this.http.get<string[]>(`${this.api}/logs/exception-types`);
  }

  getDashboardSummary(filter: LogFilter = {}): Observable<DashboardSummary> {
    let params = new HttpParams();
    if (filter.serverName) params = params.set('serverName', filter.serverName);
    if (filter.applicationName) params = params.set('applicationName', filter.applicationName);
    if (filter.dateFrom) {
      const d = new Date(filter.dateFrom);
      if (!isNaN(d.getTime())) params = params.set('dateFrom', d.toISOString());
    }
    if (filter.dateTo) {
      const d = new Date(filter.dateTo);
      if (!isNaN(d.getTime())) params = params.set('dateTo', d.toISOString());
    }
    if (filter.severity) params = params.set('severity', filter.severity);
    if (filter.category) params = params.set('category', filter.category);
    if (filter.exceptionType) params = params.set('exceptionType', filter.exceptionType);
    if (filter.searchText) params = params.set('searchText', filter.searchText);
    return this.http.get<DashboardSummary>(`${this.api}/dashboard/summary`, { params });
  }

  getDashboardCharts(filter: LogFilter = {}): Observable<DashboardCharts> {
    let params = new HttpParams();
    if (filter.serverName) params = params.set('serverName', filter.serverName);
    if (filter.applicationName) params = params.set('applicationName', filter.applicationName);
    if (filter.dateFrom) {
      const d = new Date(filter.dateFrom);
      if (!isNaN(d.getTime())) params = params.set('dateFrom', d.toISOString());
    }
    if (filter.dateTo) {
      const d = new Date(filter.dateTo);
      if (!isNaN(d.getTime())) params = params.set('dateTo', d.toISOString());
    }
    if (filter.severity) params = params.set('severity', filter.severity);
    if (filter.category) params = params.set('category', filter.category);
    if (filter.exceptionType) params = params.set('exceptionType', filter.exceptionType);
    if (filter.searchText) params = params.set('searchText', filter.searchText);
    return this.http.get<DashboardCharts>(`${this.api}/dashboard/charts`, { params });
  }

  getTopErrors(): Observable<TopError[]> {
    return this.http.get<TopError[]>(`${this.api}/dashboard/top-errors`);
  }

  /** Fetch the sibling log lines around a given timestamp from the same Filebeat file. */
  getLogContext(
    logFilePath: string,
    serverName: string,
    timestamp: string
  ): Observable<{ timestamp: string; message: string; level: string }[]> {
    let params = new HttpParams()
      .set('timestamp', new Date(timestamp).toISOString());
    if (logFilePath) params = params.set('logFilePath', logFilePath);
    if (serverName)  params = params.set('serverName', serverName);
    return this.http.get<{ timestamp: string; message: string; level: string }[]>(
      `${this.api}/logs/context`, { params }
    );
  }


}
