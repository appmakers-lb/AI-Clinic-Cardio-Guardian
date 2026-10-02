# Security and Research Boundaries

## Local-first boundary
- Raw DICOM/XA inputs remain local to the workstation.
- The model gateway binds to 127.0.0.1 by default.
- Core review functions do not require Internet access.
- Patient DICOM, model weights, secrets and local configuration are excluded from Git.

## Evidence integrity
- Model findings require model identity/version and frame-addressable evidence.
- Evidence source IDs must exist in the loaded case.
- Coverage states from a model also require evidence linked to a loaded cine.
- The software does not convert missing coverage into a normal/negative finding.

## Model boundary
The repository contains no diagnostic coronary model. The default Python adapter reports NO MODEL and refuses inference. Any future adapter must be versioned and separately validated.

## Physician-control boundary
The application may display, remember, highlight, alert and explain research evidence. It does not autonomously manipulate catheters, select treatment, deploy devices, change medications or make the final clinical decision.

## Audit boundary
Research events include import status, model connection, analysis completion/failure/cancellation, Guardian alert issue/suppression, evidence display and physician confirm/dismiss actions. Logs should avoid raw patient identifiers and source-folder paths.
