# NPC Soul Engine

Persistent, emotionally-aware NPC memory for Unity games powered by Azure AI.

Every NPC remembers every player across sessions. A blacksmith recalls that you betrayed him six weeks ago; a guard's fear rises after witnessing unprovoked violence; an ally's trust compounds through repeated kindness. Emotional state drives facial animation and dialogue tone in real time — without per-session scripting.

---

## Architecture

```
┌─────────────────────────────────────────────────────────────────────┐
│                           Unity Client                               │
│                                                                      │
│  ┌──────────────────┐  ┌──────────────────┐  ┌──────────────────┐  │
│  │ NpcSoulComponent │  │EmotionAnimCtrl   │  │NpcSoulNetworkSync│  │
│  │ LoadMemory       │  │ Blend shapes     │  │(NGO, optional)   │  │
│  │ StartDialogue    │  │ Rig weights      │  │ Server authority │  │
│  │ ApplyMemoryState │  │ Mouth / TTS sync │  │ NetworkVariable  │  │
│  └────────┬─────────┘  └──────────────────┘  └──────────────────┘  │
│           │                                                          │
│  ┌────────▼──────────────────────────────────────────────────────┐  │
│  │                    AzureMemoryService                          │  │
│  │        LRU Cache → Circuit Breaker → HTTP Client              │  │
│  └────────────────────────────┬───────────────────────────────────┘  │
│                                │                                      │
│  ┌──────────────────┐  ┌───────▼──────────────┐                      │
│  │ SignificanceScorer│  │   NpcTtsService       │                      │
│  │ (Sentis ONNX)    │  │ Azure Speech SDK      │                      │
│  │ On-device filter │  │ Word-boundary lip sync│                      │
│  └──────────────────┘  └──────────────────────┘                      │
└────────────────────────────────┬────────────────────────────────────┘
                                 │ HTTPS  x-functions-key
┌────────────────────────────────▼────────────────────────────────────┐
│                    Azure Functions  (Isolated Worker .NET 8)         │
│                                                                      │
│  POST /api/memory/process-event    GET /api/memory/{npc}/{player}   │
│  POST /api/dialogue/generate       POST /api/gossip/broadcast        │
│                                                                      │
│  PromptInjectionGuard · TokenBudgetTracker · SemanticResponseCache  │
│  ContentSafetyValidator · EmotionalWeightCalculator · GossipService │
└──────┬──────────────────┬──────────────────────────┬────────────────┘
       │                  │                           │
       ▼                  ▼                           ▼
  Cosmos DB          Azure OpenAI               Service Bus
  npc-memory-graphs  GPT-4o (high significance) gossip-topic
  player-archetypes  GPT-4o-mini (low)          memory-consolidation
                     (AI Foundry)
       │                  │                           │
       ▼                  ▼                           ▼
  Timer Jobs:        Azure ML                   Azure Speech
  MemoryDecayJob     archetype-classifier        Neural TTS
  ConsolidationJob   (managed online endpoint)   SSML → AudioClip
  ArchetypeReclass
```

---

## Feature Matrix

| Phase | Feature | Status |
|-------|---------|--------|
| 0 | Cosmos DB schema, Ebbinghaus memory decay, salient event scoring | ✅ |
| 1 | Azure Functions: ProcessEvent, GetMemory, significance filter | ✅ |
| 2 | GPT-4o dialogue generation, SSE streaming, SSML output | ✅ |
| 3 | Prompt injection hardening, content safety, token budget, semantic cache | ✅ |
| 4 | Gossip propagation (Service Bus), social graph, memory consolidation | ✅ |
| 5 | Unity client: NpcSoulComponent, AzureMemoryService, circuit breaker, LRU cache, BT nodes | ✅ |
| 6 | Azure TTS + word-boundary lip sync, Sentis ONNX emotion classifier | ✅ |
| 7 | Azure ML archetype pipeline: feature engineering, GradientBoosting, managed endpoint | ✅ |
| 8 | NGO multiplayer sync: server authority, NetworkVariable, client no-op guard | ✅ |
| 9 | Chaos tests, Azure Load Testing, GitHub Actions CI/CD with performance gate | ✅ |

---

## Repository Layout

