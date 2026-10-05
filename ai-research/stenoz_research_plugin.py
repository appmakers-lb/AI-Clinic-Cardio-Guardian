"""Stenoz ARCADE XCA research adapter for Cardio Guardian.

Research/demo only. This adapter does not diagnose coronary disease, does not
identify a coronary artery name, and does not perform clinical QCA. It combines:
- direct stenosis-candidate localization,
- a separate vessel segmentation model,
- temporal persistence across sampled cine frames,
- conservative local geometry for an apparent diameter-reduction estimate.

Negative output is never clinical clearance. Complete occlusion is not inferred.
"""
from __future__ import annotations

from dataclasses import dataclass
from hashlib import sha256
import math
import os
from pathlib import Path
from typing import Any, Iterable

import numpy as np

from stenoz_geometry import estimate_apparent_narrowing

ROOT = Path(__file__).resolve().parent
STENOSIS_MODEL_PATH = ROOT / "models" / "unet_stenosis.pt"
VESSEL_MODEL_PATH = ROOT / "models" / "unet_vessel.pt"
MODEL_ID = "STENOZ_ARCADE_XCA_RESEARCH"
MODEL_VERSION = "2026-demo-2"
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


@dataclass(frozen=True)
class _CandidateGroup:
    winner: _Candidate
    support_count: int
    first_frame: int
    last_frame: int


