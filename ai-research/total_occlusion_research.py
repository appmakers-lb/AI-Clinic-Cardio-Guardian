"""Conservative research detector for angiographic total-occlusion candidates.

This is separate from ordinary QCA stenosis measurement. It does NOT convert a
high stenosis percentage into 100%. A candidate requires an abrupt supported
vessel termination, absent forward vessel probability, proximity to an
independent stenosis-localization signal, and later temporal persistence across
multiple high-quality frames.

The detector can flag a *suspected total occlusion*. It cannot establish
chronicity; therefore it must not label a finding as CTO without clinical
history or prior imaging showing duration >= 3 months.
"""
from __future__ import annotations

from dataclasses import dataclass
import math

import numpy as np
from scipy import ndimage as ndi
from skimage.morphology import skeletonize


@dataclass(frozen=True)
class TotalOcclusionCandidate:
    x: float
    y: float
    score: float
    proximal_diameter_px: float
    abruptness_score: float
    distal_void_score: float
    stenosis_signal_score: float


def _neighbors(point: tuple[int, int], shape: tuple[int, int]):
    y, x = point
    h, w = shape
    for dy in (-1, 0, 1):
        for dx in (-1, 0, 1):
            if dy == 0 and dx == 0:
                continue
            yy, xx = y + dy, x + dx
            if 0 <= yy < h and 0 <= xx < w:
                yield yy, xx


def _degree(skeleton: np.ndarray, point: tuple[int, int]) -> int:
    return sum(1 for n in _neighbors(point, skeleton.shape) if skeleton[n])


def _local_tangent(points: np.ndarray, y: float, x: float) -> np.ndarray | None:
    if points.shape[0] < 5:
        return None
    centered = points.astype(np.float64) - np.array([[y, x]], dtype=np.float64)
    covariance = centered.T @ centered
    values, vectors = np.linalg.eigh(covariance)
    tangent = vectors[:, int(np.argmax(values))]
    norm = float(np.linalg.norm(tangent))
    if norm <= 1e-9:
        return None
    return tangent / norm


def _cross_section_width(
    mask: np.ndarray,
    center_y: float,
    center_x: float,
    tangent_y: float,
    tangent_x: float,
    *,
    max_half_width_px: int = 30,
) -> float:
    ny, nx = -tangent_x, tangent_y
    offsets = np.arange(-max_half_width_px, max_half_width_px + 1, dtype=float)
    ys = np.rint(center_y + ny * offsets).astype(int)
    xs = np.rint(center_x + nx * offsets).astype(int)

    valid = (
        (ys >= 0)
        & (xs >= 0)
        & (ys < mask.shape[0])
        & (xs < mask.shape[1])
    )
    values = np.zeros(offsets.shape, dtype=bool)
    values[valid] = mask[ys[valid], xs[valid]]

    center_index = max_half_width_px
    if not values[center_index]:
        return 0.0

    left = center_index
    while left > 0 and values[left - 1]:
        left -= 1

    right = center_index
    while right < values.size - 1 and values[right + 1]:
        right += 1

    return float(right - left + 1)


