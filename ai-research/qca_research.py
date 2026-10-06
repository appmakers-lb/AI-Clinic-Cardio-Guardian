"""Conservative research QCA geometry for coronary X-ray angiography.

This module measures lumen width from sub-pixel probability-map border
crossings perpendicular to the vessel centerline. It deliberately abstains
when the candidate is near a branch, bilateral reference vessel is missing,
borders are weak, geometry is asymmetric, or the candidate is off-vessel.

It is a research implementation, not certified clinical QCA.
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
    border_confidence: float
    longitudinal_position: float | None
    projected_reference_span_px: float
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
    max_distance: float | None,
) -> dict[tuple[int, int], float]:
    distances: dict[tuple[int, int], float] = {start: 0.0}
    queue: list[tuple[float, tuple[int, int]]] = [(0.0, start)]

    while queue:
        distance, point = heapq.heappop(queue)
        if distance != distances.get(point):
            continue
        if max_distance is not None and distance > max_distance:
            continue

        for neighbor, step in _neighbors(point, skeleton.shape):
            if not skeleton[neighbor]:
                continue
            new_distance = distance + step
            if max_distance is not None and new_distance > max_distance:
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


def _local_tangent_at(
    component_points: np.ndarray,
    point: tuple[int, int],
    radius_px: float = 9.0,
) -> np.ndarray | None:
    py, px = point
    d2 = (
        (component_points[:, 0].astype(float) - py) ** 2
        + (component_points[:, 1].astype(float) - px) ** 2
    )
    local = component_points[d2 <= radius_px * radius_px]
    return _local_tangent(local, py, px)


def _transect_diameter(
    probability: np.ndarray,
    point: tuple[int, int],
    tangent_yx: np.ndarray,
    *,
    threshold: float = 0.50,
    max_half_width_px: float = 32.0,
    step_px: float = 0.25,
) -> tuple[float, float] | None:
    """Measure a sub-pixel lumen diameter along the normal to centerline.

    Returns (diameter_px, border_confidence).
    """
    py, px = float(point[0]), float(point[1])
    ty, tx = float(tangent_yx[0]), float(tangent_yx[1])
    ny, nx = -tx, ty

    offsets = np.arange(-max_half_width_px, max_half_width_px + step_px, step_px)
    ys = py + ny * offsets
    xs = px + nx * offsets
    samples = ndi.map_coordinates(
        probability.astype(np.float32),
        [ys, xs],
        order=1,
        mode="constant",
        cval=0.0,
    )

    center_index = int(np.argmin(np.abs(offsets)))
    if samples[center_index] < threshold:
        return None

    left = center_index
    while left > 0 and samples[left] >= threshold:
        left -= 1

    right = center_index
    while right < samples.size - 1 and samples[right] >= threshold:
        right += 1

    if left == 0 or right == samples.size - 1:
        return None

    def crossing(i0: int, i1: int) -> float:
        p0, p1 = float(samples[i0]), float(samples[i1])
        x0, x1 = float(offsets[i0]), float(offsets[i1])
        if abs(p1 - p0) < 1e-9:
            return (x0 + x1) / 2.0
        alpha = (threshold - p0) / (p1 - p0)
        alpha = float(np.clip(alpha, 0.0, 1.0))
        return x0 + alpha * (x1 - x0)

    left_cross = crossing(left, left + 1)
    right_cross = crossing(right - 1, right)
    diameter = right_cross - left_cross
    if not math.isfinite(diameter) or diameter <= 1.0:
        return None

    left_gradient = abs(float(samples[left + 1] - samples[left]))
    right_gradient = abs(float(samples[right] - samples[right - 1]))
    border_confidence = float(np.clip(
        (left_gradient + right_gradient) / max(0.10, 2.0 * step_px),
        0.0,
        1.0,
    ))
    return diameter, border_confidence


def _longitudinal_position(
    skeleton: np.ndarray,
    component_mask: np.ndarray,
    center: tuple[int, int],
    distance_transform: np.ndarray,
) -> float | None:
    endpoints = [
        tuple(map(int, point))
        for point in np.argwhere(skeleton)
        if _degree(skeleton, tuple(map(int, point))) == 1
    ]
    if len(endpoints) < 2:
        return None

    # The proximal/ostial side tends to be the endpoint with the largest local
    # caliber. This is only used as a conservative linking signature, not as a
    # vessel-name classifier.
    root = max(endpoints, key=lambda p: float(distance_transform[p]))
    root_distances = _skeleton_geodesic(skeleton, root, None)
    center_distance = root_distances.get(center)
    if center_distance is None:
        return None

    component_points = np.argwhere(component_mask)
    reachable = [
        root_distances.get(tuple(map(int, point)))
        for point in component_points
        if tuple(map(int, point)) in root_distances
    ]
    reachable = [x for x in reachable if x is not None]
    if not reachable:
        return None
    farthest = max(reachable)
    if farthest <= 1e-6:
        return None
    return float(np.clip(center_distance / farthest, 0.0, 1.0))


def measure_qca(
    vessel_mask: np.ndarray,
    candidate_x: float,
    candidate_y: float,
    *,
    vessel_probability: np.ndarray | None = None,
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
    """Return a quality-gated research QCA measurement or None."""
    mask = _clean_mask(vessel_mask)
    if not np.any(mask):
        return None

    probability = (
        np.asarray(vessel_probability, dtype=np.float32)
        if vessel_probability is not None
        else mask.astype(np.float32)
    )
    if probability.shape != mask.shape:
        raise ValueError("vessel_probability and vessel_mask must share the same shape")
    probability = np.clip(probability, 0.0, 1.0)

    labels, _ = ndi.label(mask)
    skeleton_all = skeletonize(mask)
    points = np.argwhere(skeleton_all)
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
    component_id = int(labels[center])
    if component_id <= 0:
        return None

    component_mask = labels == component_id
    skeleton = skeletonize(component_mask)
    component_points = np.argwhere(skeleton)
    if component_points.shape[0] < 30:
        return None

    geodesic = _skeleton_geodesic(skeleton, center, reference_outer_px + 10.0)
    if len(geodesic) < 25:
        return None

    local_nodes = np.array(
        [p for p, d in geodesic.items() if d <= tangent_window_px],
        dtype=int,
    )
    lesion_tangent = _local_tangent(local_nodes, center[0], center[1])
    if lesion_tangent is None:
        return None

    for point, geod in geodesic.items():
        if geod <= lesion_window_px + 3.0 and _degree(skeleton, point) >= 3:
            return None

    node_records: list[tuple[tuple[int, int], float, float, float, float]] = []
    tangent_y, tangent_x = float(lesion_tangent[0]), float(lesion_tangent[1])

    for point, geod in geodesic.items():
        yy, xx = point
        projection = (yy - center[0]) * tangent_y + (xx - center[1]) * tangent_x
        local_tangent = _local_tangent_at(component_points, point)
        if local_tangent is None:
            continue

        measured = _transect_diameter(probability, point, local_tangent)
        if measured is None:
            continue
        diameter, border_confidence = measured
        if math.isfinite(diameter) and diameter > 0:
            node_records.append((point, geod, projection, diameter, border_confidence))

    if len(node_records) < 15:
        return None

    lesion_records = [
        record
        for record in node_records
        if record[1] <= lesion_window_px
    ]
    if len(lesion_records) < 3:
        return None

    lesion_diameters = np.asarray([record[3] for record in lesion_records], dtype=float)
    mld_px = float(np.percentile(lesion_diameters, 20))

    proximal_records = [
        record
        for record in node_records
        if reference_inner_px <= record[1] <= reference_outer_px and record[2] < 0
    ]
    distal_records = [
        record
        for record in node_records
        if reference_inner_px <= record[1] <= reference_outer_px and record[2] > 0
    ]

    if (
        len(proximal_records) < min_reference_points_each_side
        or len(distal_records) < min_reference_points_each_side
    ):
        return None

    proximal = np.asarray([record[3] for record in proximal_records], dtype=float)
    distal = np.asarray([record[3] for record in distal_records], dtype=float)
    proximal_ref = float(np.percentile(proximal, 75))
    distal_ref = float(np.percentile(distal, 75))
    if proximal_ref < 3.0 or distal_ref < 3.0:
        return None

    reference_px = (proximal_ref + distal_ref) / 2.0
    if mld_px >= reference_px:
        return None

    stenosis = (1.0 - (mld_px / reference_px)) * 100.0
    if not math.isfinite(stenosis) or stenosis < min_reportable_stenosis_percent:
        return None
    stenosis = float(np.clip(stenosis, 0.0, 95.0))

    symmetry = min(proximal_ref, distal_ref) / max(proximal_ref, distal_ref)
    if symmetry < 0.65:
        return None

    all_border_confidence = np.asarray(
        [record[4] for record in lesion_records + proximal_records + distal_records],
        dtype=float,
    )
    border_confidence = float(np.median(all_border_confidence))
    if border_confidence < 0.10:
        return None

    narrowed_records = [
        record
        for record in node_records
        if record[1] <= 45.0
        and (1.0 - record[3] / reference_px) * 100.0 >= 20.0
    ]
    if narrowed_records:
        projections = [record[2] for record in narrowed_records]
        lesion_length_px = max(2.0, float(max(projections) - min(projections)))
    else:
        lesion_length_px = 2.0 * lesion_window_px

    reference_count = min(len(proximal_records), len(distal_records))
    center_score = float(np.clip(
        1.0 - centerline_distance / max_centerline_distance_px,
        0.0,
        1.0,
    ))
    symmetry_score = float(np.clip((symmetry - 0.65) / 0.35, 0.0, 1.0))
    reference_score = float(np.clip(reference_count / 20.0, 0.0, 1.0))
    border_score = float(np.clip(border_confidence / 0.35, 0.0, 1.0))

    quality = (
        0.28 * center_score
        + 0.27 * symmetry_score
        + 0.20 * reference_score
        + 0.25 * border_score
    )
    if quality < 0.60:
        return None

    quality_label = "High" if quality >= 0.82 else ("Moderate" if quality >= 0.68 else "Limited")

    projected_reference_span_px = float(
        max(record[2] for record in node_records)
        - min(record[2] for record in node_records)
    )

    distance_transform = ndi.distance_transform_edt(component_mask)
    longitudinal_position = _longitudinal_position(
        skeleton,
        component_mask,
        center,
        distance_transform,
    )

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
        border_confidence=border_confidence,
        longitudinal_position=longitudinal_position,
        projected_reference_span_px=projected_reference_span_px,
        reference_diameter_mm=ref_mm,
        minimum_lumen_diameter_mm=mld_mm,
        lesion_length_mm=length_mm,
        calibration_source=calibration_source if ref_mm is not None else None,
    )
