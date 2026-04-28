"""
Azure ML training job — NPC player archetype classifier.

Input : labeled CSV with 10 feature columns + 'archetype' label column.
Output: scikit-learn GradientBoosting pipeline saved to outputs/ and
        registered as the 'archetype-classifier' model in the ML workspace.

Feature columns (must appear in this order):
  avg_trust, avg_fear, avg_hostility, avg_respect,
  combat_initiation_rate, dialogue_choice_aggression,
  trade_deception_rate, promise_broken_rate,
  avg_time_between_actions, reputation_awareness_score

Label classes (alphabetical — matches AzureMLArchetypeClassifier.Labels):
  aggressor, benefactor, diplomat, hero, neutral, trickster
"""

import argparse
import os

import joblib
import mlflow
import mlflow.sklearn
import numpy as np
import pandas as pd
from sklearn.ensemble import GradientBoostingClassifier
from sklearn.metrics import classification_report
from sklearn.model_selection import cross_val_score, train_test_split
from sklearn.pipeline import Pipeline
from sklearn.preprocessing import StandardScaler

FEATURES = [
    "avg_trust", "avg_fear", "avg_hostility", "avg_respect",
    "combat_initiation_rate", "dialogue_choice_aggression",
    "trade_deception_rate", "promise_broken_rate",
    "avg_time_between_actions", "reputation_awareness_score",
]
LABELS = ["aggressor", "benefactor", "diplomat", "hero", "neutral", "trickster"]


def parse_args() -> argparse.Namespace:
    p = argparse.ArgumentParser()
    p.add_argument("--data",          type=str, required=True, help="Path to labeled CSV")
    p.add_argument("--model-dir",     type=str, default="outputs")
    p.add_argument("--n-estimators",  type=int,   default=200)
    p.add_argument("--max-depth",     type=int,   default=5)
    p.add_argument("--learning-rate", type=float, default=0.1)
    p.add_argument("--test-size",     type=float, default=0.2)
    return p.parse_args()


def main() -> None:
    args = parse_args()
    os.makedirs(args.model_dir, exist_ok=True)

    mlflow.sklearn.autolog(log_model_signatures=True, log_input_examples=True)

    df = pd.read_csv(args.data)
    missing = [c for c in FEATURES + ["archetype"] if c not in df.columns]
    if missing:
        raise ValueError(f"Missing columns in input data: {missing}")

    X = df[FEATURES].values.astype(np.float32)
    y = df["archetype"].values

    X_train, X_test, y_train, y_test = train_test_split(
        X, y,
        test_size=args.test_size,
        random_state=42,
        stratify=y,
    )

    pipeline = Pipeline([
        ("scaler", StandardScaler()),
        ("clf", GradientBoostingClassifier(
            n_estimators=args.n_estimators,
            max_depth=args.max_depth,
            learning_rate=args.learning_rate,
            random_state=42,
            n_iter_no_change=20,
            validation_fraction=0.1,
        )),
    ])

    pipeline.fit(X_train, y_train)

    cv_acc  = cross_val_score(pipeline, X_train, y_train, cv=5, scoring="accuracy").mean()
    test_acc = pipeline.score(X_test, y_test)

    mlflow.log_metric("cv_accuracy",   float(cv_acc))
    mlflow.log_metric("test_accuracy", float(test_acc))

    report = classification_report(
        y_test, pipeline.predict(X_test),
        labels=LABELS, output_dict=True, zero_division=0,
    )
    for label in LABELS:
        if label in report:
            mlflow.log_metric(f"f1_{label}", float(report[label]["f1-score"]))

    model_path = os.path.join(args.model_dir, "archetype_classifier.pkl")
    joblib.dump({"pipeline": pipeline, "labels": LABELS, "features": FEATURES}, model_path)
    mlflow.log_artifact(model_path)

    print(f"CV accuracy : {cv_acc:.4f}")
    print(f"Test accuracy: {test_acc:.4f}")
    print(classification_report(y_test, pipeline.predict(X_test), labels=LABELS, zero_division=0))


if __name__ == "__main__":
    main()
