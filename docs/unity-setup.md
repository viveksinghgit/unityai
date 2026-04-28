# Unity Setup Guide

Step-by-step guide to get NPC Soul Engine running inside the Unity editor.

---

## Required Unity Version

**Unity 6000.0.x (Unity 6)** or later. The project uses URP, Animation Rigging, and Sentis APIs that require Unity 6.

---

## Package Requirements

Open `unity/Packages/manifest.json` — these packages must be present (they already are):

| Package | Version | Purpose |
|---------|---------|---------|
| `com.unity.sentis` | 2.1.1 | On-device ONNX inference (emotion + significance models) |
| `com.unity.netcode.gameobjects` | 2.3.1 | Multiplayer NPC state sync (optional) |
| `com.unity.animation.rigging` | 1.3.1 | Facial rig weight control |
| `com.unity.behavior` | 1.0.6 | Behavior Designer integration |
| `com.unity.ai.navigation` | 2.0.5 | NPC NavMesh movement |
| `com.unity.nuget.newtonsoft-json` | 3.2.1 | JSON deserialization |

Open the Unity project once and let Package Manager resolve all dependencies before doing anything else.

---

## Azure Speech SDK Plugin (Required for TTS)

TTS is compiled out by default (`#if SPEECH_SDK_AVAILABLE`) so the project builds without the SDK. To enable it:

### 1. Download the SDK

