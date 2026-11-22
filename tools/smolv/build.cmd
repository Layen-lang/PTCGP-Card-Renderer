@echo off
rem Rebuilds smolv_decode.exe with Visual Studio 2022 (x64), from this folder.
setlocal
cd /d "%~dp0"
call "C:\Program Files\Microsoft Visual Studio\2022\Community\Common7\Tools\VsDevCmd.bat" -arch=x64 -host_arch=x64
if errorlevel 1 exit /b %errorlevel%
cl /nologo /EHsc /O2 smolv_decode.cpp smolv.cpp /Fesmolv_decode.exe
del /q *.obj 2>nul
exit /b %errorlevel%
