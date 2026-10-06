"""Frame-quality scoring for coronary X-ray angiography research analysis.

The scorer is deliberately conservative. It favors contrast-filled, sharp,
non-saturated frames with a usable coronary tree and penalizes likely overlap
or motion instability. A single 2-D image cannot prove absence of
foreshortening; cross-view relative foreshortening is handled after lesion
linking by comparing the same lesion across distinct projections.
"""
from __future__ import annotations

from dataclasses import dataclass
import math

import numpy as np
from scipy import ndimage as ndi
from skimage.morphology import skeletonize


@dataclass(frozen=True)
class FrameQuality:
    total_score: float
    contrast_score: float
    sharpness_score: float
    saturation_score: float
    vessel_fill_score: float
    overlap_score: float
    motion_stability_score: float
    vessel_fraction: float
    overlap_risk: float
    usable: bool


def normalize_image(image: np.ndarray) -> np.ndarray:
    image = np.asarray(image, dtype=np.float32)
    finite = image[np.isfinite(image)]
    if finite.size < 16:
        return np.zeros_like(image, dtype=np.float32)

    lo = float(np.percentile(finite, 1.0))
    hi = float(np.percentile(finite, 99.0))
    if not math.isfinite(lo) or not math.isfinite(hi) or hi <= lo:
        return np.zeros_like(image, dtype=np.float32)

    return np.clip((image - lo) / (hi - lo), 0.0, 1.0)


def _mask_iou(a: np.ndarray | None, b: np.ndarray | None) -> float | None:
    if a is None or b is None:
        return None
    a = np.asarray(a, dtype=bool)
    b = np.asarray(b, dtype=bool)
    union = int(np.logical_or(a, b).sum())
    if union <= 0:
        return None
    return float(np.logical_and(a, b).sum() / union)


def _border(mask: np.ndarray) -> np.ndarray:
    eroded = ndi.binary_erosion(mask, iterations=1)
    return np.logical_and(mask, np.logical_not(eroded))


def _overlap_risk(mask: np.ndarray) -> float:
    mask = np.asarray(mask, dtype=bool)
    if mask.sum() < 50:
        return 1.0

    skeleton = skeletonize(mask)
    points = np.argwhere(skeleton)
    if points.shape[0] < 20:
        return 1.0

    neighbor_count = ndi.convolve(
        skeleton.astype(np.uint8),
        np.ones((3, 3), dtype=np.uint8),
        mode="constant",
        cval=0,
    ) - skeleton.astype(np.uint8)

    # Degree >= 4 is more suspicious for superimposed vessels/crossings than a
    # normal bifurcation (degree 3).
    crossing_fraction = float(
        np.logical_and(skeleton, neighbor_count >= 4).sum()
        / max(1, skeleton.sum())
    )

    distance = ndi.distance_transform_edt(mask)
    widths = 2.0 * distance[skeleton]
    widths = widths[np.isfinite(widths) & (widths > 0)]
    if widths.size < 10:
        width_spike_fraction = 1.0
    else:
        median_width = float(np.median(widths))
        if median_width <= 0:
            width_spike_fraction = 1.0
        else:
            width_spike_fraction = float(
                np.mean(widths > max(6.0, 2.6 * median_width))
            )

    return float(np.clip(
        0.70 * min(1.0, crossing_fraction / 0.02)
        + 0.30 * min(1.0, width_spike_fraction / 0.08),
        0.0,
        1.0,
    ))


