# AI Clinic Cardio Guardian v1.1.6

## v1.1.6 highlights

- Whole-case finding memory now suppresses duplicate IDs and duplicate evidence-linked findings.
- Evidence references must point to cine runs already loaded in the current case.
- Structured model output can now carry evidence-bound coronary coverage states.
- Coverage Guardian merges conflicting coverage conservatively so Partial/Incomplete cannot be overwritten by a later Adequate claim.
- Whole-case AI analysis is cancellable from the toolbar.
- Guardian voice alerts now use duplicate suppression and write explicit issued/suppressed audit events.
- Voice commands now use a constrained intent parser and support cine play/pause/stop in addition to navigation and evidence review.
- Added vendor-neutral research interfaces for vessel vision, calibrated QCA, physiology, IVUS and OCT.
- Added GitHub CI for Windows Release build, core safety tests, and Python gateway syntax checks.

## v1.1.5 baseline retained

- Responsive/maximized WPF layout rebuilt for laptop screens and Windows DPI scaling.
- Two-row toolbar so primary buttons no longer disappear off-screen.
- Resizable left/right panes with GridSplitters.
- Right-side Guardian/Coverage/Copilot/Voice workspace moved into tabs.
- Cardiac CD/DICOM import automatically loads the first likely coronary cine.
- Added **RUN AI WHOLE CASE** for connected research models, so every imported cine can be analyzed into one persistent case state.
- Retains the compiler fixes from v1.1.1–v1.1.3.

Windows research workstation for coronary angiography / Cath-Lab AI development.

> **RESEARCH MODE — NOT FOR CLINICAL DECISIONS**

## What v1.1.6 actually does

### Cardiac CD / USB / DICOM
- Imports an entire cardiac CD, USB, or copied DICOM folder.
- Scans DICOM image objects locally.
- Groups files by StudyInstanceUID + SeriesInstanceUID.
- Prioritizes likely coronary angiography/XA series.
- Displays patient/study metadata locally.
- Replays multi-frame DICOM cine frame-by-frame.
- Supports common compressed DICOM transfer syntaxes through fo-dicom codecs.
- Keeps all imported cine runs in one whole-case memory.

### Whole-case Guardian workflow
- LM / LAD / LCx / RCA coverage map.
- Adequate / Partial / Incomplete / Unassessed states.
- Persistent case memory across cine runs.
- Evidence references point back to source cine + frames + projection.
- "SHOW WHY" navigates to the referenced DICOM cine/frame when possible.
- Physician can confirm or dismiss a research finding.
- Manual research annotations are clearly marked MANUAL, never AI.

### AI integration
There is deliberately **no fake medical detector** in the repository.

The app supports two safe research integration paths:

1. **Import versioned structured findings JSON**
   - schema: `samples/structured_findings.schema.json`
   - example: `samples/structured_findings_EXAMPLE_ONLY.json`

2. **Local research AI gateway**
   - localhost only: `http://127.0.0.1:8765`
   - run: `ai-research\run_gateway.bat`
   - the default plugin refuses analysis until a real research model adapter is installed.

Every structured model finding must include:
- model ID + version,
- vessel + segment,
- finding type,
- confidence,
- priority,
- evidence source cine,
- frame range,
- explanation/evidence description.

### Voice
- Offline Windows speech synthesis.
- Push-to-listen constrained voice commands where a Windows speech recognizer is installed.
- Voice can read high-priority **structured research-model alerts**.
- Voice never creates a clinical finding itself.

Example commands:
- `show evidence`
- `what is incomplete`
- `anything else`
- `next cine`
- `previous cine`
- `mute voice`

## What v1.1.6 does NOT do

It does not yet contain a clinically validated model that can independently identify a blocked coronary artery from raw angiography.

That capability requires:
- a specialized medical computer-vision model,
- suitable licensed/de-identified training data,
- cardiologist labels,
- retrospective testing,
- external validation,
- shadow-mode testing,
- regulatory/hospital approval for the intended use.

