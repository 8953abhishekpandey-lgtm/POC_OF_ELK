# How GitLab Duo AI Is Used for Log Categorization

> **Simple explanation of the API, how it works, and where it lives in the code.**

---

## The Big Picture (In Plain English)

Every time a new error log comes in, our system needs to answer:
> *"What category does this error belong to? (Database error? Authentication error? etc.)"*

Instead of using a separate AI service like OpenAI or Google, we use **GitLab Duo** — the same AI assistant built into your GitLab Premium account that you use in the browser or VS Code.

The trick is: **we call the exact same internal API that GitLab's own browser uses** when you type a question in the Duo Chat panel.

---

## Why Not Use a Simple REST Call?

You might expect something like:
```
POST https://gitlab.com/api/v4/ai/classify  ← This does NOT exist
```

GitLab does NOT provide a simple public REST API for Duo Chat. Instead, Duo Chat works through two technologies internally:

| Technology | What It Is | Used For |
|---|---|---|
| **GraphQL Mutation** | A query that sends your question | Telling GitLab to process your prompt |
| **WebSocket Subscription** | A live connection that streams back the answer | Receiving the AI's response in real time |

Think of it like ordering food at a restaurant:
- **GraphQL Mutation** = You place your order with the waiter
- **WebSocket** = The kitchen calls your number when your food is ready

---

## Step-by-Step: How One Log Gets Classified

### Example Input
```
Error Message: "Violation of PRIMARY KEY constraint 'PK_Orders'"
Exception Type: "SqlException"
```

---

### Step 1 — Generate a Unique Signature (De-duplication)

**File:** `categorizer/agents.py` → `SignatureAgent.get_signature()`

Before calling any AI, we first create a **fingerprint** of the error by stripping out all dynamic parts (numbers, GUIDs, timestamps, quoted values):

```
"Violation of PRIMARY KEY constraint 'PK_Orders'"
        ↓  (normalize)
"violation of primary key constraint '{value}'"
        ↓  (MD5 hash)
signature = "a3f92c1d..."
```

**Why?** If 500 logs all say "Violation of PRIMARY KEY constraint" (with different table names), they are the same root cause. We only need to call GitLab AI **once**, not 500 times.

---

### Step 2 — Check the SQLite Cache

**File:** `categorizer/agents.py` → `CacheAgent.get()`

We check a local database file (`categorizer_cache.db`) to see if this signature was already classified before:

```
signature "a3f92c1d..." → already in DB?
    ├── YES → Return "Database / Database Operation Error" instantly (< 1ms, no AI call)
    └── NO  → Continue to Step 3
```

This is why **31 signatures are cached** (from your `/status` check) — those 31 unique error types will never call GitLab AI again.

---

### Step 3 — Get Your GitLab User ID

**File:** `categorizer/agents.py` → `DuoClassifierAgent._get_user_id()`

**API Used:**
```
POST https://gitlab.com/api/graphql
Authorization: Bearer glpat-xxxx...

Query:
  query {
    currentUser {
      id       ← We need this (e.g. "gid://gitlab/User/29528244")
    }
  }
```

**Why?** GitLab's WebSocket subscription needs your User ID to know which AI response to send back to you. This is cached after the first call — it only happens once per app startup.

---

### Step 4 — Send the Classification Prompt to GitLab Duo

**File:** `categorizer/agents.py` → `DuoClassifierAgent._send_ai_action()`

**API Used:**
```
POST https://gitlab.com/api/graphql
Authorization: Bearer glpat-xxxx...

Mutation:
  mutation AiAction($input: AiActionInput!) {
    aiAction(input: $input) {
      clientMutationId
      errors
    }
  }

Variables:
  {
    "input": {
      "chat": {
        "question": "You are a log categorizer. Classify: SqlException...",
        "resourceId": "gid://gitlab/User/29528244"
      },
      "clientSubscriptionId": "550e8400-e29b-41d4-a716-446655440000"  ← random UUID
    }
  }
```

This is **exactly** the same GraphQL mutation that fires when you type a question into GitLab Duo Chat in your browser. GitLab receives this and starts processing the AI response in the background.

> **Note:** This call returns immediately with just `{ clientMutationId: null, errors: [] }`.  
> The actual AI answer does NOT come back here — it comes via the WebSocket in Step 5.

---

### Step 5 — Listen for the AI Response via WebSocket

**File:** `categorizer/agents.py` → `DuoClassifierAgent._listen_for_response()`

**Protocol Used:**
```
wss://gitlab.com/cable
Subprotocol: actioncable-v1-json
Authorization: Bearer glpat-xxxx...
```

This is GitLab's **Action Cable** WebSocket server (a Rails technology). Here's the conversation that happens:

```
Client → Server:  { "type": "welcome" }                    ← GitLab says hello

Client → Server:  subscribe to GraphqlChannel              ← We join the channel

Server → Client:  { "type": "confirm_subscription" }       ← GitLab confirms

Client → Server:  subscription AiCompletionResponse(       ← We register listener
                    userId: "gid://gitlab/User/29528244",
                    clientSubscriptionId: "550e8400-..."   ← Must match Step 4's UUID
                  )

... (GitLab AI is processing the prompt) ...

Server → Client:  { content: '{"category":',  type: "chunk" }   ← Streaming chunk 1
Server → Client:  { content: '"Database"',    type: "chunk" }   ← Streaming chunk 2
Server → Client:  { content: ',"subcategory":', type: "chunk" } ← Streaming chunk 3
Server → Client:  { content: '"Database Operation Error"}',
                    type: "fullResponse" }                       ← Final chunk = DONE
```