```
npc-soul-engine/
├── functions/                     # Azure Functions app (.NET 8 isolated worker)
│   ├── Functions/
│   │   ├── MemoryFunctions.cs     # ProcessEvent, GetMemory HTTP triggers
│   │   ├── DialogueFunction.cs    # GenerateDialogue (streaming + non-streaming)
│   │   ├── GossipFunction.cs      # BroadcastGossip, GossipProcessor
│   │   └── DecayJobs.cs           # MemoryDecay, Consolidation, ArchetypeReclass (timer)
│   ├── Services/
│   │   ├── CosmosMemoryStore.cs
│   │   ├── DialoguePromptBuilder.cs
│   │   ├── EmotionalWeightCalculator.cs
│   │   ├── GossipService.cs
│   │   ├── MemoryDecayService.cs
│   │   ├── ArchetypeClassifierService.cs  # Azure ML + rule-based fallback
│   │   ├── ContentSafetyValidator.cs
│   │   ├── PromptInjectionGuard.cs
│   │   ├── SemanticResponseCache.cs
│   │   └── TokenBudgetTracker.cs
│   ├── Models/
│   │   ├── ApiContracts.cs        # NpcMemoryState, DialogueRequest/Response, etc.
│   │   └── NpcMemoryDocument.cs   # Cosmos document model
│   └── Program.cs                 # DI registration, FunctionConfig
│
├── functions.Tests/               # NUnit test suite (no Unity dependency)
│   ├── ArchetypeClassifierTests.cs
│   ├── ChaosTests.cs              # Fault-injection (HTTP failures, Cosmos failures)
│   ├── GossipPropagationTests.cs
│   ├── MemoryDecayTests.cs
│   ├── PromptInjectionTests.cs
│   ├── ResponseValidationTests.cs
│   └── TokenBudgetTests.cs
│
├── unity/
│   ├── Runtime/
│   │   ├── Components/
│   │   │   ├── NpcSoulComponent.cs          # Per-NPC MonoBehaviour
│   │   │   ├── NpcSoulEngineManager.cs      # Singleton, scene lifecycle
│   │   │   ├── NpcSoulNetworkSync.cs        # NGO NetworkBehaviour (optional)
│   │   │   └── EmotionAnimationController.cs
│   │   ├── Inference/
│   │   │   ├── EmotionClassifier.cs         # Sentis ONNX + rule-based fallback
│   │   │   └── SignificanceScorer.cs
│   │   ├── Services/
│   │   │   ├── AzureMemoryService.cs        # LRU cache + circuit breaker
│   │   │   ├── NpcTtsService.cs             # Azure Speech SDK, word boundaries
│   │   │   └── NpcMemoryCache.cs
│   │   ├── BehaviorTree/                    # Behavior Designer integration nodes
│   │   └── Models/                          # Serializable Unity-side contracts
│   └── Tests/                               # Unity Test Runner (Editor mode)
│
├── ml/
│   ├── train.py                   # GradientBoosting archetype classifier
│   ├── score.py                   # Managed online endpoint scoring script
│   ├── environment.yml            # Conda env (scikit-learn 1.5.2, MLflow)
│   └── pipeline.yml               # Azure ML command job submission YAML
│
├── infra/
│   ├── main.bicep                 # Root Bicep template
│   └── modules/                   # cosmos, functions, mlworkspace, keyvault, ...
│
├── .azure/
│   ├── load-test.yaml             # Azure Load Testing config (p95 < 200ms gate)
│   └── locustfile.py              # Locust traffic simulation
│
├── .github/workflows/
│   └── ci.yml                     # Build → Unit tests → Deploy staging → Load test → Prod
│
└── docs/
    ├── api-contracts.yaml         # OpenAPI 3.1 spec for all HTTP endpoints
    ├── cost-estimates.md          # Per-scale cost breakdown with optimization levers
    ├── deployment.md              # Step-by-step Azure + CI/CD setup
    ├── unity-setup.md             # Unity editor, SDK plugins, prefab wiring
    └── performance-baselines.md   # Latency targets and CI gate thresholds
```

---

## Quick Start — Local Development

### Prerequisites

- .NET 8 SDK
- Azure Functions Core Tools v4: `npm install -g azure-functions-core-tools@4`
- Docker (for Cosmos emulator)
- Unity 6000.0+ with packages: `com.unity.sentis`, `com.unity.netcode.gameobjects`, `com.unity.animation.rigging`

### 1. Start the Cosmos emulator

```bash
docker run -d -p 8081:8081 -p 10251-10254:10251-10254 \
  --name cosmos-emulator \
  mcr.microsoft.com/cosmosdb/linux/azure-cosmos-emulator
```

### 2. Configure local settings

Copy and fill in real values (OpenAI endpoint and key are the minimum needed for dialogue):

```bash
cp functions/local.settings.json functions/local.settings.local.json
# Edit local.settings.local.json with your Azure credentials
```

The Cosmos connection string for the emulator is pre-filled. Everything else degrades gracefully without real credentials:
- No `OpenAiKey` → dialogue returns a fallback message
- No `ContentSafetyKey` → safety check is skipped (logged as warning)
- No `AzureMLEndpointUri` → archetype uses rule-based classifier
- No Speech SDK DLL → TTS is silently disabled

### 3. Run the Functions app

```bash
cd functions
func start
```

### 4. Send a test event

```bash
curl -X POST http://localhost:7071/api/memory/process-event \
  -H "Content-Type: application/json" \
  -d '{
    "npcId": "npc_blacksmith",
    "playerId": "player_001",
    "actionType": "Kindness",
    "context": {
      "summary": "Player helped carry heavy goods",
      "stakes": "Low",
      "publicness": 0.8
    }
  }'
```

