# Dynamic Log Categorization & Performance Architecture Guide

This document details the high-performance dynamic log categorization architecture used in the ELK Monitor. 

The system leverages **GitLab Duo AI** to dynamically categorize logs into any appropriate category (up to 8 categories) without relying on hardcoded enums. It is protected by a multi-layered production safety pipeline (caching, deduplication, safety throttling, and self-healing bypass) to guarantee sub-second execution speeds.

---

## 🏗️ System Architecture

The pipeline is split into a C# backend orchestrator and a Python FastAPI modular microservice:

```text
 ┌────────────────────────────────────────────────────────────────────────┐
 │                              C# Backend                                │
 └──────────────────────────────────┬─────────────────────────────────────┘
                                    │
                                 1. Fetch raw logs from Elasticsearch
                                    (Index Pattern: filebeat*, logs-mock-*)
                                    │
                                    ▼
                     ┌──────────────────────────────┐
                     │      DataFetchingAgent       │
                     └──────────────┬───────────────┘
                                    │
                                 2. Call ClassifyBulkAsync() once
                                    │
                                    ▼
 ┌────────────────────────────────────────────────────────────────────────┐
 │                       Python FastAPI Microservice                      │
 └────────────────────────────────────────────────────────────────────────┘
                                    │
                                 3. Compute MD5 message signatures
                                    │
                                    ▼
                     ┌──────────────────────────────┐
                     │        SignatureAgent        │
                     └──────────────┬───────────────┘
                                    │
                                 4. Single SQLite query (get_bulk)
                                    │
                                    ▼
                     ┌──────────────────────────────┐
                     │          CacheAgent          │
                     └──────────────┬───────────────┘
                                    │
                          ┌─────────┴─────────┐
                   Found in Cache      Uncached Signatures
                          │                   │
                          │            5. Deduplicate unique signatures
                          │            6. Check Safety Cap (Max 10 calls)
                          │                   │
                          │                   ▼
                          │            ┌──────────────┐
                          │            │ GitLab Duo   │ ◄── Call REST API
                          │            └──────┬───────┘     (or local rules fallback)
                          │                   │
                          │            7. Write bulk set (set_bulk)
                          │                   │
                          ▼                   ▼
                     ┌──────────────────────────────┐
                     │ Assemble results & return    │
                     └──────────────────────────────┘
```

---

## 🤖 The Pipeline Agents

### 1. DataFetchingAgent (C#)
*   **Location**: [DataFetchingAgent.cs](file:///d:/POC_OF_ELK/backend/ELKMonitor.API/Agents/DataFetchingAgent/DataFetchingAgent.cs)
*   **Role**: Handles all communication with Elasticsearch. It queries raw log indices matching `filebeat*,logs-mock-*` and parses the JSON response into unified `LogDocument` records.

### 2. CategorizationAgent (C#)
*   **Location**: [CategorizationAgent.cs](file:///d:/POC_OF_ELK/backend/ELKMonitor.API/Agents/CategorizationAgent/CategorizationAgent.cs)
*   **Role**: Accepts bulk lists of raw logs, formats them into a JSON array, and sends a single HTTP POST request to the Python categorizer's `/classify/bulk` endpoint. It returns the raw string categories directly.

### 3. SubCategorizationAgent (C#)
*   **Location**: [SubCategorizationAgent.cs](file:///d:/POC_OF_ELK/backend/ELKMonitor.API/Agents/SubCategorizationAgent/SubCategorizationAgent.cs)
*   **Role**: Enriches logs by generating a standardized signature. It normalizes volatile identifiers (GUIDs, numbers, timestamps) and hashes the template to create a short code like `DAT-A3F2C1D4E5B6A7F8`.

### 4. SignatureAgent (Python)
*   **Location**: [agents.py](file:///d:/POC_OF_ELK/categorizer/agents.py#L10)
*   **Role**: Computes MD5 template hashes in the Python microservice to uniquely identify recurring log messages.

### 5. CacheAgent (Python)
*   **Location**: [agents.py](file:///d:/POC_OF_ELK/categorizer/agents.py#L38)
*   **Role**: Coordinates SQLite query execution. It implements batch queries (`get_bulk()`) and batch inserts (`set_bulk()`) to check and update 5,000 log signatures in less than 15ms.

### 6. DuoClassifierAgent (Python)
*   **Location**: [agents.py](file:///d:/POC_OF_ELK/categorizer/agents.py#L82)
*   **Role**: Handles dynamic LLM prompts to the GitLab Duo completions API. If the GitLab Duo request fails, it runs local regex-based classification rules.

---

## ⚡ Production Optimization & Safety Layers

To protect the system from slow response times and API failures, we've implemented four strict guardrails:

### Layer 1: Unique Template Deduplication
Logs in Elasticsearch are highly repetitive. Instead of calling GitLab Duo for all 5,000 logs:
1. We compute a signature for each log.
2. We filter unique uncached signatures.
3. If `4,000` logs represent the same database timeout error, **only 1** GitLab Duo API request is sent. The result is cached and shared across all 4,000 matching logs in the batch.

### Layer 2: SQLite get_bulk / set_bulk Caching
Opening and closing database connections inside loops is slow.
*   **`get_bulk()`**: Takes all signatures in the request and runs a chunked query (`WHERE signature IN (?, ?, ...)`) to verify signatures in batches of 500.
*   **`set_bulk()`**: Writes new categorization results in a single transaction via `executemany`.
This reduces database overhead to a single fast roundtrip.

### Layer 3: Production Safety Cap (Rate Limiter)
If a large batch containing hundreds of new, unique errors occurs (e.g. after a bad deployment):
*   We enforce a safety cap of **10 remote calls** to GitLab Duo per request.
*   The first 10 unique errors query GitLab Duo and write their results to the cache.
*   Any remaining signatures in the batch skip remote queries and fall back to the fast local classifier. This prevents the server from blocking or hitting timeouts.

### Layer 4: Self-Healing Authentication Bypass
If the GitLab Duo API returns an authentication error (HTTP `401` or `403` due to an expired token):
1. The microservice logs a warning and marks `self.use_local_only = True`.
2. All subsequent requests instantly bypass the GitLab Duo API and use local fallback rules. This avoids the 10-second timeout delays on every network query, keeping the application responsive.

---

## 📊 Dynamic Categorization Flow

We've removed all hardcoded enums from the C# codebase. Category codes and display fields are dynamically inferred at query time:

1. **Backend Aggregation**: The dashboard Groups logs dynamically by their `Category` string:
   ```csharp
   logs.GroupBy(l => l.Category).Select(g => new { Category = g.Key, Count = g.Count() })
   ```
2. **Dynamic Colors**: Category colors are generated from a dynamic theme palette as categories are found.
3. **UI Filtering**: The log table component queries `getDashboardCharts` and adds all dynamically discovered categories to the search filter dropdown on the fly.
