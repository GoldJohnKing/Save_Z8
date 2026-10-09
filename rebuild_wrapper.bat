@echo off
call "C:\Program Files\Microsoft Visual Studio\18\Enterprise\VC\Auxiliary\Build\vcvarsall.bat" x64 >nul 2>&1
set ROOT=D:\GitRepos\Save_Z8\
set EXT=%ROOT%external\libraw\
set LIBS=%ROOT%libs\
cl /nologo /LD /O2 /EHsc /W0 /I%EXT% %ROOT%native\libraw_wrapper.cpp /Fe:%LIBS%\libraw_wrapper_new.dll /link /LIBPATH:%LIBS% libraw.lib
if errorlevel 1 (echo BUILD FAILED & exit /b 1)
move /y %LIBS%\libraw_wrapper_new.dll %LIBS%\libraw_wrapper.dll >nul
del /q %LIBS%\libraw_wrapper.obj %LIBS%\libraw_wrapper.exp %LIBS%\libraw_wrapper.lib 2>nul
echo WRAPPER REBUILT
