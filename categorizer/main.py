import os
import sqlite3
from fastapi import FastAPI, HTTPException, Request
from pydantic import BaseModel
from agents import SignatureAgent, CacheAgent, DuoClassifierAgent, EmbeddingAgent, SemanticCacheAgent

app = FastAPI(title="GitLab Duo Log Categorizer Service")

# Read environment variables
GITLAB_URL = os.getenv("GITLAB_URL", "https://gitlab.com")
GITLAB_TOKEN = os.getenv("GITLAB_TOKEN", "")

if not GITLAB_TOKEN:
    print("WARNING: GITLAB_TOKEN env variable is missing or empty. GitLab Duo classification calls will fail.")

# ── Wipe stale 'Unclassified' cache entries so they get re-classified by GitLab Duo ──
try:
    with sqlite3.connect("categorizer_cache.db") as _conn:
        _deleted = _conn.execute(
            "DELETE FROM signature_cache WHERE category = 'Unclassified'"
        ).rowcount
        _conn.commit()
    if _deleted:
        print(f"[Startup] Cleared {_deleted} stale 'Unclassified' cache entries for re-classification.")
except Exception:
    pass  # DB might not exist yet on first boot

# Instantiate agents
signature_agent = SignatureAgent()
cache_agent = CacheAgent()
duo_agent = DuoClassifierAgent(gitlab_url=GITLAB_URL, gitlab_token=GITLAB_TOKEN)
embedding_agent = EmbeddingAgent()
semantic_cache_agent = SemanticCacheAgent(cache_agent, embedding_agent)

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

        # Step 3: Check Semantic Cache (Agent 5)
        # Compute embedding first using normalized structural text + exception type for rich context
        norm_msg = signature_agent.normalize(req.message)
        norm_text = f"{req.exception_type}: {norm_msg}" if req.exception_type else norm_msg
        query_emb = embedding_agent.get_embedding(norm_text)
        semantic_match = semantic_cache_agent.search(norm_text, query_vector=query_emb)
        if semantic_match:
            cat, subcat, score = semantic_match
            print(f"[classify] Semantic cache hit for '{req.message[:50]}...' with similarity {score:.4f}. Reusing category: {cat}/{subcat}")
            # Cache this specific exact signature with the computed embedding to bypass vector computation next time
            cache_agent.set(sig, cat, subcat, query_emb)
            return ClassifyResponse(category=cat, subcategory=subcat, cached=True)

        # Step 4: Run LLM classification via GitLab Duo GraphQL (Agent 3)
        cat, subcat = await duo_agent.classify(
            message=req.message,
            exception_type=req.exception_type,
            stack_trace=req.stack_trace,
            severity=req.severity
        )

        # Step 5: Write to SQLite + Semantic cache
        semantic_cache_agent.add(sig, query_emb, cat, subcat)

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

    # Step 2: Fetch all cached records in a single call (exact cache)
    cached_map = cache_agent.get_bulk(signatures)

    # Step 3: For uncached signatures, check the semantic cache
    uncached_signatures = []
    semantic_matches = {}
    for item, sig in signatures_map:
        if sig not in cached_map:
            # Check if this signature is in our uncached list already to avoid duplicate embedding calculation
            if sig not in semantic_matches and not any(s == sig for s, _, _ in uncached_signatures):
                # Run semantic search
                norm_msg = signature_agent.normalize(item.message)
                norm_text = f"{item.exception_type}: {norm_msg}" if item.exception_type else norm_msg
                query_emb = embedding_agent.get_embedding(norm_text)
                match = semantic_cache_agent.search(norm_text, query_vector=query_emb)
                if match:
                    cat, subcat, score = match
                    print(f"[classify/bulk] Semantic cache hit for '{item.message[:50]}...' (similarity {score:.4f})")
                    semantic_matches[sig] = (cat, subcat, query_emb)
                else:
                    # Really uncached (needs GitLab Duo AI)
                    uncached_signatures.append((sig, item, query_emb))

    # If any semantic matches were found, add them to cached_map (and persist exact mapping in DB)
    for sig, (cat, subcat, query_emb) in semantic_matches.items():
        cached_map[sig] = (cat, subcat)
        # Store exact mapping in SQLite
        cache_agent.set(sig, cat, subcat, query_emb)

    # Step 4: Call GitLab Duo for the remaining truly uncached entries
    uncached_classifications = {}
    duo_calls_count = 0
    max_duo_calls = 10  # Production safety rate limit cap

    for sig, matching_item, query_emb in uncached_signatures:
        # If we have reached the safety cap of remote calls, use local classification for the remainder of this batch
        if duo_calls_count >= max_duo_calls:
            cat, subcat = duo_agent.classify_local(matching_item.message, matching_item.exception_type)
            uncached_classifications[sig] = (cat, subcat)
            semantic_cache_agent.add(sig, query_emb, cat, subcat)
            continue

        try:
            cat, subcat = await duo_agent.classify(
                message=matching_item.message,
                exception_type=matching_item.exception_type,
                stack_trace=matching_item.stack_trace,
                severity=matching_item.severity
            )
            
            # Increment remote call counter if a network call was actually performed
            if not duo_agent.use_local_only and len(duo_agent.gitlab_token) >= 10:
                duo_calls_count += 1
                
            uncached_classifications[sig] = (cat, subcat)
            # Add to both SQLite & Semantic cache in memory
            semantic_cache_agent.add(sig, query_emb, cat, subcat)
        except Exception:
            cat, subcat = duo_agent.classify_local(matching_item.message, matching_item.exception_type)
            uncached_classifications[sig] = (cat, subcat)
            semantic_cache_agent.add(sig, query_emb, cat, subcat)

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
        "semantic_cache_size": len(semantic_cache_agent.signatures),
        "embedding_model_active": embedding_agent.model_name,
        "error": error_detail,
    }


