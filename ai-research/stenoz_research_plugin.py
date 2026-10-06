"""Coronary XCA research adapter for Cardio Guardian v1.3.

This is a real image-analysis pipeline for retrospective/research work:
- direct stenosis-candidate localization,
- coronary vessel segmentation,
- temporal persistence across cine frames,
- conservative research QCA measurement with bilateral reference vessel,
- multi-frame consistency gates,
- evidence-linked output.

It is NOT clinically validated, certified QCA, or a diagnostic device.
If the geometry/quality gates fail, the adapter abstains instead of reporting
a percentage. A negative result is never clinical clearance. A 100% total
occlusion is never inferred from ordinary diameter-stenosis measurement.
"""
from __future__ import annotations

from dataclasses import dataclass
from hashlib import sha256
import math
import os
from pathlib import Path
from typing import Any, Iterable

import numpy as np

from qca_research import ResearchQcaMeasurement, measure_qca

ROOT = Path(__file__).resolve().parent
STENOSIS_MODEL_PATH = ROOT / "models" / "unet_stenosis.pt"
VESSEL_MODEL_PATH = ROOT / "models" / "unet_vessel.pt"
MODEL_ID = "CARDIO_GUARDIAN_XCA_QCA_RESEARCH"
MODEL_VERSION = "2026-demo-3"
INPUT_SIZE = 512


@dataclass(frozen=True)
class _FrameRef:
    path: Path
    local_index: int
    global_index: int
    photometric: str
    rows: int
    columns: int
    pixel_spacing_row_mm: float | None
    pixel_spacing_column_mm: float | None
    calibration_source: str | None


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
    members: tuple[_Candidate, ...]
    support_count: int
    first_frame: int
    last_frame: int


