@echo off
setlocal
cd /d "%~dp0"
echo Installing Cardio Guardian research-demo dependencies...
python -m pip install -r requirements.txt
python -m pip install -r requirements-demo.txt
set CARDIO_GUARDIAN_ENABLE_RESEARCH_DEMO=1
echo Downloading and verifying the optional public research model...
python -c "from model_plugin import MODEL_PLUGIN; print('Loaded:', MODEL_PLUGIN.is_loaded); print('Model:', MODEL_PLUGIN.model_id, MODEL_PLUGIN.model_version); print('Error:', getattr(MODEL_PLUGIN, 'load_error', None)); raise SystemExit(0 if MODEL_PLUGIN.is_loaded else 1)"
if errorlevel 1 (
  echo.
  echo Research demo setup FAILED. Review the error above.
  pause
  exit /b 1
)
echo.
echo Research demo setup completed.
pause
