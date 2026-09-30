# AI Clinic Cardio Guardian — Roadmap V4

## Product split
AI Clinic Assistant remains separate from AI Clinic Cardio Guardian.
This repository is Cardio Guardian only.

## V1.0 Research Foundation — current
- Visual Studio 2022 solution
- C#/.NET WPF Windows app
- Research Mode hard-coded
- recorded cine/video shell
- whole-case coronary coverage state
- Guardian findings contract
- evidence contract
- audit log
- grounded copilot shell
- Master Agent + 12 specialist prompts
- Python AI-research layer separated from UI
- core safety test harness

## V1.1 — DICOM/XA Research Ingest
Owner: A03 + A11 + A12
- DICOM/XA reader adapter
- preserve frame identity / timestamps / acquisition metadata
- de-identification checks for research import
- manual evidence annotation tool
- no clinical inference

## V1.2 — Vessel Engine Research
Owner: A04 + A12
- image quality / abstention indicators
- vessel segmentation
- centerline
- LM/LAD/LCx/RCA labeling where supported
- best-frame assistance

## V1.3 — Lesion/QCA Research
Owner: A05 + A12
- suspected lesion localization
- reference/minimum diameter
- lesion length
- stenosis estimate
- physician correction
- evidence-linked structured output

## V1.4 — Multi-View Memory
Owner: A06
- cine-run identity
- same-lesion linkage across views
- pre/post linkage
- evidence bundles

## V1.5 — Coverage Guardian
Owner: A07
- adequate/partial/incomplete/unassessed state
- automatic coverage evidence when validated
- never equate not-seen with normal

## V1.6 — Guardian Mode
Owner: A07 + A10 + A12
- continuous recorded-case observation
- alert persistence/scoring
- alarm throttling
- high-priority review cards

## V1.7 — Voice Copilot
Owner: A08 + A10
- Show me
- Show me why
- Other view
- Anything incomplete?
- confidence/evidence response
- mute / high-priority only
- English/Arabic command architecture

## V1.8 — Physiology + IVUS/OCT Fusion
Owner: A09
- FFR/iFR import
- physiology linkage
- IVUS/OCT linkage
- disagreement cards

## V1.9 — Post-PCI + Remote Second Opinion Research
Owner: A09 + A10 + A11
- before/after review
- unresolved finding list
- remote authenticated reviewer
- synchronized annotations

## V2.0 — Retrospective Validation Candidate
Only after predefined model and human-factors gates.

## Later
- external validation
- shadow mode
- regulatory/hospital approval
- limited clinical assist

The cardiologist always decides.
