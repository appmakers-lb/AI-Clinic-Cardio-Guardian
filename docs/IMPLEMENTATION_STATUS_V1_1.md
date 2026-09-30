# Implementation Status — v1.1.3

## Implemented

### A02 — Windows Platform
- WPF / .NET 8 Visual Studio 2022 solution
- research-mode UI
- local audit log
- case reset
- clearer 3-column Cath-Lab layout

### A03 — Imaging / DICOM
- CD/USB/folder scan
- DICOM grouping by study/series
- XA/coronary prioritization
- multi-frame cine rendering
- cine playback / frame stepping
- projection metadata where present
- common fo-dicom codec integration

### A06 — Multi-view / Whole-case memory foundation
- every imported cine is registered as a case run
- stable source IDs
- evidence references link findings to run/frame/projection
- evidence navigation can jump to a referenced DICOM frame

### A07 — Guardian / Coverage
- LM/LAD/LCx/RCA coverage state
- incomplete/unassessed summary
- evidence-required findings
- version-required structured model findings
- physician confirm/dismiss
- no-model safe refusal

### A08 — Voice Copilot
- Windows TTS
- constrained push-to-listen commands when a recognizer is installed
- high-priority structured alerts can be spoken
- voice cannot create findings

### A11 — Safety / Traceability
- research-mode banner
- local JSONL audit
- source/model version attached to structured findings
- manual findings explicitly labeled MANUAL
- model gateway defaults to blocked/no-model state

### A12 — QA
- core safety tests expanded
- XAML handler consistency checks performed during packaging

## Integration-ready, but no model weights included

### A04 — Vessel Vision
The app has the contract and gateway. A real vessel-segmentation model still needs
research selection/training/validation.

### A05 — Lesion/QCA
The app can receive versioned lesion/QCA findings. No validated lesion model is
bundled.

### A09 — Physiology / IVUS / OCT
Architecture remains planned. No FFR/iFR/IVUS/OCT clinical algorithm is bundled.

## Why these parts are not faked

A random/demo lesion detector would make the UI look impressive while being unsafe and
scientifically meaningless. The repository therefore only surfaces a medical finding if
it came from:
- a human research annotation, or
- a versioned structured research model source with evidence.

## Next engineering gate

1. Test v1.1 against the user's actual cardiac CD.
2. Fix vendor-specific DICOM edge cases if present.
3. Select/benchmark vessel segmentation research models.
4. Integrate the best candidate through `model_plugin.py`.
5. Add lesion/QCA research pipeline.
6. Evaluate on de-identified retrospective cases with cardiologist annotations.
