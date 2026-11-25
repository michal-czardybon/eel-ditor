@echo off
setlocal
if not "%~1"=="" (
    call "%~1" >nul
    if errorlevel 1 exit /b 1
)
cl.exe /nologo /LD /MT /O2 /EHsc /W4 /DUNICODE /D_UNICODE ^
    /Fo"publish\win-x64\GitContextMenu.obj" "%~dp0GitContextMenu.cpp" ^
    /link /MACHINE:X64 /OUT:"publish\win-x64\EelDitorGitMenu.dll" ^
    /IMPLIB:"publish\win-x64\EelDitorGitMenu.lib" /DEF:"%~dp0GitContextMenu.def" ole32.lib
exit /b %errorlevel%
