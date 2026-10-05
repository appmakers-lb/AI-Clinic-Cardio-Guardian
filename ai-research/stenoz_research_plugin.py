"""Stenoz ARCADE XCA research adapter for Cardio Guardian.

This module intentionally emits *research review candidates*, not diagnoses.
A negative result must never be interpreted as "no stenosis".
"""
from __future__ import annotations

from dataclasses import dataclass
from hashlib import sha256
import math
import os
from pathlib import Path
from typing import Any, Iterable

import numpy as np

ROOT = Path(__file__).resolve().parent
MODEL_PATH = ROOT / "models" / "unet_stenosis.pt"
MODEL_ID = "STENOZ_ARCADE_XCA_RESEARCH"
MODEL_VERSION = "2026-demo-1"
INPUT_SIZE = 512


@dataclass(frozen=True)
class _FrameRef:
    path: Path
    local_index: int
    global_index: int
    photometric: str


@dataclass(frozen=True)
class _Candidate:
    frame: int
    confidence: float
    x: float
    y: float
    area: int


class StenozResearchPlugin:
    model_id = MODEL_ID
    model_version = MODEL_VERSION

    def __init__(self) -> None:
        self.is_loaded = False
        self.load_error: str | None = None
        self._model = None
        self._torch = None
        self._ndi = None

        if not MODEL_PATH.exists():
            self.load_error = (
                "Research checkpoint is not installed. Run "
                "ai-research\\setup_stenoz_model.bat."
            )
            return

        try:
            import torch
            from scipy import ndimage as ndi
            from stenoz_unet import StenozCompatibleUNet

            checkpoint = torch.load(MODEL_PATH, map_location="cpu")
            model = StenozCompatibleUNet(base=int(checkpoint.get("base", 32)))
            model.load_state_dict(checkpoint["state"])
            model.eval()
            torch.set_num_threads(max(1, min(10, (os.cpu_count() or 4))))

            self._torch = torch
            self._ndi = ndi
            self._model = model
            self.is_loaded = True
        except Exception as exc:
            self.load_error = f"Research checkpoint could not be loaded: {exc}"

    def analyze_series(self, request: dict[str, Any]) -> dict[str, Any]:
        if not self.is_loaded or self._model is None:
            raise RuntimeError(self.load_error or "Research model is unavailable.")

        source_id = str(request.get("sourceId", "")).strip()
        projection = str(request.get("projection", "")).strip() or None
        file_paths = [Path(p) for p in request.get("filePaths", [])]
        if not source_id or not file_paths:
            raise ValueError("sourceId and filePaths are required.")

        frame_refs = list(self._index_frames(file_paths))
        if not frame_refs:
            raise ValueError("No decodable DICOM frames were found.")

        max_frames = max(8, int(os.getenv("CARDIO_MAX_AI_FRAMES", "96")))
        sample_refs = self._sample_frames(frame_refs, max_frames)
        threshold = float(os.getenv("CARDIO_STENOZ_THRESHOLD", "0.70"))
        min_area = max(20, int(os.getenv("CARDIO_STENOZ_MIN_AREA", "40")))

        candidates: list[_Candidate] = []
        for ref in sample_refs:
            image = self._read_frame(ref)
            candidate = self._detect_frame(image, ref.global_index, threshold, min_area)
            if candidate is not None:
                candidates.append(candidate)

        grouped = self._group_candidates(candidates, sample_refs)
        findings = []
        for rank, candidate in enumerate(grouped[:3], start=1):
            digest = sha256(
                f"{source_id}|{candidate.frame}|{candidate.x:.3f}|{candidate.y:.3f}".encode("utf-8")
            ).hexdigest()[:12]

            high_priority = candidate.confidence >= 0.90
            findings.append(
                {
                    "id": f"stenoz-{digest}",
                    "vessel": "Unspecified",
                    "segment": "image-level review",
                    "findingType": "StenosisCandidate",
                    "confidence": round(candidate.confidence, 4),
                    "priority": "HighPriorityReview" if high_priority else "Review",
                    "explanation": (
                        "Research-only X-ray angiography U-Net candidate. "
                        "This output is not clinically validated and may contain false positives. "
                        "Physician review of the source cine is required."
                    ),
                    "measurementSummary": (
                        "No stenosis percentage, lesion severity, vessel identity, or treatment "
                        "recommendation is inferred by this research adapter."
                    ),
                    "evidence": [
                        {
                            "sourceId": source_id,
                            "frameStart": candidate.frame,
                            "frameEnd": candidate.frame,
                            "projection": projection,
                            "description": (
                                f"Research candidate rank {rank}; model score "
                                f"{candidate.confidence:.2f}; approximate model-space center "
                                f"x={candidate.x:.0f}, y={candidate.y:.0f}; area={candidate.area}px. "
                                "Not a diagnosis."
                            ),
                        }
                    ],
                }
            )

        return {
            "modelId": self.model_id,
            "modelVersion": self.model_version,
            "findings": findings,
            "coverage": [],
            "analysisNote": (
                f"Research adapter sampled {len(sample_refs)} of {len(frame_refs)} frame(s). "
                "No finding must never be interpreted as normal or disease-free."
            ),
        }

    def _index_frames(self, paths: list[Path]) -> Iterable[_FrameRef]:
        import pydicom

        global_index = 0
        for path in paths:
            if not path.exists():
                raise FileNotFoundError(str(path))

            ds = pydicom.dcmread(str(path), stop_before_pixels=True, force=True)
            try:
                count = int(getattr(ds, "NumberOfFrames", 1) or 1)
            except Exception:
                count = 1
            photometric = str(getattr(ds, "PhotometricInterpretation", "") or "")

            for local_index in range(max(1, count)):
                yield _FrameRef(path, local_index, global_index, photometric)
                global_index += 1

    @staticmethod
    def _sample_frames(frames: list[_FrameRef], max_frames: int) -> list[_FrameRef]:
        if len(frames) <= max_frames:
            return frames

        indices = np.linspace(0, len(frames) - 1, num=max_frames, dtype=int)
        unique = sorted(set(int(i) for i in indices))
        return [frames[i] for i in unique]

    @staticmethod
    def _read_frame(ref: _FrameRef) -> np.ndarray:
        from pydicom.pixels import pixel_array

        arr = np.asarray(pixel_array(str(ref.path), index=ref.local_index))
        if arr.ndim == 3 and arr.shape[-1] in (3, 4):
            arr = arr[..., :3].astype(np.float32).mean(axis=-1)
        elif arr.ndim != 2:
            arr = np.squeeze(arr)
            if arr.ndim != 2:
                raise ValueError(f"Unsupported DICOM pixel shape {arr.shape} in {ref.path.name}")

        image = arr.astype(np.float32)
        if ref.photometric.upper() == "MONOCHROME1":
            image = image.max() - image
        return image

    def _detect_frame(
        self,
        image: np.ndarray,
        frame_index: int,
        threshold: float,
        min_area: int,
    ) -> _Candidate | None:
        torch = self._torch
        ndi = self._ndi
        assert torch is not None and ndi is not None and self._model is not None

        image = image.astype(np.float32)
        lo = float(np.nanmin(image))
        hi = float(np.nanmax(image))
        if not math.isfinite(lo) or not math.isfinite(hi) or hi <= lo:
            return None

        image = (image - lo) / (hi - lo + 1e-6)
        tensor = torch.from_numpy(image[None, None])
        tensor = torch.nn.functional.interpolate(
            tensor,
            size=(INPUT_SIZE, INPUT_SIZE),
            mode="bilinear",
            align_corners=False,
        )

        with torch.no_grad():
            prob = torch.sigmoid(self._model(tensor))[0, 0].cpu().numpy()

        mask = prob > threshold
        labels, count = ndi.label(mask)

        best: _Candidate | None = None
        for label_id in range(1, int(count) + 1):
            ys, xs = np.where(labels == label_id)
            if len(xs) < min_area:
                continue

            confidence = float(prob[ys, xs].max())
            candidate = _Candidate(
                frame=frame_index,
                confidence=confidence,
                x=float(xs.mean()),
                y=float(ys.mean()),
                area=int(len(xs)),
            )
            if best is None or candidate.confidence > best.confidence:
                best = candidate
        return best

    @staticmethod
    def _group_candidates(
        candidates: list[_Candidate],
        sampled: list[_FrameRef],
    ) -> list[_Candidate]:
        if not candidates:
            return []

        sampled_indices = [x.global_index for x in sampled]
        gaps = [
            b - a
            for a, b in zip(sampled_indices, sampled_indices[1:])
            if b > a
        ]
        typical_gap = int(np.median(gaps)) if gaps else 1
        merge_gap = max(2, typical_gap * 2)

        ordered = sorted(candidates, key=lambda c: c.frame)
        groups: list[list[_Candidate]] = [[ordered[0]]]
        for candidate in ordered[1:]:
            if candidate.frame - groups[-1][-1].frame <= merge_gap:
                groups[-1].append(candidate)
            else:
                groups.append([candidate])

        winners = [max(group, key=lambda c: c.confidence) for group in groups]
        return sorted(winners, key=lambda c: c.confidence, reverse=True)