```bash
curl http://localhost:7071/api/memory/npc_blacksmith/player_001
```

---

## Deployment

See [docs/deployment.md](docs/deployment.md) for the full step-by-step guide covering:
- Azure infrastructure provisioning (Bicep)
- GitHub Actions secrets and OIDC setup
- Azure ML training job submission and endpoint deployment
- Slot swap promotion pattern (staging → production)

---

## Unity Integration

See [docs/unity-setup.md](docs/unity-setup.md) for:
- Required Unity packages and versions
- Azure Speech SDK DLL installation
- NPC prefab wiring (component checklist)
- `AzureSoulEngineConfig` ScriptableObject fields
- Behavior Designer node reference
- Multiplayer (NGO) setup

---

## Testing

```bash
# All server-side unit + chaos tests (no Azure required)
dotnet test functions.Tests/NpcSoulEngine.Functions.Tests.csproj -v normal

# Unity tests (open project in Unity, run via Test Runner window)
# Window → General → Test Runner → Edit Mode → Run All
```

Test coverage by module:

| Suite | Tests | What it covers |
|-------|-------|----------------|
| `ArchetypeClassifierTests` | 18 | Feature extraction, all 6 archetypes, priority ordering, score integrity |
| `ChaosTests` | 6 | HTTP 500/timeout/malformed JSON fallbacks, job fault isolation |
| `MemoryDecayTests` | — | Ebbinghaus decay rates, consolidation triggers |
| `GossipPropagationTests` | — | Hop attenuation, social graph routing |
| `PromptInjectionTests` | — | 15 injection patterns blocked |
| `EmotionClassifierTests` | 14 | All 8 emotion branches, PAD vector signs, intensity clamping |
| `CircuitBreakerTests` | — | Open/half-open/closed state transitions |
| `NpcMemoryCacheTests` | — | LRU eviction, dirty-flag tracking |

---

## Configuration Reference

All fields live in `AzureSoulEngineConfig` (Unity ScriptableObject) and `local.settings.json` / Azure App Settings (server).

### Server (`FunctionConfig`)

| Setting | Default | Description |
|---------|---------|-------------|
| `Gpt4oDeploymentName` | `gpt-4o` | High-significance dialogue model |
| `Gpt4oMiniDeploymentName` | `gpt-4o-mini` | Low-significance events |
| `MaxTokensPerDialogueCall` | `2000` | Total context budget |
| `MaxOutputTokensPerDialogueCall` | `400` | Max response length |
| `DialogueCacheTtlMinutes` | `5` | Semantic cache TTL |
| `DialogueCacheMaxEntries` | `200` | Semantic cache size |
| `AzureMLEndpointUri` | _(empty)_ | Archetype classifier endpoint; empty = rule-based |
| `AzureMLEndpointKey` | _(empty)_ | Bearer token for the ML endpoint |

### Unity (`AzureSoulEngineConfig`)

| Field | Default | Description |
|-------|---------|-------------|
| `functionsBaseUrl` | — | Azure Functions app base URL |
| `functionsHostKey` | — | `x-functions-key` header value |
| `memoryReadTimeoutSeconds` | `2` | GET /memory timeout |
| `memoryEventTimeoutSeconds` | `8` | POST /event timeout |
| `dialogueTimeoutSeconds` | `15` | Dialogue generation timeout |
| `localCacheCapacity` | `50` | LRU cache slots (NPC×player pairs) |
| `cacheSyncIntervalSeconds` | `60` | Dirty-cache flush interval |
| `circuitBreakerFailureThreshold` | `10` | Failures before circuit opens |
| `circuitBreakerResetSeconds` | `30` | Open→half-open wait |
| `significanceThreshold` | `0.3` | Minimum score to send event to Azure |
| `prefetchRadiusMetres` | `30` | Distance to warm NPC memory |
| `enableTts` | `true` | Requires Speech SDK DLL + credentials |
| `speechSubscriptionKey` | — | Azure Speech Services key |
| `speechRegion` | — | e.g. `eastus` |

---

## Cost Estimates

See [docs/cost-estimates.md](docs/cost-estimates.md) for full per-scale breakdown.

**Summary** (with all optimizations enabled):

| Scale | Monthly estimate |
|-------|-----------------|
| Dev / staging | ~$30–80 |
| 100 concurrent players | ~$200–250 |
| 1,000 concurrent players | ~$1,500–1,800 |
| 10,000 concurrent players | ~$12,000–15,000 |

The largest cost driver at every scale is GPT-4o dialogue generation. The significance filter (on-device Sentis) reduces LLM calls by ~70%; the semantic response cache adds a further ~20% reduction.

---

## Contributing

1. Branch from `main` — `feat/`, `fix/`, `chore/` prefixes
2. All server-side unit tests must pass: `dotnet test`
3. No new public API without an entry in `docs/api-contracts.yaml`
4. CI enforces p95 < 200 ms on staging before production promotion

## License

MIT
