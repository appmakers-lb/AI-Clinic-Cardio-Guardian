\
"""
Replace this file with a real, versioned research model adapter.

The default plugin deliberately refuses to analyze images. This prevents the
application from presenting synthetic/random output as a medical finding.
"""


class _NoModelPlugin:
    model_id = "NO_MODEL"
    model_version = "0"
    is_loaded = False

    def analyze_series(self, request):
        raise RuntimeError("No research model is loaded.")


MODEL_PLUGIN = _NoModelPlugin()