def detect_total_occlusion_candidates(
    vessel_mask: np.ndarray,
    vessel_probability: np.ndarray,
    stenosis_points: list[tuple[float, float, float]],
    *,
    image_edge_margin_px: int = 24,
    max_stenosis_distance_px: float = 34.0,
    minimum_proximal_diameter_px: float = 4.0,
) -> list[TotalOcclusionCandidate]:
    mask = np.asarray(vessel_mask, dtype=bool)
    probability = np.asarray(vessel_probability, dtype=np.float32)
    if mask.shape != probability.shape or mask.sum() < 80 or not stenosis_points:
        return []

    labels, count = ndi.label(mask)
    if count <= 0:
        return []

    sizes = np.bincount(labels.ravel())
    keep = sizes >= 80
    keep[0] = False
    mask = keep[labels]
    if not np.any(mask):
        return []

    skeleton = skeletonize(mask)
    distance = ndi.distance_transform_edt(mask)
    points = np.argwhere(skeleton)
    h, w = mask.shape

    endpoints = [
        tuple(map(int, p))
        for p in points
        if _degree(skeleton, tuple(map(int, p))) == 1
    ]

    results: list[TotalOcclusionCandidate] = []

    for endpoint in endpoints:
        ey, ex = endpoint
        if (
            ex < image_edge_margin_px
            or ey < image_edge_margin_px
            or ex >= w - image_edge_margin_px
            or ey >= h - image_edge_margin_px
        ):
            continue

        component_id = int(labels[endpoint])
        if component_id <= 0:
            continue
        component_points = np.argwhere(np.logical_and(skeleton, labels == component_id))
        if component_points.shape[0] < 35:
            continue

        d2 = (
            (component_points[:, 0].astype(float) - ey) ** 2
            + (component_points[:, 1].astype(float) - ex) ** 2
        )
        local = component_points[d2 <= 18.0 ** 2]
        tangent = _local_tangent(local, ey, ex)
        if tangent is None:
            continue

        # Orient the tangent so the negative direction points back into the
        # vessel and the positive direction points beyond the endpoint.
        ty, tx = float(tangent[0]), float(tangent[1])
        probe_back = (
            int(round(ey - ty * 8.0)),
            int(round(ex - tx * 8.0)),
        )
        probe_forward = (
            int(round(ey + ty * 8.0)),
            int(round(ex + tx * 8.0)),
        )

        def inside(point: tuple[int, int]) -> bool:
            yy, xx = point
            return 0 <= yy < h and 0 <= xx < w and bool(mask[yy, xx])

        if not inside(probe_back) and inside(probe_forward):
            ty, tx = -ty, -tx
            probe_back, probe_forward = probe_forward, probe_back

        if not inside(probe_back):
            continue

        # Sample proximal caliber 6-18 px behind the apparent cap.
        proximal_widths = []
        for step in np.linspace(6.0, 18.0, 7):
            yy = int(round(ey - ty * step))
            xx = int(round(ex - tx * step))
            if 0 <= yy < h and 0 <= xx < w and mask[yy, xx]:
                proximal_widths.append(2.0 * float(distance[yy, xx]))

        if len(proximal_widths) < 4:
            continue

        proximal_diameter = float(np.median(proximal_widths))
        if proximal_diameter < minimum_proximal_diameter_px:
            continue

        # Locate the actual forward edge of the segmented vessel. A skeleton
        # endpoint lies inside a thick blunt vessel, so distance-transform radius
        # at the endpoint alone cannot distinguish an abrupt cap from tapering.
        forward_inside_steps: list[float] = []
        for step in np.linspace(0.0, 24.0, 49):
            yy = int(round(ey + ty * step))
            xx = int(round(ex + tx * step))
            if 0 <= yy < h and 0 <= xx < w and mask[yy, xx]:
                forward_inside_steps.append(float(step))
            elif forward_inside_steps:
                break

        if not forward_inside_steps:
            continue

        cap_step = max(forward_inside_steps)
        near_cap_step = max(0.0, cap_step - 1.5)
        near_cap_width = _cross_section_width(
            mask,
            ey + ty * near_cap_step,
            ex + tx * near_cap_step,
            ty,
            tx,
        )
        if near_cap_width <= 0:
            continue

        # A blunt/abrupt end retains most of its upstream caliber until the
        # termination; a normal distal taper becomes progressively thinner.
        abruptness = float(np.clip(
            near_cap_width / max(proximal_diameter, 1e-6),
            0.0,
            1.0,
        ))
        if abruptness < 0.60:
            continue

        # Look forward in a narrow cone. A true supported abrupt cutoff should
        # have little vessel probability continuing beyond the cap.
        forward_values = []
        for step in np.linspace(4.0, 24.0, 11):
            cy = ey + ty * step
            cx = ex + tx * step
            for lateral in (-3.0, 0.0, 3.0):
                ny, nx = -tx, ty
                yy = cy + ny * lateral
                xx = cx + nx * lateral
                value = ndi.map_coordinates(
                    probability,
                    [[yy], [xx]],
                    order=1,
                    mode="constant",
                    cval=0.0,
                )[0]
                forward_values.append(float(value))

        mean_forward = float(np.mean(forward_values)) if forward_values else 1.0
        distal_void = float(np.clip((0.35 - mean_forward) / 0.35, 0.0, 1.0))
        if distal_void < 0.45:
            continue

        nearest_stenosis_distance = float("inf")
        stenosis_score = 0.0
        for sx, sy, confidence in stenosis_points:
            d = math.hypot(float(sx) - ex, float(sy) - ey)
            if d < nearest_stenosis_distance:
                nearest_stenosis_distance = d
                stenosis_score = float(confidence)

        if nearest_stenosis_distance > max_stenosis_distance_px or stenosis_score < 0.55:
            continue

        proximity = float(np.clip(
            1.0 - nearest_stenosis_distance / max_stenosis_distance_px,
            0.0,
            1.0,
        ))
        score = (
            0.34 * abruptness
            + 0.32 * distal_void
            + 0.22 * stenosis_score
            + 0.12 * proximity
        )

        if score < 0.62:
            continue

        results.append(
            TotalOcclusionCandidate(
                x=float(ex),
                y=float(ey),
                score=float(np.clip(score, 0.0, 1.0)),
                proximal_diameter_px=proximal_diameter,
                abruptness_score=abruptness,
                distal_void_score=distal_void,
                stenosis_signal_score=stenosis_score,
            )
        )

    results.sort(key=lambda item: item.score, reverse=True)
    return results[:4]
