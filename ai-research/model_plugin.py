"""
Research model adapter boundary.

The default remains fail-closed. If the optional Stenoz research checkpoint has
been explicitly installed, Cardio Guardian loads it as a research-only XCA
stenosis-candidate adapter.
"""


class _NoModelPlugin:
    model_id = "NO_MODEL"
    model_version = "0"
    is_loaded = False
    load_error = (
        "No research model is installed. Run ai-research\\setup_stenoz_model.bat "
        "to install the optional research/demo checkpoint."
    )

    def analyze_series(self, request):
        raise RuntimeError(self.load_error)


try:
    from stenoz_research_plugin import StenozResearchPlugin

    _candidate = StenozResearchPlugin()
    MODEL_PLUGIN = _candidate if _candidate.is_loaded else _NoModelPlugin()
    if not _candidate.is_loaded:
        MODEL_PLUGIN.load_error = _candidate.load_error or MODEL_PLUGIN.load_error
except Exception as exc:
    MODEL_PLUGIN = _NoModelPlugin()
    MODEL_PLUGIN.load_error = f"Research model adapter could not initialize: {exc}"
