# AI Clinic Cardio Guardian v1.1.4

## UI / DPI hardening

- Window now starts maximized and remains freely resizable/minimizable.
- Reduced minimum window size to support common laptop resolutions and Windows display scaling.
- Replaced one-line toolbar with a two-row toolbar so controls do not disappear off-screen.
- Rebuilt main layout with adjustable left/right panes and GridSplitters.
- Replaced the tall right sidebar with tabs: Guardian, Coverage, Copilot, Voice/System.
- Rebuilt viewer controls as a 2x3 grid so every button stays visible.
- Added PerMonitorV2 DPI awareness in app.manifest.
- Disabled horizontal scrolling/clipping in key lists and panels.
- Preserved all existing DICOM, case-memory, Guardian, voice, audit and local-AI event handlers.

## Clinical status

Research mode only. No raw-image diagnostic model is bundled.
