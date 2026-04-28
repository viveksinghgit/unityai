"""
Azure ML managed online endpoint scoring script.

init() is called once at container start to load the model.
run()  is called per-request.

Request body  (JSON): {"features": [[f0, f1, ..., f9]]}
Response body (JSON): {
    "archetypes":    ["hero"],
    "probabilities": [[0.02, 0.05, 0.08, 0.76, 0.05, 0.04]],
    "labels":        ["aggressor", "benefactor", "diplomat", "hero", "neutral", "trickster"]
}
"""

import json
import logging
import os

import joblib
import numpy as np

logger = logging.getLogger(__name__)
_model_artifact = None


def init() -> None:
    global _model_artifact
    model_dir  = os.getenv("AZUREML_MODEL_DIR", ".")
    model_path = os.path.join(model_dir, "archetype_classifier.pkl")
    _model_artifact = joblib.load(model_path)
    logger.info("Archetype classifier loaded from %s", model_path)


def run(raw_data: str) -> str:
    try:
        data = json.loads(raw_data)
    except json.JSONDecodeError as exc:
        return json.dumps({"error": f"Invalid JSON: {exc}"})

    if "features" not in data:
        return json.dumps({"error": "Request must contain a 'features' key"})

    X         = np.array(data["features"], dtype=np.float32)
    pipeline  = _model_artifact["pipeline"]
    labels    = _model_artifact["labels"]

    predictions   = pipeline.predict(X).tolist()
    probabilities = pipeline.predict_proba(X).tolist()

    return json.dumps({
        "archetypes":    predictions,
        "probabilities": probabilities,
        "labels":        labels,
    })
