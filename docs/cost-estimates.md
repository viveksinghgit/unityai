# Cost Estimates

All prices are Azure East US list prices as of Q2 2026. Actual bills will differ based on region, commitment discounts, and Dev/Test subscription pricing. Enable Azure Cost Management alerts before going to production.

---

## Pricing Reference (per unit)

| Service | Unit | Price |
|---------|------|-------|
| **Azure OpenAI GPT-4o** input | 1M tokens | $2.50 |
| **Azure OpenAI GPT-4o** output | 1M tokens | $10.00 |
| **Azure OpenAI GPT-4o-mini** input | 1M tokens | $0.15 |
| **Azure OpenAI GPT-4o-mini** output | 1M tokens | $0.60 |
| **Cosmos DB Serverless** | 1M RUs | $0.25 |
| **Cosmos DB Serverless** storage | GB/month | $0.25 |
| **Cosmos DB Provisioned** (single region) | 100 RU/s | $0.008/hr |
| **Azure Functions Consumption** executions | 1M | $0.20 |
| **Azure Functions Premium EP1** | instance/hr | $0.169 |
| **Azure Speech Neural TTS** | 1M characters | $16.00 |
| **Azure Content Safety** | 1,000 text records | $1.00 |
| **Azure Service Bus Standard** | 1M operations | $0.05 |
| **Azure ML compute** Standard_DS3_v2 | hour | $0.27 |
| **Azure ML managed endpoint** Standard_DS2_v2 | hour | $0.095 |
| **Azure Load Testing** | 1,000 VU-hours | $10.00 |
| **App Insights** ingestion (after 5 GB free) | GB | $2.30 |

---

## Interaction Cost Model

One "NPC interaction" = player takes an action near an NPC that generates a memory event and (sometimes) a dialogue exchange.

### Without optimizations (worst case)

| Step | Service call | Tokens / units | Cost |
|------|-------------|----------------|------|
| Significance check (on-device) | Sentis CPU | — | $0.00 |
| Memory read | Cosmos 1 RU | — | $0.00025/1M RU |
| Emotional weight calc | GPT-4o-mini | 400 in + 60 out | $0.000097 |
| Cosmos upsert | Cosmos 5 RU | — | $0.00125/1M RU |
| Dialogue generation | GPT-4o | 1,500 in + 200 out | $0.00575 |
| Content safety check | Content Safety | 1 record | $0.001 |
| TTS synthesis | Speech 200 chars | — | $0.0032 |
| **Total per interaction** | | | **~$0.010** |

### With all optimizations enabled

| Optimization | Reduction |
|-------------|-----------|
| Significance filter (Sentis, threshold 0.3) | 70% of events → no LLM call |
| GPT-4o-mini for low-significance events | 80% of LLM events → mini |
| Semantic response cache (20% hit rate) | 20% of dialogues → $0 |
| Local LRU cache (80% hit rate on reads) | 80% of reads → $0 |

Effective cost per interaction: **~$0.0025**

---

## Environment Estimates

### Dev / Local

No Azure costs for pure local development (Cosmos emulator, no OpenAI). When testing against real Azure services:

| Resource | Usage | Monthly |
|----------|-------|---------|
| Cosmos DB Serverless | ~500k RUs/month | $0.13 |
| Azure OpenAI | ~50k tokens/month | $0.30 |
| Azure Speech | ~100k chars/month | $1.60 |
| Content Safety | ~500 records/month | $0.50 |
| App Insights | < 5 GB free tier | $0.00 |
| Key Vault | < 10k operations | $0.03 |
| Storage | < 1 GB | $0.02 |
| **Total** | | **~$5–10/month** |

### Staging / QA

