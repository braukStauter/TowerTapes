@echo off
echo Building TowerTapes...
echo.
dotnet publish -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -o publish
echo.
if exist "publish\TowerTapes.exe" (
    echo Build succeeded!
    echo Output: publish\TowerTapes.exe
    echo.
    for %%A in ("publish\TowerTapes.exe") do echo Size: %%~zA bytes
) else (
    echo Build FAILED. Check errors above.
)
echo.
pause
