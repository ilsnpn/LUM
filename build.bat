@echo off
REM ============================================================
REM  LUM - recompilation sans rien installer
REM  Utilise le compilateur C# fourni avec Windows (.NET Framework 4.x)
REM ============================================================
setlocal
set CSC=%WINDIR%\Microsoft.NET\Framework64\v4.0.30319\csc.exe
if not exist "%CSC%" set CSC=%WINDIR%\Microsoft.NET\Framework\v4.0.30319\csc.exe
if not exist "%CSC%" (
  echo csc.exe introuvable : .NET Framework 4.x absent ?
  exit /b 1
)
"%CSC%" /nologo /target:winexe /optimize+ /platform:anycpu /out:"%~dp0LUM.exe" ^
  /win32icon:"%~dp0src\LUM.ico" ^
  /r:System.Windows.Forms.dll /r:System.Drawing.dll ^
  "%~dp0src\*.cs"
if errorlevel 1 ( echo. & echo ECHEC de la compilation & exit /b 1 )
echo. & echo OK : LUM.exe genere.
REM Le site propose l'exe en telechargement : on garde sa copie a jour
if exist "%~dp0site\download" (
  copy /y "%~dp0LUM.exe" "%~dp0site\download\LUM.exe" >nul && echo Copie dans site\download\ pour le site.
)
