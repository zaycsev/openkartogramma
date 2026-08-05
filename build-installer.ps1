param(
    [string]$Version   = "1.1.3",
    [string]$Config    = "Release",
    [string]$InnoSetup = "",
    [string]$AcadPath  = ""
)

$ErrorActionPreference = "Stop"
$root = $PSScriptRoot

# --- Auto-detect Inno Setup (ISCC.exe) ---
if (-not $InnoSetup -or -not (Test-Path $InnoSetup)) {
    $innoCandidates = @(
        "C:\Program Files (x86)\Inno Setup 6\ISCC.exe",
        "C:\Program Files\Inno Setup 6\ISCC.exe",
        "$env:LOCALAPPDATA\Programs\Inno Setup 6\ISCC.exe"
    )
    $InnoSetup = $innoCandidates | Where-Object { Test-Path $_ } | Select-Object -First 1
}

# --- AutoCAD/Civil 3D DLLs для сборки ---
# .csproj сам находит DLL отдельно для каждой линейки:
#   net48 ← установленный Civil 3D 2015-2024, иначе libs\acad-refs\net48
#   net8  ← установленный Civil 3D 2025+,    иначе libs\acad-refs\net8
# Референсные DLL лежат в проекте — интернет и установленный AutoCAD для
# сборки не нужны. Оба таргета собираются на любой машине, поэтому
# установщик ВСЕГДА поддерживает Civil 3D 2015-2024 (net48) и 2025+
# включая будущие версии (net8, в PackageContents.xml без верхней границы).

function Step { Write-Host "`n> $args" -ForegroundColor Cyan  }
function Ok   { Write-Host "  OK: $args" -ForegroundColor Green }
function Fail { Write-Host "  FAIL: $args" -ForegroundColor Red; exit 1 }
function Info { Write-Host "  ..: $args" -ForegroundColor Gray  }

Info "AutoCAD/Civil 3D DLLs resolved per-target by .csproj (local install or libs\acad-refs)"

# Step 1: build net8
Step "Build net8.0-windows..."
dotnet build "$root\KartogrammaPlugin.csproj" -c $Config -f net8.0-windows --nologo
if ($LASTEXITCODE -ne 0) { Fail "net8 build failed" }
Ok "net8.0-windows done"

# Step 2: build net48
Step "Build net48..."
dotnet build "$root\KartogrammaPlugin.csproj" -c $Config -f net48 --nologo
if ($LASTEXITCODE -ne 0) { Fail "net48 build failed" }
Ok "net48 done"

# Step 3: assemble dist\bundle
Step "Assembling dist\bundle..."

$distBundle = "$root\dist\bundle"

# Чистим папку перед сборкой. В installer.iss файлы помечены
# skipifsourcedoesntexist: если копирование ниже не состоится, в установщик
# молча уедет DLL от ПРЕДЫДУЩЕЙ сборки. Пустая папка превращает такую
# ситуацию в явную ошибку вместо порчи релиза.
if (Test-Path $distBundle) { Remove-Item $distBundle -Recurse -Force }

New-Item -ItemType Directory -Force -Path "$distBundle\Contents\net8"  | Out-Null
New-Item -ItemType Directory -Force -Path "$distBundle\Contents\net48" | Out-Null

Copy-Item "$root\bundle\PackageContents.xml" "$distBundle\PackageContents.xml" -Force
Ok "PackageContents.xml"

$net8Src  = "$root\bin\$Config\net8.0-windows\openkartogramma.dll"
$net8Deps = "$root\bin\$Config\net8.0-windows\openkartogramma.deps.json"
if (Test-Path $net8Src) {
    Copy-Item $net8Src "$distBundle\Contents\net8\" -Force
    $sizeKb = [math]::Round((Get-Item $net8Src).Length / 1024)
    Ok "net8\openkartogramma.dll ($sizeKb kb)"
    if (Test-Path $net8Deps) {
        Copy-Item $net8Deps "$distBundle\Contents\net8\" -Force
        Ok "net8\openkartogramma.deps.json"
    } else {
        Info "deps.json not found"
    }
} else {
    Fail "net8 DLL not found: $net8Src"
}

$net48Src = "$root\bin\$Config\net48\openkartogramma.dll"
if (Test-Path $net48Src) {
    Copy-Item $net48Src "$distBundle\Contents\net48\" -Force
    Ok "net48\openkartogramma.dll"
} else {
    # Референсные DLL лежат в libs\acad-refs, поэтому net48 обязан собираться
    # на любой машине. Отсутствие DLL здесь — не «частичная поддержка», а
    # сломанный релиз: пользователи Civil 3D 2015-2024 останутся без плагина.
    Fail "net48 DLL not found: $net48Src"
}

# Step 4: generate installer graphics
Step "Generating installer assets..."
$global:LASTEXITCODE = 0
& "$root\generate-installer-assets.ps1" -Version $Version
# Проверяем по фактическим артефактам, а не по $LASTEXITCODE: предыдущий шаг
# (пропущенная из-за блокировки сборка net48) мог оставить ненулевой код.
if (-not (Test-Path "$root\dist\assets\setup.ico")) { Fail "Asset generation failed" }
Ok "Assets ready (dist\assets\)"

# Step 5: Inno Setup
Step "Compiling installer (Inno Setup)..."

if (-not (Test-Path $InnoSetup)) {
    Write-Host "  Inno Setup not found: $InnoSetup" -ForegroundColor Yellow
    Write-Host "  Download: https://jrsoftware.org/isdl.php" -ForegroundColor Yellow
    exit 0
}

& $InnoSetup /DAppVersion="$Version" "$root\installer.iss"
if ($LASTEXITCODE -ne 0) { Fail "Inno Setup error" }

$exePath = "$root\dist\Setup_openkartogramma_v$Version.exe"
if (Test-Path $exePath) {
    $sizeMb = [math]::Round((Get-Item $exePath).Length / 1MB, 1)
    Write-Host ""
    Write-Host "  ================================================" -ForegroundColor Green
    Write-Host "  Installer ready: $exePath" -ForegroundColor Green
    Write-Host "  Size: $sizeMb MB" -ForegroundColor Gray
    Write-Host "  ================================================" -ForegroundColor Green
} else {
    Fail "Installer was not created"
}
