"""Research-only vessel confirmation and apparent narrowing estimator.

This is NOT clinical QCA. It uses a research vessel U-Net plus local binary-mask
geometry to reject candidates that are not near a segmented vessel and, when
quality gates pass, estimate an *apparent diameter reduction* with a wide
uncertainty interval.

The estimate is intentionally capped below 100%. Complete occlusion requires
separate validated logic and must not be inferred from this geometry alone.
"""
from __future__ import annotations

from dataclasses import dataclass
import math

import numpy as np
from scipy import ndimage as ndi
from skimage.morphology import skeletonize


@dataclass(frozen=True)
class ApparentNarrowingEstimate:
    percent: float
    lower: float
    upper: float
    candidate_to_centerline_px: float
    local_radius_px: float
    reference_radius_px: float


def clean_vessel_mask(mask: np.ndarray, min_component: int = 60) -> np.ndarray:
    mask = np.asarray(mask, dtype=bool)
    labels, count = ndi.label(mask)
    if count <= 0:
        return np.zeros_like(mask, dtype=bool)

    sizes = np.bincount(labels.ravel())
    keep = sizes >= max(1, int(min_component))
    keep[0] = False
    return keep[labels]


def estimate_apparent_narrowing(
    vessel_mask: np.ndarray,
    candidate_x: float,
    candidate_y: float,
    *,
    max_centerline_distance_px: float = 32.0,
    reference_inner_px: float = 20.0,
    reference_outer_px: float = 90.0,
    min_reference_points: int = 10,
) -> ApparentNarrowingEstimate | None:
    """Estimate relative local diameter loss near a model candidate.

    Coordinates are in the same pixel space as vessel_mask. The method:
      1) skeletonizes the segmented vessel,
      2) finds the centerline point nearest the candidate,
      3) reads local radius from the distance transform,
      4) compares it with a robust nearby reference radius.

    Returns None when quality gates fail. This is deliberately conservative.
    """
    mask = clean_vessel_mask(vessel_mask)
    if not np.any(mask):
        return None

    distance = ndi.distance_transform_edt(mask)
    skeleton = skeletonize(mask)
    points = np.argwhere(skeleton)
    if points.shape[0] < 15:
        return None

    cy = float(candidate_y)
    cx = float(candidate_x)
    d2 = (points[:, 0].astype(float) - cy) ** 2 + (points[:, 1].astype(float) - cx) ** 2
    nearest_idx = int(np.argmin(d2))
    centerline_distance = math.sqrt(float(d2[nearest_idx]))
    if centerline_distance > max_centerline_distance_px:
        return None

    py, px = (int(points[nearest_idx, 0]), int(points[nearest_idx, 1]))
    local_radius = float(distance[py, px])
    if not math.isfinite(local_radius) or local_radius < 1.0:
        return None

    # Keep reference points on the same connected vessel component.
    labels, _ = ndi.label(mask)
    component_id = int(labels[py, px])
    if component_id <= 0:
        return None

    point_labels = labels[points[:, 0], points[:, 1]]
    spatial_distance = np.sqrt(d2)
    reference_selector = (
        (point_labels == component_id)
        & (spatial_distance >= reference_inner_px)
        & (spatial_distance <= reference_outer_px)
    )
    reference_points = points[reference_selector]
    if reference_points.shape[0] < min_reference_points:
        return None

    reference_radii = distance[reference_points[:, 0], reference_points[:, 1]]
    reference_radii = reference_radii[np.isfinite(reference_radii) & (reference_radii >= 1.5)]
    if reference_radii.size < min_reference_points:
        return None

    # 75th percentile approximates a nearby non-narrowed reference segment while
    # reducing sensitivity to a single branch-point maximum.
    reference_radius = float(np.percentile(reference_radii, 75))
    if reference_radius < 2.0 or local_radius >= reference_radius:
        return None

    percent = (1.0 - local_radius / reference_radius) * 100.0
    if not math.isfinite(percent) or percent < 20.0:
        return None

    # Never claim complete occlusion from this geometry. Keep uncertainty broad
    # because this is a 2D, uncalibrated, research-only apparent diameter estimate.
    percent = float(np.clip(percent, 20.0, 95.0))
    uncertainty = 15.0
    lower = float(np.clip(percent - uncertainty, 0.0, 95.0))
    upper = float(np.clip(percent + uncertainty, 0.0, 95.0))

    return ApparentNarrowingEstimate(
        percent=percent,
        lower=lower,
        upper=upper,
        candidate_to_centerline_px=centerline_distance,
        local_radius_px=local_radius,
        reference_radius_px=reference_radius,
    )