| Resource | Usage | Monthly |
|----------|-------|---------|
| Cosmos DB Serverless | ~5M RUs/month | $1.25 |
| Azure OpenAI GPT-4o | ~500k tokens/month | $6.25 |
| Azure OpenAI GPT-4o-mini | ~2M tokens/month | $0.60 |
| Azure Speech | ~1M chars/month | $16.00 |
| Content Safety | ~5k records/month | $5.00 |
| Azure Functions Consumption | ~2M executions/month | $0.40 |
| Load Testing (4 runs × 10 VU × 3 min) | 12 VU-hours | $0.12 |
| App Insights | ~2 GB/month | $0.00 (free tier) |
| Service Bus Standard | ~1M ops/month | $0.05 |
| ML workspace (8hr training run × 2/month) | DS3_v2 16hr | $4.32 |
| ML online endpoint (test only, 10hr/month) | DS2_v2 10hr | $0.95 |
| **Total** | | **~$35–55/month** |

---

## Production at Scale

Assumptions:
- Average session = 60 minutes
- 5 NPC interactions per player per hour
- 30% of interactions result in a dialogue request (player directly speaks to NPC)
- Significance filter eliminates 70% of events (no LLM call)
- Of remaining 30% of events: 80% use GPT-4o-mini, 20% use GPT-4o
- Semantic cache hits 20% of all dialogue requests
- TTS enabled for all dialogue responses
- Content safety checked on all dialogue outputs
- Memory prefetch active (mostly cache hits after first load)

### 100 Concurrent Players (CCU)

**Hourly rates:**

| Component | Calls/hr | Cost/hr |
|-----------|----------|---------|
| Memory reads (20% cache miss → Cosmos) | 100 | ~$0.00 |
| Event processing GPT-4o-mini (15/hr) | 15 | $0.02 |
| Dialogue GPT-4o (12/hr after 20% cache) | 12 | $0.17 |
| Azure Speech TTS (30 dialogues × 200 chars) | 6k chars | $0.10 |
| Content Safety (30 checks) | 30 | $0.03 |
| Cosmos writes (150/hr) | 150 RU-batches | ~$0.00 |
| Functions invocations (~650/hr) | 650 | ~$0.00 |
| **Subtotal** | | **~$0.32/hr** |

**Monthly (720 hours):** ~**$230**

Additional fixed costs:
- Azure Functions Premium EP1 (no cold starts): $122
- Cosmos provisioned 400 RU/s: $58
- ML endpoint (1× DS2_v2 always-on): $68
- App Insights (~5 GB/month): $0
- Service Bus: $1
- **Total: ~$480/month**

### 1,000 Concurrent Players (CCU)

Scale the hourly rate × 10:

| Component | Monthly |
|-----------|---------|
| OpenAI GPT-4o (dialogue) | $1,224 |
| OpenAI GPT-4o-mini (events) | $144 |
| Azure Speech TTS | $720 |
| Content Safety | $216 |
| Azure Functions Premium EP1 × 2 | $244 |
| Cosmos provisioned 4,000 RU/s | $579 |
| ML endpoint (1× DS2_v2) | $68 |
| App Insights (~15 GB) | $23 |
| Service Bus | $5 |
| **Total** | **~$3,220/month** |

### 10,000 Concurrent Players (CCU)

| Component | Monthly |
|-----------|---------|
| OpenAI GPT-4o (dialogue) | $12,240 |
| OpenAI GPT-4o-mini (events) | $1,440 |
| Azure Speech TTS | $7,200 |
| Content Safety | $2,160 |
| Azure Functions Premium EP2 × 4 | $978 |
| Cosmos provisioned 40,000 RU/s (multi-region) | $11,578 |
| ML endpoint (3× DS2_v2) | $205 |
| App Insights (~100 GB) | $207 |
| Service Bus Premium (high throughput) | $667 |
| **Total** | **~$36,675/month** |

