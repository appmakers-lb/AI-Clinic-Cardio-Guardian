\
@echo off
setlocal
cd /d "%~dp0"
python -m pip install -r requirements.txt
python research_gateway.py
pause
