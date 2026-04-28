"""
Locust load test for NPC Soul Engine Azure Functions.

Simulates typical game-client traffic patterns:
  - 60% GetMemory  (fast read, 2s timeout in prod)
  - 30% PostEvent  (memory event, 8s timeout)
  - 10% Dialogue   (generate dialogue, 15s timeout)

Environment variables:
  FUNCTIONS_BASE_URL  — e.g. https://func-npc-soul-staging.azurewebsites.net
  FUNCTIONS_HOST_KEY  — Azure Functions host key
"""

import json
import os
import random

from locust import HttpUser, between, task

BASE_URL   = os.environ.get("FUNCTIONS_BASE_URL", "http://localhost:7071")
HOST_KEY   = os.environ.get("FUNCTIONS_HOST_KEY", "")

NPC_IDS    = [f"npc_{i:03d}" for i in range(1, 11)]
PLAYER_IDS = [f"player_{i:03d}" for i in range(1, 21)]

HEADERS = {
    "Content-Type": "application/json",
    "x-functions-key": HOST_KEY,
}


class NpcSoulEngineUser(HttpUser):
    wait_time = between(0.5, 2.0)
    host = BASE_URL

    @task(6)
    def get_memory(self):
        npc_id    = random.choice(NPC_IDS)
        player_id = random.choice(PLAYER_IDS)
        with self.client.get(
            f"/api/memory/{npc_id}/{player_id}",
            headers=HEADERS,
            name="/api/memory/[npcId]/[playerId]",
            catch_response=True,
        ) as resp:
            if resp.status_code not in (200, 404):
                resp.failure(f"Unexpected status {resp.status_code}")

    @task(3)
    def post_event(self):
        payload = {
            "npcId":      random.choice(NPC_IDS),
            "playerId":   random.choice(PLAYER_IDS),
            "actionType": random.choice(["GreetingGiven", "TradeCompleted", "AttackInitiated"]),
            "context": {
                "sceneName": "TownSquare",
                "stakes":    "Medium",
                "publicness": round(random.uniform(0, 1), 2),
                "summary":   "Load test event",
            },
            "timestamp": "2026-04-28T00:00:00Z",
            "zone":      "zone_default",
        }
        with self.client.post(
            "/api/memory/event",
            data=json.dumps(payload),
            headers=HEADERS,
            name="/api/memory/event",
            catch_response=True,
        ) as resp:
            if resp.status_code != 200:
                resp.failure(f"Unexpected status {resp.status_code}")

    @task(1)
    def generate_dialogue(self):
        payload = {
            "npcId":     random.choice(NPC_IDS),
            "playerId":  random.choice(PLAYER_IDS),
            "utterance": "Hello, do you have any work for me?",
            "streaming": False,
        }
        with self.client.post(
            "/api/dialogue/generate",
            data=json.dumps(payload),
            headers=HEADERS,
            name="/api/dialogue/generate",
            catch_response=True,
            timeout=20,
        ) as resp:
            if resp.status_code != 200:
                resp.failure(f"Unexpected status {resp.status_code}")
