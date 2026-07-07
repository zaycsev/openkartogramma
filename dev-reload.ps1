# ============================================================
#  dev-reload.ps1 - quick dev cycle
#
#  Steps:
#    1. Build project (net8 only, fast)
#    2. Close Civil 3D if running
#    3. Open Civil 3D with test drawing
#
#  From VS Code: Ctrl+Shift+B -> "dev: rebuild & restart Civil 3D"
#  Manually: .\dev-reload.ps1
#  With drawing: .\dev-reload.ps1 -Dwg "C:\test\surfaces.dwg"
# ============================================================

param(
    [string]$Dwg = "",       # path to test .dwg (leave empty for blank drawing)
    [string]$Civil3D = ""    # path to acad.exe if non-standard
)

$ErrorActionPreference = "Stop"
$root = $PSScriptRoot

function Step  { Write-Host "`n>> $args" -ForegroundColor Cyan  }
function Ok    { Write-Host "  OK $args" -ForegroundColor Green }
function Info  { Write-Host "  .. $args" -ForegroundColor Gray  }

# -- 1. Find acad.exe ---------------------------------------------------------
if (-not $Civil3D) {
    # ── Универсальный поиск Civil 3D на любом ПК ────────────────────────────
    # Стратегия (от лучшего к худшему):
    #   1) Реестр Autodesk → точный путь к acad.exe для Civil 3D любой версии
    #   2) Сканирование стандартных папок установки (Program Files и др.)
    #   3) Все диски → C:\Program Files\Autodesk и аналоги
    # Приоритет внутри найденного: сначала версии с Civil 3D (C3D\AeccDbMgd.dll),
    # затем — обычный AutoCAD как fallback.

    $found = New-Object System.Collections.Generic.List[object]

    function Add-Candidate($exePath) {
        if (-not $exePath) { return }
        if (-not (Test-Path $exePath)) { return }
        $dir = Split-Path $exePath -Parent
        $hasC3D = (Test-Path (Join-Path $dir 'C3D\AeccDbMgd.dll')) -or `
                  (Test-Path (Join-Path $dir 'AeccDbMgd.dll'))
        # Год из имени папки ("AutoCAD 2024" → 2024); 0, если не распознан
        $year = 0
        if ($dir -match 'AutoCAD\s+(\d{4})') { $year = [int]$Matches[1] }
        $found.Add([pscustomobject]@{
            Exe = $exePath; Dir = $dir; HasC3D = $hasC3D; Year = $year
        }) | Out-Null
    }

    # 1) Реестр: HKLM\SOFTWARE\Autodesk\AutoCAD\R**\ACAD-****:409 → AcadLocation
    try {
        $regRoots = @(
            'HKLM:\SOFTWARE\Autodesk\AutoCAD',
            'HKLM:\SOFTWARE\WOW6432Node\Autodesk\AutoCAD'
        )
        foreach ($regRoot in $regRoots) {
            if (-not (Test-Path $regRoot)) { continue }
            Get-ChildItem $regRoot -ErrorAction SilentlyContinue | ForEach-Object {
                Get-ChildItem $_.PSPath -ErrorAction SilentlyContinue | ForEach-Object {
                    $p = (Get-ItemProperty $_.PSPath -ErrorAction SilentlyContinue).AcadLocation
                    if ($p) { Add-Candidate (Join-Path $p 'acad.exe') }
                }
            }
        }
    } catch { }

    # 2) Стандартные папки установки
    $bases = @(
        "$env:ProgramFiles\Autodesk",
        "${env:ProgramFiles(x86)}\Autodesk",
        'C:\Program Files\Autodesk',
        'D:\Program Files\Autodesk'
    ) | Where-Object { $_ -and (Test-Path $_) } | Select-Object -Unique

    foreach ($base in $bases) {
        Get-ChildItem -Path $base -Directory -Filter 'AutoCAD*' -ErrorAction SilentlyContinue |
            ForEach-Object { Add-Candidate (Join-Path $_.FullName 'acad.exe') }
    }

    # Сортировка: сначала с Civil 3D, затем по убыванию года
    $best = $found |
        Sort-Object @{Expression='HasC3D';Descending=$true},
                    @{Expression='Year';  Descending=$true} |
        Select-Object -First 1 -Unique

    if ($best) { $Civil3D = $best.Exe }
}

if (-not $Civil3D -or -not (Test-Path $Civil3D)) {
    Write-Host "  acad.exe not found. Specify path:" -ForegroundColor Yellow
    Write-Host "  .\dev-reload.ps1 -Civil3D 'C:\...\acad.exe'" -ForegroundColor Yellow
    # Build anyway, just don't launch Civil 3D
}

# -- Путь к AutoCAD для КОМПИЛЯЦИИ ────────────────────────────────────────────
#  Больше НЕ передаём -p:AcadPathResolved: .csproj сам находит DLL отдельно
#  для каждой линейки (net48 ← Civil 3D 2015-2024, net8 ← 2025+), а если
#  нужной линейки на ПК нет — берёт референсные DLL из libs\acad-refs
#  (лежат в проекте, интернет не нужен). Навязывать один путь обоим
#  таргетам нельзя: net48 против DLL 2025+ падает с CS1705.
Info "AutoCAD/Civil 3D DLLs resolved per-target by .csproj (local install or libs\acad-refs)"

# -- 2. Close Civil 3D (ДО сборки!) ------------------------------------------
#  Civil 3D держит загруженную DLL заблокированной — если собирать при
#  запущенном C3D, шаг деплоя (копирование в ApplicationPlugins) падает с
#  «file is being used by another process», и НОВАЯ сборка не попадает в бандл
#  (в продукт грузится старая DLL). Поэтому сначала закрываем C3D, потом собираем.
$acadProcs = Get-Process -Name "acad" -ErrorAction SilentlyContinue
if ($acadProcs) {
    Step "Closing Civil 3D..."
    $acadProcs | ForEach-Object {
        $_.CloseMainWindow() | Out-Null
    }
    $waited = 0
    while ((Get-Process -Name "acad" -ErrorAction SilentlyContinue) -and $waited -lt 15) {
        Start-Sleep -Milliseconds 500
        $waited++
        Write-Host "  .. waiting for close..." -ForegroundColor Gray
    }
    Get-Process -Name "acad" -ErrorAction SilentlyContinue | Stop-Process -Force
    Ok "Civil 3D closed"
} else {
    Info "Civil 3D was not running"
}

# -- 3. Build (обе версии: net48 для C3D 2015-2024, net8 для 2025+) -----------
#  Собираем ОБА таргета, чтобы обновить именно ту DLL, которую грузит ваша
#  версия Civil 3D, независимо от того, какая она.
Step "Building (net48 + net8)..."
$t = [Diagnostics.Stopwatch]::StartNew()

dotnet build "$root\KartogrammaPlugin.csproj" `
    -c Debug `
    --nologo -v minimal

$t.Stop()
if ($LASTEXITCODE -ne 0) {
    Write-Host "`n  BUILD FAILED - Civil 3D not restarted" -ForegroundColor Red
    exit 1
}
Ok "Built in $([math]::Round($t.Elapsed.TotalSeconds, 1)) sec"
Ok "DLL deployed to ApplicationPlugins automatically"

# Ensure PackageContents.xml is in the bundle
$bundleRoot = "$env:APPDATA\Autodesk\ApplicationPlugins\KartogrammaPlugin.bundle"
$pcSrc = "$root\bundle\PackageContents.xml"
$pcDst = "$bundleRoot\PackageContents.xml"
if ((Test-Path $pcSrc) -and -not (Test-Path $pcDst)) {
    Copy-Item $pcSrc $pcDst
    Ok "PackageContents.xml copied to bundle"
}

# -- 4. Launch Civil 3D -------------------------------------------------------
if ($Civil3D -and (Test-Path $Civil3D)) {
    Step "Starting Civil 3D..."
    $acadDir = Split-Path $Civil3D -Parent
    $acadArgs = @("/product", "C3D", "/language", "ru-RU", "/p", "<<C3D_Metric>>")

    # /ld AecBase.dbx — только если файл реально есть рядом с acad.exe
    $aecBase = Join-Path $acadDir 'AecBase.dbx'
    if (Test-Path $aecBase) {
        $acadArgs = @("/ld", "`"$aecBase`"") + $acadArgs
    } else {
        Info "AecBase.dbx not found — launching without /ld"
    }

    if ($Dwg -and (Test-Path $Dwg)) {
        $acadArgs += $Dwg
        Info "Test drawing: $Dwg"
    }

    Start-Process -FilePath $Civil3D -ArgumentList $acadArgs
    Ok "Civil 3D started"
    Write-Host ""
    Write-Host "  Next steps:" -ForegroundColor White
    Write-Host "  1. Wait for Civil 3D to fully load" -ForegroundColor Gray
    Write-Host "  2. Press F5 in VS Code -> 'Attach to AutoCAD Civil 3D 2024'" -ForegroundColor Gray
    Write-Host "  3. Run OpenKartogramma command in Civil 3D" -ForegroundColor Gray
    Write-Host ""
} else {
    Write-Host "`n  Build OK. Launch Civil 3D manually." -ForegroundColor Yellow
}
