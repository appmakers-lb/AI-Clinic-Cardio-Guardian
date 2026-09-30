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
