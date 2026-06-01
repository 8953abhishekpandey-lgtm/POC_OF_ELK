import re
import sqlite3
import hashlib
import requests
import json
import os
import uuid
import asyncio
import websockets
from typing import Optional, Tuple

# ── Agent 1: SignatureAgent (Normalizer) ──────────────────────────────────────
class SignatureAgent:
    def __init__(self):
        self.guid_pattern = re.compile(r"[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}", re.IGNORECASE)
        self.timestamp_pattern = re.compile(r"\b\d{4}-\d{2}-\d{2}[t\s]\d{2}:\d{2}:\d{2}(?:\.\d+)?z?\b", re.IGNORECASE)
        self.number_pattern = re.compile(r"\b\d+\b")
        self.single_quoted_pattern = re.compile(r"'[^']*'")
        self.double_quoted_pattern = re.compile(r'"[^"]*"')
        self.whitespace_pattern = re.compile(r"\s+")

    def normalize(self, message: str) -> str:
        if not message:
            return ""
        n = message.lower()
        n = self.guid_pattern.sub("{guid}", n)
        n = self.timestamp_pattern.sub("{timestamp}", n)
        n = self.number_pattern.sub("{number}", n)
        n = self.single_quoted_pattern.sub("'{value}'", n)
        n = self.double_quoted_pattern.sub('"{value}"', n)
        n = self.whitespace_pattern.sub(" ", n).strip()
        return n[:500]

    def get_signature(self, message: str, exception_type: str, app_name: str) -> str:
        norm_msg = self.normalize(message)
        source = f"{exception_type.strip().lower()}|{app_name.strip().lower()}|{norm_msg}"
        return hashlib.md5(source.encode('utf-8')).hexdigest()


# ── Agent 2: CacheAgent (SQLite Database) ─────────────────────────────────────
class CacheAgent:
    def __init__(self, db_path: str = "categorizer_cache.db"):
        self.db_path = db_path
        self._init_db()

    def _init_db(self):
        with sqlite3.connect(self.db_path) as conn:
            conn.execute("""
                CREATE TABLE IF NOT EXISTS signature_cache (
                    signature TEXT PRIMARY KEY,
                    category TEXT NOT NULL,
                    subcategory TEXT NOT NULL,
                    created_at DATETIME DEFAULT CURRENT_TIMESTAMP
                )
            """)
            conn.commit()

    def get(self, signature: str) -> Optional[Tuple[str, str]]:
        with sqlite3.connect(self.db_path) as conn:
            cursor = conn.cursor()
            cursor.execute(
                "SELECT category, subcategory FROM signature_cache WHERE signature = ?",
                (signature,)
            )
            row = cursor.fetchone()
            return row if row else None

    def get_bulk(self, signatures: list[str]) -> dict[str, Tuple[str, str]]:
        if not signatures:
            return {}
        results = {}
        with sqlite3.connect(self.db_path) as conn:
            cursor = conn.cursor()
            chunk_size = 500
            for i in range(0, len(signatures), chunk_size):
                chunk = signatures[i:i+chunk_size]
                placeholders = ",".join(["?"] * len(chunk))
                cursor.execute(
                    f"SELECT signature, category, subcategory FROM signature_cache WHERE signature IN ({placeholders})",
                    chunk
                )
                for sig, cat, subcat in cursor.fetchall():
                    results[sig] = (cat, subcat)
        return results

    def set(self, signature: str, category: str, subcategory: str):
        with sqlite3.connect(self.db_path) as conn:
            conn.execute(
                "INSERT OR REPLACE INTO signature_cache (signature, category, subcategory) VALUES (?, ?, ?)",
                (signature, category, subcategory)
            )
            conn.commit()

    def set_bulk(self, entries: list[Tuple[str, str, str]]):
        if not entries:
            return
        with sqlite3.connect(self.db_path) as conn:
            conn.executemany(
                "INSERT OR REPLACE INTO signature_cache (signature, category, subcategory) VALUES (?, ?, ?)",
                entries
            )
            conn.commit()


