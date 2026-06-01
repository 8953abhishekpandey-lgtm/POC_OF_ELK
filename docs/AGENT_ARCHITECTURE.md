# ELK Monitor — Multi-Agent Architecture Guide

> **Last Updated:** 2026-05-29
> This document explains every agent in the log-processing pipeline:
> what it does, its interface, its full code structure, and how they wire together.

---

## Table of Contents

1. [Architecture Overview](#architecture-overview)
2. [Data Flow Diagram](#data-flow-diagram)
3. [Agent 1 — DataFetchingAgent](#agent-1--datafetchingagent)
4. [Agent 2 — CategorizationAgent](#agent-2--categorizationagent)
5. [Agent 3 — SubCategorizationAgent](#agent-3--subcategorizationagent)
6. [Orchestrators — LogService & DashboardService](#orchestrators)
7. [Dependency Injection Wiring](#dependency-injection-wiring)
8. [Clean Folder Structure](#clean-folder-structure)
9. [Adding a New Category / Rule](#adding-a-new-category--rule)

---

## Architecture Overview

The log pipeline is split into **3 dedicated agents**, each with a single, well-defined responsibility:

| # | Agent | Folder | Input | Output |
|---|---|---|---|---|
| 1 | **DataFetchingAgent** | `Agents/DataFetchingAgent/` | `LogFilterDto` | `List<LogDocument>` |
| 2 | **CategorizationAgent** | `Agents/CategorizationAgent/` | raw text fields | `(ErrorCategory, subcategoryLabel)` |
| 3 | **SubCategorizationAgent** | `Agents/SubCategorizationAgent/` | `LogDocument` + Category | `ErrorClassification` |

The `LogService` and `DashboardService` are **thin orchestrators** — they call the agents in sequence but contain no query logic, no rules, and no parsing code themselves.

---

## Data Flow Diagram

```
┌─────────────────────────────────────────────────────────────┐
│                    HTTP Request arrives                      │
│              GET /api/logs   or   GET /api/dashboard/*       │
└────────────────────────┬────────────────────────────────────┘
                         │
                         ▼
         ┌───────────────────────────────┐
         │  LogService / DashboardService │  ← Thin orchestrators
         │  (coordination only)           │
         └───────────┬───────────────────┘
                     │
                     ▼
    ╔═══════════════════════════════════════╗
    ║  AGENT 1 — DataFetchingAgent          ║
    ║                                       ║
    ║  • Builds Elasticsearch query         ║
    ║  • Handles date/app/server/severity   ║
    ║    filters                            ║
    ║  • Parses raw JSON → LogDocument      ║
    ║  • Handles multi-alias field names    ║
    ║    (tags, host.name, fields.app...)   ║
    ║                                       ║
    ║  Output: List<LogDocument>            ║
    ╚══════════════════╦════════════════════╝
                       ║  foreach doc
                       ▼
    ╔═══════════════════════════════════════╗
    ║  AGENT 2 — CategorizationAgent        ║
    ║                                       ║
    ║  • Combines message + exceptionType   ║
    ║    + stackTrace into one text         ║
    ║  • Applies priority-ordered rules     ║
    ║  • Each rule: keywords + regex        ║
    ║  • First matching rule wins           ║
    ║                                       ║
    ║  Categories: A(App) B(DB) C(Infra)    ║
    ║              D(Auth) E(Integration)   ║
    ║              F(Validation) U(Unknown) ║
    ║                                       ║
    ║  Output: (ErrorCategory, subcategory) ║
    ╚══════════════════╦════════════════════╝
                       ║
                       ▼
    ╔═══════════════════════════════════════╗
    ║  AGENT 3 — SubCategorizationAgent     ║
    ║                                       ║
    ║  • Normalizes raw message             ║
    ║    - strips GUIDs → {guid}            ║
    ║    - strips timestamps → {timestamp}  ║
    ║    - strips numbers → {number}        ║
    ║    - strips quoted values → {value}   ║
    ║  • Creates ErrorSignature             ║
    ║    SHA-256 of (cat+sub+ex+app+msg)    ║
    ║    → "B-A3F2C1D4E5B6A7F8"            ║
    ║                                       ║
    ║  Output: ErrorClassification          ║
    ╚══════════════════╦════════════════════╝
                       ║
                       ▼
         ┌─────────────────────────┐
         │    NormalizedLog         │
         │  (final response model) │
         └─────────────────────────┘
```

---

## Agent 1 — DataFetchingAgent

**File:** `Agents/DataFetchingAgent/DataFetchingAgent.cs`
**Interface:** `Agents/DataFetchingAgent/IDataFetchingAgent.cs`
**Supporting:** `Agents/DataFetchingAgent/LogDocument.cs`

### What It Does
This is the **only place in the codebase that talks to Elasticsearch**. It:
- Builds the ES `bool/filter` query from a `LogFilterDto`
- Handles multi-alias field names (different log shippers write the same field under different names)
- Parses raw Elasticsearch JSON hits into `LogDocument` objects using a custom `JsonConverter`
- Provides count queries (for trend/total calculations)
- Provides distinct-term aggregations (for filter dropdowns)

### Interface

```csharp
public interface IDataFetchingAgent : IAgent
{
    // Fetch up to `size` documents matching the filter
    Task<List<LogDocument>> FetchLogsAsync(LogFilterDto filter, int size = 1000);

    // Fast ES count query — no documents returned
    Task<long> CountAsync(LogFilterDto filter);

    // Aggregation for dropdown values (app names, server names)
    Task<List<string>> GetDistinctTermsAsync(string[] keywordFields);
}
```

### Key Implementation Details

**Query Builder** — `BuildQuery(QueryDescriptor<LogDocument> q, LogFilterDto filter)`
- Always filters to ERROR + FATAL severity (configurable per call)
- Applies date range, application, server, and full-text search filters
- Uses `bool/filter` for performance (no scoring needed)
- This method was previously duplicated in both `LogService` and `DashboardService` — now exists in ONE place only

**LogDocument + LogDocumentConverter**
- The `LogDocumentConverter` handles every known Elasticsearch field-name variation:

| Logical Field | Tried Keys (in order) |
|---|---|
| Timestamp | `@timestamp`, `timestamp`, `date` |
| Level | `level`, `severity`, `log_level`, `loglevel` → sniffed from message text |
| Server | `host.name` → `hostname`, `server`, `server_name`, `host_name` |
| App | `tags[]` → `tags (string)` → `log.file.path` → `fields.application` → `service.name` → `app` |
| Environment | `fields.environment` → `environment`, `env` |

---

## Agent 2 — CategorizationAgent

**File:** `Agents/CategorizationAgent/CategorizationAgent.cs`
**Interface:** `Agents/CategorizationAgent/ICategorizationAgent.cs`
**Supporting:** `Agents/CategorizationAgent/CategorizationRule.cs`

### What It Does
Applies **priority-ordered, deterministic classification rules** to assign each log entry to one of 6 error categories. No AI/ML — fully rule-based and testable.

### Interface

```csharp
public interface ICategorizationAgent : IAgent
{
    // Returns (Category, SubcategoryLabel) — (Unknown, "Unclassified") if no rule matches
    (ErrorCategory Category, string Subcategory) Classify(
        string message,
        string exceptionType,
        string stackTrace,
        string severity);
}
```

### How Rules Work

```csharp
// Rules are evaluated in ORDER. First match wins.
// Each CategorizationRule has:
//   - An ErrorCategory (A through F)
//   - A subcategory label string
//   - An array of keywords (case-insensitive Contains check)
//   - An optional array of compiled Regex patterns

// Example rule:
new(ErrorCategory.CategoryBDatabaseErrors, "SQL Timeout", new[]
{
    "sql timeout", "timeout expired", "sql command exceeded timeout", "query timeout"
})

// Example rule with regex for HTTP status codes:
new(ErrorCategory.CategoryDAuthenticationErrors, "Unauthorized Access", new[]
{
    "unauthorized", "unauthenticated", "unauthorizedaccessexception", "authentication failed"
}, new[] { @"(?:http|status|response|...)\s*:?\s*401\b" })
```

### Category Map

| Code | Category | Example Subcategories |
|---|---|---|
| **A** | Application Errors | Null Reference, Invalid Operation, Collection Error |
| **B** | Database Errors | SQL Timeout, Deadlock, Connection Failure, Constraint Violation |
| **C** | Infrastructure Errors | Redis Failure, Memory Pressure, Network Connectivity, Container/Deployment |
| **D** | Authentication Errors | Token Expired, Invalid Token, Unauthorized Access, Permission Denied |
| **E** | Integration Errors | Bad Gateway, HTTP Failure, Rate Limited, Serialization Failure |
| **F** | Validation Errors | Missing Required Value, Invalid Format, Invalid Request Body |
| **U** | Unknown | Unclassified |

### Why Rules Are Priority-Ordered
More **specific** rules come first, more **general** ones last. For example:
- `"SQL Timeout"` rule (specific) comes before `"Database Connection Failure"` (broad)
- `"General Application Exception"` (catches any `"exception"` keyword) is the very last rule

---

## Agent 3 — SubCategorizationAgent

**File:** `Agents/SubCategorizationAgent/SubCategorizationAgent.cs`
**Interface:** `Agents/SubCategorizationAgent/ISubCategorizationAgent.cs`

### What It Does
Takes the `LogDocument` + the `(Category, Subcategory)` from Agent 2 and produces the final `ErrorClassification` by:
1. **Normalizing** the raw message (removes volatile tokens so the same logical error always produces the same text)
2. **Generating a deterministic ErrorSignature** (SHA-256 hash for grouping recurring errors)

### Interface

```csharp
public interface ISubCategorizationAgent : IAgent
{
    ErrorClassification Enrich(
        LogDocument doc,
        ErrorCategory category,
        string subcategory,
        string applicationName);
}
```

### Message Normalization (Step 1)

```
Input:  "User 12345 not found in database 'OrdersDB' at 2024-01-15T10:30:00Z"
                                                                              ↓
Step 1: lowercase
        "user 12345 not found in database 'ordersdb' at 2024-01-15t10:30:00z"
Step 2: strip GUIDs → {guid}           (no GUIDs here)
Step 3: strip timestamps → {timestamp} "user 12345 not found in database 'ordersdb' at {timestamp}"
Step 4: strip numbers → {number}       "user {number} not found in database 'ordersdb' at {timestamp}"
Step 5: strip quoted → '{value}'       "user {number} not found in database '{value}' at {timestamp}"
Step 6: collapse whitespace + trim
Output: "user {number} not found in database '{value}' at {timestamp}"
```

Same output for `User 67890 not found in database 'PaymentsDB'` → enables grouping.

### Error Signature (Step 2)

```
Source: "{category_code}|{subcategory}|{exception_type}|{app_name}|{normalized_message}"
Hash:   SHA-256 of above string
Result: "B-A3F2C1D4E5B6A7F8"  (category code + first 16 hex chars of hash)
```

Two logs with the same root cause always produce the same signature → reliable "Top Errors" grouping.

### Output: `ErrorClassification` record

```csharp
public sealed record ErrorClassification
{
    public ErrorCategory Category      { get; init; }
    public string CategoryCode         => Category.ToCode();       // "A", "B", ..., "U"
    public string CategoryName         => Category.ToDisplayName(); // "Category B - Database Errors"
    public string Subcategory          { get; init; }              // "SQL Timeout"
    public string NormalizedMessage    { get; init; }              // cleaned message text
    public string ErrorSignature       { get; init; }              // "B-A3F2C1D4..."
}
```

---

## Orchestrators

### LogService (`Services/LogService.cs`)

Handles log retrieval with two pagination strategies:

```
No category filter  →  Simple fetch (DataFetchingAgent.FetchLogsAsync)
Category filter     →  Time-window walking (batches of 1000, backwards in time)
                        because category is assigned in-memory, not filterable in ES
```

### DashboardService (`Dashboard/DashboardService.cs`)

Fires **parallel ES queries** for maximum performance:
```csharp
// All 6 queries run simultaneously:
var totalErrorsTask = _dataAgent.CountAsync(errorFilter);
var totalFatalsTask = _dataAgent.CountAsync(fatalFilter);
var last24Task      = _dataAgent.CountAsync(last24hFilter);
var last7Task       = _dataAgent.CountAsync(last7dFilter);
var prev7Task       = _dataAgent.CountAsync(prev7dFilter);
var sampleTask      = FetchNormalizedAsync(filter, 5000);
await Task.WhenAll(...all 6...);
```

---

## Dependency Injection Wiring

**File:** `Program.cs`

```csharp
// ── Infrastructure ─────────────────────────────────────────────
builder.Services.AddSingleton(esSettings);
builder.Services.AddSingleton<ElasticsearchClient>(...);

// ── 3-Agent Pipeline ───────────────────────────────────────────
// Singleton: agents are stateless — safe to share across requests
builder.Services.AddSingleton<IDataFetchingAgent, DataFetchingAgent>();
builder.Services.AddSingleton<ICategorizationAgent, CategorizationAgent>();
builder.Services.AddSingleton<ISubCategorizationAgent, SubCategorizationAgent>();

// ── Orchestrators (Scoped — one per HTTP request) ──────────────
builder.Services.AddScoped<ILogService, LogService>();
builder.Services.AddScoped<IDashboardService, DashboardService>();
```

**Why Singleton for agents?**
All three agents are stateless — they hold no mutable per-request data. Rules are built once in the constructor and reused. This is safe and avoids repeated allocation.

---

## Clean Folder Structure

```
backend/ELKMonitor.API/
│
├── Agents/                                ← NEW: all pipeline agents
│   ├── IAgent.cs                          ← marker interface
│   │
│   ├── DataFetchingAgent/                 ← Agent 1
│   │   ├── IDataFetchingAgent.cs
│   │   ├── DataFetchingAgent.cs
│   │   └── LogDocument.cs                 ← raw ES document + JSON converter
│   │
│   ├── CategorizationAgent/               ← Agent 2
│   │   ├── ICategorizationAgent.cs
│   │   ├── CategorizationAgent.cs
│   │   └── CategorizationRule.cs
│   │
│   └── SubCategorizationAgent/            ← Agent 3
│       ├── ISubCategorizationAgent.cs
│       └── SubCategorizationAgent.cs
│
├── Controllers/
│   ├── LogsController.cs
│   └── DashboardController.cs
│
├── Dashboard/
│   └── DashboardService.cs                ← thin orchestrator (was 442 lines)
│
├── DTOs/
│   └── LogDtos.cs
│
├── Elasticsearch/
│   ├── ElasticsearchClientFactory.cs
│   └── ElasticsearchSettings.cs
│
├── Helpers/
│   └── MockDataSeeder.cs
│
├── Models/
│   ├── ErrorCategory.cs
│   └── NormalizedLog.cs
│
├── Services/
│   └── LogService.cs                      ← thin orchestrator (was 557 lines)
│
└── Program.cs
```

**Deleted:**
- ~~`Categorization/CategorizationEngine.cs`~~ — split into Agent 2 + Agent 3

---

## Adding a New Category / Rule

To add a new error category or a new subcategory within an existing category:

### New Subcategory in Existing Category
Edit `Agents/CategorizationAgent/CategorizationAgent.cs` → `BuildRules()`:
```csharp
// Add a new rule in the correct category section (before the broader rules)
new(ErrorCategory.CategoryBDatabaseErrors, "Replication Lag", new[]
{
    "replication lag", "replica delay", "slave behind master"
}),
```

### New Top-Level Category
1. Add the new enum value to `Models/ErrorCategory.cs`
2. Add `ToDisplayName()`, `ToCode()`, `ToColor()` cases in `ErrorCategoryExtensions`
3. Add rules for the new category in `CategorizationAgent.BuildRules()`

No other files need to change — the agents are fully isolated.
