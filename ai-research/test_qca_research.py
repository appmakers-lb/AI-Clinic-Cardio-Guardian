"""Deterministic synthetic checks for the research QCA geometry engine.

These tests do not establish clinical performance. They only verify that the
measurement code behaves sensibly on controlled binary-vessel geometry.
"""
from __future__ import annotations

import numpy as np

from qca_research import measure_qca


def make_horizontal_vessel(
    *,
    width: int = 512,
    height: int = 512,
    center_y: int = 256,
    normal_radius: int = 10,
    lesion_radius: int | None = None,
    lesion_start: int = 240,
    lesion_end: int = 272,
) -> np.ndarray:
    yy, xx = np.ogrid[:height, :width]
    radius = np.full(width, normal_radius, dtype=float)
    if lesion_radius is not None:
        radius[lesion_start:lesion_end + 1] = lesion_radius
        # Short linear shoulders reduce unrealistic discontinuity.
        shoulder = 10
        for i in range(1, shoulder + 1):
            left = lesion_start - i
            right = lesion_end + i
            if left >= 0:
                radius[left] = lesion_radius + (normal_radius - lesion_radius) * i / shoulder
            if right < width:
                radius[right] = lesion_radius + (normal_radius - lesion_radius) * i / shoulder

    return np.abs(yy - center_y) <= radius[xx]


def test_known_narrowing() -> None:
    mask = make_horizontal_vessel(lesion_radius=5)
    measurement = measure_qca(mask, candidate_x=256, candidate_y=256)
    assert measurement is not None, "expected synthetic stenosis to be measurable"
    assert 35 <= measurement.diameter_stenosis_percent <= 65, measurement
    assert measurement.minimum_lumen_diameter_px < measurement.reference_diameter_px
    assert measurement.quality_score >= 0.65


def test_uniform_vessel_abstains() -> None:
    mask = make_horizontal_vessel(lesion_radius=None)
    measurement = measure_qca(mask, candidate_x=256, candidate_y=256)
    assert measurement is None, "uniform vessel must not be reported as stenosis"


def test_candidate_off_vessel_abstains() -> None:
    mask = make_horizontal_vessel(lesion_radius=5)
    measurement = measure_qca(mask, candidate_x=256, candidate_y=100)
    assert measurement is None, "off-vessel candidate must be rejected"


if __name__ == "__main__":
    test_known_narrowing()
    test_uniform_vessel_abstains()
    test_candidate_off_vessel_abstains()
    print("Research QCA synthetic checks passed.")
