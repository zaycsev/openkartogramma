# ============================================================
#  Первоначальная установка бандла КартограммаПлагин
#  Запустите ОДИН РАЗ от имени обычного пользователя.
#  После этого Civil 3D будет загружать плагин автоматически.
# ============================================================

$ErrorActionPreference = "Stop"
$bundleRoot = "$env:APPDATA\Autodesk\ApplicationPlugins\KartogrammaPlugin.bundle"

Write-Host "`n=== Установка бандла КартограммаПлагин ===" -ForegroundColor Cyan

# 1. Создать структуру папок
$dirs = @(
    "$bundleRoot\Contents\net8",
    "$bundleRoot\Contents\net48"
)
foreach ($d in $dirs) {
    if (-not (Test-Path $d)) {
        New-Item -ItemType Directory -Path $d -Force | Out-Null
        Write-Host "  Создана папка: $d" -ForegroundColor Green
    } else {
        Write-Host "  Уже существует: $d" -ForegroundColor Yellow
    }
}

# 2. Скопировать PackageContents.xml
$src = Join-Path $PSScriptRoot "bundle\PackageContents.xml"
$dst = "$bundleRoot\PackageContents.xml"
Copy-Item -Path $src -Destination $dst -Force
Write-Host "  Скопирован: PackageContents.xml" -ForegroundColor Green

# 3. Первичная сборка проекта
Write-Host "`n  Запуск сборки (dotnet build)..." -ForegroundColor Cyan
Push-Location $PSScriptRoot
dotnet build KartogrammaPlugin.csproj -c Debug
$buildOk = $LASTEXITCODE -eq 0
Pop-Location

if ($buildOk) {
    Write-Host "`n=== Готово! ===" -ForegroundColor Green
    Write-Host "Бандл установлен в: $bundleRoot" -ForegroundColor White
    Write-Host ""
    Write-Host "Следующие шаги:" -ForegroundColor Cyan
    Write-Host "  1. Запустите AutoCAD Civil 3D"
    Write-Host "  2. Введите команду: OpenKartogramma"
    Write-Host "  3. Для отладки из VS Code: запустите Civil 3D,`n     затем F5 в VS Code → 'Attach to AutoCAD'"
} else {
    Write-Host "`n=== ОШИБКА СБОРКИ ===" -ForegroundColor Red
    Write-Host "Проверьте пути в KartogrammaPlugin.csproj" -ForegroundColor Yellow
}