We collect all chunks and join them to get the full JSON string.

---

### Step 6 — Parse the AI Response and Cache It

**File:** `categorizer/agents.py` → `DuoClassifierAgent._classify_via_graphql()`

The collected text looks like:
```json
{"category": "Database", "subcategory": "Database Operation Error"}
```

We extract `category` and `subcategory`, then write them to SQLite:
```
signature "a3f92c1d..." → "Database" / "Database Operation Error"
```

From now on, any log with the same root cause returns instantly from cache — no more AI calls.

---

### Step 7 — Return to the C# Backend

**File:** `categorizer/main.py` → `POST /classify/bulk`

The Python service returns:
```json
{
  "category": "Database",
  "subcategory": "Database Operation Error",
  "cached": false
}
```

The C# backend receives this, assigns the category to the log, generates the error signature (e.g. `DB-A3F92C1D...`), and sends it to the frontend.

---

## The Fallback — When GitLab AI Is Not Available

If at any point the GitLab connection fails (timeout, network issue, invalid token), the code automatically falls back to **local regex keyword rules** — no error, no crash, the app keeps running.

```python
try:
    result = await _classify_via_graphql(...)   # Try GitLab Duo
    if result:
        return result
except Exception:
    pass  # Silently ignore

return classify_local(message, exception_type)  # Always works
```

**Local rules example:**
```
message contains "sql" or "database"  →  "Database" / "Database Operation Error"
message contains "unauthorized"       →  "Authentication" / "Unauthorized Access"
message contains "validation"         →  "Validation" / "Input Validation Error"
```

---

## Where Each Piece Lives in the Code

```
d:\POC_OF_ELK\
│
├── categorizer/
│   ├── agents.py          ← ALL the GitLab AI logic lives here
│   │   ├── SignatureAgent      → Normalizes errors, creates MD5 fingerprint
│   │   ├── CacheAgent          → Reads/writes SQLite cache
│   │   └── DuoClassifierAgent  → Calls GitLab GraphQL + WebSocket
│   │       ├── _get_user_id()          → Step 3: Fetch GitLab user GID
│   │       ├── _send_ai_action()       → Step 4: GraphQL mutation
│   │       ├── _listen_for_response()  → Step 5: WebSocket subscription
│   │       ├── _classify_via_graphql() → Step 6: Orchestrate 3+4+5
│   │       └── classify_local()        → Fallback regex rules
│   │
│   └── main.py            ← FastAPI web server with endpoints
│       ├── POST /classify        → Single log classification
│       ├── POST /classify/bulk   → Bulk classification (used by C# backend)
│       ├── GET  /status          → Live diagnostic: is GitLab Duo active?
│       ├── POST /test-classify   → Test a single log, see which source was used
│       └── GET  /health          → Simple health check
│
├── backend/
│   └── ELKMonitor.API/
│       └── Agents/CategorizationAgent/
│           └── CategorizationAgent.cs  ← C# code that calls the Python /classify/bulk
│
└── .env                   ← GITLAB_TOKEN and GITLAB_URL go here
```

---

## Authentication — What Token Is Needed

Your Personal Access Token (PAT) from GitLab is set in `.env`:

```env
GITLAB_URL=https://gitlab.com
GITLAB_TOKEN=glpat-xxxxxxxxxxxxxxxxxxxx
```

**Required scope:** `api` (which is standard for any GitLab Premium PAT)

**How to create one:**
1. Go to **GitLab.com → your profile picture → Edit profile**
2. Left sidebar → **Access Tokens**
3. Click **Add new token**
4. Name it anything (e.g. `elk-monitor`)
5. Select scope: ✅ `api`
6. Click **Create personal access token**
7. Copy the token → paste into `.env`

---

## Quick Verification

| What to check | How to check |
|---|---|
| Is GitLab Duo active? | Open `http://localhost:8000/status` |
| Test a specific log | POST to `http://localhost:8000/test-classify` via `http://localhost:8000/docs` |
| Watch live classification | `docker logs elk-monitor-categorizer -f` |
| How many are cached? | `cached_signatures` field in `/status` response |

**Signs it's using GitLab Duo (in Docker logs):**
```
[DuoClassifier] Authenticated as GitLab user: gid://gitlab/User/29528244
[DuoClassifier] GitLab Duo classified: Database / Database Operation Error
```

**Signs it fell back to local rules:**
```
[DuoClassifier] GitLab GraphQL error: ... — falling back to local rules.
```

---

## Important Notes

> [!NOTE]
> The `aiAction` GraphQL mutation and `aiCompletionResponse` subscription are **internal GitLab APIs** — the same ones used by the Duo Chat browser UI and VS Code extension. They are not officially documented for external use, but they work with a valid Premium PAT and have been stable since GitLab 16.x.

> [!TIP]
> Once a log signature is cached in SQLite, it **never calls GitLab AI again** for that error type. So over time, as more errors are seen, the AI is called less and less and the system gets faster.

> [!WARNING]
> If you rotate your GitLab PAT, update the `GITLAB_TOKEN` in `.env` and run `docker compose up -d` to apply it. The old token will stop working and the system will fall back to local rules until the new token is applied.
