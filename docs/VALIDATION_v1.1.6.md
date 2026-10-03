# v1.1.6 Validation Matrix

> RESEARCH MODE — NOT FOR CLINICAL DECISIONS

## Automated release gates
- Windows .NET 8 restore succeeds.
- Windows Release build succeeds.
- Core safety regression executable succeeds.
- Python research gateway and no-model adapter pass syntax compilation.

## Safety invariants covered by tests
- Structured model findings require model ID/version.
- Finding confidence must remain in [0,1].
- Findings require evidence references.
- Finding evidence must reference a cine already loaded into the case.
- Duplicate finding IDs are blocked.
- Equivalent evidence-linked findings are suppressed across repeated analyses.
- Coverage output requires explicit evidence.
- Coverage evidence must reference a loaded cine.
- Conflicting coverage is merged conservatively.
- NO_MODEL packages cannot be imported as medical-model output.
- Duplicate Guardian voice alerts are suppressed inside the configured window.
- Unsupported voice commands remain Unknown and are not executed.
- Physical QCA measurements require an explicit calibration source.
- Vessel geometry uses bounded normalized coordinates.
- Physiology / IVUS / OCT research objects require explicit adapter provenance.

## Manual workstation checks before merge
1. Import a non-PHI or approved de-identified XA/DICOM test study.
2. Verify cine list, first likely coronary load, play/pause/frame navigation.
3. Verify cancellation of selected-cine and whole-case model requests.
4. Verify no analysis buttons enable when the local gateway reports NO MODEL.
5. Import structured example output only after replacing evidence source IDs with loaded test cine IDs.
6. Verify duplicate import does not duplicate whole-case findings.
7. Verify Coverage tab never treats unassessed territory as normal.
8. Verify SHOW WHY navigates to the linked cine/frame.
9. Verify confirm/dismiss actions are written to the local audit log.
10. Verify voice navigation only executes constrained commands.

## Explicitly outside v1.1.6 validation
- Clinical sensitivity/specificity.
- Stenosis/occlusion diagnostic performance.
- QCA accuracy.
- FFR/iFR/QFR clinical agreement.
- IVUS/OCT measurement accuracy.
- Prospective cath-lab use.

Those require real datasets, cardiologist ground truth, external validation, formal quality/risk controls and applicable regulatory/hospital approval.