Download the **Azure Cognitive Services Speech SDK for Unity** from:
[https://aka.ms/csspeech/unityrelease](https://aka.ms/csspeech/unityrelease)

Extract the `.unitypackage`.

### 2. Import into the project

Double-click the `.unitypackage` in the file browser with the Unity project open. Import everything into `Assets/Plugins/`.

The critical file is:
```
Assets/Plugins/Microsoft.CognitiveServices.Speech.core.dll
```

### 3. Enable the compile symbol

**Edit → Project Settings → Player → Other Settings → Scripting Define Symbols**

Add: `SPEECH_SDK_AVAILABLE`

Click Apply. The project will recompile. `NpcTtsService` is now active.

### 4. Platform notes

- **Windows/Mac/Linux** (Editor + Standalone): Speech SDK `.dll` works directly
- **iOS**: Use the `.framework` from the package instead; add it to `Assets/Plugins/iOS/`
- **Android**: Use the `.aar` from the package; add it to `Assets/Plugins/Android/`
- **WebGL**: Speech SDK is not supported. Leave `SPEECH_SDK_AVAILABLE` undefined on WebGL builds; TTS degrades gracefully to silence.

---

## ONNX Model Files (Required for Full ML Features)

Place model files in:
```
Assets/StreamingAssets/Models/
```

| File | Used by | Required? |
|------|---------|----------|
| `emotion_classifier.onnx` | `EmotionClassifier` | No — rule-based fallback runs without it |
| `significance_scorer.onnx` | `SignificanceScorer` | No — rule-based fallback runs without it |

The rule-based fallbacks are fully functional and are the default for new installations. The ONNX models improve nuance but are not required to ship.

To export the significance scorer ONNX after training, use the same `ml/train.py` pattern adapted for the 4-input significance model.

---

## ScriptableObject Configuration

Create an `AzureSoulEngineConfig` asset:

**Assets → Create → NPC Soul Engine → Config**

Fill in these fields:

| Field | Value |
|-------|-------|
| Functions Base URL | `https://func-npc-soul-dev.azurewebsites.net` |
| Functions Host Key | Your Azure Functions host key |
| Speech Subscription Key | Your Azure Speech key (leave blank to disable TTS) |
| Speech Region | `eastus` (or your region) |
| Enable TTS | ✅ (only if Speech SDK is installed) |

Leave all other fields at their defaults for development.

---

## NPC Prefab Setup

Every NPC that participates in the Soul Engine needs this component stack on its root GameObject:

### Required Components

| Component | Notes |
|-----------|-------|
| `NpcSoulComponent` | Core — set `NpcId` to a unique string (e.g. `npc_blacksmith`) |
| `EmotionAnimationController` | Assign `Face Renderer`, rig `Brow Rig`, `Mouth Rig`, `Micro Expression Rig`, and `Body Animator` in the inspector |
| `Animator` | Body animation controller with `EmotionIndex` (int), `EmotionIntensity` (float), `TrustScore` (float), `AvoidPlayer` (bool), `AlertGuards` (bool) parameters |
| `AudioSource` | For TTS speech playback. The component auto-adds one if missing, but pre-assigning avoids a runtime allocation. |

### Optional: Animation Rigging

Add an `Animation Rigging` component hierarchy under the Animator:
```
NPC Root
 └── Rig Builder
      ├── Brow Rig      ← assign to EmotionAnimationController.browRig
      ├── Mouth Rig     ← assign to EmotionAnimationController.mouthRig
      └── MicroExpr Rig ← assign to EmotionAnimationController.microExpressionRig
```

The rigs are driven by emotion state and TTS word boundaries. Without them the blend shape system still works — the rig weight fields are set but Unity ignores them gracefully.

### Optional: Multiplayer (NGO)

If using Netcode for GameObjects, add these additional components:

| Component | Notes |
|-----------|-------|
| `NetworkObject` | Required by NGO. Check `Always Replicate` |
| `NpcSoulNetworkSync` | Handles server-authority state sync. Auto-added constraint: `RequireComponent(NpcSoulComponent)` |

The NPC prefab must be registered in `NetworkManager.NetworkPrefabs`.

**Important:** Only the server/host runs Azure API calls. Clients receive `NpcMemoryStateSyncData` via `NetworkVariable` and apply animations locally. The `StartDialogue` and `LoadMemoryCoroutine` methods return immediately (no-op) on clients.

---

## Scene Setup

### NpcSoulEngineManager

Add a single `NpcSoulEngineManager` GameObject to your initial/bootstrap scene.

1. **GameObject → Create Empty** → name it `NpcSoulEngineManager`
2. Add `NpcSoulEngineManager` component
3. Assign your `AzureSoulEngineConfig` ScriptableObject
4. Optionally set `Player Id Override` for QA testing with a fixed player ID

The manager is marked `DontDestroyOnLoad` and will persist across scene loads.

### Player Tag

The manager's proximity prefetch uses `GameObject.FindGameObjectWithTag("Player")`. Ensure your player character has the `Player` tag.

---

## Behavior Tree Integration

The following nodes are available for Behavior Designer:

| Node | Type | What it does |
|------|------|-------------|
| `FetchNpcMemoryAction` | Action | Calls `LoadMemoryCoroutine` and waits for `MemoryLoaded == true` |
| `InitiateDialogueAction` | Action | Calls `StartDialogue` and waits for response callback |
| `EvaluateTrustCondition` | Condition | Returns `true` if `CurrentMemory.trustScore > threshold` |
| `TriggerPersonalityDriftAction` | Action | Manually triggers an emotion re-evaluation |
| `AvoidPlayerAction` | Action | Activates `behaviorOverrides.avoidPlayer` locomotion |

### Example BT structure

```
Selector
 ├── Sequence (hostile path)
 │    ├── EvaluateTrustCondition (threshold: 20, invert: true)
 │    ├── FetchNpcMemoryAction
 │    └── AvoidPlayerAction
 └── Sequence (normal path)
      ├── FetchNpcMemoryAction
      └── InitiateDialogueAction
```

---

## Testing in the Editor

### Play Mode Smoke Test

1. Open any test scene with an NPC prefab and the `NpcSoulEngineManager`
2. Make sure `AzureSoulEngineConfig.functionsBaseUrl` points to your local Functions (`http://localhost:7071`) or staging
3. Press Play
4. In the BT runner, manually execute `FetchNpcMemoryAction` for an NPC
5. Check the Console for `[NpcSoulEngine]` log messages
6. `NpcSoulComponent.MemoryLoaded` should flip to `true`
7. `EmotionAnimationController` blend shape targets should update

### Unity Test Runner

**Window → General → Test Runner → Edit Mode → Run All**

This runs all tests in `unity/Tests/`:
- `EmotionClassifierTests` (14 tests)
- `CircuitBreakerTests`
- `NpcMemoryCacheTests`
- `EmotionalWeightTests`
- `SignificanceScorerTests`

No Play mode or Azure connection required — all tests use rule-based fallbacks.

---

## Common Issues

| Problem | Cause | Fix |
|---------|-------|-----|
| `[NpcSoulEngine] NpcSoulComponent has no NpcId` | NpcId field is blank in inspector | Set a unique string ID on the component |
| `NpcTtsService: Speech SDK not available` | `SPEECH_SDK_AVAILABLE` symbol not set | See "Azure Speech SDK Plugin" section above |
| `EmotionClassifier: ONNX model not found` | Model file missing from StreamingAssets | Normal — rule-based fallback is active. Place the `.onnx` file to enable the model. |
| `AzureMemoryService: Circuit open` | Azure Functions unreachable | Check `functionsBaseUrl` and `functionsHostKey` in the config asset. Circuit resets after 30s. |
| `NpcSoulNetworkSync not found` | NGO component missing | Either add `NpcSoulNetworkSync` to the prefab or remove the NGO package if you're building single-player. |
| Blend shapes not animating | `faceRenderer` unassigned | Assign the NPC's `SkinnedMeshRenderer` to `EmotionAnimationController.faceRenderer` |
