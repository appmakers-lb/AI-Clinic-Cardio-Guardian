@echo off
setlocal
cd /d "%~dp0"
python -m pip install -r requirements-stenoz.txt
if errorlevel 1 exit /b %errorlevel%
python setup_stenoz_research_model.py
pause