class StenozResearchPlugin:
    model_id = MODEL_ID
    model_version = MODEL_VERSION

    def __init__(self) -> None:
        self.is_loaded = False
        self.load_error: str | None = None
        self._stenosis_model = None
        self._vessel_model = None
        self._torch = None
        self._ndi = None

        missing = [
            str(p.name)
            for p in (STENOSIS_MODEL_PATH, VESSEL_MODEL_PATH)
            if not p.exists()
        ]
        if missing:
            self.load_error = (
                "Research checkpoints are incomplete (" + ", ".join(missing) + "). "
                "Run ai-research\\setup_stenoz_model.bat."
            )
            return

        try:
            import torch
            from scipy import ndimage as ndi
            from stenoz_unet import StenozCompatibleUNet

            stenosis_ckpt = torch.load(STENOSIS_MODEL_PATH, map_location="cpu")
            stenosis_model = StenozCompatibleUNet(base=int(stenosis_ckpt.get("base", 32)))
            stenosis_model.load_state_dict(stenosis_ckpt["state"])
            stenosis_model.eval()

            vessel_ckpt = torch.load(VESSEL_MODEL_PATH, map_location="cpu")
            vessel_model = StenozCompatibleUNet(base=int(vessel_ckpt.get("base", 32)))
            vessel_model.load_state_dict(vessel_ckpt["state"])
            vessel_model.eval()

            torch.set_num_threads(max(1, min(10, (os.cpu_count() or 4))))
            self._torch = torch
            self._ndi = ndi
            self._stenosis_model = stenosis_model
            self._vessel_model = vessel_model
            self.is_loaded = True
        except Exception as exc:
            self.load_error = f"Research checkpoints could not be loaded: {exc}"

    def analyze_series(self, request: dict[str, Any]) -> dict[str, Any]:
        if not self.is_loaded or self._stenosis_model is None or self._vessel_model is None:
            raise RuntimeError(self.load_error or "Research model is unavailable.")

        source_id = str(request.get("sourceId", "")).strip()
        projection = str(request.get("projection", "")).strip() or None
        modality = str(request.get("modality", "")).strip().upper()
        file_paths = [Path(p) for p in request.get("filePaths", [])]

        if not source_id or not file_paths:
            raise ValueError("sourceId and filePaths are required.")
        if modality and modality not in {"XA", "XRF"}:
            raise ValueError(
                f"Research adapter supports X-ray angiography only; received modality '{modality}'."
            )

        frame_refs = list(self._index_frames(file_paths))
        if not frame_refs:
            raise ValueError("No decodable DICOM frames were found.")

        max_frames = max(8, int(os.getenv("CARDIO_MAX_AI_FRAMES", "96")))
        sample_refs = self._sample_frames(frame_refs, max_frames)
        threshold = float(os.getenv("CARDIO_STENOZ_THRESHOLD", "0.70"))
        min_area = max(20, int(os.getenv("CARDIO_STENOZ_MIN_AREA", "40")))
        min_support = max(2, int(os.getenv("CARDIO_STENOZ_MIN_SUPPORT", "2")))

        candidates: list[_Candidate] = []
        for ref in sample_refs:
            image = self._read_frame(ref)
            candidate = self._detect_frame(image, ref.global_index, threshold, min_area)
            if candidate is not None:
                candidates.append(candidate)

        grouped = self._group_candidates(candidates, sample_refs)
        grouped = [g for g in grouped if g.support_count >= min_support]

        frame_by_index = {ref.global_index: ref for ref in frame_refs}
        findings: list[dict[str, Any]] = []
        rejected_geometry = 0

        for rank, group in enumerate(grouped[:6], start=1):
            candidate = group.winner
            frame_ref = frame_by_index.get(candidate.frame)
            if frame_ref is None:
                continue

            image = self._read_frame(frame_ref)
            vessel_mask = self._segment_vessels(image)
            estimate = estimate_apparent_narrowing(
                vessel_mask,
                candidate_x=candidate.x,
                candidate_y=candidate.y,
            )

            # Product goal: surface suspected narrowed vessel regions, not every
            # direct-model heatmap. If vessel geometry does not support the
            # candidate, do not show it as a stenosis finding.
            if estimate is None:
                rejected_geometry += 1
                continue

            digest = sha256(
                f"{source_id}|{candidate.frame}|{candidate.x:.3f}|{candidate.y:.3f}".encode("utf-8")
            ).hexdigest()[:12]

            high_priority = candidate.confidence >= 0.80 and estimate.percent >= 70.0
            findings.append(
                {
                    "id": f"stenoz-{digest}",
                    "vessel": "Unspecified coronary vessel",
                    "segment": "image-level review",
                    "findingType": "SuspectedStenosis",
                    "confidence": round(candidate.confidence, 4),
                    "estimatedDiameterStenosisPercent": round(estimate.percent, 1),
                    "estimatedDiameterStenosisLowerPercent": round(estimate.lower, 1),
                    "estimatedDiameterStenosisUpperPercent": round(estimate.upper, 1),
                    "priority": "HighPriorityReview" if high_priority else "Review",
                    "explanation": (
                        "Research-only candidate supported by direct stenosis localization, "
                        "temporal persistence, and vessel segmentation. "
                        "The system does not know the coronary artery name yet and this is not a diagnosis."
                    ),
                    "measurementSummary": (
                        f"Research apparent diameter reduction ~{estimate.percent:.0f}% "
                        f"(wide range {estimate.lower:.0f}-{estimate.upper:.0f}%). "
                        "Uncalibrated 2D estimate; not clinical QCA. "
                        "Complete occlusion is not inferred by this method."
                    ),
                    "evidence": [
                        {
                            "sourceId": source_id,
                            "frameStart": candidate.frame,
                            "frameEnd": candidate.frame,
                            "projection": projection,
                            "normalizedCenterX": round(candidate.x / INPUT_SIZE, 6),
                            "normalizedCenterY": round(candidate.y / INPUT_SIZE, 6),
                            "normalizedRadius": round(
                                min(
                                    0.18,
                                    max(
                                        0.035,
                                        (math.sqrt(candidate.area / math.pi) / INPUT_SIZE) * 1.6,
                                    ),
                                ),
                                6,
                            ),
                            "description": (
                                f"Research candidate rank {rank}; direct-model score "
                                f"{candidate.confidence:.2f}; persisted on {group.support_count} sampled frame(s); "
                                f"apparent diameter reduction estimate {estimate.percent:.0f}% "
                                f"({estimate.lower:.0f}-{estimate.upper:.0f}% wide research range). "
                                "Not a diagnosis or clinical QCA."
                            ),
                        }
                    ],
                }
            )

        return {
            "modelId": self.model_id,
            "modelVersion": self.model_version,
            "findings": findings[:3],
            "coverage": [],
            "analysisNote": (
                f"Research adapter sampled {len(sample_refs)} of {len(frame_refs)} frame(s); "
                f"{rejected_geometry} temporally persistent direct candidate(s) were rejected "
                "because vessel geometry did not support them. "
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

    def _prepare_tensor(self, image: np.ndarray):
        torch = self._torch
        assert torch is not None

        image = image.astype(np.float32)
        lo = float(np.nanmin(image))
        hi = float(np.nanmax(image))
        if not math.isfinite(lo) or not math.isfinite(hi) or hi <= lo:
            return None

        image = (image - lo) / (hi - lo + 1e-6)
        tensor = torch.from_numpy(image[None, None])
        return torch.nn.functional.interpolate(
            tensor,
            size=(INPUT_SIZE, INPUT_SIZE),
            mode="bilinear",
            align_corners=False,
        )

    def _detect_frame(
        self,
        image: np.ndarray,
        frame_index: int,
        threshold: float,
        min_area: int,
    ) -> _Candidate | None:
        torch = self._torch
        ndi = self._ndi
        assert torch is not None and ndi is not None and self._stenosis_model is not None

        tensor = self._prepare_tensor(image)
        if tensor is None:
            return None

        with torch.no_grad():
            prob = torch.sigmoid(self._stenosis_model(tensor))[0, 0].cpu().numpy()

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

    def _segment_vessels(self, image: np.ndarray) -> np.ndarray:
        torch = self._torch
        ndi = self._ndi
        assert torch is not None and ndi is not None and self._vessel_model is not None

        tensor = self._prepare_tensor(image)
        if tensor is None:
            return np.zeros((INPUT_SIZE, INPUT_SIZE), dtype=bool)

        with torch.no_grad():
            prob = torch.sigmoid(self._vessel_model(tensor))[0, 0].cpu().numpy()

        mask = prob > 0.50
        labels, count = ndi.label(mask)
        if count:
            sizes = np.bincount(labels.ravel())
            keep = sizes >= 60
            keep[0] = False
            mask = keep[labels]
        return mask

    @staticmethod
    def _group_candidates(
        candidates: list[_Candidate],
        sampled: list[_FrameRef],
    ) -> list[_CandidateGroup]:
        if not candidates:
            return []

        sampled_indices = [x.global_index for x in sampled]
        gaps = [b - a for a, b in zip(sampled_indices, sampled_indices[1:]) if b > a]
        typical_gap = int(np.median(gaps)) if gaps else 1
        merge_gap = max(2, typical_gap * 2)
        max_spatial_shift = 96.0

        ordered = sorted(candidates, key=lambda c: c.frame)
        groups: list[list[_Candidate]] = [[ordered[0]]]

        for candidate in ordered[1:]:
            previous = groups[-1][-1]
            frame_close = candidate.frame - previous.frame <= merge_gap
            spatial_close = math.hypot(candidate.x - previous.x, candidate.y - previous.y) <= max_spatial_shift
            if frame_close and spatial_close:
                groups[-1].append(candidate)
            else:
                groups.append([candidate])

        result = []
        for group in groups:
            winner = max(group, key=lambda c: c.confidence)
            result.append(
                _CandidateGroup(
                    winner=winner,
                    support_count=len(group),
                    first_frame=group[0].frame,
                    last_frame=group[-1].frame,
                )
            )
        return sorted(result, key=lambda g: (g.support_count, g.winner.confidence), reverse=True)
