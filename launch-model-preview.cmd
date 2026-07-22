@echo off
cd /d "%~dp0"
python "Scripts\Preview\preview-card-model.py" %*
if errorlevel 1 pause
