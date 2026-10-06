"""Research QCA engine for AI Clinic Cardio Guardian.

This module performs an actual image-measurement pipeline on a segmented
coronary vessel mask. It is intended for retrospective/research validation,
not clinical use. It deliberately abstains when geometry is unreliable.

Method:
- clean the vessel mask;
- skeletonize the vessel;
- find the centerline point nearest the AI lesion candidate;
- reject candidates far from the centerline or near a branch point;
- estimate local vessel diameter from the Euclidean distance transform;
- estimate proximal/distal reference diameter from opposite sides of the
  local centerline tangent;
- compute percent diameter stenosis = (1 - MLD/reference) * 100;
- estimate lesion length from the narrowed centerline span;
- emit quality metrics and optional image-plane millimetres when DICOM
  spacing is available.

DICOM PixelSpacing/ImagerPixelSpacing is not equivalent to catheter-based
isocenter calibration. Millimetre values from this module are therefore
explicitly labelled image-plane research measurements.
"""
from __future__ import annotations

from dataclasses import dataclass
import heapq
import math
from typing import Iterable

import numpy as np
from scipy import ndimage as ndi
from skimage.morphology import skeletonize


@dataclass(frozen=True)
class ResearchQcaMeasurement:
    diameter_stenosis_percent: float
    reference_diameter_px: float
    minimum_lumen_diameter_px: float
    lesion_length_px: float
    quality_score: float
    quality_label: str
    candidate_to_centerline_px: float
    proximal_reference_diameter_px: float
    distal_reference_diameter_px: float
    reference_symmetry: float
    reference_points_each_side: int
    reference_diameter_mm: float | None = None
    minimum_lumen_diameter_mm: float | None = None
    lesion_length_mm: float | None = None
    calibration_source: str | None = None


def _clean_mask(mask: np.ndarray, min_component: int = 80) -> np.ndarray:
    mask = np.asarray(mask, dtype=bool)
    labels, count = ndi.label(mask)
    if count <= 0:
        return np.zeros_like(mask, dtype=bool)
    sizes = np.bincount(labels.ravel())
    keep = sizes >= max(1, int(min_component))
    keep[0] = False
    return keep[labels]


def _neighbors(point: tuple[int, int], shape: tuple[int, int]) -> Iterable[tuple[tuple[int, int], float]]:
    y, x = point
    h, w = shape
    for dy in (-1, 0, 1):
        for dx in (-1, 0, 1):
            if dy == 0 and dx == 0:
                continue
            yy, xx = y + dy, x + dx
            if 0 <= yy < h and 0 <= xx < w:
                yield (yy, xx), (math.sqrt(2.0) if dy and dx else 1.0)


def _skeleton_geodesic(
    skeleton: np.ndarray,
    start: tuple[int, int],
    max_distance: float,
) -> dict[tuple[int, int], float]:
    distances: dict[tuple[int, int], float] = {start: 0.0}
    queue: list[tuple[float, tuple[int, int]]] = [(0.0, start)]

    while queue:
        distance, point = heapq.heappop(queue)
        if distance != distances.get(point):
            continue
        if distance > max_distance:
            continue

        for neighbor, step in _neighbors(point, skeleton.shape):
            if not skeleton[neighbor]:
                continue
            new_distance = distance + step
            if new_distance > max_distance:
                continue
            if new_distance < distances.get(neighbor, float("inf")):
                distances[neighbor] = new_distance
                heapq.heappush(queue, (new_distance, neighbor))

    return distances


def _degree(skeleton: np.ndarray, point: tuple[int, int]) -> int:
    return sum(1 for n, _ in _neighbors(point, skeleton.shape) if skeleton[n])


def _local_tangent(points: np.ndarray, center_y: float, center_x: float) -> np.ndarray | None:
    if points.shape[0] < 5:
        return None

    centered = points.astype(np.float64) - np.array([[center_y, center_x]], dtype=np.float64)
    covariance = centered.T @ centered
    values, vectors = np.linalg.eigh(covariance)
    tangent_yx = vectors[:, int(np.argmax(values))]
    norm = float(np.linalg.norm(tangent_yx))
    if norm <= 1e-9:
        return None
    return tangent_yx / norm


