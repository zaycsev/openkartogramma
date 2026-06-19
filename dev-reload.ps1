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
#  Авто-выбор в .csproj берёт старшую версию AutoCAD, но обычный AutoCAD без
#  Civil 3D не содержит AeccDbMgd, а его accoremgd может быть .NET 8 (CS1705 при
#  сборке net48). Поэтому для сборки явно выбираем папку с Civil 3D DLL.
$buildAcadPath = ""
function Test-HasC3D($dir) {
    return (Test-Path (Join-Path $dir 'C3D\AeccDbMgd.dll')) -or `
           (Test-Path (Join-Path $dir 'AeccDbMgd.dll'))
}
if ($found -and $found.Count -gt 0) {
    $bestBuild = $found |
        Where-Object { $_.HasC3D } |
        Sort-Object @{Expression='Year';Descending=$true} |
        Select-Object -First 1
    if ($bestBuild) { $buildAcadPath = $bestBuild.Dir }
}
if (-not $buildAcadPath -and $Civil3D) {
    $cand = Split-Path $Civil3D -Parent
    if (Test-HasC3D $cand) { $buildAcadPath = $cand }
}

$buildProps = @()
if ($buildAcadPath) {
    $buildProps = @("-p:AcadPathResolved=$buildAcadPath")
    Info "Build DLLs from: $buildAcadPath"
} else {
    Info "No Civil 3D DLLs found - MSBuild will auto-resolve (may fail)"
}

# -- 2. Build net48 (Civil 3D 2024 uses .NET Framework 4.8) -------------------
Step "Building (net48)..."
$t = [Diagnostics.Stopwatch]::StartNew()

dotnet build "$root\KartogrammaPlugin.csproj" `
    -c Debug `
    -f net48 `
    --nologo -v minimal @buildProps

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

# -- 3. Close Civil 3D --------------------------------------------------------
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
