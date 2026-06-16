@echo off
cd /d "%~dp0OpportunityTracker"
dotnet publish -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -o "..\..\publish"
copy "..\..\publish\OpportunityTracker.exe" "..\..\"
rmdir /s /q "..\..\publish"
echo Build complete: OpportunityTracker.exe
