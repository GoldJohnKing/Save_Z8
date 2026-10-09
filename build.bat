@echo off
setlocal enabledelayedexpansion
REM ============================================================
REM Save Z8 Desktop - One-Click Build Script
REM Usage: build.bat [Release^|Debug] (default: Release)
REM Prereq: Visual Studio 2022/2026 + .NET 8 SDK + git (with submodules)
REM ============================================================

set CONFIG=%1
if "%CONFIG%"=="" set CONFIG=Release
set ROOT=%~dp0
set LIBS=%ROOT%libs
set EXT=%ROOT%external\libraw

echo ============================================================
echo  Save Z8 Desktop Build - %CONFIG%
echo ============================================================

REM --- Step 0: Check prerequisites ---
where dotnet >nul 2>&1 || (echo ERROR: dotnet not found. Install .NET 8 SDK. & exit /b 1)

REM Find vcvarsall.bat
set VCVARS=
for %%y in (18 17) do (
    for %%e in (Enterprise Community Professional BuildTools) do (
        if exist "C:\Program Files\Microsoft Visual Studio\%%y\%%e\VC\Auxiliary\Build\vcvarsall.bat" (
            if "!VCVARS!"=="" set VCVARS=C:\Program Files\Microsoft Visual Studio\%%y\%%e\VC\Auxiliary\Build\vcvarsall.bat
        )
    )
)
if "!VCVARS!"=="" (
    echo ERROR: Visual Studio vcvarsall.bat not found.
    exit /b 1
)
echo [0/4] Found VS: !VCVARS!

REM --- Step 1: Init submodules if needed ---
if not exist "%EXT%\src\libraw_c_api.cpp" (
    echo [1/4] Initializing git submodules...
    cd /d %ROOT%
    git submodule update --init --recursive
    if errorlevel 1 (echo ERROR: git submodule failed & exit /b 1)
) else (
    echo [1/4] Submodules OK
)

REM --- Step 2: Build LibRaw DLL ---
if exist "%LIBS%\libraw.dll" (
    echo [2/4] libraw.dll exists, skipping. Delete libs\libraw.dll to rebuild.
    goto :build_wrapper
)

echo [2/4] Building LibRaw 0.21.3...
call "!VCVARS!" x64 >nul 2>&1
cd /d %EXT%

if not exist bin mkdir bin
if not exist lib mkdir lib
if not exist object mkdir object

nmake /f Makefile.msvc bin\libraw.dll /NOLOGO
if errorlevel 1 (
    echo ERROR: LibRaw build failed
    exit /b 1
)

if not exist %LIBS% mkdir %LIBS%
copy /y bin\libraw.dll %LIBS%\ >nul
copy /y lib\libraw.lib %LIBS%\ >nul
echo       libraw.dll built OK

:build_wrapper
REM --- Step 3: Build libraw_wrapper.dll ---
if exist "%LIBS%\libraw_wrapper.dll" (
    echo [3/4] libraw_wrapper.dll exists, skipping.
    goto :build_dotnet
)

echo [3/4] Building libraw_wrapper.dll...
call "!VCVARS!" x64 >nul 2>&1

cl /LD /O2 /EHsc /W0 /I%EXT% %ROOT%native\libraw_wrapper.cpp /Fe:%LIBS%\libraw_wrapper.dll /link /LIBPATH:%LIBS% libraw.lib
if errorlevel 1 (
    echo ERROR: libraw_wrapper build failed
    exit /b 1
)
del /q %LIBS%\libraw_wrapper.obj %LIBS%\libraw_wrapper.exp %LIBS%\libraw_wrapper.lib 2>nul
echo       libraw_wrapper.dll built OK

:build_dotnet
REM --- Step 4: Build .NET solution ---
echo [4/4] Building .NET solution (%CONFIG%)...
cd /d %ROOT%
set Platform=
dotnet build SaveZ8.sln -c %CONFIG% -p:Platform="Any CPU"
if errorlevel 1 (
    echo ERROR: .NET build failed
    exit /b 1
)

REM --- Copy DLLs to output ---
set OUTDIR=%ROOT%src\SaveZ8.UI\bin\%CONFIG%\net8.0-windows
if not exist "%OUTDIR%" mkdir "%OUTDIR%"
if exist "%LIBS%\libraw.dll" copy /y "%LIBS%\libraw.dll" "%OUTDIR%\" >nul
if exist "%LIBS%\libraw_wrapper.dll" copy /y "%LIBS%\libraw_wrapper.dll" "%OUTDIR%\" >nul

echo.
echo ============================================================
echo  BUILD SUCCESSFUL
echo  Executable:   %OUTDIR%\SaveZ8.UI.exe
echo  LibRaw DLL:   %OUTDIR%\libraw.dll
echo  Wrapper DLL:  %OUTDIR%\libraw_wrapper.dll
echo ============================================================
