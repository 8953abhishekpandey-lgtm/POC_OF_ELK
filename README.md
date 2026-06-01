# ELK Stack Centralized Error Monitor POC

A full-stack proof of concept for centralized **ERROR** and **FATAL** log monitoring using an existing ELK Stack. The backend fetches logs from Elasticsearch, normalizes mixed log formats, applies deterministic rule-based categorization, and serves dashboard APIs to an Angular UI.

This POC is intentionally not AI-based. Categorization uses auditable rules only: keywords, exception names, regex-capable rules, and severity.

## Current Target Environment

| Item | Value |
|---|---|
| Kibana | `http://10.0.0.28:5601` |
| Elasticsearch | `http://10.0.0.28:9200` |
| Username | `elastic` |
| Index pattern | `filebeat-*-error,logs-mock-*` |
| Backend | .NET 8 Web API |
| Frontend | Angular 21 + Chart.js/ng2-charts |
| Elasticsearch client | `Elastic.Clients.Elasticsearch` |

The Docker Compose file starts only the POC backend and frontend. It does not start Elasticsearch or Kibana because your ELK Stack is already running.

## Architecture

```text
D:\POC_OF_ELK
├── backend
│   ├── Dockerfile
│   └── ELKMonitor.API
│       ├── Controllers
│       ├── Services
│       ├── Models
│       ├── DTOs
│       ├── Elasticsearch
│       ├── Categorization
│       ├── Dashboard
│       └── Helpers
├── frontend
│   └── elk-monitor-ui
│       └── src/app
│           ├── dashboard
│           ├── logs
│           ├── services
│           └── shared
├── .env.example
└── docker-compose.yml
```

The category engine is behind `ICategorizationEngine`, so future AI-assisted analysis can be added later without changing controllers or dashboard API contracts.

## Quick Start With Docker

Create a `.env` file from `.env.example` and set the real Elasticsearch password:

```powershell
Copy-Item .env.example .env
```

Then start the POC:

```powershell
docker compose up -d --build
```

Open:

```text
Frontend dashboard: http://localhost:4200
Backend Swagger:    http://localhost:5000/swagger
Existing Kibana:    http://10.0.0.28:5601
```

## Manual Setup

Backend:

```powershell
cd D:\POC_OF_ELK\backend\ELKMonitor.API
dotnet restore
dotnet run --urls http://localhost:5000
```

Frontend:

```powershell
cd D:\POC_OF_ELK\frontend\elk-monitor-ui
npm install
npm run start
```

Open `http://localhost:4200`.

## Configuration

Backend configuration lives in `backend/ELKMonitor.API/appsettings.json` and can be overridden with environment variables:

```text
Elasticsearch__Url
Elasticsearch__Username
Elasticsearch__Password
Elasticsearch__CloudId
Elasticsearch__ApiKey
Elasticsearch__IndexPattern
Elasticsearch__VerifySsl
Elasticsearch__RequestTimeoutSeconds
```

Default index pattern:

```text
filebeat-*-error,logs-mock-*
```

This lets the app read both your Filebeat error indexes and the seeded mock index `logs-mock-2024`.

## API Endpoints

Fetch normalized logs:

```http
GET /api/logs?serverName=prod-server-01&applicationName=OrderService&severity=ERROR&page=1&pageSize=25
```

Dashboard summary:

```http
GET /api/dashboard/summary
```

Category distribution:

```http
GET /api/dashboard/categories
```

Top frequent errors:

```http
GET /api/dashboard/top-errors
```

Filter options:

```http
GET /api/logs/apps
GET /api/logs/servers
```

Example response from `/api/logs`:

```json
{
  "items": [
    {
      "timestamp": "2026-05-27T12:45:00Z",
      "serverName": "prod-server-01",
      "applicationName": "OrderService",
      "severity": "ERROR",
      "errorMessage": "SqlException: Cannot open database 'OrdersDB'.",
      "exceptionType": "System.Data.SqlClient.SqlException",
      "categoryCode": "B",
      "category": "Category B - Database Errors",
      "subcategory": "Connection String Issue",
      "errorSignature": "B-AC11314FBB91D9FA",
      "environment": "Production"
    }
  ],
  "total": 1,
  "page": 1,
  "pageSize": 25,
  "totalPages": 1
}
```

## Rule-Based Categories

