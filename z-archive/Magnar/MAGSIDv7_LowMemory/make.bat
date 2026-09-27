@echo off
set ROOT=..\..\..
set SOURCE=%1
IF [%1]==[] set SOURCE="source.s"
echo "Compiling source file: %SOURCE%"
if not errorlevel 0 goto error

%ROOT%\bin\win_x86\c6510.exe -p %ROOT%\include\ -s program.prg $0801 + %SOURCE% -y program.sym
if not errorlevel 0 goto error
%ROOT%\bin\win_x86\x64 -model c64c -autostartprgmode 1 -moncommands program.sym "program.prg"

if not errorlevel 0 goto error

goto end
:error
pause
:end

REM %ROOT%\bin\win_amd64\exomizer sfx basic program.prg -o packed.prg -B
REM %ROOT%\bin\win_amd64\pucrunch.exe program.prg packed.prg
REM %ROOT%\bin\win_amd64\c1541 -format diskname,id d64 image.d64 -attach image.d64 -write packed.prg program
REM %ROOT%\bin\win_amd64\x64 -autostart "image.d64:program" -moncommands program.sym
