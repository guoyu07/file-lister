@ECHO off
SETLOCAL

:: for more verbose output (eg, for testing), remove "REM CHATTY " from beginning of all lines and comment out lines that end with "REM CHATTY". note that blank lines of output from file-lister.exe call will be filtered. fixing that was a low priority. may do so later.

REM ECHO Current working directory is "%cd%"

:: force the current working directory to be where this .bat file resides, so we can use relative references to file-lister.exe and config.json. This is necessary to run snap.bat ad hoc from a shortcut since snap.bat requires admin rights because of registry listing (see more info below). This is NOT necessary to run snap.bat (via scheduledSnap.vbs) from the Task Scheduler (assuming we specify "Start in" accordingly on the task action).
:: more info: apparently setting the "Start in" value of a shortcut does not set the current working directory to this value when launching the shortcut IF THE SHORTCUT IS SET TO "Run as administrator". Instead, the working directory is always "C:\Windows\System32".
:: see: Command prompt ignores "start in" location when elevated - Microsoft Community:  http://answers.microsoft.com/en-us/windows/forum/windows_8-performance/command-prompt-ignores-start-in-location-when/b6c1c59c-b9a9-48bf-be9c-867e5b796f42?auth=1

cd %~dp0

REM ECHO Changed current working directory to "%cd%"


SET lbl=Manual

IF "%~1"=="" (
	SET /p lbl="Label: "
) ELSE (
	SET lbl=%1
)

REM Normalize outer quotes
SET lbl=###%lbl%###
SET lbl=%lbl:"###=%
SET lbl=%lbl:###"=%
SET lbl=%lbl:###=%
SET lbl="%lbl%"

ECHO.

REM CHATTY SET outputPath=

SETLOCAL EnableDelayedExpansion

ECHO %time% Listing files...

REM CHATTY ECHO.

FOR /F "delims=" %%G in ('file-lister.exe config.json %lbl%') DO (
	REM CHATTY IF "!outputPath!"==""  (
		SET outputPath=%%G
	    GOTO done   :: only evaluate loop body once. REM CHATTY
	REM CHATTY )
	REM CHATTY ECHO %%G
)
:done

REM CHATTY ECHO.

IF "%outputPath:~0,15%"=="Output target: " (
	REM Remove "Output target: " at beginning of line
	SET "outputPath=!outputPath:Output target: =!"

	REM Remove outer quotes
	SET outputPath=###!outputPath!###
	SET outputPath=!outputPath:"###=!
	SET outputPath=!outputPath:###"=!
	SET outputPath=!outputPath:###=!

	REM ECHO !outputPath!

	ECHO !time! Exporting registry...

	regedit /e "!outputPath!\registry.txt"

	ECHO !time! DONE
)