> At 10k CCU the Cosmos DB cost overtakes OpenAI. Consider switching to provisioned throughput with autoscale and enabling the [Cosmos DB free tier](https://learn.microsoft.com/en-us/azure/cosmos-db/free-tier) for development containers.

---

## Cost Optimization Levers

Listed in implementation priority order (highest impact first):

### 1. Significance Filter — already implemented ✅
**Impact: 70% reduction in LLM calls**

The on-device `SignificanceScorer` (Sentis ONNX with rule-based fallback) scores every interaction at `< 1ms` before sending it to Azure. Events below `significanceThreshold = 0.3` update local state only — no HTTP call, no LLM.

Tune this threshold based on your game's pacing. A threshold of 0.4 saves more money but misses subtle NPC reactions.

### 2. Model Routing — already implemented ✅
**Impact: 5× cost reduction on event processing**

`DialoguePromptBuilder.IsHighSignificance` routes to GPT-4o only for high-significance interactions (combat, betrayal, major quests). Routine events (greetings, trade completions) use GPT-4o-mini.

### 3. Semantic Response Cache — already implemented ✅
**Impact: 20–30% cache hit rate on dialogue**

`SemanticResponseCache` caches dialogue responses for 5 minutes by NPC+player identity. Rapidly repeated player questions ("hello hello hello") hit the cache instead of GPT-4o.

Increase `DialogueCacheTtlMinutes` carefully — stale dialogue feels wrong if the NPC's memory state just changed.

### 4. Local LRU Cache — already implemented ✅
**Impact: 80% reduction in Cosmos reads**

`NpcMemoryCache` keeps up to `localCacheCapacity = 50` NPC-player pairs in memory. Proximity prefetch (`prefetchRadiusMetres = 30m`) warms the cache before the player speaks.

### 5. Azure OpenAI Prompt Caching
**Impact: 50% reduction on input token cost for repeated system prompts**

The NPC system prompt is identical across requests for the same NPC. Azure OpenAI automatically caches prompts ≥ 1,024 tokens. Ensure your system prompt is ≥ 1,024 tokens and placed before the dynamic memory context in the message array. This halves input costs with zero code changes.

### 6. Reserved Capacity / Commitment Tiers
**Impact: 40–65% discount**

| Resource | Commitment | Discount |
|----------|-----------|---------|
| Azure OpenAI | 100 PTUs | ~35% vs pay-as-you-go |
| Cosmos DB | 1-year reserved | 20% |
| Azure Functions | Premium reserved | 40% vs on-demand |
| Azure Speech | Commitment tier (>1M chars/month) | ~20% |

Only purchase commitments after measuring actual steady-state usage for 30 days.

### 7. GPT-4o-mini for All Dialogue (optional trade-off)
**Impact: ~15× cost reduction on dialogue**

If dialogue quality at GPT-4o-mini level is acceptable for your game (lighter RPGs, mobile), routing all dialogue to mini reduces monthly costs at 1k CCU from ~$3,200 to ~$450. Test with your NPC profiles before committing.

### 8. TTS Cost Control
**Impact: variable**

- Limit TTS to primary NPCs or voiced cutscenes. Background NPCs with low interaction rates do not need TTS.
- Set `enableTts = false` on `AzureSoulEngineConfig` for non-voiced NPCs — `NpcTtsService` gracefully skips synthesis without breaking anything.
- At 10k CCU, Speech is the second-largest cost. Consider pre-generating common NPC phrases offline and blending with live synthesis for rare/dynamic responses.

---

## Budget Alerts

Set these in Azure Cost Management before launch:

| Alert | Threshold | Action |
|-------|-----------|--------|
| Daily spend | $150 (1k CCU) | Email ops team |
| Daily spend | $300 (1k CCU) | Page on-call |
| OpenAI spend | 80% of monthly budget | Reduce TTL on semantic cache |
| Anomaly detection | 2× baseline spend | Automatic investigation |

The Functions app should also have a hard API Management policy that rate-limits dialogue requests to N per player per hour to prevent runaway costs from bots or abuse.

---

## Cost at Break-Even Against Dedicated LLM Server

A single 8× A100 server running an open-source 70B model locally would cost ~$12,000/month (cloud GPU rental). NPC Soul Engine becomes more expensive than self-hosted around **3,000–4,000 CCU** if you route all dialogue through GPT-4o.

Below that threshold, Azure pay-as-you-go is cheaper with zero ops overhead. Above it, consider a hybrid: GPT-4o for high-significance cutscene moments, a self-hosted model for routine interactions.