The program intentionally refuses to invent stenosis/occlusion findings when no versioned model output is present.

## Open in Visual Studio 2022

1. Extract the ZIP.
2. Open `AIClinic.CardioGuardian.sln`.
3. Ensure Visual Studio 2022 has **.NET desktop development** installed.
4. Allow NuGet restore.
5. Set `AIClinic.CardioGuardian.Desktop` as Startup Project.
6. Press **F5**.

Required NuGet packages restore automatically:
- `fo-dicom 5.2.6`
- `fo-dicom.Imaging.Desktop 5.2.6`
- `fo-dicom.Codecs 5.16.7`
- `System.Speech 10.0.12`

## Use with a cardiac CD / USB

1. Insert the cardiac CD or copy it to a USB/folder.
2. Start the app.
3. Press **IMPORT CARDIAC CD / USB / DICOM**.
4. Select the CD/USB root folder.
5. Wait for the scan.
6. Choose a likely `XA` / coronary series on the left.
7. Press **LOAD SELECTED CINE**.
8. Use Play / Frame buttons to review.
9. The right panel tracks:
   - findings,
   - coronary coverage,
   - Cardio Copilot,
   - voice.

The import stays local. The application does not upload the DICOM study to a cloud service.

## Local AI research gateway

Run:

`ai-research\run_gateway.bat`

Then press **CONNECT LOCAL AI**.

The default gateway will report:
`NO MODEL`

That is intentional. Replace `ai-research\model_plugin.py` only with a real,
versioned research model adapter.

## Architecture

- `AIClinic.CardioGuardian.Core` — case state, coronary map, findings, Guardian policy.
- `AIClinic.CardioGuardian.Desktop` — WPF workstation, DICOM/cine viewer, voice, audit.
- `ai-research` — isolated Python model research/gateway.
- `agents` — Master Agent + specialist engineering prompts.
- `samples` — structured finding contract/schema.
- `tests` — core safety/contract tests.

## Master safety principle

**THE AI WATCHES, REMEMBERS, HIGHLIGHTS, WARNS, EXPLAINS AND ANSWERS.  
THE CARDIOLOGIST DECIDES.**


## v1.2.0 research AI demo

v1.2.0 adds an optional **research-only X-ray coronary angiography stenosis-candidate model** for physician demonstrations and engineering evaluation.

What it does:
- runs locally through the existing localhost research gateway;
- samples frames from an imported DICOM cine;
- uses an ARCADE-trained U-Net research checkpoint to flag image-level stenosis candidates;
- links every candidate to the source cine and frame for **SHOW WHY** / evidence review;
- can speak high-priority research candidate alerts through the existing Windows voice layer;
- never reports a negative result as "normal", "no stenosis", or clinical clearance.

One-time local setup on a workstation:

```bat
ai-research\setup_stenoz_model.bat
```

Then start the gateway:

```bat
ai-research\run_gateway.bat
```

Open Cardio Guardian, import the DICOM study, connect the local AI, select a cine, and run **RUN RESEARCH AI ON SELECTED CINE**.

Important limitations:
- this is a research/demo checkpoint, not a clinically validated or certified medical device;
- the upstream model has known false positives;
- it does not identify the vessel/segment, stenosis percentage, lesion severity, or treatment;
- it is restricted to X-ray angiography research input;
- physician review of the original cine remains mandatory.


## v1.2.1 vessel-supported narrowing research gate

v1.2.1 tightens the demo so the direct stenosis heatmap is **not** shown by itself. A research finding is surfaced only when:
- the candidate persists across at least two sampled cine frames;
- a separate vessel-segmentation model supports the candidate region;
- conservative local vessel geometry can produce a bounded apparent diameter-reduction estimate.

When those gates pass, the UI can show an approximate apparent narrowing percentage with a deliberately wide uncertainty range. This remains an uncalibrated 2D research estimate, **not clinical QCA**. The current build still does not identify LAD/LCx/RCA automatically and does not infer complete occlusion (100%); those require separate validated workstreams.
