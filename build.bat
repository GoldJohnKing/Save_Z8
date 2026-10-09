@echo off
setlocal enabledelayedexpansion
REM ============================================================
REM Save Z8 Desktop - One-Click Build Script
REM Usage: build.bat [Release|Debug]   (default: Release)
REM
REM Layout (normalized by Directory.Build.props):
REM   build\obj\<Project>           intermediates
REM   build\bin\<Config>\<Project>  build outputs
REM   out\                          staged distributable (exe + DLLs)
REM   libs\                         native artifacts (libraw, wrapper)
REM Prereq: VS 2022/2026 + .NET 8 SDK + git (submodules)
REM ============================================================

set CONFIG=%1
if "%CONFIG%"=="" set CONFIG=Release
set ROOT=%~dp0
set LIBS=%ROOT%libs
set EXT=%ROOT%external\libraw
set OUT=%ROOT%out

echo ============================================================
echo  Save Z8 Desktop Build - %CONFIG%
echo ============================================================

REM --- Step 0: prerequisites ---
where dotnet >nul 2>&1 || (echo ERROR: dotnet not found. Install .NET 8 SDK. & exit /b 1)

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
echo [0/5] Found VS: !VCVARS!

REM --- Step 1: submodules ---
if exist "%EXT%\src\libraw_c_api.cpp" (
    echo [1/5] Submodules OK
) else (
    echo [1/5] Initializing git submodules...
    cd /d %ROOT%
    git submodule update --init --recursive
    if errorlevel 1 (echo ERROR: git submodule failed & exit /b 1)
)

REM --- Step 2: LibRaw DLL (skip if already built) ---
if exist "%LIBS%\libraw.dll" (
    echo [2/5] libraw.dll exists, skipping. Delete libs\libraw.dll to rebuild.
    goto :wrapper
)

echo [2/5] Building LibRaw 0.21.3...
call "!VCVARS!" x64 >nul 2>&1
cd /d %EXT%
if not exist bin mkdir bin
if not exist lib mkdir lib
if not exist object mkdir object

nmake /f Makefile.msvc bin\libraw.dll /NOLOGO
if errorlevel 1 (echo ERROR: LibRaw build failed & exit /b 1)

if not exist %LIBS% mkdir %LIBS%
copy /y bin\libraw.dll %LIBS%\ >nul
copy /y lib\libraw.lib %LIBS%\ >nul
echo       libraw.dll built OK

:wrapper
REM --- Step 3: wrapper DLL (always rebuilt - small and fast) ---
echo [3/5] Building libraw_wrapper.dll...
call "!VCVARS!" x64 >nul 2>&1
if not exist %LIBS% mkdir %LIBS%

cl /nologo /LD /O2 /EHsc /W0 /I%EXT% %ROOT%native\libraw_wrapper.cpp /Fe:%LIBS%\libraw_wrapper.dll /link /LIBPATH:%LIBS% libraw.lib
if errorlevel 1 (echo ERROR: libraw_wrapper build failed & exit /b 1)
del /q %LIBS%\libraw_wrapper.obj %LIBS%\libraw_wrapper.exp %LIBS%\libraw_wrapper.lib 2>nul
echo       libraw_wrapper.dll built OK

REM --- Step 4: .NET solution ---
echo [4/5] Building .NET solution (%CONFIG%)...
cd /d %ROOT%
set Platform=
dotnet build SaveZ8.sln -c %CONFIG% -p:Platform="Any CPU"
if errorlevel 1 (echo ERROR: .NET build failed & exit /b 1)

REM --- Step 5: stage distributable to out\ ---
echo [5/5] Staging distributable to out\...
set BINDIR=%ROOT%build\bin\SaveZ8.UI\%CONFIG%\net8.0-windows
if not exist "%BINDIR%\SaveZ8.UI.exe" (
    echo ERROR: build output not found under %BINDIR%
    exit /b 1
)
if exist "%OUT%" rmdir /s /q "%OUT%"
mkdir "%OUT%"
copy /y "%BINDIR%\SaveZ8.UI.exe" "%OUT%\" >nul
copy /y "%BINDIR%\SaveZ8.UI.dll" "%OUT%\" >nul
copy /y "%BINDIR%\SaveZ8.UI.runtimeconfig.json" "%OUT%\" >nul
copy /y "%BINDIR%\SaveZ8.UI.deps.json" "%OUT%\" >nul
copy /y "%BINDIR%\SaveZ8.Core.dll" "%OUT%\" >nul
for %%f in ("%BINDIR%\CommunityToolkit.*.dll" "%BINDIR%\Microsoft.*.dll") do copy /y "%%f" "%OUT%\" >nul
copy /y "%LIBS%\libraw.dll" "%OUT%\" >nul
copy /y "%LIBS%\libraw_wrapper.dll" "%OUT%\" >nul

echo.
echo ============================================================
echo  BUILD SUCCESSFUL
echo  Distributable: %OUT%\SaveZ8.UI.exe  (+ DLLs)
echo ============================================================
