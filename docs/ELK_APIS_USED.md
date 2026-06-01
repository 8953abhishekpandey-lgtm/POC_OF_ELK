# Elasticsearch APIs Used By The POC

This file lists the Elasticsearch API operations used by the backend and where each one is implemented.

The backend uses the official .NET client:

```xml
<PackageReference Include="Elastic.Clients.Elasticsearch" Version="8.19.0" />
```

Implementation entry points:

```text
backend/ELKMonitor.API/Services/LogService.cs
backend/ELKMonitor.API/Dashboard/DashboardService.cs
backend/ELKMonitor.API/Helpers/MockDataSeeder.cs
backend/ELKMonitor.API/Elasticsearch/ElasticsearchClientFactory.cs
```

## 1. Search API - Fetch Logs

Client call:

```csharp
_esClient.SearchAsync<LogDocument>(...)
```

Used in:

```text
LogService.GetLogsAsync
DashboardService.FetchNormalizedLogsAsync
```

Purpose:

- Fetch paginated ERROR/FATAL logs.
- Apply filters for server, application, date range, severity, and search text.
- Sort by `@timestamp desc`.
- Deserialize mixed raw log formats into `LogDocument`.

Equivalent Elasticsearch API:

```http
POST /filebeat-*-error,logs-mock-*/_search
```

## 2. Count API - Dashboard Summary

Client call:

```csharp
_esClient.CountAsync<LogDocument>(...)
```

Used in:

```text
DashboardService.CountAsync
```

Purpose:

- Count ERROR logs.
- Count FATAL logs.
- Count logs in the last 24 hours.
- Count logs in the last 7 days.
- Count logs in the previous 7 days for trend analysis.

Equivalent Elasticsearch API:

```http
POST /filebeat-*-error,logs-mock-*/_count
```

## 3. Search API With Terms Aggregations - Dropdown Values

Client call:

```csharp
_esClient.SearchAsync<LogDocument>(s => s.Size(0).Aggregations(...))
```

Used in:

```text
LogService.GetApplicationNamesAsync
LogService.GetServerNamesAsync
```

Purpose:

- Populate application dropdown.
- Populate server dropdown.

Equivalent Elasticsearch API:

```http
POST /filebeat-*-error,logs-mock-*/_search
```

Implementation details:

- Aggregates over several possible app fields: `fields.application.keyword`, `service.name.keyword`, `application.keyword`, `app.keyword`, `app_name.keyword`, `tags.keyword`.
- Aggregates over several possible server fields: `host.name.keyword`, `hostname.keyword`, `server.keyword`, `server_name.keyword`, `host_name.keyword`.
- Merges returned buckets into one distinct list.

## 4. Bulk API - Mock Log Seeding

Client call:

```csharp
_esClient.BulkAsync(...)
```

Used in:

```text
MockDataSeeder.SeedAsync
```

Purpose:

- Insert sample ERROR/FATAL logs into `logs-mock-2024`.
- Runs on startup only when the mock index is empty.

Equivalent Elasticsearch API:

```http
POST /_bulk
```

## 5. Count API - Mock Seed Check

Client call:

```csharp
_esClient.CountAsync<object>(...)
```

Used in:

```text
MockDataSeeder.SeedAsync
```

Purpose:

- Check whether `logs-mock-2024` already contains documents.
- Avoid duplicating test logs on every restart.

Equivalent Elasticsearch API:

```http
GET /logs-mock-2024/_count
```

## Main Query Shape

The log fetch query is built as a boolean filter:

```json
{
  "from": 0,
  "size": 50,
  "sort": [
    { "@timestamp": { "order": "desc" } }
  ],
  "query": {
    "bool": {
      "filter": [
        {
          "bool": {
            "should": [
              { "terms": { "level.keyword": ["ERROR", "FATAL"] } },
              { "terms": { "severity.keyword": ["ERROR", "FATAL"] } },
              { "terms": { "log.level.keyword": ["ERROR", "FATAL"] } },
              { "match": { "message": "ERROR" } },
              { "match": { "message": "FATAL" } }
            ],
            "minimum_should_match": 1
          }
        }
      ]
    }
  }
}
```

## What The Backend Does After Fetching

The Elasticsearch APIs return raw log documents. The backend then:

1. Extracts `@timestamp`, host, application, severity, message, exception, stack trace, logger, thread, and environment.
2. Normalizes those fields into `NormalizedLog`.
3. Runs rule-based classification into Category A-F.
4. Creates a stable error signature.
5. Deduplicates dashboard top errors by that signature.
6. Computes frequency, first/last occurrence, duration, and D-1 delta.
