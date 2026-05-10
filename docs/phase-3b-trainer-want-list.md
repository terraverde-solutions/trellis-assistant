# Phase 3.B Trainer-want-list (worker scratch)

**Status:** worker scratch — NOT an authoritative artifact. Hub drafts the canonical Trainer-side brief separately after reading the trellis-trainer codebase. This doc consolidates Assistant's Phase 3.B contract proposal into a single working artifact for hub to reference + flags explicit unknowns about Trainer-side state.

**Source:** Phase 3.B Assistant-side surface ([prior coordination message — Q1–Q6 + 4 cross-cutting concerns](#)) + my own follow-up reading of post-Macro-3 state.

**Audience:** hub (drafting Trainer-side brief). Trainer-team is the eventual reader via hub's brief.

---

## What Assistant needs from Trainer (recap, framed as Trainer-side asks)

### 1. Loopback-HTTP search endpoint

**Ask:** expose a `POST /api/search` (or whatever Trainer chooses) endpoint on `127.0.0.1:<trainer-port>/`. Same loopback pattern as Phase 2's Ollama-loopback + Phase 1's Postgres-loopback.

**Why:** GB10 single-box production has both Assistant + Trainer on the same host; cross-process boundary, zero network. In-process integration would force both into one binary (rejected during Phase 0 separation).

**Assistant-side wiring:** `Trainer:BaseUrl=http://127.0.0.1:<port>/` config knob, `AddHttpClient<ISearchClient>().AddTypedClient(...)` with the late-resolution `Func<Uri>` provider Phase 1+2+3.A established.

**Unknown — Trainer-side**:
- Does Trainer already expose any search endpoint? If so, what's the current shape? (Likely it has an internal ingestion-side surface; search-from-other-services may be new.)
- What port does Trainer-qa run on? (Need this to populate `Trainer:BaseUrl` defaults; hub should confirm from `/etc/trellis-trainer-qa.env` on GB10.)

---

### 2. Request shape with auth-context tenant scoping

**Ask:**
```json
{
  "query": "string (required, non-empty)",
  "top_k": "int (optional, default 5, server-clamped max 20)",
  "filter": {
    "project_id": "string?",
    "source_type": "string?"
  }
}
```

**Plus:** Trainer reads `tenant_id` from a request header (`X-Trellis-Tenant-Id`, validated by Trainer's tenant-header middleware mirror) — **NOT from the request body**. Body-side tenant_id is a leak vector if a misbehaving LLM emits `filter.tenant_id`; auth-context tenant scoping is non-negotiable.

**Server-side `top_k` clamping:** the LLM may emit `top_k: 100` due to confusion; Trainer caps at 20 (silently or 400 — Trainer-team's call). Defense against runaway result sets.

**Unknowns — Trainer-side:**
- **Does Trainer have a tenant-header middleware mirror today?** Trellis.Assistant's `TenantClaimsMiddleware` (Macro 3 PR 2) reads JWT claims canonically + falls back to `X-Trellis-*` headers (deprecated). Trainer's posture is unknown — does it currently accept any cross-service auth, or is it loopback-trust like pre-Macro-3 Assistant was?
- **`source_type` per-chunk:** does Trainer's index track `source_type` per chunk currently? If not, this becomes a sibling **ingestion-side ask**. Phase 3.B Assistant-side can ship without `source_type` if Trainer declines (just `filter: { project_id? }` for v0).
- **`project_id`:** Trellis adds projects post-3.B (Phase 5+ identity work hints at this). Trainer's index may or may not have `project_id` per chunk; flag for ingestion-side.

---

### 3. Response shape

**Ask:**
```json
{
  "results": [
    {
      "chunk_id": "string (Ulid or Trainer-internal id)",
      "document_id": "string",
      "content": "string — the chunk text the LLM reads",
      "source_uri": "string?",
      "score": "float (Trainer's similarity metric; LLM uses for ordering signal)",
      "metadata": "object? (free-form: source_type, ingested_at, etc.)"
    }
  ],
  "took_ms": "int (operator debugging)",
  "corpus_status": "string? (\"ready\" | \"indexing\" | \"no_corpus\"; absent means \"ready\")"
}
```

**Why an envelope, not a raw array:** `took_ms` is essential for operator debugging (slow tool dispatches surface in `AgentStep.duration_ms` but a Trainer-side breakdown distinguishes Trainer-slow from network-slow). `corpus_status` is the empty-vs-not-yet-indexed signal.

**Why `metadata` free-form:** lets Trainer add fields without a schema migration. Phase 3.B Assistant stores the raw response in `agent_steps.tool_output_json`; the LLM sees it in next-iteration context. If a metadata field becomes load-bearing, promote to a typed field then.

**Why drop `total_count`:** expensive to compute against an index when result set is paginated; v0 doesn't need it.

**Unknowns — Trainer-side:**
- **`chunk_id` shape** — Ulid? Trainer-internal sequence? Hash of (document_id, position)? Trainer-team picks; Assistant treats as opaque string.
- **`score` semantics** — cosine similarity? L2 distance? Reciprocal rank fusion if Trainer mixes embedding + BM25? LLM uses score as ordering signal only; absolute value is opaque to Assistant. But Trainer's choice may affect operator interpretation in journalctl logs.
- **`took_ms` granularity** — wall-clock from Trainer-side request entry to response emit, OR just the embedding+search cost? Both are useful; Trainer-team picks.

---

### 4. Tool descriptor (Assistant-side, but flagged for context)

```csharp
public sealed class SearchDocumentsTool : IAgentTool
{
    public AgentToolDescriptor Descriptor => new()
    {
        Name = "search_documents",
        Description =
            "Search the user's indexed documents for content relevant to the " +
            "query. Returns up to top_k chunks ordered by similarity. Use this " +
            "when the user asks about their own documents or when external " +
            "knowledge is needed; don't use for general-knowledge questions " +
            "the LLM can answer directly.",
        ParameterSchema = """
        {
            "type": "object",
            "properties": {
                "query": { "type": "string", "minLength": 1 },
                "top_k": { "type": "integer", "minimum": 1, "maximum": 20 },
                "filter": {
                    "type": "object",
                    "properties": {
                        "project_id": { "type": "string" },
                        "source_type": { "type": "string" }
                    }
                }
            },
            "required": ["query"]
        }
        """,
        Category = AgentToolCategory.Search,
    };
}
```

**Phase 3.B turns on `ToolRegistry` JSON Schema startup validation** (deferred from Phase 3.A.1's trimmed validation per Phase 3.A C7 ratification). Trainer-side has no input on this — Assistant-internal concern. Flagged so hub knows the JSON Schema validation work rides Phase 3.B.

---

### 5. Empty + error envelopes

| Path | HTTP status | Body | Assistant-side `IAgentTool.Output` |
|---|---|---|---|
| Match found | 200 | `{ results: [...], took_ms: N }` | `Success=true`, `ResultJson = body` |
| Empty match (corpus has chunks, none match) | 200 | `{ results: [], took_ms: N, corpus_status: "ready" }` | `Success=true`, `ResultJson = body` |
| Tenant has no corpus | 200 | `{ results: [], corpus_status: "no_corpus" }` | `Success=true` |
| Tenant indexing in progress | 200 | `{ results: [], corpus_status: "indexing" }` | `Success=true` |
| Trainer 4xx (bad query) | 4xx | `{ error: "..." }` | `Success=false`, `ErrorMessage = body.error` |
| Trainer 5xx / unreachable | exception | n/a | `Success=false`, `ErrorMessage = ex.Message` |
| CT tripped | n/a | n/a | `OperationCanceledException` (per Core's contract) |

**Why all empty-success paths are 200 + Success=true:** they're not failures; the LLM should surface them differently (no results / no corpus / still indexing). Treating empty as failure would loop the model into retry-with-different-query when there's nothing to find.

**Unknown — Trainer-side:**
- **Does Trainer track per-tenant indexing state today?** The `corpus_status` field requires Trainer to know whether a tenant has indexed corpus + whether ingestion is in flight. If not, this becomes a sibling ingestion-side ask. Phase 3.B Assistant-side can ship without `corpus_status` if Trainer declines (LLM just sees empty results without a "no corpus" disambiguator — operator-side metric tracks the case differently).

---

### 6. Embeddings model coordination — **NONE NEEDED Assistant-side**

The embedding model lives entirely server-side at Trainer. The LLM emits a query string; the tool sends that string to Trainer; Trainer embeds it server-side; results return as text. The LLM's chat model (mistral-small / qwen2.5 / llama3.3) is decoupled from Trainer's embedding model.

**Phase 3.A.2's `Assistant:Agent:Model` machinery** already handles "conversation pinned to non-tool-aware model can't call search_documents" — the agent path uses `qwen2.5:72b` (tool-supporting) regardless of conversation's pinned chat model.

**Unknowns — Trainer-side:**
- **What embedding model does Trainer use?** `nomic-embed-text` is my assumption from the GB10 model list (per the QA env doc); hub should verify. Doesn't affect Assistant code; affects QA setup expectations.
- **Re-indexing posture** — if Trainer changes embedding models (e.g., swaps to a newer `nomic-embed-text-v2`), what's the re-indexing path? Trainer's plate; Assistant transparently consumes whatever's in the index.

---

## Cross-cutting concerns (Assistant-side flags for Trainer-side scoping)

### CC1 — Auth between Assistant ↔ Trainer

**Assistant proposal:** v0 GB10 single-box: loopback-trust (same as Assistant ↔ Ollama). Phase 5 may mandate proper S2S (JWT or shared header secret). Loopback-trust is sufficient until Phase 5 lands real auth; Trainer-side can implement loopback-only-bind on its socket as a defense-in-depth posture.

**Unknown — Trainer-side:**
- Does Trainer have its own JWT bearer validation today? If yes, Assistant needs to mint a service-to-service JWT against Trainer's audience. Macro 3 PR 2 added Assistant-side JWT bearer validation; sister Macro 3 PR 3 (or whichever number) for Trainer would be the canonical companion. Sequencing: if Trainer expects JWT, Assistant ships Phase 3.B with a placeholder loopback-trust path + Macro 3 PR N adds the JWT. If Trainer is loopback-trust today, Phase 3.B ships as-is.

### CC2 — Cancellation propagation

**Assistant proposal:** `IAgentTool.RunAsync` honors `CancellationToken`; the Phase 3.B `SearchDocumentsTool`'s `HttpClient` call needs `cancellationToken` threaded through to `SendAsync`. Trainer-side: when the HTTP request is cancelled, server-side embedding/search work should also cancel.

**Unknown — Trainer-side:**
- **Does Trainer's framework propagate request cancellation to in-flight embedding/search work?** Standard ASP.NET Core handles this (`HttpContext.RequestAborted` flows through); Trainer's posture is unknown. If Trainer ignores cancellation, a long-running embedding call after caller-disconnect wastes Trainer-side compute — acceptable for v0, flag for Trainer-team.

### CC3 — Per-tenant rate limiting on `/api/search`

**Assistant proposal:** Trainer-side concern. A runaway agent loop could hammer Trainer at 25 dispatches × N concurrent runs (Assistant's `MaxSteps=25` per `AgentBudgetGate.DefaultMaxSteps` × concurrency). Assistant doesn't enforce upstream rate limits (would couple Assistant to Trainer's load model).

**Unknown — Trainer-side:**
- Does Trainer have per-tenant rate limiting today? If not, flag for Trainer-team — they own the load-protection plate.

### CC4 — Indexing-side ingestion (out of scope for /api/search contract)

**Out of scope for this PR + brief:** corpus ingestion, chunk extraction, embedding generation. Trainer-team's plate. If any ask above requires Trainer-side ingestion changes (e.g., tracking `source_type` per chunk, surfacing `corpus_status` per tenant), surface as a sibling ingestion-change brief that hub bundles into the Trainer-side scoping.

**Sibling-ask candidates from above:**
- `source_type` per-chunk tracking (Q2)
- `project_id` per-chunk tracking (Q2; less likely useful pre-Phase-5)
- `corpus_status` per-tenant signal (Q5)

---

## Disposition matrix — what each unknown affects

| Unknown | Affects Assistant code? | Affects Phase 3.B scaffold? | Hub action |
|---|:-:|:-:|---|
| Trainer port number | No (config) | No (just `/etc/trellis-assistant-qa.env` line) | Read trellis-trainer-qa.env on GB10 |
| Trainer existing search endpoint | Maybe | Maybe (if exists, may need shape negotiation) | Read trellis-trainer source |
| Trainer tenant-header middleware | Yes | Yes (CC1 — ships placeholder if missing) | Read trellis-trainer source |
| `source_type` per-chunk | No | Yes (drop `source_type` from filter) | Hub bundles ingestion-side ask if missing |
| `project_id` per-chunk | No | Yes (drop `project_id` from filter) | Hub bundles ingestion-side ask if missing |
| Embedding model | No | No (Assistant transparent) | Hub verifies via `ollama list` on GB10 |
| `corpus_status` per-tenant | No | Yes (drop the disambiguator if missing) | Hub bundles ingestion-side ask if missing |
| Auth posture (JWT vs loopback) | Yes | Yes (placeholder vs real S2S JWT) | Hub reads trellis-trainer Program.cs |
| Cancellation propagation | No (Assistant CT semantics unchanged) | No | Hub flags for Trainer-team |
| Rate limiting | No | No | Hub flags for Trainer-team |

---

## Concrete examples — what Assistant would emit

### Happy path — top-3 search

```http
POST /api/search HTTP/1.1
Host: 127.0.0.1:<trainer-port>
Authorization: Bearer <s2s-token>  // or X-Trellis-Tenant-Id header in v0 loopback-trust
X-Trellis-Tenant-Id: 00000000-0000-0000-0000-00000000000a
Content-Type: application/json

{
  "query": "What's our refund policy?",
  "top_k": 3,
  "filter": { "source_type": "policy_pdf" }
}
```

Assistant expected response:

```http
HTTP/1.1 200 OK
Content-Type: application/json

{
  "results": [
    {
      "chunk_id": "01J...",
      "document_id": "01K...",
      "content": "Refund policy: customers may return items within 30 days of purchase...",
      "source_uri": "s3://bucket/refund-policy.pdf#page=2",
      "score": 0.87,
      "metadata": { "source_type": "policy_pdf", "page": 2 }
    },
    { /* result 2 */ },
    { /* result 3 */ }
  ],
  "took_ms": 47,
  "corpus_status": "ready"
}
```

### Empty corpus

```http
HTTP/1.1 200 OK
Content-Type: application/json

{ "results": [], "took_ms": 12, "corpus_status": "no_corpus" }
```

LLM-facing envelope persisted in `agent_steps.tool_output_json` is the body verbatim. The LLM next-iteration context shows it as a successful tool dispatch with structured "no corpus" signal; the assistant turn the model emits typically says "you haven't uploaded documents yet" rather than "I couldn't find anything."

### Bad query (4xx from Trainer)

```http
HTTP/1.1 400 Bad Request
Content-Type: application/json

{ "error": "query is required" }
```

→ Assistant `IAgentTool.RunAsync` returns `Success=false`, `ErrorMessage="query is required"`. Executor maps to `AgentStepStatus.Failed`. LLM sees the error envelope + can retry with corrected args.

---

## What I'm waiting on (worker side)

Hub's canonical Trainer-side brief — drafted after reading the trellis-trainer codebase. This scratch doc speeds that drafting; not a substitute. Worker-side scaffold for Phase 3.B remains blocked until joint contract ratifies.

If hub finds any unknowns above already answered by their codebase read (e.g., "Trainer already exposes /api/search with shape X"), let me know — I update the disposition matrix and we converge faster.
