"""
AI Clinic Cardio Guardian — LOCAL RESEARCH GATEWAY

Localhost-only bridge between the Windows research workstation and an explicitly
installed, versioned research model adapter. This gateway is not a diagnostic
model and fails closed when no model is available.
"""

from __future__ import annotations

from datetime import datetime, timezone
from pathlib import Path
from typing import Any

from fastapi import FastAPI, HTTPException
from pydantic import BaseModel, Field, field_validator

try:
    from model_plugin import MODEL_PLUGIN
except Exception:
    MODEL_PLUGIN = None

SERVICE_VERSION = "1.2.0"
app = FastAPI(
    title="AI Clinic Cardio Guardian Research Gateway",
    version=SERVICE_VERSION,
)


class SeriesRequest(BaseModel):
    sourceId: str = Field(min_length=1, max_length=200)
    studyInstanceUid: str = Field(default="", max_length=128)
    seriesInstanceUid: str = Field(default="", max_length=128)
    modality: str = Field(default="", max_length=32)
    projection: str = Field(default="", max_length=256)
    frameCount: int = Field(default=0, ge=0)
    estimatedFramesPerSecond: float = Field(default=0.0, ge=0.0, le=240.0)
    filePaths: list[str] = Field(min_length=1)

    @field_validator("filePaths")
    @classmethod
    def validate_file_paths(cls, values: list[str]) -> list[str]:
        cleaned: list[str] = []
        for value in values:
            if not value or not value.strip():
                raise ValueError("filePaths cannot contain empty values")
            path = Path(value)
            if not path.is_absolute():
                raise ValueError("Every model input path must be absolute")
            cleaned.append(str(path))
        return cleaned


@app.get("/health")
def health() -> dict[str, Any]:
    loaded = bool(MODEL_PLUGIN and getattr(MODEL_PLUGIN, "is_loaded", False))
    return {
        "service": "AI Clinic Cardio Guardian Research Gateway",
        "version": SERVICE_VERSION,
        "modelLoaded": loaded,
        "modelId": getattr(MODEL_PLUGIN, "model_id", None) if loaded else None,
        "modelVersion": getattr(MODEL_PLUGIN, "model_version", None) if loaded else None,
        "message": (
            "Research/demo XCA model loaded. Outputs are unvalidated research candidates only."
            if loaded
            else (
                getattr(MODEL_PLUGIN, "load_error", None)
                or "No research medical-vision model loaded. Analysis is blocked."
            )
        ),
    }


@app.post("/analyze-series")
def analyze_series(request: SeriesRequest) -> dict[str, Any]:
    if not MODEL_PLUGIN or not getattr(MODEL_PLUGIN, "is_loaded", False):
        raise HTTPException(
            status_code=503,
            detail="No versioned research model plugin is loaded.",
        )

    model_id = str(getattr(MODEL_PLUGIN, "model_id", "")).strip()
    model_version = str(getattr(MODEL_PLUGIN, "model_version", "")).strip()
    if not model_id or not model_version or model_id == "NO_MODEL":
        raise HTTPException(
            status_code=503,
            detail="Loaded model adapter does not expose a valid model ID/version.",
        )

    try:
        result = MODEL_PLUGIN.analyze_series(request.model_dump())
    except Exception as exc:
        raise HTTPException(status_code=500, detail=f"Research model adapter failed: {exc}") from exc

    if not isinstance(result, dict):
        raise HTTPException(status_code=500, detail="Research model adapter returned a non-object result.")

    result.setdefault("modelId", model_id)
    result.setdefault("modelVersion", model_version)
    result.setdefault("generatedAtUtc", datetime.now(timezone.utc).isoformat())
    result.setdefault("findings", [])

    if not isinstance(result["findings"], list):
        raise HTTPException(status_code=500, detail="Research model adapter findings must be an array.")

    return result


if __name__ == "__main__":
    import uvicorn

    uvicorn.run(app, host="127.0.0.1", port=8765)