@dataclass(frozen=True)
class _MeasuredCandidate:
    candidate: _Candidate
    measurement: ResearchQcaMeasurement


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
        min_qca_frames = max(2, int(os.getenv("CARDIO_QCA_MIN_FRAMES", "2")))
        max_qca_variability = float(os.getenv("CARDIO_QCA_MAX_VARIABILITY_PERCENT", "18"))

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
        rejected_temporal_or_qca = 0

        for rank, group in enumerate(grouped[:8], start=1):
            measured: list[_MeasuredCandidate] = []

            # Measure several independent frames. A single frame is not enough
            # to publish a stenosis percentage.
            members = sorted(group.members, key=lambda c: c.confidence, reverse=True)[:6]
            for candidate in members:
                frame_ref = frame_by_index.get(candidate.frame)
                if frame_ref is None:
                    continue

                image = self._read_frame(frame_ref)
                vessel_mask = self._segment_vessels(image)
                spacing_mm, calibration_source = self._model_grid_spacing(frame_ref)

                measurement = measure_qca(
                    vessel_mask,
                    candidate_x=candidate.x,
                    candidate_y=candidate.y,
                    pixel_spacing_mm=spacing_mm,
                    calibration_source=calibration_source,
                )
                if measurement is not None:
                    measured.append(_MeasuredCandidate(candidate, measurement))

            if len(measured) < min_qca_frames:
                rejected_temporal_or_qca += 1
                continue

            percents = np.asarray(
                [m.measurement.diameter_stenosis_percent for m in measured],
                dtype=float,
            )
            median_percent = float(np.median(percents))
            variability = float(np.max(percents) - np.min(percents))
            mad = float(np.median(np.abs(percents - median_percent)))

            # Strong abstention gate: a lesion percentage is not reported when
            # the same candidate is unstable across the sampled cine frames.
            if variability > max_qca_variability:
                rejected_temporal_or_qca += 1
                continue

            quality_scores = np.asarray(
                [m.measurement.quality_score for m in measured],
                dtype=float,
            )
            aggregate_quality = float(np.median(quality_scores))
            if aggregate_quality < 0.65:
                rejected_temporal_or_qca += 1
                continue

            if aggregate_quality >= 0.80 and variability <= 8 and len(measured) >= 3:
                quality_label = "High"
            else:
                quality_label = "Moderate"

            uncertainty = max(8.0, 2.0 * 1.4826 * mad, variability / 2.0)
            lower = float(np.clip(median_percent - uncertainty, 0.0, 95.0))
            upper = float(np.clip(median_percent + uncertainty, 0.0, 95.0))

            representative = min(
                measured,
                key=lambda m: abs(m.measurement.diameter_stenosis_percent - median_percent),
            )
            candidate = representative.candidate

            reference_px = float(np.median(
                [m.measurement.reference_diameter_px for m in measured]
            ))
            mld_px = float(np.median(
                [m.measurement.minimum_lumen_diameter_px for m in measured]
            ))
            lesion_length_px = float(np.median(
                [m.measurement.lesion_length_px for m in measured]
            ))

            mm_measurements = [
                m.measurement
                for m in measured
                if m.measurement.reference_diameter_mm is not None
                and m.measurement.minimum_lumen_diameter_mm is not None
                and m.measurement.lesion_length_mm is not None
            ]
            reference_mm = mld_mm = lesion_length_mm = None
            calibration_source = None
            if len(mm_measurements) >= min_qca_frames:
                calibration_names = {
                    str(m.calibration_source)
                    for m in mm_measurements
                    if m.calibration_source
                }
                if len(calibration_names) == 1:
                    reference_mm = float(np.median(
                        [m.reference_diameter_mm for m in mm_measurements if m.reference_diameter_mm is not None]
                    ))
                    mld_mm = float(np.median(
                        [m.minimum_lumen_diameter_mm for m in mm_measurements if m.minimum_lumen_diameter_mm is not None]
                    ))
                    lesion_length_mm = float(np.median(
                        [m.lesion_length_mm for m in mm_measurements if m.lesion_length_mm is not None]
                    ))
                    calibration_source = next(iter(calibration_names))

            digest = sha256(
                f"{source_id}|{candidate.frame}|{candidate.x:.3f}|{candidate.y:.3f}|{median_percent:.2f}".encode("utf-8")
            ).hexdigest()[:12]

            high_priority = (
                candidate.confidence >= 0.80
                and median_percent >= 70.0
                and quality_label in {"High", "Moderate"}
            )

            evidence = []
            ordered_measured = [representative] + [m for m in measured if m is not representative]
            for evidence_rank, item in enumerate(ordered_measured[:3], start=1):
                e_candidate = item.candidate
                e_measurement = item.measurement
                evidence.append(
                    {
                        "sourceId": source_id,
                        "frameStart": e_candidate.frame,
                        "frameEnd": e_candidate.frame,
                        "projection": projection,
                        "normalizedCenterX": round(e_candidate.x / INPUT_SIZE, 6),
                        "normalizedCenterY": round(e_candidate.y / INPUT_SIZE, 6),
                        "normalizedRadius": round(
                            min(
                                0.18,
                                max(
                                    0.035,
                                    (math.sqrt(e_candidate.area / math.pi) / INPUT_SIZE) * 1.6,
                                ),
                            ),
                            6,
                        ),
                        "description": (
                            f"QCA evidence {evidence_rank}; direct-model score "
                            f"{e_candidate.confidence:.2f}; frame stenosis estimate "
                            f"{e_measurement.diameter_stenosis_percent:.1f}%; "
                            f"geometry quality {e_measurement.quality_label}. "
                            "Research measurement only."
                        ),
                    }
                )

            physical_summary = (
                f" Reference diameter {reference_mm:.2f} mm; MLD {mld_mm:.2f} mm; "
                f"lesion length {lesion_length_mm:.1f} mm; {calibration_source}."
                if reference_mm is not None and mld_mm is not None and lesion_length_mm is not None
                else (
                    f" Reference diameter {reference_px:.1f} px; MLD {mld_px:.1f} px; "
                    f"lesion length {lesion_length_px:.1f} px. No reliable physical calibration."
                )
            )

            findings.append(
                {
                    "id": f"qca-{digest}",
                    "vessel": "Unspecified coronary vessel",
                    "segment": "image-level review",
                    "findingType": "SuspectedStenosis",
                    "confidence": round(candidate.confidence, 4),
                    "estimatedDiameterStenosisPercent": round(median_percent, 1),
                    "estimatedDiameterStenosisLowerPercent": round(lower, 1),
                    "estimatedDiameterStenosisUpperPercent": round(upper, 1),
                    "referenceDiameterPixels": round(reference_px, 2),
                    "minimumLumenDiameterPixels": round(mld_px, 2),
                    "lesionLengthPixels": round(lesion_length_px, 2),
                    "referenceDiameterMm": round(reference_mm, 3) if reference_mm is not None else None,
                    "minimumLumenDiameterMm": round(mld_mm, 3) if mld_mm is not None else None,
                    "lesionLengthMm": round(lesion_length_mm, 3) if lesion_length_mm is not None else None,
                    "measurementQuality": quality_label,
                    "measurementQualityScore": round(aggregate_quality, 4),
                    "measurementFrameCount": len(measured),
                    "measurementVariabilityPercent": round(variability, 2),
                    "calibrationSource": calibration_source,
                    "priority": "HighPriorityReview" if high_priority else "Review",
                    "explanation": (
                        "Research QCA candidate supported by a direct stenosis model, "
                        "coronary vessel segmentation, bilateral reference-vessel geometry, "
                        "and repeatable measurements across multiple sampled cine frames. "
                        "Coronary artery identity is not yet automatically assigned."
                    ),
                    "measurementSummary": (
                        f"Research diameter stenosis {median_percent:.1f}% "
                        f"(range {lower:.1f}-{upper:.1f}%); {quality_label} measurement quality; "
                        f"{len(measured)} measured frame(s); variability {variability:.1f} percentage points."
                        + physical_summary
                        + " Not certified clinical QCA."
                    ),
                    "evidence": evidence,
                }
            )

        return {
            "modelId": self.model_id,
            "modelVersion": self.model_version,
            "findings": findings[:3],
            "coverage": [],
            "analysisNote": (
                f"Sampled {len(sample_refs)} of {len(frame_refs)} frame(s). "
                f"{rejected_temporal_or_qca} persistent candidate group(s) were withheld "
                "because multi-frame QCA quality/consistency gates failed. "
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
            rows = int(getattr(ds, "Rows", INPUT_SIZE) or INPUT_SIZE)
            columns = int(getattr(ds, "Columns", INPUT_SIZE) or INPUT_SIZE)
            row_mm, column_mm, calibration_source = self._extract_spacing(ds)

            for local_index in range(max(1, count)):
                yield _FrameRef(
                    path=path,
                    local_index=local_index,
                    global_index=global_index,
                    photometric=photometric,
                    rows=rows,
                    columns=columns,
                    pixel_spacing_row_mm=row_mm,
                    pixel_spacing_column_mm=column_mm,
                    calibration_source=calibration_source,
                )
                global_index += 1

    @staticmethod
    def _extract_spacing(ds: Any) -> tuple[float | None, float | None, str | None]:
        for attribute, label in (
            ("PixelSpacing", "DICOM PixelSpacing image-plane research calibration"),
            ("ImagerPixelSpacing", "DICOM ImagerPixelSpacing detector-plane research calibration"),
        ):
            value = getattr(ds, attribute, None)
            if value is None:
                continue
            try:
                row_mm = float(value[0])
                column_mm = float(value[1])
            except Exception:
                continue
            if row_mm > 0 and column_mm > 0 and math.isfinite(row_mm) and math.isfinite(column_mm):
                return row_mm, column_mm, label
        return None, None, None

    @staticmethod
    def _model_grid_spacing(ref: _FrameRef) -> tuple[float | None, str | None]:
        if (
            ref.pixel_spacing_row_mm is None
            or ref.pixel_spacing_column_mm is None
            or not ref.calibration_source
            or ref.rows <= 0
            or ref.columns <= 0
        ):
            return None, None

        row_on_grid = ref.pixel_spacing_row_mm * ref.rows / INPUT_SIZE
        column_on_grid = ref.pixel_spacing_column_mm * ref.columns / INPUT_SIZE
        ratio = max(row_on_grid, column_on_grid) / max(1e-9, min(row_on_grid, column_on_grid))
        if ratio > 1.10:
            return None, None

        spacing = (row_on_grid + column_on_grid) / 2.0
        return spacing, ref.calibration_source

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

        result: list[_CandidateGroup] = []
        for group in groups:
            winner = max(group, key=lambda c: c.confidence)
            result.append(
                _CandidateGroup(
                    winner=winner,
                    members=tuple(group),
                    support_count=len(group),
                    first_frame=group[0].frame,
                    last_frame=group[-1].frame,
                )
            )
        return sorted(result, key=lambda g: (g.support_count, g.winner.confidence), reverse=True)
