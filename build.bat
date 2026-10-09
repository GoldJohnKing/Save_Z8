@echo off
setlocal enabledelayedexpansion
REM ============================================================
REM Save Z8 Desktop - Build & Publish Script (Release only)
REM Usage: build.bat
REM
REM Produces: out\SaveZ8.UI.exe  (single-file, self-contained,
REM           native DLLs embedded, ~46 MB, no .NET runtime needed)
REM Layout (normalized by Directory.Build.props):
REM   build\obj\<Project>            intermediates
REM   build\bin\<Project>\<Config>   build outputs (dev debugging)
REM   build\publish                  single-file publish staging
REM   out\                           FINAL distributable
REM Prereq: VS 2022/2026 + .NET 8 SDK + git (submodules)
REM ============================================================

set ROOT=%~dp0
set LIBS=%ROOT%libs
set EXT=%ROOT%external\libraw
set OUT=%ROOT%out
set PUB=%ROOT%build\publish

echo ============================================================
echo  Save Z8 Desktop - Release Build ^& Publish
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
echo [0/4] Found VS: !VCVARS!

REM --- Step 1: submodules ---
if exist "%EXT%\src\libraw_c_api.cpp" (
    echo [1/4] Submodules OK
) else (
    echo [1/4] Initializing git submodules...
    cd /d %ROOT%
    git submodule update --init --recursive
    if errorlevel 1 (echo ERROR: git submodule failed & exit /b 1)
)

REM --- Step 2: LibRaw DLL (skip if already built) ---
if exist "%LIBS%\libraw.dll" (
    echo [2/4] libraw.dll exists, skipping. Delete libs\libraw.dll to rebuild.
    goto :wrapper
)

echo [2/4] Building LibRaw 0.21.3...
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
echo [3/4] Building libraw_wrapper.dll...
call "!VCVARS!" x64 >nul 2>&1
if not exist %LIBS% mkdir %LIBS%

cl /nologo /LD /O2 /EHsc /W0 /I%EXT% %ROOT%native\libraw_wrapper.cpp /Fe:%LIBS%\libraw_wrapper.dll /link /LIBPATH:%LIBS% libraw.lib
if errorlevel 1 (echo ERROR: libraw_wrapper build failed & exit /b 1)
del /q %LIBS%\libraw_wrapper.obj %LIBS%\libraw_wrapper.exp %LIBS%\libraw_wrapper.lib 2>nul
echo       libraw_wrapper.dll built OK

REM --- Step 4: single-file publish (Release, self-contained) ---
echo [4/4] Publishing single-file exe...
cd /d %ROOT%
set Platform=
if exist "%PUB%" rmdir /s /q "%PUB%"
dotnet publish src\SaveZ8.UI\SaveZ8.UI.csproj -c Release -r win-x64 --self-contained true ^
  -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true ^
  -p:EnableCompressionInSingleFile=true -p:Platform="Any CPU" ^
  -p:DebugType=none -o "%PUB%"
if errorlevel 1 (echo ERROR: publish failed & exit /b 1)

REM --- Stage final distributable to out\ ---
if exist "%OUT%" rmdir /s /q "%OUT%"
mkdir "%OUT%"
copy /y "%PUB%\SaveZ8.UI.exe" "%OUT%\" >nul

echo.
echo ============================================================
echo  BUILD SUCCESSFUL
echo  Distributable: %OUT%\SaveZ8.UI.exe  (single file)
echo  Smoke test:    out\SaveZ8.UI.exe --selftest ^<file.nef^>
echo ============================================================
