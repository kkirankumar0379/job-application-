@echo off
rem Starts the JobAgent backend (port 5000) and frontend (port 5173) in their own windows.
rem Keep both windows open while you use the app. Close a window to stop that server.
cd /d "%~dp0"
start "JobAgent backend" cmd /k "cd /d backend\JobAgent.Api && dotnet run"
start "JobAgent frontend" cmd /k "cd /d frontend && npm run dev"
timeout /t 12 /nobreak >nul
start "" http://localhost:5173
