@echo off
rem Builds out\armorpaint.unitypackage using Unity in batch mode.
rem Override the Unity editor with: set UNITY_EXE=C:\path\to\Unity.exe
setlocal

set "ROOT=%~dp0"
set "ROOT=%ROOT:~0,-1%"
set "OUT_DIR=%ROOT%\out"
set "PACKAGE=%OUT_DIR%\armorpaint.unitypackage"
set "TEMP_PROJECT=%TEMP%\armorpaint_unity_build"
set "LOG=%OUT_DIR%\build.log"

rem Find Unity: use UNITY_EXE if set, otherwise the newest editor installed via Unity Hub
if not defined UNITY_EXE (
	set "HUB_EDITORS=%ProgramFiles%\Unity\Hub\Editor"
	for /f "delims=" %%v in ('dir /b /ad /o:n "%ProgramFiles%\Unity\Hub\Editor" 2^>nul') do (
		if exist "%ProgramFiles%\Unity\Hub\Editor\%%v\Editor\Unity.exe" set "UNITY_EXE=%ProgramFiles%\Unity\Hub\Editor\%%v\Editor\Unity.exe"
	)
)
if not defined UNITY_EXE (
	echo Unity not found. Set UNITY_EXE to the path of Unity.exe.
	exit /b 1
)
echo Using %UNITY_EXE%

rem Prepare a temporary Unity project containing the plugin under Assets\ArmorPaint
if exist "%TEMP_PROJECT%" rmdir /s /q "%TEMP_PROJECT%"
mkdir "%TEMP_PROJECT%\Assets\ArmorPaint" || exit /b 1
xcopy /e /i /q /y "%ROOT%\Editor" "%TEMP_PROJECT%\Assets\ArmorPaint\Editor" >nul || exit /b 1
for %%f in (Editor.meta LICENSE.md LICENSE.md.meta README.md README.md.meta) do (
	copy /y "%ROOT%\%%f" "%TEMP_PROJECT%\Assets\ArmorPaint\" >nul || exit /b 1
)

if not exist "%OUT_DIR%" mkdir "%OUT_DIR%"
if exist "%PACKAGE%" del /q "%PACKAGE%"

echo Exporting package...
"%UNITY_EXE%" -batchmode -nographics -quit -projectPath "%TEMP_PROJECT%" -exportPackage Assets/ArmorPaint "%PACKAGE%" -logFile "%LOG%"
set "RESULT=%ERRORLEVEL%"

rmdir /s /q "%TEMP_PROJECT%"

if not "%RESULT%"=="0" (
	echo Unity failed with exit code %RESULT%, see %LOG%
	exit /b %RESULT%
)
if not exist "%PACKAGE%" (
	echo Package was not created, see %LOG%
	exit /b 1
)
del /q "%LOG%"
echo Created %PACKAGE%
endlocal