def score_frame(
    image: np.ndarray,
    vessel_probability: np.ndarray,
    vessel_mask: np.ndarray,
    *,
    previous_vessel_mask: np.ndarray | None = None,
    next_vessel_mask: np.ndarray | None = None,
) -> FrameQuality:
    image_n = normalize_image(image)
    vessel_probability = np.asarray(vessel_probability, dtype=np.float32)
    vessel_mask = np.asarray(vessel_mask, dtype=bool)

    if image_n.shape != vessel_mask.shape:
        raise ValueError("image and vessel mask must share the same shape")

    vessel_fraction = float(vessel_mask.mean())
    if vessel_mask.sum() < 50:
        return FrameQuality(
            total_score=0.0,
            contrast_score=0.0,
            sharpness_score=0.0,
            saturation_score=0.0,
            vessel_fill_score=0.0,
            overlap_score=0.0,
            motion_stability_score=0.0,
            vessel_fraction=vessel_fraction,
            overlap_risk=1.0,
            usable=False,
        )

    dilated = ndi.binary_dilation(vessel_mask, iterations=5)
    background_ring = np.logical_and(dilated, np.logical_not(vessel_mask))
    if background_ring.sum() < 20:
        background_ring = np.logical_not(vessel_mask)

    vessel_median = float(np.median(image_n[vessel_mask]))
    background_median = float(np.median(image_n[background_ring]))
    contrast_delta = abs(vessel_median - background_median)
    contrast_score = float(np.clip((contrast_delta - 0.025) / 0.18, 0.0, 1.0))

    gy, gx = np.gradient(image_n)
    grad = np.hypot(gx, gy)
    border = _border(vessel_mask)
    border_grad = grad[border]
    sharpness = float(np.median(border_grad)) if border_grad.size else 0.0
    sharpness_score = float(np.clip((sharpness - 0.015) / 0.12, 0.0, 1.0))

    saturated_fraction = float(np.mean((image_n <= 0.005) | (image_n >= 0.995)))
    saturation_score = float(np.clip(1.0 - saturated_fraction / 0.30, 0.0, 1.0))

    # Typical coronary tree masks occupy a modest image fraction. Very small
    # masks suggest poor opacification; very large masks usually indicate a
    # segmentation/background failure.
    if vessel_fraction < 0.004:
        vessel_fill_score = vessel_fraction / 0.004
    elif vessel_fraction <= 0.18:
        vessel_fill_score = 1.0
    elif vessel_fraction < 0.32:
        vessel_fill_score = 1.0 - (vessel_fraction - 0.18) / 0.14
    else:
        vessel_fill_score = 0.0
    vessel_fill_score = float(np.clip(vessel_fill_score, 0.0, 1.0))

    overlap_risk = _overlap_risk(vessel_mask)
    overlap_score = 1.0 - overlap_risk

    ious = [
        value
        for value in (
            _mask_iou(vessel_mask, previous_vessel_mask),
            _mask_iou(vessel_mask, next_vessel_mask),
        )
        if value is not None
    ]
    motion_stability_score = float(np.median(ious)) if ious else 0.65

    total = (
        0.28 * contrast_score
        + 0.20 * sharpness_score
        + 0.12 * saturation_score
        + 0.18 * vessel_fill_score
        + 0.12 * overlap_score
        + 0.10 * motion_stability_score
    )

    usable = (
        total >= 0.52
        and contrast_score >= 0.30
        and vessel_fill_score >= 0.45
        and overlap_risk <= 0.80
    )

    return FrameQuality(
        total_score=float(np.clip(total, 0.0, 1.0)),
        contrast_score=contrast_score,
        sharpness_score=sharpness_score,
        saturation_score=saturation_score,
        vessel_fill_score=vessel_fill_score,
        overlap_score=overlap_score,
        motion_stability_score=motion_stability_score,
        vessel_fraction=vessel_fraction,
        overlap_risk=overlap_risk,
        usable=usable,
    )


def select_best_frames(
    frame_indices: list[int],
    qualities: dict[int, FrameQuality],
    *,
    maximum: int,
    minimum_spacing_frames: int = 1,
) -> list[int]:
    ranked = sorted(
        (
            (qualities[index].total_score, index)
            for index in frame_indices
            if index in qualities and qualities[index].usable
        ),
        reverse=True,
    )

    selected: list[int] = []
    for _, index in ranked:
        if any(abs(index - existing) < minimum_spacing_frames for existing in selected):
            continue
        selected.append(index)
        if len(selected) >= maximum:
            break

    return sorted(selected)