@app.post("/test-classify")
async def test_classify(req: ClassifyRequest):
    """
    Run a single log through the full pipeline and return the result
    along with which source was used (sqlite_cache, semantic_cache, gitlab_duo_graphql, or local_fallback).
    Useful for verifying vector embeddings are working.
    """
    import time

    start = time.time()
    sig = signature_agent.get_signature(
        message=req.message,
        exception_type=req.exception_type,
        app_name=req.application_name
    )

    # 1. Check exact cache first
    cached = cache_agent.get(sig)
    if cached:
        cat, subcat = cached
        elapsed = round(time.time() - start, 4)
        return {
            "category": cat,
            "subcategory": subcat,
            "source": "sqlite_cache",
            "signature": sig,
            "elapsed_seconds": elapsed,
            "note": "Result was already cached exactly — delete database to force a fresh AI call."
        }

    # 2. Check semantic cache
    norm_msg = signature_agent.normalize(req.message)
    norm_text = f"{req.exception_type}: {norm_msg}" if req.exception_type else norm_msg
    query_emb = embedding_agent.get_embedding(norm_text)
    semantic_match = semantic_cache_agent.search(norm_text, query_vector=query_emb)
    if semantic_match:
        cat, subcat, score = semantic_match
        # Save signature to exact cache for next time
        cache_agent.set(sig, cat, subcat, query_emb)
        elapsed = round(time.time() - start, 4)
        return {
            "category": cat,
            "subcategory": subcat,
            "source": "semantic_cache",
            "similarity_score": round(score, 4),
            "signature": sig,
            "elapsed_seconds": elapsed,
            "note": f"✅ Semantic cache hit (similarity: {score:.4f}). Reused cached classification."
        }

    # 3. Try GitLab Duo (awaitable — no asyncio.run() nesting needed)
    try:
        if not duo_agent.use_local_only and duo_agent.gitlab_token and len(duo_agent.gitlab_token) >= 10:
            result = await duo_agent._classify_via_graphql(
                req.message, req.exception_type, req.stack_trace, req.severity
            )
            if result:
                cat, subcat = result
                semantic_cache_agent.add(sig, query_emb, cat, subcat)
                elapsed = round(time.time() - start, 4)
                return {
                    "category": cat,
                    "subcategory": subcat,
                    "source": "gitlab_duo_graphql",
                    "signature": sig,
                    "elapsed_seconds": elapsed,
                    "note": "✅ GitLab Duo AI was called successfully and result was cached."
                }
    except Exception as e:
        pass

    # 4. Fallback
    cat, subcat = duo_agent.classify_local(req.message, req.exception_type)
    semantic_cache_agent.add(sig, query_emb, cat, subcat)
    elapsed = round(time.time() - start, 4)
    return {
        "category": cat,
        "subcategory": subcat,
        "source": "local_regex_fallback",
        "signature": sig,
        "elapsed_seconds": elapsed,
        "note": "⚠️ Local regex rules were used. Check /status for why GitLab Duo is unavailable."
    }