# ── Agent 3: DuoClassifierAgent (GitLab GraphQL aiAction) ─────────────────────
#
# How this works:
#   GitLab Duo Chat (browser + IDE) internally uses two GraphQL operations:
#     1. Mutation  → aiAction(input: { chat: { question: "..." } })
#        Sends the prompt to GitLab's AI abstraction layer.
#     2. Subscription → aiCompletionResponse (userId, resourceId, clientSubscriptionId)
#        Streams the AI's response back over a WebSocket (graphql-ws protocol).
#
#   This is the SAME mechanism GitLab Duo Chat uses in the browser.
#   It works with a Premium PAT that has the `api` or `ai_features` scope.
#
#   Endpoint:
#     GraphQL HTTP  → POST {GITLAB_URL}/api/graphql
#     GraphQL WS    → wss://{host}/cable  (Action Cable protocol)
#
class DuoClassifierAgent:
    def __init__(self, gitlab_url: str, gitlab_token: str):
        self.gitlab_url = gitlab_url.rstrip('/')
        self.gitlab_token = gitlab_token
        self.use_local_only = False
        self._user_id: Optional[str] = None  # cached after first fetch

    # ── Public entry point ──────────────────────────────────────────────────
    def classify(self, message: str, exception_type: str, stack_trace: str, severity: str) -> Tuple[str, str]:
        """
        Try GitLab Duo GraphQL → fallback to local regex rules.
        """
        if self.use_local_only or not self.gitlab_token or self.gitlab_token.startswith("<") or len(self.gitlab_token) < 10:
            return self.classify_local(message, exception_type)

        try:
            result = asyncio.run(self._classify_via_graphql(message, exception_type, stack_trace, severity))
            if result:
                return result
        except Exception as e:
            print(f"[DuoClassifier] GitLab GraphQL error: {e} — falling back to local rules.")

        return self.classify_local(message, exception_type)

    # ── Step 1: Get current user's GitLab global ID ─────────────────────────
    def _get_user_id(self) -> Optional[str]:
        """
        Fetch the authenticated user's GitLab global ID (gid://gitlab/User/NNN).
        Required as the resourceId for the aiCompletionResponse subscription.
        Result is cached after the first call.
        """
        if self._user_id:
            return self._user_id

        query = """
        query {
          currentUser {
            id
          }
        }
        """
        try:
            resp = requests.post(
                f"{self.gitlab_url}/api/graphql",
                json={"query": query},
                headers={
                    "Authorization": f"Bearer {self.gitlab_token}",
                    "Content-Type": "application/json"
                },
                timeout=10
            )
            resp.raise_for_status()
            data = resp.json()
            user_id = data.get("data", {}).get("currentUser", {}).get("id")
            if user_id:
                self._user_id = user_id
                print(f"[DuoClassifier] Authenticated as GitLab user: {user_id}")
                return user_id
        except Exception as e:
            print(f"[DuoClassifier] Failed to fetch currentUser: {e}")

        return None

    # ── Step 2: Send aiAction mutation (triggers Duo Chat AI processing) ────
    def _send_ai_action(self, question: str, resource_id: str, client_sub_id: str) -> bool:
        """
        Send the aiAction GraphQL mutation.
        This tells GitLab's AI abstraction layer to process the question.
        The response will arrive asynchronously via the WebSocket subscription.
        """
        mutation = """
        mutation AiAction($input: AiActionInput!) {
          aiAction(input: $input) {
            clientMutationId
            errors
          }
        }
        """
        variables = {
            "input": {
                "chat": {
                    "question": question,
                    "resourceId": resource_id
                },
                "clientSubscriptionId": client_sub_id
            }
        }
        resp = requests.post(
            f"{self.gitlab_url}/api/graphql",
            json={"query": mutation, "variables": variables},
            headers={
                "Authorization": f"Bearer {self.gitlab_token}",
                "Content-Type": "application/json",
                "X-GitLab-Duo-Chat": "true"
            },
            timeout=15
        )

        if resp.status_code in [401, 403]:
            print(f"[DuoClassifier] Unauthorized (HTTP {resp.status_code}). Disabling remote classification.")
            self.use_local_only = True
            raise PermissionError("GitLab token unauthorized for Duo AI.")

        resp.raise_for_status()
        result = resp.json()
        errors = result.get("data", {}).get("aiAction", {}).get("errors", [])
        if errors:
            raise RuntimeError(f"aiAction errors: {errors}")

        return True

    # ── Step 3: Listen for AI response via Action Cable WebSocket ────────────
    async def _listen_for_response(self, user_id: str, resource_id: str, client_sub_id: str, timeout: int = 20) -> Optional[str]:
        """
        Connect to GitLab's Action Cable WebSocket and subscribe to
        aiCompletionResponse to receive the streamed AI response.

        Protocol: graphql-ws over Action Cable (wss://{host}/cable)
        """
        host = self.gitlab_url.replace("https://", "").replace("http://", "")
        ws_url = f"wss://{host}/cable"

        subscription_query = """
        subscription AiCompletionResponse($userId: UserID, $resourceId: AiModelID, $clientSubscriptionId: String, $htmlResponse: Boolean) {
          aiCompletionResponse(userId: $userId, resourceId: $resourceId, clientSubscriptionId: $clientSubscriptionId, htmlResponse: $htmlResponse) {
            id
            requestId
            content
            contentHtml
            errors
            role
            timestamp
            type
            chunkId
          }
        }
        """

        variables = {
            "userId": user_id,
            "resourceId": resource_id,
            "clientSubscriptionId": client_sub_id,
            "htmlResponse": False
        }

        full_response = []
        headers = {
            "Authorization": f"Bearer {self.gitlab_token}",
            "Origin": self.gitlab_url,
        }

        try:
            async with websockets.connect(
                ws_url,
                additional_headers=headers,
                subprotocols=["actioncable-v1-json"],
                open_timeout=10
            ) as ws:
                # Action Cable handshake
                welcome = await asyncio.wait_for(ws.recv(), timeout=5)
                welcome_data = json.loads(welcome)
                if welcome_data.get("type") != "welcome":
                    raise RuntimeError(f"Unexpected Action Cable message: {welcome_data}")

                # Subscribe to the AI completion channel
                subscribe_msg = json.dumps({
                    "command": "subscribe",
                    "identifier": json.dumps({
                        "channel": "GraphqlChannel"
                    })
                })
                await ws.send(subscribe_msg)

                # Wait for subscription confirmation
                confirmed = False
                for _ in range(5):
                    msg = await asyncio.wait_for(ws.recv(), timeout=5)
                    msg_data = json.loads(msg)
                    if msg_data.get("type") == "confirm_subscription":
                        confirmed = True
                        break

                if not confirmed:
                    raise RuntimeError("Failed to confirm Action Cable subscription.")

                # Send the GraphQL subscription operation
                execute_msg = json.dumps({
                    "command": "message",
                    "identifier": json.dumps({"channel": "GraphqlChannel"}),
                    "data": json.dumps({
                        "query": subscription_query,
                        "variables": variables,
                        "operationName": "AiCompletionResponse"
                    })
                })
                await ws.send(execute_msg)

                # Collect streamed chunks until done or timeout
                deadline = asyncio.get_event_loop().time() + timeout
                while asyncio.get_event_loop().time() < deadline:
                    try:
                        raw = await asyncio.wait_for(ws.recv(), timeout=3)
                        msg_data = json.loads(raw)
                        message = msg_data.get("message", {})
                        payload = message.get("result", {}).get("data", {}).get("aiCompletionResponse", {})

                        if not payload:
                            continue

                        chunk_content = payload.get("content", "")
                        chunk_type = payload.get("type", "")
                        errors = payload.get("errors", [])

                        if errors:
                            raise RuntimeError(f"AI response errors: {errors}")

                        if chunk_content:
                            full_response.append(chunk_content)

                        # "fullResponse" type signals the final complete chunk
                        if chunk_type == "fullResponse":
                            break

                    except asyncio.TimeoutError:
                        # No new chunk in 3s — check if we received anything
                        if full_response:
                            break
                        continue

        except Exception as e:
            if full_response:
                # Partial response is still usable
                pass
            else:
                raise

        return "".join(full_response) if full_response else None

    # ── Orchestrator ─────────────────────────────────────────────────────────
    async def _classify_via_graphql(self, message: str, exception_type: str, stack_trace: str, severity: str) -> Optional[Tuple[str, str]]:
        """
        Full flow:
          1. Get current user ID
          2. Send aiAction mutation
          3. Listen for aiCompletionResponse on WebSocket
          4. Parse JSON from AI response
        """
        user_id = self._get_user_id()
        if not user_id:
            raise RuntimeError("Could not retrieve GitLab user ID.")

        client_sub_id = str(uuid.uuid4())
        resource_id = user_id  # Use user GID as the resource

        prompt = f"""You are an expert log categorizer. Analyze this application error and assign it a category and subcategory.

Log Details:
- Message: {message[:500]}
- Exception Type: {exception_type}
- Stack Trace: {stack_trace[:500]}
- Severity: {severity}

Rules:
- Use a short professional category name (1-2 words) such as Database, Authentication, Validation, Integration, Infrastructure, Application.
- Keep category consistent across similar errors (aim for 6-8 total distinct categories).
- Do NOT use prefixes like 'Category A' or numbers.

Respond ONLY with this JSON object, no explanation, no markdown:
{{"category": "CategoryName", "subcategory": "Short 2-3 word label"}}"""

        # Send the mutation
        self._send_ai_action(prompt, resource_id, client_sub_id)

        # Wait for the response via WebSocket
        ai_text = await self._listen_for_response(user_id, resource_id, client_sub_id)

        if not ai_text:
            raise ValueError("Empty response from GitLab Duo AI.")

        # Parse the JSON classification result
        data = self._extract_json(ai_text)
        category = data.get("category", "Unclassified").strip()
        subcategory = data.get("subcategory", "Unclassified Error").strip()

        # Sanitize: remove any "Category X - " prefix that might slip through
        category = re.sub(r'^(?i)category\s+[a-z]\s*-\s*', '', category).title()
        if not category or category.lower() == "unclassified":
            category = "Unclassified"

        print(f"[DuoClassifier] GitLab Duo classified: {category} / {subcategory}")
        return category, subcategory

    # ── Local fallback (regex keyword rules) ──────────────────────────────────
    def classify_local(self, message: str, exception_type: str) -> Tuple[str, str]:
        msg_lower = (message or "").lower()
        exc_lower = (exception_type or "").lower()

        # 1. Database Errors
        if any(x in msg_lower or x in exc_lower for x in ["sql", "database", "connection pool", "deadlock", "postgres", "mysql", "oracle", "dbupdate", "dbcontext", "error number"]):
            subcat = "Database Connection Failure" if "connect" in msg_lower else "Database Operation Error"
            return "Database", subcat

        # 2. Authentication / Security Errors
        if any(x in msg_lower or x in exc_lower for x in ["unauthorized", "unauthenticated", "token", "expired", "jwt", "login", "credentials", "permission", "access denied", "idx10223"]):
            return "Authentication", "Unauthorized Access"

        # 3. Validation Errors
        if any(x in msg_lower or x in exc_lower for x in ["validation", "invalid", "argumentnull", "nullreference", "bad request", "format", "missing"]):
            return "Validation", "Input Validation Error"

        # 4. Integration Errors
        if any(x in msg_lower or x in exc_lower for x in ["http", "api", "rest", "soap", "timeout", "endpoint", "socket", "gateway", "unreachable"]):
            return "Integration", "API Integration Error"

        # 5. Infrastructure Errors
        if any(x in msg_lower or x in exc_lower for x in ["out of memory", "disk full", "cpu", "host", "network", "server", "dns", "socketexception"]):
            return "Infrastructure", "Infrastructure Resource Error"

        # 6. Default Fallback
        if exception_type:
            return "Application", f"{exception_type} Error"

        return "Unclassified", "Unclassified Error"

    # ── JSON extractor ────────────────────────────────────────────────────────
    def _extract_json(self, text: str) -> dict:
        # 1. Try markdown JSON block
        json_block_match = re.search(r"```json\s*(\{.*?\})\s*```", text, re.DOTALL | re.IGNORECASE)
        if json_block_match:
            try:
                return json.loads(json_block_match.group(1))
            except Exception:
                pass

        # 2. Try raw curly brackets
        curly_match = re.search(r"(\{.*?\})", text, re.DOTALL)
        if curly_match:
            try:
                return json.loads(curly_match.group(1))
            except Exception:
                pass

        # 3. Try direct parse
        try:
            return json.loads(text.strip())
        except Exception:
            pass

        raise ValueError(f"Failed to parse classification JSON from: {text[:200]}")
