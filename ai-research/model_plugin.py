"""
Research model adapter boundary.

The default remains fail-closed. A public, unvalidated research demonstration
adapter can be enabled explicitly with CARDIO_GUARDIAN_ENABLE_RESEARCH_DEMO=1.
It must never be presented as a validated clinical detector.
"""

from __future__ import annotations

import os


class _NoModelPlugin:
    model_id = "NO_MODEL"
    model_version = "0"
    is_loaded = False
    load_error = None

    def analyze_series(self, request):
        raise RuntimeError("No research model is loaded.")


def _load_plugin():
    enabled = os.getenv("CARDIO_GUARDIAN_ENABLE_RESEARCH_DEMO", "").strip() == "1"
    if not enabled:
        return _NoModelPlugin()

    try:
        from demo_stenosis_plugin import ResearchStenosisDemoPlugin

        return ResearchStenosisDemoPlugin()
    except Exception as exc:
        plugin = _NoModelPlugin()
        plugin.load_error = str(exc)
        return plugin


MODEL_PLUGIN = _load_plugin()
