"""Cardio Guardian coronary XCA research adapter v1.4.

Pipeline:
- sample cine frames;
- segment coronary vessels first;
- score frame quality (contrast, border sharpness, saturation, vessel fill,
  likely overlap and temporal stability);
- run stenosis localization only on usable frames;
- track multiple candidates across time;
- measure sub-pixel lumen borders perpendicular to centerline;
- calculate reference diameter, MLD, lesion length and percent diameter
  stenosis with strict multi-frame repeatability gates;
- run a separate abrupt-cutoff total-occlusion detector;
- preserve evidence and metadata needed for cross-projection lesion linking.

This is research software. It is not clinically validated or certified.
"""
from __future__ import annotations

from dataclasses import dataclass
from hashlib import sha256
import math
import os
from pathlib import Path
from typing import Any, Iterable

import numpy as np

from frame_quality import FrameQuality, score_frame, select_best_frames
from multiview_linker import infer_injection_side
from qca_calibration import CalibrationResult, calibration_from_dicom, calibration_on_model_grid
from qca_research import ResearchQcaMeasurement, measure_qca
from total_occlusion_research import TotalOcclusionCandidate, detect_total_occlusion_candidates

ROOT = Path(__file__).resolve().parent
STENOSIS_MODEL_PATH = ROOT / "models" / "unet_stenosis.pt"
VESSEL_MODEL_PATH = ROOT / "models" / "unet_vessel.pt"
MODEL_ID = "CARDIO_GUARDIAN_XCA_QCA_RESEARCH"
MODEL_VERSION = "2026-demo-4.1"
INPUT_SIZE = 512


@dataclass(frozen=True)
class _FrameRef:
    path: Path
    local_index: int
    global_index: int
    photometric: str
    rows: int
    columns: int
    calibration: CalibrationResult | None
    metadata_text: str
    primary_angle: float | None
    secondary_angle: float | None


@dataclass(frozen=True)
class _FrameData:
    ref: _FrameRef
    image: np.ndarray
    vessel_probability: np.ndarray
    vessel_mask: np.ndarray
    quality: FrameQuality


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
    frame_quality: FrameQuality


