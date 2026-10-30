@echo off
rem compile via response file (ASCII only, avoids cmd codepage issues)
rem works on any Windows 10/11 with .NET Framework 4.x, no VS or SDK needed
cd /d %~dp0
set CSC=%WINDIR%\Microsoft.NET\Framework64\v4.0.30319\csc.exe
if not exist "%CSC%" set CSC=%WINDIR%\Microsoft.NET\Framework\v4.0.30319\csc.exe
if not exist "%CSC%" (
  echo .NET Framework 4.x csc.exe not found
  exit /b 1
)
"%CSC%" @compile.rsp
