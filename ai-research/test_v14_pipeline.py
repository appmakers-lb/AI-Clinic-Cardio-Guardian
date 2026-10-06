"""Synthetic safety/regression checks for the v1.4 research pipeline.

These tests verify software invariants only. They do not establish clinical
accuracy or validation.
"""
from __future__ import annotations

import numpy as np

from frame_quality import score_frame
from multiview_linker import link_findings_across_views
from qca_calibration import calibration_from_dicom
from total_occlusion_research import detect_total_occlusion_candidates


class _Dataset:
    pass


def test_calibration_requires_evidence() -> None:
    ds = _Dataset()
    ds.PixelSpacing = [0.20, 0.20]
    ds.ImagerPixelSpacing = [0.25, 0.25]
    ds.PixelSpacingCalibrationType = "GEOMETRY"
    ds.PixelSpacingCalibrationDescription = "Synthetic geometry calibration"

    result = calibration_from_dicom(ds)
    assert result is not None
    assert result.reliable_for_physical_measurement
    assert abs(result.row_mm_per_pixel - 0.20) < 1e-9

    unverified = _Dataset()
    unverified.PixelSpacing = [0.20, 0.20]
    assert calibration_from_dicom(unverified) is None


def test_frame_quality_prefers_contrast_filled_frame() -> None:
    h = w = 512
    yy, xx = np.ogrid[:h, :w]
    mask = (np.abs(yy - 256) <= 8) & (xx >= 80) & (xx <= 430)
    probability = mask.astype(np.float32) * 0.95

    image = np.full((h, w), 0.75, dtype=np.float32)
    image[mask] = 0.20
    high = score_frame(image, probability, mask)

    flat = np.full((h, w), 0.5, dtype=np.float32)
    low = score_frame(flat, probability, mask)

    assert high.total_score > low.total_score
    assert high.contrast_score > low.contrast_score


def test_multiview_linking_requires_identity_evidence() -> None:
    a = {
        "id": "A",
        "findingType": "SuspectedStenosis",
        "injectionSide": "LEFT",
        "longitudinalPosition": 0.42,
        "referenceDiameterPixels": 18.0,
        "estimatedDiameterStenosisPercent": 72.0,
        "estimatedDiameterStenosisLowerPercent": 64.0,
        "estimatedDiameterStenosisUpperPercent": 80.0,
        "measurementFrameCount": 3,
        "measurementQualityScore": 0.82,
        "frameQualityScore": 0.80,
        "frameOverlapRisk": 0.10,
        "projectedReferenceSpanPixels": 90.0,
        "evidence": [{"sourceId": "SER-A", "projection": "Primary 30"}],
    }
    b = {
        "id": "B",
        "findingType": "SuspectedStenosis",
        "injectionSide": "LEFT",
        "longitudinalPosition": 0.47,
        "referenceDiameterPixels": 17.0,
        "estimatedDiameterStenosisPercent": 76.0,
        "estimatedDiameterStenosisLowerPercent": 67.0,
        "estimatedDiameterStenosisUpperPercent": 84.0,
        "measurementFrameCount": 4,
        "measurementQualityScore": 0.86,
        "frameQualityScore": 0.84,
        "frameOverlapRisk": 0.08,
        "projectedReferenceSpanPixels": 112.0,
        "evidence": [{"sourceId": "SER-B", "projection": "Primary -25"}],
    }

    linked = link_findings_across_views([a, b])
    assert len(linked) == 1
    assert linked[0]["multiViewConfirmed"] is True
    assert linked[0]["sourceSeriesCount"] == 2
    assert linked[0]["projectionCount"] == 2

    unknown = dict(b)
    unknown["id"] = "C"
    unknown["injectionSide"] = None
    unknown["evidence"] = [{"sourceId": "SER-C", "projection": "Primary 10"}]
    not_linked = link_findings_across_views([a, unknown])
    assert len(not_linked) == 2


def test_total_occlusion_detector_is_separate_and_conservative() -> None:
    h = w = 512
    yy, xx = np.ogrid[:h, :w]
    mask = (
        (np.abs(yy - 256) <= 8)
        & (xx >= 50)
        & (xx <= 250)
    )
    probability = mask.astype(np.float32) * 0.95

    candidates = detect_total_occlusion_candidates(
        mask,
        probability,
        stenosis_points=[(248.0, 256.0, 0.92)],
    )
    assert candidates, "expected synthetic abrupt supported cutoff"
    assert candidates[0].score >= 0.62

    no_independent_signal = detect_total_occlusion_candidates(
        mask,
        probability,
        stenosis_points=[],
    )
    assert no_independent_signal == []


if __name__ == "__main__":
    test_calibration_requires_evidence()
    test_frame_quality_prefers_contrast_filled_frame()
    test_multiview_linking_requires_identity_evidence()
    test_total_occlusion_detector_is_separate_and_conservative()
    print("v1.4 synthetic pipeline safety checks passed.")
