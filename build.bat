@echo off
chcp 65001 > nul
echo ============================================================
echo  Сборка плагина: Картограмма земляных работ (Civil 3D)
echo ============================================================
echo.

:: Проверить наличие dotnet
where dotnet >nul 2>&1
if %errorlevel% neq 0 (
    echo ОШИБКА: .NET SDK не найден.
    echo Скачайте и установите .NET SDK с https://dotnet.microsoft.com/download
    pause
    exit /b 1
)

dotnet --version
echo.

:: Сборка в Release
dotnet build KartogrammaPlugin.csproj -c Release

if %errorlevel% equ 0 (
    echo.
    echo ============================================================
    echo  Сборка успешна!
    echo  DLL находится в: bin\Release\net48\KartogrammaPlugin.dll
    echo.
    echo  Установка в Civil 3D:
    echo    1. Откройте AutoCAD Civil 3D
    echo    2. Введите команду: NETLOAD
    echo    3. Выберите файл KartogrammaPlugin.dll
    echo    4. Введите команду: КАРТОГРАММА
    echo ============================================================
) else (
    echo.
    echo ОШИБКА СБОРКИ!
    echo Проверьте пути к Civil 3D DLL в файле KartogrammaPlugin.csproj
    echo и измените переменную AcadPath на путь вашей установки.
)

pause
