"""Physical calibration hierarchy for research QCA.

Priority:
1. Explicit catheter/object calibration supplied by an approved adapter.
2. DICOM PixelSpacing with explicit GEOMETRY/FIDUCIAL calibration metadata,
   or PixelSpacing demonstrably different from detector spacing.
3. Detector spacing corrected by EstimatedRadiographicMagnificationFactor.
4. Unverified spacing is preserved for audit but is NOT accepted for physical
   millimetre output.

The engine never invents millimetres when calibration cannot be established.
"""
from __future__ import annotations

from dataclasses import dataclass
import math
from typing import Any


@dataclass(frozen=True)
class CalibrationResult:
    row_mm_per_pixel: float
    column_mm_per_pixel: float
    source: str
    quality_score: float
    method: str
    description: str | None = None

    @property
    def reliable_for_physical_measurement(self) -> bool:
        return self.quality_score >= 0.75


def _pair(value: Any) -> tuple[float, float] | None:
    if value is None:
        return None
    try:
        row = float(value[0])
        col = float(value[1])
    except Exception:
        return None
    if (
        not math.isfinite(row)
        or not math.isfinite(col)
        or row <= 0
        or col <= 0
    ):
        return None
    return row, col


def _different(a: tuple[float, float], b: tuple[float, float], tolerance: float = 0.01) -> bool:
    for left, right in zip(a, b):
        if abs(left - right) / max(abs(left), abs(right), 1e-9) > tolerance:
            return True
    return False


def calibration_from_dicom(
    ds: Any,
    *,
    explicit_mm_per_pixel: float | None = None,
    explicit_source: str | None = None,
) -> CalibrationResult | None:
    """Resolve a conservative image-plane calibration from DICOM metadata.

    explicit_mm_per_pixel is intended for an approved catheter/object
    calibration adapter that already measured a known reference object.
    """
    if (
        explicit_mm_per_pixel is not None
        and math.isfinite(explicit_mm_per_pixel)
        and explicit_mm_per_pixel > 0
    ):
        return CalibrationResult(
            row_mm_per_pixel=float(explicit_mm_per_pixel),
            column_mm_per_pixel=float(explicit_mm_per_pixel),
            source=explicit_source or "Approved external catheter/object calibration",
            quality_score=0.98,
            method="CATHETER_OR_OBJECT",
            description="Explicit known-object calibration supplied to the research gateway.",
        )

    pixel = _pair(getattr(ds, "PixelSpacing", None))
    imager = _pair(getattr(ds, "ImagerPixelSpacing", None))
    nominal = _pair(getattr(ds, "NominalScannedPixelSpacing", None))
    calibration_type = str(getattr(ds, "PixelSpacingCalibrationType", "") or "").strip().upper()
    calibration_description = str(
        getattr(ds, "PixelSpacingCalibrationDescription", "") or ""
    ).strip() or None

    detector_reference = imager or nominal

    if pixel is not None:
        explicitly_calibrated = calibration_type in {"GEOMETRY", "FIDUCIAL"}
        corrected_vs_detector = (
            detector_reference is not None
            and _different(pixel, detector_reference)
        )

        if explicitly_calibrated or corrected_vs_detector:
            reason = (
                f"DICOM PixelSpacing ({calibration_type})"
                if explicitly_calibrated
                else "DICOM PixelSpacing corrected relative to detector spacing"
            )
            return CalibrationResult(
                row_mm_per_pixel=pixel[0],
                column_mm_per_pixel=pixel[1],
                source=reason,
                quality_score=0.92 if explicitly_calibrated else 0.86,
                method="DICOM_PIXEL_SPACING_CALIBRATED",
                description=calibration_description,
            )

        # DICOM explicitly warns that calibration cannot be assumed from a
        # bare PixelSpacing value when calibration metadata is absent.
        # Keep this untrusted value out of physical QCA output.
        return None

    magnification_raw = getattr(ds, "EstimatedRadiographicMagnificationFactor", None)
    try:
        magnification = float(magnification_raw)
    except Exception:
        magnification = float("nan")

    if (
        imager is not None
        and math.isfinite(magnification)
        and magnification > 0
    ):
        return CalibrationResult(
            row_mm_per_pixel=imager[0] / magnification,
            column_mm_per_pixel=imager[1] / magnification,
            source="DICOM ImagerPixelSpacing corrected by EstimatedRadiographicMagnificationFactor",
            quality_score=0.80,
            method="DICOM_MAGNIFICATION_CORRECTED",
            description=None,
        )

    return None


def calibration_on_model_grid(
    calibration: CalibrationResult | None,
    *,
    original_rows: int,
    original_columns: int,
    model_rows: int,
    model_columns: int,
) -> CalibrationResult | None:
    if calibration is None:
        return None
    if min(original_rows, original_columns, model_rows, model_columns) <= 0:
        return None

    row = calibration.row_mm_per_pixel * original_rows / model_rows
    col = calibration.column_mm_per_pixel * original_columns / model_columns

    ratio = max(row, col) / max(1e-9, min(row, col))
    if ratio > 1.10:
        return None

    return CalibrationResult(
        row_mm_per_pixel=row,
        column_mm_per_pixel=col,
        source=calibration.source,
        quality_score=calibration.quality_score,
        method=calibration.method,
        description=calibration.description,
    )
