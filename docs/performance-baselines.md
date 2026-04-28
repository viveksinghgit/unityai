# Performance Baselines

Establish these numbers **before writing feature code** (Phase 0).
They become automated CI gates in Phase 9.

## Azure Infrastructure Baselines

| Metric | Target | Actual (fill in) | Test Method |
|---|---|---|---|
| Cosmos DB point-read p99 (empty container, in-region) | < 8ms | — | Azure Load Testing, 100 RPS, 5 min |
| Cosmos DB point-read p99 (50k documents) | < 15ms | — | Azure Load Testing, 100 RPS, 5 min |
| Cosmos DB upsert p99 | < 20ms | — | Azure Load Testing, 50 RPS |
| Azure Functions cold start (Premium EP1) | < 800ms | — | Invoke first request after 15min idle |
| Azure Functions warm invocation | < 100ms | — | Sustained 10 RPS over 2 min |
| GPT-4o first token latency (AI Foundry, East US) | < 1200ms | — | 20 sequential requests, measure via streaming |
| GPT-4o-mini first token latency | < 600ms | — | 20 sequential requests |
| Service Bus enqueue + receive round-trip | < 50ms | — | Service Bus Explorer, 100 messages |
| Service Bus gossip topic end-to-end (enqueue → subscription trigger) | < 3s | — | Timer-measured Function log |

## Full Pipeline Targets

| Pipeline | Target | Notes |
|---|---|---|
| Memory event (Unity → process-event → Cosmos) | < 3000ms | No GPT-4o, just weight calc + upsert |
| Dialogue generation first token (streaming) | < 1500ms | GPT-4o only |
| Dialogue generation full response | < 5000ms | GPT-4o + SSML build |
| Gossip propagation (1 hop, 5 targets) | < 10s end-to-end | Service Bus + 5 Cosmos upserts |
| Memory prefetch (GET) | < 200ms in-cache | After first load, from local cache |
| Memory prefetch (GET cold, Azure) | < 300ms | Cosmos point-read from Unity |
| NPC proximity prefetch activation | < 1s | Player enters 30m radius → memory loaded |

## Unity Runtime Targets

| Metric | Target | Test Method |
|---|---|---|
| `EmotionAnimationController.Update()` frame budget | < 0.3ms | Unity Profiler deep profile, 1000 frames |
| `PlayerActionMonitor` ring buffer enqueue | < 0.01ms | Unity Profiler |
| `NpcMemoryCache` LRU lookup | < 0.05ms | Unity Profiler |
| Local significance scorer (Sentis) | < 1ms | Unity Profiler, 100 calls |
| Local significance scorer (rule-based fallback) | < 0.01ms | Unity Profiler |
| Memory cache hit rate (normal play session) | > 80% | Custom App Insights counter |

## Cost Targets (at 1000 concurrent players)

| Resource | Per-call cost | 1000 players × 5 calls/min | Daily cap |
|---|---|---|---|
| GPT-4o (1500 input + 200 output tokens) | ~$0.011 | ~$3,300/hr | Alert at $5,000/day |
| GPT-4o-mini (500 input + 80 output tokens) | ~$0.00036 | ~$108/hr | — |
| Cosmos reads (1 RU each) | $0.00025 | ~$75/hr | — |
| Cosmos writes (5 RU each) | ~$0.00125 | ~$375/hr | — |
| Service Bus messages | ~$0.0000004 | negligible | — |

**Cost controls in priority order:**
1. Significance filter (Sentis) — target 70% call reduction
2. GPT-4o-mini for low-significance interactions
3. Semantic response caching (target 20–30% hit rate)
4. Hard per-player budget cap via API Management policy (graceful degradation above cap)

## Automated Gate Configuration (Phase 9)

Add to CI pipeline (`azure-pipelines.yml`):

```yaml
- task: AzureLoadTest@1
  displayName: 'Performance gates'
  inputs:
    loadTestConfigFile: 'tests/load/memory-read.yaml'
    failCriteria:
      - 'avg(response_time_ms) > 15'
      - 'percentage(error) > 1'
```

Failing any gate blocks the PR merge.