Detailed category flow, subcategory rules, deduplication, signatures, and D-1 analysis are documented in:

```text
docs/CATEGORIZATION_FLOW.md
```

| Category | Example rules |
|---|---|
| Category A - Application Errors | `NullReferenceException`, `InvalidOperationException`, `KeyNotFoundException` |
| Category B - Database Errors | `SqlException`, `JDBC`, `DbContext`, `SQL Timeout`, `deadlock` |
| Category C - Infrastructure Errors | `Redis`, `SocketException`, `DNS`, `OutOfMemoryException`, `Docker` |
| Category D - Authentication Errors | `Unauthorized`, `401`, `403`, `JWT`, `token expired`, `access denied` |
| Category E - Integration Errors | `HttpRequestException`, `502`, `503`, `429`, `external API`, `webhook` |
| Category F - Validation Errors | `ArgumentNullException`, `FormatException`, `BadHttpRequestException`, `validation failed` |
| Unknown Error | Fallback |

Rules are implemented in:

```text
backend/ELKMonitor.API/Categorization/CategorizationEngine.cs
```

## Elasticsearch APIs Used

The Elasticsearch APIs/query patterns and exact code locations are documented separately in:

```text
docs/ELK_APIS_USED.md
```

## Dashboard UI

The Angular dashboard includes:

- Summary cards for total ERROR logs, total FATAL logs, total applications, total servers, last 24 hours, and 7-day trend.
- Charts for error count by application, category distribution, server distribution, and errors over time.
- Recent ERROR/FATAL logs table.
- Top frequent errors table.
- Log explorer with server, application, date range, severity, category, and text filters.
- Detail modal with message, exception type, stack trace, server, app, environment, and logger.

## Sample Elasticsearch Queries

Fetch only ERROR/FATAL logs:

```json
GET filebeat-*-error,logs-mock-*/_search
{
  "query": {
    "bool": {
      "filter": [
        {
          "bool": {
            "should": [
              { "terms": { "level.keyword": ["ERROR", "FATAL"] } },
              { "terms": { "severity.keyword": ["ERROR", "FATAL"] } },
              { "terms": { "log.level.keyword": ["ERROR", "FATAL"] } },
              { "match": { "message": "ERROR FATAL" } }
            ],
            "minimum_should_match": 1
          }
        },
        { "range": { "@timestamp": { "gte": "now-7d" } } }
      ]
    }
  },
  "sort": [{ "@timestamp": "desc" }],
  "size": 50
}
```

Count by application:

```json
GET filebeat-*-error,logs-mock-*/_search
{
  "size": 0,
  "aggs": {
    "by_application": {
      "terms": { "field": "fields.application.keyword", "size": 20 },
      "aggs": {
        "by_severity": {
          "terms": { "field": "level.keyword" }
        }
      }
    }
  }
}
```

Hourly timeline:

```json
GET filebeat-*-error,logs-mock-*/_search
{
  "size": 0,
  "aggs": {
    "timeline": {
      "date_histogram": {
        "field": "@timestamp",
        "calendar_interval": "hour"
      }
    }
  }
}
```

## Sample Test Logs

The backend seeds `logs-mock-2024` with 250 mock ERROR/FATAL logs on startup if that index is empty. Example document:

```json
{
  "@timestamp": "2026-05-27T12:30:00Z",
  "level": "ERROR",
  "message": "NullReferenceException: Object reference not set. 'customer' was null.",
  "exception": {
    "type": "System.NullReferenceException",
    "stacktrace": "at NotificationService.SendOrderConfirmation(Order order)"
  },
  "fields": {
    "application": "OrderService",
    "environment": "Production",
    "logger": "App.Services.OrderService"
  },
  "host": {
    "name": "prod-server-01"
  },
  "thread": "Thread-12"
}
```

## Future Scalability Hooks

The project is structured so these modules can be added later:

- AI-based error analysis by replacing or decorating `ICategorizationEngine`.
- GitLab integration through a future log enrichment service.
- SmartCodeAssist through repository/error mapping.
- Alert notifications through an `IAlertService`.
- Auto-fix suggestions through a separate analysis module.
- Repository mapping through source path and stack trace enrichment.

## Verification Commands

```powershell
dotnet build backend\ELKMonitor.API\ELKMonitor.API.csproj
cd frontend\elk-monitor-ui
npm run build
```
