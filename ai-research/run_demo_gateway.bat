@echo off
setlocal
cd /d "%~dp0"
set CARDIO_GUARDIAN_ENABLE_RESEARCH_DEMO=1
echo Starting RESEARCH-ONLY AI demo gateway on localhost:8765
echo Outputs are not validated for clinical decisions.
python research_gateway.py
pause
