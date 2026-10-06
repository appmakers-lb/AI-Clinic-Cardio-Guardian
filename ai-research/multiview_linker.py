"""Conservative cross-series / cross-projection lesion linking.

The linker only merges findings when it has enough identity evidence:
- same inferred left/right coronary injection side from DICOM/series metadata;
- compatible finding type;
- similar longitudinal position along the segmented vessel component;
- similar reference caliber and stenosis severity;
- evidence from distinct source series.

It deliberately abstains when injection side or longitudinal position is
unknown. This avoids pretending that two spatially unrelated 2-D hotspots are
necessarily the same lesion.
"""
from __future__ import annotations

from hashlib import sha256
import math
import re
from typing import Any

import numpy as np


_LEFT_PATTERNS = (
    r"\blca\b",
    r"left\s+coronary",
    r"left\s+main",
    r"\blad\b",
    r"circumflex",
    r"\blcx\b",
)
_RIGHT_PATTERNS = (
    r"\brca\b",
    r"right\s+coronary",
)


def infer_injection_side(*metadata: str | None) -> str | None:
    text = " ".join(x or "" for x in metadata).lower()
    left = any(re.search(pattern, text) for pattern in _LEFT_PATTERNS)
    right = any(re.search(pattern, text) for pattern in _RIGHT_PATTERNS)

    if left and not right:
        return "LEFT"
    if right and not left:
        return "RIGHT"
    return None


def _float(item: dict[str, Any], key: str) -> float | None:
    value = item.get(key)
    try:
        number = float(value)
    except Exception:
        return None
    return number if math.isfinite(number) else None


def _compatible(a: dict[str, Any], b: dict[str, Any]) -> bool:
    side_a = a.get("injectionSide")
    side_b = b.get("injectionSide")
    if not side_a or side_a != side_b:
        return False

    type_a = str(a.get("findingType") or "")
    type_b = str(b.get("findingType") or "")
    if type_a != type_b:
        return False

    if str(a.get("evidence", [{}])[0].get("sourceId", "")) == str(
        b.get("evidence", [{}])[0].get("sourceId", "")
    ):
        return False

    pos_a = _float(a, "longitudinalPosition")
    pos_b = _float(b, "longitudinalPosition")
    if pos_a is None or pos_b is None or abs(pos_a - pos_b) > 0.16:
        return False

    ref_a = _float(a, "referenceDiameterPixels")
    ref_b = _float(b, "referenceDiameterPixels")
    if ref_a is not None and ref_b is not None:
        ratio = max(ref_a, ref_b) / max(1e-9, min(ref_a, ref_b))
        if ratio > 1.55:
            return False

    if type_a == "SuspectedStenosis":
        pct_a = _float(a, "estimatedDiameterStenosisPercent")
        pct_b = _float(b, "estimatedDiameterStenosisPercent")
        if pct_a is None or pct_b is None or abs(pct_a - pct_b) > 18.0:
            return False

    if type_a == "SuspectedTotalOcclusion":
        score_a = _float(a, "totalOcclusionScore")
        score_b = _float(b, "totalOcclusionScore")
        if score_a is None or score_b is None or min(score_a, score_b) < 0.62:
            return False

    return True


def _view_rank(finding: dict[str, Any], max_span: float) -> float:
    frame_quality = _float(finding, "frameQualityScore") or 0.0
    overlap_risk = _float(finding, "frameOverlapRisk")
    overlap_score = 1.0 - (overlap_risk if overlap_risk is not None else 0.5)
    span = _float(finding, "projectedReferenceSpanPixels") or 0.0
    span_score = span / max(max_span, 1e-9)
    measurement_quality = _float(finding, "measurementQualityScore") or 0.0

    return (
        0.34 * frame_quality
        + 0.26 * overlap_score
        + 0.22 * span_score
        + 0.18 * measurement_quality
    )


