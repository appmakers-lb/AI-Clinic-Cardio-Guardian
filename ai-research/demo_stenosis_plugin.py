"""
Optional RESEARCH-ONLY coronary angiography stenosis-candidate adapter.

This adapter intentionally does not identify a coronary vessel/segment, quantify
stenosis, or make a clinical diagnosis. It runs a public YOLOv8 research model
on sampled DICOM frames and returns evidence-linked frame-level candidate boxes
for physician review.

The adapter is disabled unless CARDIO_GUARDIAN_ENABLE_RESEARCH_DEMO=1.
"""

from __future__ import annotations

import hashlib
import math
import os
from pathlib import Path
from typing import Any

import numpy as np
import pydicom
from huggingface_hub import hf_hub_download
from ultralytics import YOLO

MODEL_REPO = "rachitgoyell/stenosis-detection"
MODEL_FILE = "best.pt"


def _weight_version(path: str) -> str:
    digest = hashlib.sha256()
    with open(path, "rb") as stream:
        for chunk in iter(lambda: stream.read(1024 * 1024), b""):
            digest.update(chunk)
    return digest.hexdigest()[:16]


def _normalize_frame(frame: np.ndarray, photometric: str) -> np.ndarray:
    array = np.asarray(frame)

    if array.ndim == 3 and array.shape[-1] >= 3:
        # Convert RGB-like data to luminance without assuming OpenCV channel order.
        array = (
            0.299 * array[..., 0].astype(np.float32)
            + 0.587 * array[..., 1].astype(np.float32)
            + 0.114 * array[..., 2].astype(np.float32)
        )
    else:
        array = np.squeeze(array)

    if array.ndim != 2:
        raise ValueError(f"Unsupported DICOM frame shape: {array.shape}")

    values = array.astype(np.float32)
    finite = values[np.isfinite(values)]
    if finite.size == 0:
        raise ValueError("DICOM frame contains no finite pixel values.")

    low = float(np.percentile(finite, 1.0))
    high = float(np.percentile(finite, 99.0))
    if not math.isfinite(low) or not math.isfinite(high) or high <= low:
        low = float(finite.min())
        high = float(finite.max())
    if high <= low:
        image = np.zeros(values.shape, dtype=np.uint8)
    else:
        image = np.clip((values - low) * (255.0 / (high - low)), 0, 255).astype(np.uint8)

    if str(photometric).upper() == "MONOCHROME1":
        image = 255 - image

    # Ultralytics accepts HxWx3 uint8 arrays.
    return np.repeat(image[..., None], 3, axis=2)


def _frame_count(dataset: pydicom.dataset.Dataset) -> int:
    try:
        return max(1, int(getattr(dataset, "NumberOfFrames", 1)))
    except Exception:
        return 1


def _iter_dicom_frames(paths: list[str]):
    global_index = 0
    for raw_path in paths:
        path = Path(raw_path)
        dataset = pydicom.dcmread(str(path), force=False)
        expected = _frame_count(dataset)
        pixel_array = dataset.pixel_array
        photometric = str(getattr(dataset, "PhotometricInterpretation", ""))

        if expected > 1:
            if pixel_array.shape[0] != expected:
                raise ValueError(
                    f"Decoded frame count mismatch for {path.name}: "
                    f"expected {expected}, decoded shape {pixel_array.shape}."
                )
            frames = pixel_array
        else:
            frames = [pixel_array]

        for local_index, frame in enumerate(frames):
            yield global_index + local_index, _normalize_frame(frame, photometric)

        global_index += expected


