import os
from fastapi import FastAPI, HTTPException, Request
from pydantic import BaseModel
from agents import SignatureAgent, CacheAgent, DuoClassifierAgent

app = FastAPI(title="GitLab Duo Log Categorizer Service")

# Read environment variables
GITLAB_URL = os.getenv("GITLAB_URL", "https://gitlab.com")
GITLAB_TOKEN = os.getenv("GITLAB_TOKEN", "")

if not GITLAB_TOKEN:
    print("WARNING: GITLAB_TOKEN env variable is missing or empty. GitLab Duo classification calls will fail.")

# Instantiate agents
signature_agent = SignatureAgent()
cache_agent = CacheAgent()
duo_agent = DuoClassifierAgent(gitlab_url=GITLAB_URL, gitlab_token=GITLAB_TOKEN)

# Pydantic schemas
class ClassifyRequest(BaseModel):
    message: str
    exception_type: str = ""
    stack_trace: str = ""
    severity: str = "ERROR"
    application_name: str = "Unknown"

class ClassifyResponse(BaseModel):
    category: str
    subcategory: str
    cached: bool

@app.post("/classify", response_model=ClassifyResponse)
async def classify_log(req: ClassifyRequest):
    try:
        # Step 1: Generate unique log signature (Agent 1)
        sig = signature_agent.get_signature(
            message=req.message,
            exception_type=req.exception_type,
            app_name=req.application_name
        )

        # Step 2: Check SQLite cache (Agent 2)
        cached_result = cache_agent.get(sig)
        if cached_result:
            cat, subcat = cached_result
            return ClassifyResponse(category=cat, subcategory=subcat, cached=True)

        # Step 3: Run LLM classification (Agent 3)
        cat, subcat = duo_agent.classify(
            message=req.message,
            exception_type=req.exception_type,
            stack_trace=req.stack_trace,
            severity=req.severity
        )

        # Step 4: Write to SQLite cache (Agent 2)
        cache_agent.set(sig, cat, subcat)

        return ClassifyResponse(category=cat, subcategory=subcat, cached=False)

    except Exception as e:
        raise HTTPException(status_code=500, detail=str(e))

@app.post("/classify/bulk", response_model=list[ClassifyResponse])
async def classify_logs_bulk(req: list[ClassifyRequest]):
    # Step 1: Pre-calculate all signatures
    signatures = []
    signatures_map = []
    for item in req:
        sig = signature_agent.get_signature(
            message=item.message,
            exception_type=item.exception_type,
            app_name=item.application_name
        )
        signatures.append(sig)
        signatures_map.append((item, sig))

    # Step 2: Fetch all cached records in a single call
    cached_map = cache_agent.get_bulk(signatures)

    # Step 3: Deduplicate unique uncached signatures to avoid duplicate calls
    uncached_signatures = set()
    for item, sig in signatures_map:
        if sig not in cached_map:
            uncached_signatures.add(sig)

    new_entries = []
    uncached_classifications = {}
    duo_calls_count = 0
    max_duo_calls = 10  # Production safety rate limit cap

    for sig in uncached_signatures:
        matching_item = next(item for item, s in signatures_map if s == sig)
        
        # If we have reached the safety cap of remote calls, use local classification for the remainder of this batch
        if duo_calls_count >= max_duo_calls:
            cat, subcat = duo_agent.classify_local(matching_item.message, matching_item.exception_type)
            uncached_classifications[sig] = (cat, subcat)
            continue

        try:
            cat, subcat = duo_agent.classify(
                message=matching_item.message,
                exception_type=matching_item.exception_type,
                stack_trace=matching_item.stack_trace,
                severity=matching_item.severity
            )
            
            # Increment remote call counter if a network call was actually performed
            if not duo_agent.use_local_only and len(duo_agent.gitlab_token) >= 10:
                duo_calls_count += 1
                
            uncached_classifications[sig] = (cat, subcat)
            new_entries.append((sig, cat, subcat))
        except Exception:
            cat, subcat = duo_agent.classify_local(matching_item.message, matching_item.exception_type)
            uncached_classifications[sig] = (cat, subcat)

    # Step 4: Save new entries in bulk
    if new_entries:
        cache_agent.set_bulk(new_entries)

    # Step 5: Assemble results in original order
    results = []
    for item, sig in signatures_map:
        if sig in cached_map:
            cat, subcat = cached_map[sig]
            results.append(ClassifyResponse(category=cat, subcategory=subcat, cached=True))
        else:
            cat, subcat = uncached_classifications[sig]
            results.append(ClassifyResponse(category=cat, subcategory=subcat, cached=False))

    return results

@app.get("/health")
async def health_check():
    return {"status": "healthy", "gitlab_url": GITLAB_URL}