def link_findings_across_views(
    findings: list[dict[str, Any]],
) -> list[dict[str, Any]]:
    if not findings:
        return []

    groups: list[list[dict[str, Any]]] = []

    for finding in sorted(
        findings,
        key=lambda item: (
            str(item.get("injectionSide") or ""),
            str(item.get("findingType") or ""),
            _float(item, "longitudinalPosition") or -1.0,
        ),
    ):
        best_group: list[dict[str, Any]] | None = None
        best_distance = float("inf")

        for group in groups:
            representative = group[0]
            if not _compatible(representative, finding):
                continue

            pos_a = _float(representative, "longitudinalPosition") or 0.0
            pos_b = _float(finding, "longitudinalPosition") or 0.0
            distance = abs(pos_a - pos_b)
            if distance < best_distance:
                best_group = group
                best_distance = distance

        if best_group is None:
            groups.append([finding])
        else:
            best_group.append(finding)

    merged: list[dict[str, Any]] = []

    for group in groups:
        sources = {
            str(evidence.get("sourceId") or "")
            for finding in group
            for evidence in finding.get("evidence", [])
            if evidence.get("sourceId")
        }
        projections = {
            str(evidence.get("projection") or "").strip()
            for finding in group
            for evidence in finding.get("evidence", [])
            if str(evidence.get("projection") or "").strip()
        }

        if len(group) == 1 or len(sources) < 2:
            item = dict(group[0])
            item["multiViewConfirmed"] = False
            item["projectionCount"] = max(1, len(projections))
            item["sourceSeriesCount"] = max(1, len(sources))
            merged.append(item)
            continue

        max_span = max(
            (_float(finding, "projectedReferenceSpanPixels") or 0.0)
            for finding in group
        )
        ranked = sorted(group, key=lambda item: _view_rank(item, max_span), reverse=True)
        best = dict(ranked[0])

        group_key = "|".join(sorted(sources))
        best["lesionGroupId"] = "lesion-" + sha256(group_key.encode("utf-8")).hexdigest()[:12]
        best["multiViewConfirmed"] = True
        best["projectionCount"] = max(1, len(projections))
        best["sourceSeriesCount"] = len(sources)

        if best.get("findingType") == "SuspectedStenosis":
            percentages = np.asarray(
                [
                    value
                    for value in (
                        _float(finding, "estimatedDiameterStenosisPercent")
                        for finding in group
                    )
                    if value is not None
                ],
                dtype=float,
            )
            if percentages.size:
                median = float(np.median(percentages))
                cross_view_variability = float(np.max(percentages) - np.min(percentages))
                best["estimatedDiameterStenosisPercent"] = round(median, 1)
                best["crossViewVariabilityPercent"] = round(cross_view_variability, 2)

                existing_lower = [
                    value
                    for value in (
                        _float(finding, "estimatedDiameterStenosisLowerPercent")
                        for finding in group
                    )
                    if value is not None
                ]
                existing_upper = [
                    value
                    for value in (
                        _float(finding, "estimatedDiameterStenosisUpperPercent")
                        for finding in group
                    )
                    if value is not None
                ]
                spread = max(8.0, cross_view_variability / 2.0)
                lower = min(existing_lower) if existing_lower else median - spread
                upper = max(existing_upper) if existing_upper else median + spread
                best["estimatedDiameterStenosisLowerPercent"] = round(max(0.0, lower), 1)
                best["estimatedDiameterStenosisUpperPercent"] = round(min(95.0, upper), 1)

        all_evidence = []
        for finding in ranked:
            all_evidence.extend(finding.get("evidence", []))
        best["evidence"] = all_evidence

        best["measurementFrameCount"] = int(
            sum(int(finding.get("measurementFrameCount") or 0) for finding in group)
        )

        best["measurementSummary"] = (
            str(best.get("measurementSummary") or "")
            + f" Multi-view linked across {len(sources)} series"
            + (f" / {len(projections)} projection(s)." if projections else ".")
        ).strip()

        merged.append(best)

    merged.sort(
        key=lambda item: (
            1 if item.get("multiViewConfirmed") else 0,
            _float(item, "estimatedDiameterStenosisPercent") or 0.0,
            _float(item, "totalOcclusionScore") or 0.0,
        ),
        reverse=True,
    )
    return merged