@dataclass(frozen=True)
class _OcclusionFrameCandidate:
    frame: int
    candidate: TotalOcclusionCandidate
    frame_quality: FrameQuality


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
        explicit_calibration = request.get("catheterCalibrationMmPerPixel")
        explicit_calibration_source = request.get("catheterCalibrationSource")

        if not source_id or not file_paths:
            raise ValueError("sourceId and filePaths are required.")
        if modality and modality not in {"XA", "XRF"}:
            raise ValueError(
                f"Research adapter supports X-ray angiography only; received modality '{modality}'."
            )

        frame_refs = list(
            self._index_frames(
                file_paths,
                explicit_calibration=explicit_calibration,
                explicit_calibration_source=explicit_calibration_source,
            )
        )
        if not frame_refs:
            raise ValueError("No decodable DICOM frames were found.")

        max_frames = max(12, int(os.getenv("CARDIO_MAX_AI_FRAMES", "96")))
        sample_refs = self._sample_frames(frame_refs, max_frames)
        threshold = float(os.getenv("CARDIO_STENOZ_THRESHOLD", "0.70"))
        min_area = max(20, int(os.getenv("CARDIO_STENOZ_MIN_AREA", "40")))
        min_support = max(2, int(os.getenv("CARDIO_STENOZ_MIN_SUPPORT", "2")))
        min_qca_frames = max(2, int(os.getenv("CARDIO_QCA_MIN_FRAMES", "2")))
        max_qca_variability = float(os.getenv("CARDIO_QCA_MAX_VARIABILITY_PERCENT", "18"))
        max_candidates_per_frame = max(
            1, int(os.getenv("CARDIO_STENOZ_MAX_CANDIDATES_PER_FRAME", "4"))
        )
        max_quality_frames = max(
            8, int(os.getenv("CARDIO_MAX_QUALITY_SELECTED_FRAMES", "64"))
        )

        raw_cache: dict[int, tuple[np.ndarray, np.ndarray, np.ndarray]] = {}
        for ref in sample_refs:
            image = self._read_frame(ref)
            vessel_probability, vessel_mask = self._segment_vessels(image)
            raw_cache[ref.global_index] = (image, vessel_probability, vessel_mask)

        qualities: dict[int, FrameQuality] = {}
        for index, ref in enumerate(sample_refs):
            image, vessel_probability, vessel_mask = raw_cache[ref.global_index]
            previous_mask = (
                raw_cache[sample_refs[index - 1].global_index][2]
                if index > 0
                else None
            )
            next_mask = (
                raw_cache[sample_refs[index + 1].global_index][2]
                if index + 1 < len(sample_refs)
                else None
            )
            qualities[ref.global_index] = score_frame(
                image,
                vessel_probability,
                vessel_mask,
                previous_vessel_mask=previous_mask,
                next_vessel_mask=next_mask,
            )

        usable_indices = select_best_frames(
            [ref.global_index for ref in sample_refs],
            qualities,
            maximum=min(max_quality_frames, len(sample_refs)),
            minimum_spacing_frames=1,
        )
        usable_set = set(usable_indices)
        selected_refs = [ref for ref in sample_refs if ref.global_index in usable_set]

        if len(selected_refs) < min_support:
            return {
                "modelId": self.model_id,
                "modelVersion": self.model_version,
                "findings": [],
                "coverage": [],
                "analysisNote": (
                    f"Frame-quality gate retained only {len(selected_refs)} of "
                    f"{len(sample_refs)} sampled frame(s). Contrast/overlap/sharpness "
                    "quality was insufficient for a reliable research measurement."
                ),
            }

        frame_data: dict[int, _FrameData] = {
            ref.global_index: _FrameData(
                ref=ref,
                image=raw_cache[ref.global_index][0],
                vessel_probability=raw_cache[ref.global_index][1],
                vessel_mask=raw_cache[ref.global_index][2],
                quality=qualities[ref.global_index],
            )
            for ref in selected_refs
        }

        candidates: list[_Candidate] = []
        candidates_by_frame: dict[int, list[_Candidate]] = {}
        for ref in selected_refs:
            image = frame_data[ref.global_index].image
            frame_candidates = self._detect_frame_candidates(
                image,
                ref.global_index,
                threshold,
                min_area,
                max_candidates_per_frame,
            )
            candidates_by_frame[ref.global_index] = frame_candidates
            candidates.extend(frame_candidates)

        grouped = self._group_candidates(candidates, selected_refs)
        grouped = [g for g in grouped if g.support_count >= min_support]

        metadata_text = " ".join(
            [
                str(request.get("projection", "") or ""),
                str(request.get("seriesDescription", "") or ""),
                str(request.get("protocolName", "") or ""),
            ]
            + [ref.metadata_text for ref in selected_refs[:3]]
        )
        injection_side = infer_injection_side(metadata_text)

        findings: list[dict[str, Any]] = []
        withheld_review_candidates: list[dict[str, Any]] = []
        rejected_temporal_or_qca = 0

        def add_withheld_review_candidate(
            group: _CandidateGroup,
            reason: str,
            measured_items: list[_MeasuredCandidate],
        ) -> None:
            winner = group.winner
            if winner.confidence < 0.70 or group.support_count < min_support:
                return

            evidence = []
            evidence_members = sorted(
                group.members,
                key=lambda item: (
                    frame_data[item.frame].quality.total_score
                    if item.frame in frame_data
                    else 0.0,
                    item.confidence,
                ),
                reverse=True,
            )[:4]

            for evidence_rank, item in enumerate(evidence_members, start=1):
                quality = (
                    frame_data[item.frame].quality.total_score
                    if item.frame in frame_data
                    else 0.0
                )
                evidence.append(
                    {
                        "sourceId": source_id,
                        "frameStart": item.frame,
                        "frameEnd": item.frame,
                        "projection": projection,
                        "normalizedCenterX": round(item.x / INPUT_SIZE, 6),
                        "normalizedCenterY": round(item.y / INPUT_SIZE, 6),
                        "normalizedRadius": round(
                            min(
                                0.16,
                                max(
                                    0.04,
                                    (math.sqrt(item.area / math.pi) / INPUT_SIZE) * 1.8,
                                ),
                            ),
                            6,
                        ),
                        "description": (
                            f"Persistent research stenosis candidate {evidence_rank}; "
                            f"localization score {item.confidence:.2f}; "
                            f"frame quality {quality:.2f}. QCA percentage withheld: {reason}."
                        ),
                    }
                )

            if not evidence:
                return

            digest = sha256(
                f"{source_id}|REVIEW|{winner.frame}|{winner.x:.3f}|{winner.y:.3f}|{reason}".encode("utf-8")
            ).hexdigest()[:12]

            measured_count = len(measured_items)
            best_frame_quality = max(
                (
                    frame_data[item.frame].quality.total_score
                    for item in evidence_members
                    if item.frame in frame_data
                ),
                default=0.0,
            )

            withheld_review_candidates.append(
                {
                    "id": f"review-{digest}",
                    "vessel": "Unspecified coronary vessel",
                    "segment": "image-level review",
                    "findingType": "ResearchStenosisCandidate",
                    "confidence": round(winner.confidence, 4),
                    "measurementQuality": "Limited",
                    "measurementQualityScore": round(
                        max(
                            [item.measurement.quality_score for item in measured_items]
                            or [0.0]
                        ),
                        4,
                    ),
                    "measurementFrameCount": max(group.support_count, measured_count),
                    "frameQualityScore": round(best_frame_quality, 4),
                    "injectionSide": injection_side,
                    "priority": "Review",
                    "explanation": (
                        "The research model localized a persistent suspicious vessel region, "
                        "but the quantitative QCA gates were not strong enough to publish a "
                        "stenosis percentage. Review the highlighted evidence instead of "
                        "interpreting this as a diagnosis."
                    ),
                    "measurementSummary": (
                        f"Persistent candidate across {group.support_count} sampled frame(s). "
                        f"QCA percentage withheld: {reason}."
                    ),
                    "evidence": evidence,
                }
            )

        for rank, group in enumerate(grouped[:10], start=1):
            measured: list[_MeasuredCandidate] = []

            members = sorted(
                group.members,
                key=lambda candidate: (
                    frame_data[candidate.frame].quality.total_score,
                    candidate.confidence,
                ),
                reverse=True,
            )[:8]

            for candidate in members:
                data = frame_data.get(candidate.frame)
                if data is None:
                    continue

                grid_calibration = calibration_on_model_grid(
                    data.ref.calibration,
                    original_rows=data.ref.rows,
                    original_columns=data.ref.columns,
                    model_rows=INPUT_SIZE,
                    model_columns=INPUT_SIZE,
                )
                spacing_mm = None
                calibration_source = None
                if (
                    grid_calibration is not None
                    and grid_calibration.reliable_for_physical_measurement
                ):
                    spacing_mm = (
                        grid_calibration.row_mm_per_pixel
                        + grid_calibration.column_mm_per_pixel
                    ) / 2.0
                    calibration_source = grid_calibration.source

                measurement = measure_qca(
                    data.vessel_mask,
                    candidate_x=candidate.x,
                    candidate_y=candidate.y,
                    vessel_probability=data.vessel_probability,
                    pixel_spacing_mm=spacing_mm,
                    calibration_source=calibration_source,
                )
                if measurement is not None:
                    measured.append(
                        _MeasuredCandidate(
                            candidate=candidate,
                            measurement=measurement,
                            frame_quality=data.quality,
                        )
                    )

            if len(measured) < min_qca_frames:
                rejected_temporal_or_qca += 1
                add_withheld_review_candidate(
                    group,
                    "insufficient repeatable lumen/reference geometry",
                    measured,
                )
                continue

            percents = np.asarray(
                [item.measurement.diameter_stenosis_percent for item in measured],
                dtype=float,
            )
            median_percent = float(np.median(percents))
            variability = float(np.max(percents) - np.min(percents))
            mad = float(np.median(np.abs(percents - median_percent)))
            if variability > max_qca_variability:
                rejected_temporal_or_qca += 1
                add_withheld_review_candidate(
                    group,
                    f"cross-frame variability {variability:.1f} pp exceeded the {max_qca_variability:.1f} pp gate",
                    measured,
                )
                continue

            geometry_quality = float(np.median(
                [item.measurement.quality_score for item in measured]
            ))
            frame_quality = float(np.median(
                [item.frame_quality.total_score for item in measured]
            ))
            aggregate_quality = 0.76 * geometry_quality + 0.24 * frame_quality
            if aggregate_quality < 0.68:
                rejected_temporal_or_qca += 1
                add_withheld_review_candidate(
                    group,
                    f"measurement quality {aggregate_quality:.2f} was below the 0.68 reporting gate",
                    measured,
                )
                continue

            quality_label = (
                "High"
                if aggregate_quality >= 0.82 and variability <= 8 and len(measured) >= 3
                else "Moderate"
            )

            uncertainty = max(7.0, 2.0 * 1.4826 * mad, variability / 2.0)
            lower = float(np.clip(median_percent - uncertainty, 0.0, 95.0))
            upper = float(np.clip(median_percent + uncertainty, 0.0, 95.0))

            representative = max(
                measured,
                key=lambda item: (
                    0.55 * item.frame_quality.total_score
                    + 0.45 * item.measurement.quality_score
                    - 0.01 * abs(
                        item.measurement.diameter_stenosis_percent - median_percent
                    )
                ),
            )
            candidate = representative.candidate

            reference_px = float(np.median(
                [item.measurement.reference_diameter_px for item in measured]
            ))
            mld_px = float(np.median(
                [item.measurement.minimum_lumen_diameter_px for item in measured]
            ))
            lesion_length_px = float(np.median(
                [item.measurement.lesion_length_px for item in measured]
            ))
            border_confidence = float(np.median(
                [item.measurement.border_confidence for item in measured]
            ))
            longitudinal_positions = [
                item.measurement.longitudinal_position
                for item in measured
                if item.measurement.longitudinal_position is not None
            ]
            longitudinal_position = (
                float(np.median(longitudinal_positions))
                if longitudinal_positions
                else None
            )
            projected_span = float(max(
                item.measurement.projected_reference_span_px for item in measured
            ))

            mm_measurements = [
                item.measurement
                for item in measured
                if item.measurement.reference_diameter_mm is not None
                and item.measurement.minimum_lumen_diameter_mm is not None
                and item.measurement.lesion_length_mm is not None
            ]
            reference_mm = mld_mm = lesion_length_mm = None
            calibration_source = None
            if len(mm_measurements) >= min_qca_frames:
                calibration_names = {
                    str(item.calibration_source)
                    for item in mm_measurements
                    if item.calibration_source
                }
                if len(calibration_names) == 1:
                    reference_mm = float(np.median(
                        [item.reference_diameter_mm for item in mm_measurements]
                    ))
                    mld_mm = float(np.median(
                        [item.minimum_lumen_diameter_mm for item in mm_measurements]
                    ))
                    lesion_length_mm = float(np.median(
                        [item.lesion_length_mm for item in mm_measurements]
                    ))
                    calibration_source = next(iter(calibration_names))

            digest = sha256(
                f"{source_id}|{candidate.frame}|{candidate.x:.3f}|"
                f"{candidate.y:.3f}|{median_percent:.2f}".encode("utf-8")
            ).hexdigest()[:12]

            high_priority = (
                candidate.confidence >= 0.80
                and median_percent >= 70.0
                and quality_label in {"High", "Moderate"}
            )

            evidence = []
            ordered_measured = [representative] + [
                item for item in measured if item is not representative
            ]
            for evidence_rank, item in enumerate(ordered_measured[:4], start=1):
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
                            f"QCA evidence {evidence_rank}; lesion score "
                            f"{e_candidate.confidence:.2f}; frame stenosis "
                            f"{e_measurement.diameter_stenosis_percent:.1f}%; "
                            f"frame quality {item.frame_quality.total_score:.2f}; "
                            f"border confidence {e_measurement.border_confidence:.2f}."
                        ),
                    }
                )

            physical_summary = (
                f" Reference diameter {reference_mm:.2f} mm; MLD {mld_mm:.2f} mm; "
                f"lesion length {lesion_length_mm:.1f} mm; {calibration_source}."
                if reference_mm is not None and mld_mm is not None and lesion_length_mm is not None
                else (
                    f" Reference diameter {reference_px:.1f} px; MLD {mld_px:.1f} px; "
                    f"lesion length {lesion_length_px:.1f} px. "
                    "Physical values withheld because reliable calibration was unavailable."
                )
            )

            representative_quality = representative.frame_quality
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
                    "borderConfidence": round(border_confidence, 4),
                    "longitudinalPosition": round(longitudinal_position, 5) if longitudinal_position is not None else None,
                    "projectedReferenceSpanPixels": round(projected_span, 2),
                    "frameQualityScore": round(representative_quality.total_score, 4),
                    "frameOverlapRisk": round(representative_quality.overlap_risk, 4),
                    "injectionSide": injection_side,
                    "priority": "HighPriorityReview" if high_priority else "Review",
                    "explanation": (
                        "Research QCA candidate supported by direct stenosis localization, "
                        "coronary vessel segmentation, sub-pixel lumen borders, bilateral "
                        "reference-vessel geometry, high-quality frame selection and repeatable "
                        "measurements across multiple cine frames."
                    ),
                    "measurementSummary": (
                        f"Research diameter stenosis {median_percent:.1f}% "
                        f"(range {lower:.1f}-{upper:.1f}%); {quality_label} quality; "
                        f"{len(measured)} measured frame(s); variability {variability:.1f} pp; "
                        f"frame quality {frame_quality:.2f}; border confidence {border_confidence:.2f}."
                        + physical_summary
                        + " Not certified clinical QCA."
                    ),
                    "evidence": evidence,
                }
            )

        # Independent total-occlusion path. It is not derived from the QCA
        # percentage and therefore cannot turn a 90-95% stenosis into 100%.
        occlusion_frames: list[_OcclusionFrameCandidate] = []
        for ref in selected_refs:
            data = frame_data[ref.global_index]
            stenosis_points = [
                (candidate.x, candidate.y, candidate.confidence)
                for candidate in candidates_by_frame.get(ref.global_index, [])
            ]
            for occlusion in detect_total_occlusion_candidates(
                data.vessel_mask,
                data.vessel_probability,
                stenosis_points,
            ):
                occlusion_frames.append(
                    _OcclusionFrameCandidate(
                        frame=ref.global_index,
                        candidate=occlusion,
                        frame_quality=data.quality,
                    )
                )

        occlusion_groups = self._group_occlusion_candidates(
            occlusion_frames,
            selected_refs,
        )
        min_occlusion_support = max(
            3, int(os.getenv("CARDIO_OCCLUSION_MIN_SUPPORT", "3"))
        )

        for group_index, group in enumerate(occlusion_groups, start=1):
            if len(group) < min_occlusion_support:
                continue

            scores = np.asarray([item.candidate.score for item in group], dtype=float)
            quality_scores = np.asarray(
                [item.frame_quality.total_score for item in group],
                dtype=float,
            )
            aggregate_score = float(np.median(scores))
            aggregate_frame_quality = float(np.median(quality_scores))
            if aggregate_score < 0.68 or aggregate_frame_quality < 0.60:
                continue

            representative = max(
                group,
                key=lambda item: (
                    item.candidate.score,
                    item.frame_quality.total_score,
                ),
            )
            occlusion = representative.candidate
            quality_label = (
                "High"
                if aggregate_score >= 0.82 and len(group) >= 4
                else "Moderate"
            )
            digest = sha256(
                f"{source_id}|OCC|{occlusion.x:.2f}|{occlusion.y:.2f}|"
                f"{aggregate_score:.3f}".encode("utf-8")
            ).hexdigest()[:12]

            evidence = [
                {
                    "sourceId": source_id,
                    "frameStart": item.frame,
                    "frameEnd": item.frame,
                    "projection": projection,
                    "normalizedCenterX": round(item.candidate.x / INPUT_SIZE, 6),
                    "normalizedCenterY": round(item.candidate.y / INPUT_SIZE, 6),
                    "normalizedRadius": 0.06,
                    "description": (
                        f"Independent total-occlusion evidence; abrupt cutoff "
                        f"{item.candidate.abruptness_score:.2f}; distal void "
                        f"{item.candidate.distal_void_score:.2f}; detector score "
                        f"{item.candidate.score:.2f}; frame quality "
                        f"{item.frame_quality.total_score:.2f}."
                    ),
                }
                for item in sorted(
                    group,
                    key=lambda item: item.candidate.score,
                    reverse=True,
                )[:4]
            ]

            findings.append(
                {
                    "id": f"occ-{digest}",
                    "vessel": "Unspecified coronary vessel",
                    "segment": "image-level review",
                    "findingType": "SuspectedTotalOcclusion",
                    "confidence": round(aggregate_score, 4),
                    "occlusionPercent": 100.0,
                    "totalOcclusionScore": round(aggregate_score, 4),
                    "totalOcclusionFrameCount": len(group),
                    "chronicityEstablished": False,
                    "measurementQuality": quality_label,
                    "measurementQualityScore": round(
                        0.75 * aggregate_score + 0.25 * aggregate_frame_quality,
                        4,
                    ),
                    "measurementFrameCount": len(group),
                    "frameQualityScore": round(aggregate_frame_quality, 4),
                    "injectionSide": injection_side,
                    "priority": "HighPriorityReview",
                    "explanation": (
                        "Separate research total-occlusion detector found a persistent "
                        "abrupt vessel cutoff with absent forward vessel probability and "
                        "independent stenosis-localization support. This is not derived "
                        "from a high QCA percentage."
                    ),
                    "measurementSummary": (
                        "Suspected total occlusion candidate. If total occlusion is "
                        "confirmed by the physician, anatomic diameter stenosis is 100%. "
                        "Chronicity is NOT established by this image detector, so it does "
                        "not by itself diagnose CTO."
                    ),
                    "evidence": evidence,
                }
            )

        if not findings and withheld_review_candidates:
            withheld_review_candidates.sort(
                key=lambda item: (
                    float(item.get("frameQualityScore") or 0.0),
                    float(item.get("confidence") or 0.0),
                ),
                reverse=True,
            )
            findings.extend(withheld_review_candidates[:3])

        max_reported_findings = max(
            1, int(os.getenv("CARDIO_MAX_REPORTED_FINDINGS", "10"))
        )
        findings.sort(
            key=lambda finding: (
                1 if finding.get("findingType") == "SuspectedTotalOcclusion" else 0,
                float(finding.get("estimatedDiameterStenosisPercent") or 0),
                float(finding.get("confidence") or 0),
            ),
            reverse=True,
        )

        return {
            "modelId": self.model_id,
            "modelVersion": self.model_version,
            "findings": findings[:max_reported_findings],
            "coverage": [],
            "analysisNote": (
                f"Sampled {len(sample_refs)} of {len(frame_refs)} frame(s); "
                f"{len(selected_refs)} passed contrast/sharpness/vessel/overlap quality gates. "
                f"{rejected_temporal_or_qca} persistent stenosis candidate group(s) failed "
                "quantitative QCA geometry/repeatability gates. When no qualified measurement "
                "exists, up to three persistent regions may be shown as review-only candidates "
                "without a stenosis percentage. "
                "Single-view foreshortening cannot be proven absent; whole-case multi-view "
                "linking ranks compatible projections by vessel span and frame quality. "
                "A negative result is never clinical clearance."
            ),
        }

    def _index_frames(
        self,
        paths: list[Path],
        *,
        explicit_calibration: Any = None,
        explicit_calibration_source: Any = None,
    ) -> Iterable[_FrameRef]:
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

            try:
                explicit_value = (
                    float(explicit_calibration)
                    if explicit_calibration is not None
                    else None
                )
            except Exception:
                explicit_value = None

            calibration = calibration_from_dicom(
                ds,
                explicit_mm_per_pixel=explicit_value,
                explicit_source=(
                    str(explicit_calibration_source)
                    if explicit_calibration_source
                    else None
                ),
            )

            metadata_text = " ".join(
                str(getattr(ds, name, "") or "")
                for name in (
                    "SeriesDescription",
                    "ProtocolName",
                    "StudyDescription",
                    "BodyPartExamined",
                )
            )

            primary_angle = self._safe_float(
                getattr(ds, "PositionerPrimaryAngle", None)
            )
            secondary_angle = self._safe_float(
                getattr(ds, "PositionerSecondaryAngle", None)
            )

            for local_index in range(max(1, count)):
                yield _FrameRef(
                    path=path,
                    local_index=local_index,
                    global_index=global_index,
                    photometric=photometric,
                    rows=rows,
                    columns=columns,
                    calibration=calibration,
                    metadata_text=metadata_text,
                    primary_angle=primary_angle,
                    secondary_angle=secondary_angle,
                )
                global_index += 1

    @staticmethod
    def _safe_float(value: Any) -> float | None:
        try:
            number = float(value)
        except Exception:
            return None
        return number if math.isfinite(number) else None

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
                raise ValueError(
                    f"Unsupported DICOM pixel shape {arr.shape} in {ref.path.name}"
                )

        image = arr.astype(np.float32)
        if ref.photometric.upper() == "MONOCHROME1":
            image = image.max() - image
        return image

    def _prepare_tensor(self, image: np.ndarray):
        torch = self._torch
        assert torch is not None

        image = image.astype(np.float32)
        finite = image[np.isfinite(image)]
        if finite.size < 16:
            return None
        lo = float(np.percentile(finite, 1.0))
        hi = float(np.percentile(finite, 99.0))
        if not math.isfinite(lo) or not math.isfinite(hi) or hi <= lo:
            return None

        image = np.clip((image - lo) / (hi - lo + 1e-6), 0.0, 1.0)
        tensor = torch.from_numpy(image[None, None])
        return torch.nn.functional.interpolate(
            tensor,
            size=(INPUT_SIZE, INPUT_SIZE),
            mode="bilinear",
            align_corners=False,
        )

    def _detect_frame_candidates(
        self,
        image: np.ndarray,
        frame_index: int,
        threshold: float,
        min_area: int,
        max_candidates: int,
    ) -> list[_Candidate]:
        torch = self._torch
        ndi = self._ndi
        assert torch is not None and ndi is not None and self._stenosis_model is not None

        tensor = self._prepare_tensor(image)
        if tensor is None:
            return []

        with torch.no_grad():
            prob = torch.sigmoid(self._stenosis_model(tensor))[0, 0].cpu().numpy()

        mask = prob > threshold
        labels, count = ndi.label(mask)

        candidates: list[_Candidate] = []
        for label_id in range(1, int(count) + 1):
            ys, xs = np.where(labels == label_id)
            if len(xs) < min_area:
                continue

            component_prob = prob[ys, xs]
            confidence = float(component_prob.max())
            weight_sum = float(component_prob.sum())
            if weight_sum > 1e-9:
                x = float((xs * component_prob).sum() / weight_sum)
                y = float((ys * component_prob).sum() / weight_sum)
            else:
                x = float(xs.mean())
                y = float(ys.mean())

            candidates.append(
                _Candidate(
                    frame=frame_index,
                    confidence=confidence,
                    x=x,
                    y=y,
                    area=int(len(xs)),
                )
            )

        candidates.sort(key=lambda item: (item.confidence, item.area), reverse=True)
        return candidates[:max_candidates]

    def _segment_vessels(self, image: np.ndarray) -> tuple[np.ndarray, np.ndarray]:
        torch = self._torch
        ndi = self._ndi
        assert torch is not None and ndi is not None and self._vessel_model is not None

        tensor = self._prepare_tensor(image)
        if tensor is None:
            return (
                np.zeros((INPUT_SIZE, INPUT_SIZE), dtype=np.float32),
                np.zeros((INPUT_SIZE, INPUT_SIZE), dtype=bool),
            )

        with torch.no_grad():
            probability = torch.sigmoid(
                self._vessel_model(tensor)
            )[0, 0].cpu().numpy().astype(np.float32)

        mask = probability > 0.50
        labels, count = ndi.label(mask)
        if count:
            sizes = np.bincount(labels.ravel())
            keep = sizes >= 60
            keep[0] = False
            mask = keep[labels]
            probability = probability * mask.astype(np.float32)

        return probability, mask

    @staticmethod
    def _group_candidates(
        candidates: list[_Candidate],
        sampled: list[_FrameRef],
    ) -> list[_CandidateGroup]:
        if not candidates:
            return []

        sampled_indices = [item.global_index for item in sampled]
        gaps = [
            b - a
            for a, b in zip(sampled_indices, sampled_indices[1:])
            if b > a
        ]
        typical_gap = int(np.median(gaps)) if gaps else 1
        merge_gap = max(2, typical_gap * 2)
        max_spatial_shift = 96.0

        tracks: list[list[_Candidate]] = []
        for candidate in sorted(
            candidates,
            key=lambda item: (item.frame, -item.confidence),
        ):
            best_track: list[_Candidate] | None = None
            best_distance = float("inf")

            for track in tracks:
                last = track[-1]
                if candidate.frame <= last.frame:
                    continue
                if candidate.frame - last.frame > merge_gap:
                    continue

                distance = math.hypot(
                    candidate.x - last.x,
                    candidate.y - last.y,
                )
                if distance <= max_spatial_shift and distance < best_distance:
                    best_track = track
                    best_distance = distance

            if best_track is None:
                tracks.append([candidate])
            else:
                best_track.append(candidate)

        result: list[_CandidateGroup] = []
        for track in tracks:
            winner = max(track, key=lambda item: item.confidence)
            result.append(
                _CandidateGroup(
                    winner=winner,
                    members=tuple(track),
                    support_count=len(track),
                    first_frame=track[0].frame,
                    last_frame=track[-1].frame,
                )
            )

        return sorted(
            result,
            key=lambda group: (
                group.support_count,
                group.winner.confidence,
            ),
            reverse=True,
        )

    @staticmethod
    def _group_occlusion_candidates(
        candidates: list[_OcclusionFrameCandidate],
        sampled: list[_FrameRef],
    ) -> list[list[_OcclusionFrameCandidate]]:
        if not candidates:
            return []

        sampled_indices = [item.global_index for item in sampled]
        gaps = [
            b - a
            for a, b in zip(sampled_indices, sampled_indices[1:])
            if b > a
        ]
        typical_gap = int(np.median(gaps)) if gaps else 1
        merge_gap = max(2, typical_gap * 2)
        max_spatial_shift = 72.0

        tracks: list[list[_OcclusionFrameCandidate]] = []
        for item in sorted(
            candidates,
            key=lambda candidate: (
                candidate.frame,
                -candidate.candidate.score,
            ),
        ):
            best_track = None
            best_distance = float("inf")

            for track in tracks:
                last = track[-1]
                if item.frame <= last.frame:
                    continue
                if item.frame - last.frame > merge_gap:
                    continue

                distance = math.hypot(
                    item.candidate.x - last.candidate.x,
                    item.candidate.y - last.candidate.y,
                )
                if distance <= max_spatial_shift and distance < best_distance:
                    best_track = track
                    best_distance = distance

            if best_track is None:
                tracks.append([item])
            else:
                best_track.append(item)

        return sorted(
            tracks,
            key=lambda track: (
                len(track),
                float(np.median([item.candidate.score for item in track])),
            ),
            reverse=True,
        )
