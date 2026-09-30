# Current Sprint S1 — C# Windows Foundation + Imaging Contract

Master Agent owns integration and release.

## Assigned now
- **A02 Windows Platform:** Visual Studio 2022 / C# WPF foundation, workstation lifecycle, packaging.
- **A03 Imaging/DICOM:** define research cine/DICOM/XA ingest contract; next implementation target.
- **A06 Multi-View Memory:** CaseState and segment identity contract.
- **A07 Guardian/Coverage:** coverage states, no-finding-vs-not-assessed separation, alert policy.
- **A08 Voice Copilot:** grounded shell only; no medical inference generation.
- **A11 Security/Regulatory:** Research Mode boundary + local audit.
- **A12 Validation/QA:** safety test harness and go/no-go gates.

## Queued after S1 baseline
- **A04 Vessel Vision:** segmentation, centerlines, vessel labels, best-frame research.
- **A05 Lesion/QCA:** evidence-linked lesion/QCA outputs.
- **A09 Physiology Fusion:** FFR/iFR and IVUS/OCT integration.
- **A10 Human Factors:** alert burden, table-side UX, remote second opinion.
- **A01 Competitor/Product:** maintain dated claims and Lebanon verification registry.

## Master release rule
No specialist can promote a finding to a user-facing Guardian alert unless the output is structured, versioned, evidence-linked, and passes the current Research-Mode policy.
