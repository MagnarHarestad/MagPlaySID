@echo off
set SOURCE=%1
IF [%1]==[] set SOURCE="image.d64"
echo "Running U64"
if not errorlevel 0 goto error

u64-windows-amd64.exe run image -t local -k="test" -m 192.168.3.160 -p %SOURCE%