class ResearchStenosisDemoPlugin:
    model_id = "HF-ATRIO-YOLOV8-RESEARCH-STENOSIS"
    is_loaded = False

    def __init__(self) -> None:
        self.load_error: str | None = None
        self.model_version = "not-loaded"
        self._model = None
        self._confidence = float(os.getenv("CARDIO_GUARDIAN_DEMO_CONFIDENCE", "0.35"))
        self._max_frames = max(1, int(os.getenv("CARDIO_GUARDIAN_DEMO_MAX_FRAMES", "48")))
        self._max_findings = max(1, int(os.getenv("CARDIO_GUARDIAN_DEMO_MAX_FINDINGS", "10")))

        try:
            model_path = hf_hub_download(repo_id=MODEL_REPO, filename=MODEL_FILE)
            self.model_version = f"best.pt-sha256-{_weight_version(model_path)}"
            self._model = YOLO(model_path)
            self.is_loaded = True
        except Exception as exc:
            self.load_error = str(exc)
            self.is_loaded = False

    def analyze_series(self, request: dict[str, Any]) -> dict[str, Any]:
        if not self.is_loaded or self._model is None:
            raise RuntimeError(self.load_error or "Research demo model is not loaded.")

        source_id = str(request.get("sourceId", "")).strip()
        if not source_id:
            raise ValueError("sourceId is required.")

        paths = [str(value) for value in request.get("filePaths", [])]
        if not paths:
            raise ValueError("At least one DICOM path is required.")

        declared_frames = max(1, int(request.get("frameCount") or 1))
        stride = max(1, math.ceil(declared_frames / self._max_frames))
        projection = str(request.get("projection", "")).strip() or None

        detections: list[dict[str, Any]] = []
        decoded_frames = 0
        analyzed_frames = 0

        for frame_index, image in _iter_dicom_frames(paths):
            decoded_frames += 1
            if frame_index % stride != 0:
                continue

            analyzed_frames += 1
            prediction = self._model.predict(
                source=image,
                imgsz=640,
                conf=self._confidence,
                iou=0.45,
                verbose=False,
            )[0]

            boxes = getattr(prediction, "boxes", None)
            if boxes is None:
                continue

            height, width = image.shape[:2]
            for box in boxes:
                confidence = float(box.conf[0].item())
                x1, y1, x2, y2 = [float(value) for value in box.xyxy[0].tolist()]
                region = {
                    "xMin": max(0.0, min(1.0, x1 / width)),
                    "yMin": max(0.0, min(1.0, y1 / height)),
                    "xMax": max(0.0, min(1.0, x2 / width)),
                    "yMax": max(0.0, min(1.0, y2 / height)),
                }
                if region["xMax"] <= region["xMin"] or region["yMax"] <= region["yMin"]:
                    continue

                fingerprint = (
                    f"{source_id}|{frame_index}|"
                    f"{region['xMin']:.4f}|{region['yMin']:.4f}|"
                    f"{region['xMax']:.4f}|{region['yMax']:.4f}"
                )
                finding_id = "DEMO-" + hashlib.sha256(fingerprint.encode("utf-8")).hexdigest()[:16]

                detections.append(
                    {
                        "id": finding_id,
                        "vessel": "Unassigned",
                        "segment": "frame-level",
                        "findingType": "Research stenosis candidate",
                        "confidence": confidence,
                        "priority": "Review",
                        "explanation": (
                            "Unvalidated research object-detector candidate. "
                            "Vessel, coronary segment, percent stenosis and clinical significance "
                            "are not inferred. Cardiologist review is required."
                        ),
                        "measurementSummary": None,
                        "evidence": [
                            {
                                "sourceId": source_id,
                                "frameStart": frame_index,
                                "frameEnd": frame_index,
                                "projection": projection,
                                "description": (
                                    "Frame-level candidate region from a public research model; "
                                    "not a clinical diagnosis."
                                ),
                                "region": region,
                            }
                        ],
                    }
                )

        detections.sort(key=lambda item: item["confidence"], reverse=True)
        findings = detections[: self._max_findings]

        return {
            "modelId": self.model_id,
            "modelVersion": self.model_version,
            "findings": findings,
            "coverage": [],
            "researchOnly": True,
            "analysisNotes": {
                "decodedFrames": decoded_frames,
                "analyzedFrames": analyzed_frames,
                "samplingStride": stride,
                "confidenceThreshold": self._confidence,
                "candidateCountBeforeLimit": len(detections),
                "candidateLimit": self._max_findings,
                "disclaimer": (
                    "Research demonstration only. Not externally validated, not cleared for "
                    "clinical use, and not a substitute for cardiologist interpretation."
                ),
            },
        }
