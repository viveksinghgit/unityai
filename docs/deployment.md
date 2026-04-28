# Deployment Guide

End-to-end steps from a clean Azure subscription to a running production system.

---

## Prerequisites

| Tool | Version | Install |
|------|---------|---------|
| Azure CLI | ≥ 2.60 | `winget install Microsoft.AzureCLI` |
| Azure Functions Core Tools | v4 | `npm i -g azure-functions-core-tools@4` |
| .NET SDK | 8.0 | [dotnet.microsoft.com](https://dotnet.microsoft.com) |
| Python | 3.11 | For ML training scripts |
| Azure ML CLI extension | v2 | `az extension add -n ml` |

---

## Step 1 — Azure Login and Subscription

```bash
az login
az account set --subscription "YOUR_SUBSCRIPTION_NAME_OR_ID"
```

---

## Step 2 — Provision Infrastructure

The Bicep templates in `infra/` create all required resources in one command.

```bash
# Create the resource group
az group create \
  --name rg-npc-soul-engine \
  --location eastus

# Preview the deployment (no changes made)
az deployment group what-if \
  --resource-group rg-npc-soul-engine \
  --template-file infra/main.bicep \
  --parameters @infra/parameters/dev.json

# Deploy
az deployment group create \
  --resource-group rg-npc-soul-engine \
  --template-file infra/main.bicep \
  --parameters @infra/parameters/dev.json \
  --name npc-soul-initial
```

**What gets created:**
- Azure Functions app (`func-npc-soul-dev`) with staging slot
- Cosmos DB account with `NpcSoulEngine` database + 4 containers
- Azure OpenAI account (AI Foundry) with GPT-4o and GPT-4o-mini deployments
- Azure Cognitive Services (Speech + Content Safety)
- Service Bus namespace with `npc-gossip` topic and `memory-consolidation` queue
- Azure ML workspace with `training-cluster` compute + `archetype-classifier` endpoint
- Key Vault (all secrets stored here; Functions pulls via managed identity)
- App Insights workspace
- Storage account

For production, switch to `infra/parameters/prod.json` (higher Cosmos RU/s, Premium Functions plan, multi-region).

---

## Step 3 — Populate Key Vault Secrets

The Bicep creates the Key Vault but cannot populate secrets from the CLI without your credentials available. Add them manually:

```bash
KV_NAME=$(az deployment group show \
  --resource-group rg-npc-soul-engine \
  --name npc-soul-initial \
  --query properties.outputs.keyVaultName.value -o tsv)

# Azure OpenAI
az keyvault secret set --vault-name $KV_NAME \
  --name "OpenAiKey" --value "YOUR_OPENAI_KEY"

az keyvault secret set --vault-name $KV_NAME \
  --name "OpenAiEndpoint" --value "https://YOUR-FOUNDRY.openai.azure.com/"

# Azure Speech
az keyvault secret set --vault-name $KV_NAME \
  --name "SpeechSubscriptionKey" --value "YOUR_SPEECH_KEY"

# Content Safety
az keyvault secret set --vault-name $KV_NAME \
  --name "ContentSafetyKey" --value "YOUR_CONTENT_SAFETY_KEY"

az keyvault secret set --vault-name $KV_NAME \
  --name "ContentSafetyEndpoint" --value "https://YOUR-REGION.api.cognitive.microsoft.com/"
```

The Functions app's managed identity already has `Key Vault Secrets User` role from the Bicep template.

---

## Step 4 — Deploy the Functions App Manually (first time)

```bash
cd functions
dotnet publish -c Release -o ./publish

FUNC_APP=$(az deployment group show \
  --resource-group rg-npc-soul-engine \
  --name npc-soul-initial \
  --query properties.outputs.functionAppName.value -o tsv)

az functionapp deployment source config-zip \
  --resource-group rg-npc-soul-engine \
  --name $FUNC_APP \
  --src ./publish.zip
```

After the first manual deploy, all subsequent deployments are handled by GitHub Actions.

### Verify it's running

```bash
FUNC_URL=$(az functionapp show \
  --resource-group rg-npc-soul-engine \
  --name $FUNC_APP \
  --query defaultHostName -o tsv)

HOST_KEY=$(az functionapp keys list \
  --resource-group rg-npc-soul-engine \
  --name $FUNC_APP \
  --query functionKeys.default -o tsv)

# Health check
curl "https://$FUNC_URL/api/memory/npc_test/player_test" \
  -H "x-functions-key: $HOST_KEY"
# Expected: {"npcId":"npc_test","playerId":"player_test","trustScore":50,...}
```

---

## Step 5 — GitHub Actions CI/CD Setup

### 5a. Create a Service Principal with Federated Credentials (OIDC)

No long-lived secrets are stored in GitHub. GitHub Actions authenticates with Azure using short-lived OIDC tokens.

```bash
# Create the app registration
APP_ID=$(az ad app create \
  --display-name "npc-soul-github-actions" \
  --query appId -o tsv)

# Create service principal
SP_ID=$(az ad sp create --id $APP_ID --query id -o tsv)

# Assign Contributor role on the resource group
az role assignment create \
  --assignee $APP_ID \
  --role Contributor \
  --scope "/subscriptions/$(az account show --query id -o tsv)/resourceGroups/rg-npc-soul-engine"

# Add federated credential for GitHub Actions
# Replace YOUR_ORG and YOUR_REPO
az ad app federated-credential create \
  --id $APP_ID \
  --parameters '{
    "name": "github-main",
    "issuer": "https://token.actions.githubusercontent.com",
    "subject": "repo:YOUR_ORG/YOUR_REPO:ref:refs/heads/main",
    "audiences": ["api://AzureADTokenExchange"]
  }'

# Also add for pull requests (so test jobs work on PRs)
az ad app federated-credential create \
  --id $APP_ID \
  --parameters '{
    "name": "github-pr",
    "issuer": "https://token.actions.githubusercontent.com",
    "subject": "repo:YOUR_ORG/YOUR_REPO:pull_request",
    "audiences": ["api://AzureADTokenExchange"]
  }'

# Print the three values needed as GitHub secrets
echo "AZURE_CLIENT_ID: $APP_ID"
echo "AZURE_TENANT_ID: $(az account show --query tenantId -o tsv)"
echo "AZURE_SUBSCRIPTION_ID: $(az account show --query id -o tsv)"
```

### 5b. Add GitHub Secrets

In your GitHub repository: **Settings → Secrets and variables → Actions → New repository secret**

| Secret | Value |
|--------|-------|
| `AZURE_CLIENT_ID` | App registration client ID (printed above) |
| `AZURE_TENANT_ID` | Tenant ID (printed above) |
| `AZURE_SUBSCRIPTION_ID` | Subscription ID (printed above) |
| `STAGING_FUNCTIONS_HOST_KEY` | Host key from the staging Functions slot |

### 5c. Create GitHub Environments

In your GitHub repository: **Settings → Environments**

1. Create `staging` — no protection rules
2. Create `production` — add yourself as Required Reviewer

The CI pipeline in `.github/workflows/ci.yml` targets these environments and will pause before promoting staging to production until you approve.

### 5d. Create the Azure Load Testing Resource

```bash
az load create \
  --name npc-soul-load-test \
  --resource-group rg-npc-soul-engine \
  --location eastus

# Grant the GitHub Actions service principal access
az role assignment create \
  --assignee $APP_ID \
  --role "Load Test Contributor" \
  --scope $(az load show --name npc-soul-load-test \
    --resource-group rg-npc-soul-engine --query id -o tsv)
```

---

## Step 6 — Azure ML Archetype Pipeline

This step is optional. `ArchetypeReclassificationJob` uses the rule-based fallback until the endpoint is live.

### 6a. Prepare labeled training data

Create a CSV with these columns:
```
avg_trust,avg_fear,avg_hostility,avg_respect,combat_initiation_rate,
dialogue_choice_aggression,trade_deception_rate,promise_broken_rate,
avg_time_between_actions,reputation_awareness_score,archetype
```

Labels must be one of: `aggressor`, `benefactor`, `diplomat`, `hero`, `neutral`, `trickster`

You can bootstrap labels from the rule-based classifier:

```python
# bootstrap_labels.py — run against your dev Cosmos data
import json, requests

docs = [...]  # fetch from Cosmos
for doc in docs:
    features = extract_features(doc)  # avg scores from NpcMemoryDocuments
    label = rule_based_classify(features)  # apply same thresholds as C# code
    print(f"{','.join(map(str, features.to_array()))},{label}")
```

### 6b. Register the data asset and submit the training job

```bash
# Register the CSV as a data asset
az ml data create \
  --name archetype-labels \
  --version 1 \
  --path ./ml/data/labeled_players.csv \
  --type uri_file \
  --workspace-name YOUR_ML_WORKSPACE \
  --resource-group rg-npc-soul-engine

# Submit the training job
JOB_NAME=$(az ml job create \
  --file ml/pipeline.yml \
  --workspace-name YOUR_ML_WORKSPACE \
  --resource-group rg-npc-soul-engine \
  --query name -o tsv)

# Stream logs
az ml job stream --name $JOB_NAME \
  --workspace-name YOUR_ML_WORKSPACE \
  --resource-group rg-npc-soul-engine
```

### 6c. Deploy the trained model

```bash
# After job completes, deploy to the pre-existing online endpoint
az ml online-deployment create \
  --name blue \
  --endpoint archetype-classifier \
  --model azureml:archetype-classifier@latest \
  --instance-type Standard_DS2_v2 \
  --instance-count 1 \
  --scoring-script score.py \
  --code-path ./ml \
  --environment azureml:archetype-classifier-env@latest \
  --workspace-name YOUR_ML_WORKSPACE \
  --resource-group rg-npc-soul-engine

# Route 100% of traffic to the new deployment
az ml online-endpoint update \
  --name archetype-classifier \
  --traffic "blue=100" \
  --workspace-name YOUR_ML_WORKSPACE \
  --resource-group rg-npc-soul-engine

# Get the scoring URI and key, then add to Functions app settings
ENDPOINT_URI=$(az ml online-endpoint show \
  --name archetype-classifier \
  --workspace-name YOUR_ML_WORKSPACE \
  --resource-group rg-npc-soul-engine \
  --query scoring_uri -o tsv)

ENDPOINT_KEY=$(az ml online-endpoint get-credentials \
  --name archetype-classifier \
  --workspace-name YOUR_ML_WORKSPACE \
  --resource-group rg-npc-soul-engine \
  --query primaryKey -o tsv)

az functionapp config appsettings set \
  --resource-group rg-npc-soul-engine \
  --name $FUNC_APP \
  --settings "AzureMLEndpointUri=$ENDPOINT_URI" "AzureMLEndpointKey=$ENDPOINT_KEY"
```

---

## Step 7 — Verify the Full Pipeline

```bash
# 1. Send a memory event
curl -X POST "https://$FUNC_URL/api/memory/process-event" \
  -H "x-functions-key: $HOST_KEY" \
  -H "Content-Type: application/json" \
  -d '{
    "npcId": "npc_blacksmith",
    "playerId": "player_001",
    "actionType": "Betrayal",
    "context": {
      "summary": "Player sold stolen goods through blacksmith",
      "stakes": "High",
      "publicness": 0.9
    }
  }'

# 2. Check updated memory state
curl "https://$FUNC_URL/api/memory/npc_blacksmith/player_001" \
  -H "x-functions-key: $HOST_KEY"
# Expect: hostilityScore > 0, trustScore < 50

# 3. Generate dialogue
curl -X POST "https://$FUNC_URL/api/dialogue/generate" \
  -H "x-functions-key: $HOST_KEY" \
  -H "Content-Type: application/json" \
  -d '{
    "npcId": "npc_blacksmith",
    "playerId": "player_001",
    "utterance": "Good morning! Got any work for me?",
    "streaming": false,
    "npcProfile": {
      "name": "Gareth the Blacksmith",
      "baseDescription": "A gruff but honest craftsman who values reputation above all."
    }
  }'
# Expect: cold/dismissive response reflecting the betrayal

# 4. Trigger archetype reclassification manually
curl -X POST "https://$FUNC_URL/admin/functions/ArchetypeReclassificationJob" \
  -H "x-functions-key: $HOST_KEY" \
  -H "Content-Type: application/json" \
  -d '{}'
```

---

## Ongoing Operations

### Monitoring

- **App Insights**: Function failures, latency percentiles, dependency calls
- **Cosmos DB Metrics**: RU consumption, throttled requests (watch for 429s)
- **Azure OpenAI Metrics**: Token usage, rate limit errors
- **Circuit Breaker state**: logged to App Insights as custom events from `CircuitBreaker.cs`

### Rotating Secrets

All secrets are in Key Vault. Rotate without downtime:
1. Add new secret version in Key Vault
2. Functions app picks up the new version automatically (no restart required with Key Vault references)

### Scaling

The Functions Consumption plan scales to zero automatically. For sustained load > 500 RPS, switch to the **Premium EP1** plan in `infra/parameters/prod.json` to eliminate cold starts.

Cosmos DB: adjust RU/s in the portal or via CLI — no downtime required.