def measure_qca(
    vessel_mask: np.ndarray,
    candidate_x: float,
    candidate_y: float,
    *,
    pixel_spacing_mm: float | None = None,
    calibration_source: str | None = None,
    max_centerline_distance_px: float = 24.0,
    tangent_window_px: float = 18.0,
    lesion_window_px: float = 10.0,
    reference_inner_px: float = 18.0,
    reference_outer_px: float = 75.0,
    min_reference_points_each_side: int = 7,
    min_reportable_stenosis_percent: float = 20.0,
) -> ResearchQcaMeasurement | None:
    """Return a conservative research QCA measurement or None.

    The routine rejects branch-point ambiguity, insufficient bilateral
    reference vessel, poor reference symmetry, and weak narrowing.
    """
    mask = _clean_mask(vessel_mask)
    if not np.any(mask):
        return None

    distance = ndi.distance_transform_edt(mask)
    skeleton = skeletonize(mask)
    points = np.argwhere(skeleton)
    if points.shape[0] < 30:
        return None

    cy = float(candidate_y)
    cx = float(candidate_x)
    d2 = (points[:, 0].astype(float) - cy) ** 2 + (points[:, 1].astype(float) - cx) ** 2
    nearest_i = int(np.argmin(d2))
    centerline_distance = math.sqrt(float(d2[nearest_i]))
    if centerline_distance > max_centerline_distance_px:
        return None

    center = (int(points[nearest_i, 0]), int(points[nearest_i, 1]))
    if not mask[center] or not skeleton[center]:
        return None

    geodesic = _skeleton_geodesic(skeleton, center, reference_outer_px + 8.0)
    if len(geodesic) < 25:
        return None

    local_nodes = np.array(
        [p for p, d in geodesic.items() if d <= tangent_window_px],
        dtype=int,
    )
    tangent = _local_tangent(local_nodes, center[0], center[1])
    if tangent is None:
        return None

    # Reject a lesion candidate too close to a true branch/bifurcation.
    for point, geod in geodesic.items():
        if geod <= lesion_window_px + 3.0 and _degree(skeleton, point) >= 3:
            return None

    node_records: list[tuple[tuple[int, int], float, float, float]] = []
    tangent_y, tangent_x = float(tangent[0]), float(tangent[1])
    for point, geod in geodesic.items():
        yy, xx = point
        projection = (yy - center[0]) * tangent_y + (xx - center[1]) * tangent_x
        diameter = 2.0 * float(distance[yy, xx])
        if math.isfinite(diameter) and diameter > 0:
            node_records.append((point, geod, projection, diameter))

    if not node_records:
        return None

    lesion_diameters = [
        diameter
        for _, geod, _, diameter in node_records
        if geod <= lesion_window_px
    ]
    if len(lesion_diameters) < 3:
        return None

    # Use a low percentile instead of one single skeleton pixel to reduce
    # one-pixel segmentation noise while still representing the MLD.
    mld_px = float(np.percentile(np.asarray(lesion_diameters), 20))

    proximal = [
        diameter
        for _, geod, projection, diameter in node_records
        if reference_inner_px <= geod <= reference_outer_px and projection < 0
    ]
    distal = [
        diameter
        for _, geod, projection, diameter in node_records
        if reference_inner_px <= geod <= reference_outer_px and projection > 0
    ]

    if len(proximal) < min_reference_points_each_side or len(distal) < min_reference_points_each_side:
        return None

    proximal_ref = float(np.percentile(np.asarray(proximal), 75))
    distal_ref = float(np.percentile(np.asarray(distal), 75))
    if proximal_ref < 3.0 or distal_ref < 3.0:
        return None

    reference_px = (proximal_ref + distal_ref) / 2.0
    if mld_px >= reference_px:
        return None

    stenosis = (1.0 - (mld_px / reference_px)) * 100.0
    if not math.isfinite(stenosis) or stenosis < min_reportable_stenosis_percent:
        return None

    # Avoid turning an unvalidated 2D contour measurement into a 100% occlusion
    # claim. Total occlusion remains a separate future detector.
    stenosis = float(np.clip(stenosis, 0.0, 95.0))

    symmetry = min(proximal_ref, distal_ref) / max(proximal_ref, distal_ref)
    if symmetry < 0.60:
        return None

    narrowed_nodes = [
        (projection, geod)
        for _, geod, projection, diameter in node_records
        if geod <= 45.0 and (1.0 - diameter / reference_px) * 100.0 >= 20.0
    ]
    if narrowed_nodes:
        projections = [x[0] for x in narrowed_nodes]
        lesion_length_px = max(2.0, float(max(projections) - min(projections)))
    else:
        lesion_length_px = 2.0 * lesion_window_px

    reference_count = min(len(proximal), len(distal))
    center_score = float(np.clip(1.0 - centerline_distance / max_centerline_distance_px, 0.0, 1.0))
    symmetry_score = float(np.clip((symmetry - 0.60) / 0.40, 0.0, 1.0))
    reference_score = float(np.clip(reference_count / 20.0, 0.0, 1.0))
    quality = 0.40 * center_score + 0.35 * symmetry_score + 0.25 * reference_score

    if quality < 0.55:
        return None

    quality_label = "High" if quality >= 0.80 else ("Moderate" if quality >= 0.65 else "Limited")

    ref_mm = mld_mm = length_mm = None
    if pixel_spacing_mm is not None and math.isfinite(pixel_spacing_mm) and pixel_spacing_mm > 0:
        ref_mm = reference_px * pixel_spacing_mm
        mld_mm = mld_px * pixel_spacing_mm
        length_mm = lesion_length_px * pixel_spacing_mm

    return ResearchQcaMeasurement(
        diameter_stenosis_percent=stenosis,
        reference_diameter_px=reference_px,
        minimum_lumen_diameter_px=mld_px,
        lesion_length_px=lesion_length_px,
        quality_score=float(np.clip(quality, 0.0, 1.0)),
        quality_label=quality_label,
        candidate_to_centerline_px=centerline_distance,
        proximal_reference_diameter_px=proximal_ref,
        distal_reference_diameter_px=distal_ref,
        reference_symmetry=symmetry,
        reference_points_each_side=reference_count,
        reference_diameter_mm=ref_mm,
        minimum_lumen_diameter_mm=mld_mm,
        lesion_length_mm=length_mm,
        calibration_source=calibration_source if ref_mm is not None else None,
    )
