# AI Research Layer — v1.1

This directory is intentionally isolated from the Windows application.

The Windows application can now:
- import cardiac DICOM/XA studies locally,
- keep whole-case cine memory,
- import **versioned structured research findings**,
- show evidence-linked findings,
- navigate back to referenced frames,
- use those structured findings for Guardian/voice workflow.

What is deliberately NOT included:
- a fake stenosis/occlusion detector,
- an unvalidated LLM guessing from raw angiography,
- autonomous treatment advice.

## Model contract

A future medical computer-vision pipeline must export JSON matching:

`../samples/structured_findings.schema.json`

Every finding must contain:
- model ID/version,
- vessel/segment,
- finding type,
- confidence,
- priority,
- one or more evidence references,
- source cine ID and frame range.

The Windows app blocks versionless structured model output.

## Intended research stack

Typical research components can include:
- PyTorch / MONAI / OpenCV
- pydicom
- ONNX / ONNX Runtime
- model-specific vessel segmentation / lesion/QCA networks

Model selection and clinical validation are separate workstreams. Do not use research
output for patient care unless the model, intended use, deployment and validation have
been approved for that context.


## Optional research-demo detector — v1.2.0 branch

This branch adds an **opt-in research demonstration** using the public
`rachitgoyell/stenosis-detection` YOLOv8 checkpoint from Hugging Face.

The demo is intentionally constrained:

- It flags frame-level **research stenosis candidates** only.
- It does **not** assign LAD/LCx/RCA or a coronary segment.
- It does **not** estimate percent stenosis, QCA, FFR, physiology, or treatment.
- It does **not** mark coverage as adequate.
- Findings stay at `Review` priority and require cardiologist review.
- Evidence includes the source cine, source frame and a normalized candidate box.
- The Windows viewer can jump to the evidence frame and draw the candidate box.
- The default gateway remains fail-closed; the demo must be enabled explicitly.

### Setup on the Windows research workstation

1. Run `ai-research\setup_research_demo.bat` once while online.
2. Run `ai-research\run_demo_gateway.bat`.
3. In Cardio Guardian, import a de-identified coronary DICOM study.
4. Click **CONNECT LOCAL AI**.
5. Run **AI ON SELECTED CINE** or **AI WHOLE CASE**.
6. Select a model finding and click **SHOW EVIDENCE**.

The model weights are not committed to this repository. They are downloaded from
the model publisher at setup time. The local gateway fingerprints the downloaded
checkpoint with SHA-256 and exposes that fingerprint as the model version so an
output can be tied to the exact local weights used.

### Safety boundary

This is a physician-facing **research demo**, not a validated detector or medical
device. Model output may be wrong or incomplete and must not be used for patient
care, diagnosis, exclusion of disease, or treatment decisions. External clinical
validation, locked model/version control, cybersecurity review, QMS/regulatory
work and prospective validation remain separate release gates.
