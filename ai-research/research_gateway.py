\
"""
AI Clinic Cardio Guardian — LOCAL RESEARCH GATEWAY

This service is intentionally localhost-only and refuses analysis until a real
research model plugin is installed. It is a bridge between the C# workstation
and a future specialized medical-vision model; it is NOT a diagnostic model.
"""

from __future__ import annotations

from datetime import datetime, timezone
from typing import Any
from fastapi import FastAPI, HTTPException
from pydantic import BaseModel

try:
    from model_plugin import MODEL_PLUGIN
except Exception:
    MODEL_PLUGIN = None

app = FastAPI(title="AI Clinic Cardio Guardian Research Gateway", version="1.1.1")


class SeriesRequest(BaseModel):
    sourceId: str
    studyInstanceUid: str
    seriesInstanceUid: str
    modality: str = ""
    projection: str = ""
    frameCount: int = 0
    estimatedFramesPerSecond: float = 0.0
    filePaths: list[str]


@app.get("/health")
def health() -> dict[str, Any]:
    loaded = bool(MODEL_PLUGIN and getattr(MODEL_PLUGIN, "is_loaded", False))
    return {
        "service": "AI Clinic Cardio Guardian Research Gateway",
        "version": "1.1.1",
        "modelLoaded": loaded,
        "message": (
            "Research model plugin loaded."
            if loaded
            else "No research medical-vision model loaded. Analysis is blocked."
        ),
    }


@app.post("/analyze-series")
def analyze_series(request: SeriesRequest) -> dict[str, Any]:
    if not MODEL_PLUGIN or not getattr(MODEL_PLUGIN, "is_loaded", False):
        raise HTTPException(
            status_code=503,
            detail="No validated/versioned research model plugin is loaded."
        )

    result = MODEL_PLUGIN.analyze_series(request.model_dump())

    # The plugin is required to return the same structured contract consumed by C#.
    result.setdefault("modelId", getattr(MODEL_PLUGIN, "model_id", "unknown"))
    result.setdefault("modelVersion", getattr(MODEL_PLUGIN, "model_version", "unknown"))
    result.setdefault("generatedAtUtc", datetime.now(timezone.utc).isoformat())
    result.setdefault("findings", [])
    return result


if __name__ == "__main__":
    import uvicorn
    uvicorn.run(app, host="127.0.0.1", port=8765)
