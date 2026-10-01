"""
Research model adapter boundary.

Replace MODEL_PLUGIN only with a real, versioned research adapter. The default
implementation deliberately refuses analysis so the workstation cannot present
synthetic/random output as a medical finding.
"""


class _NoModelPlugin:
    model_id = "NO_MODEL"
    model_version = "0"
    is_loaded = False

    def analyze_series(self, request):
        raise RuntimeError("No research model is loaded.")


MODEL_PLUGIN = _NoModelPlugin()
