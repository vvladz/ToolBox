@echo off
setlocal
pushd "%~dp0"
if errorlevel 1 exit /b 1
dotnet run --file "%~dp0run.cs" -- %*
set "toolbox_exit=%errorlevel%"
popd
exit /b %toolbox_exit%
