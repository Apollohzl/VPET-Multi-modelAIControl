@echo off
echo ========================================
echo   BigWorld Mod Deploy
echo ========================================

set "SRC=F:\SteamLibrary\steamapps\workshop\content\1920960\PetChat"
set "DST=F:\SteamLibrary\steamapps\common\VPet\mod\PetChat"

echo.
echo [1/3] Deleting old PetChat directory...
if exist "%DST%" (
    rmdir /s /q "%DST%"
    echo     Deleted: %DST%
) else (
    echo     Not exist, skip
)

echo.
echo [2/3] Copying PetChat to mod directory...
xcopy "%SRC%" "%DST%\" /e /i /q /y

echo.
echo [3/3] Deploy done!
echo   Src: %SRC%
echo   Dst: %DST%
echo.