@app.get("/status")
async def status():
    """
    Diagnostic endpoint — shows exactly which classification mode is active.
    Open http://localhost:8000/status in your browser to check.
    """
    import requests as req_lib

    token_present = bool(GITLAB_TOKEN and not GITLAB_TOKEN.startswith("<") and len(GITLAB_TOKEN) >= 10)
    gitlab_reachable = False
    user_id = None
    duo_enabled = False
    token_valid = False
    error_detail = None

    if token_present and not duo_agent.use_local_only:
        # Test 1: Can we reach GitLab and is the token valid?
        try:
            resp = req_lib.post(
                f"{GITLAB_URL}/api/graphql",
                json={"query": "query { currentUser { id username } }"},
                headers={
                    "Authorization": f"Bearer {GITLAB_TOKEN}",
                    "Content-Type": "application/json"
                },
                timeout=8
            )
            gitlab_reachable = True
            if resp.status_code == 200:
                data = resp.json()
                user_data = data.get("data", {}).get("currentUser")
                if user_data:
                    token_valid = True
                    user_id = user_data.get("id")
                    username = user_data.get("username", "unknown")
                    duo_enabled = True
                else:
                    error_detail = "Token valid but currentUser returned null — check Duo licence."
            elif resp.status_code in [401, 403]:
                error_detail = f"Token rejected by GitLab (HTTP {resp.status_code}) — check your PAT."
            else:
                error_detail = f"Unexpected HTTP {resp.status_code} from GitLab GraphQL."
        except Exception as e:
            error_detail = f"Cannot reach GitLab: {str(e)}"
    elif duo_agent.use_local_only:
        error_detail = "Duo disabled at runtime (previous token auth failure)."
    else:
        error_detail = "GITLAB_TOKEN is missing or looks like a placeholder."

    # Count cached signatures
    import sqlite3
    cached_count = 0
    try:
        with sqlite3.connect("categorizer_cache.db") as conn:
            cached_count = conn.execute("SELECT COUNT(*) FROM signature_cache").fetchone()[0]
    except Exception:
        pass

    active_mode = "gitlab_duo_graphql" if duo_enabled else "local_regex_fallback"

    return {
        "active_mode": active_mode,
        "description": (
            "Using GitLab Duo AI via GraphQL aiAction mutation"
            if duo_enabled
            else "Using local regex keyword rules (no AI)"
        ),
        "gitlab_url": GITLAB_URL,
        "token_present": token_present,
        "gitlab_reachable": gitlab_reachable,
        "token_valid": token_valid,
        "authenticated_user_id": user_id if token_valid else None,
        "duo_runtime_disabled": duo_agent.use_local_only,
        "cached_signatures": cached_count,
        "error": error_detail,
    }


@app.post("/test-classify")
async def test_classify(req: ClassifyRequest):
    """
    Run a single log through the full pipeline and return the result
    along with which source was used (gitlab_duo or local_fallback).
    Useful for verifying GitLab Duo is actually being called.
    """
    import time

    sig = signature_agent.get_signature(
        message=req.message,
        exception_type=req.exception_type,
        app_name=req.application_name
    )

    # Check cache first
    cached = cache_agent.get(sig)
    if cached:
        cat, subcat = cached
        return {
            "category": cat,
            "subcategory": subcat,
            "source": "sqlite_cache",
            "signature": sig,
            "note": "Result was already cached — delete categorizer_cache.db to force a fresh AI call."
        }

    # Try GitLab Duo
    start = time.time()
    used_duo = False
    try:
        if not duo_agent.use_local_only and duo_agent.gitlab_token and len(duo_agent.gitlab_token) >= 10:
            import asyncio
            result = await asyncio.get_event_loop().run_in_executor(
                None,
                lambda: asyncio.run(duo_agent._classify_via_graphql(
                    req.message, req.exception_type, req.stack_trace, req.severity
                ))
            )
            if result:
                cat, subcat = result
                used_duo = True
                cache_agent.set(sig, cat, subcat)
                elapsed = round(time.time() - start, 2)
                return {
                    "category": cat,
                    "subcategory": subcat,
                    "source": "gitlab_duo_graphql",
                    "signature": sig,
                    "elapsed_seconds": elapsed,
                    "note": "✅ GitLab Duo AI was called successfully."
                }
    except Exception as e:
        pass

    # Fallback
    cat, subcat = duo_agent.classify_local(req.message, req.exception_type)
    cache_agent.set(sig, cat, subcat)
    elapsed = round(time.time() - start, 2)
    return {
        "category": cat,
        "subcategory": subcat,
        "source": "local_regex_fallback",
        "signature": sig,
        "elapsed_seconds": elapsed,
        "note": "⚠️ Local regex rules were used. Check /status for why GitLab Duo is unavailable."
    }

