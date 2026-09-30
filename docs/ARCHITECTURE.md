# Architecture

## Runtime boundaries

Primary Cath-Lab imaging system
  -> independent validated export/feed
     -> AI Clinic Cardio Guardian Windows workstation
        -> local UI / case state / audit
        -> versioned inference adapter
           -> local ONNX/TensorRT/native/Python service depending validated deployment

Failure of Guardian must never stop primary imaging.

## Structured inference contract
Medical model output must include:
- model name/version
- source/cine identifier
- frame references
- image-quality status
- coverage state
- structured findings
- confidence
- evidence references

The Copilot can explain these outputs, but cannot create them.
