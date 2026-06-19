param(
    [string]$Version   = "1.1.1",
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

# --- Auto-detect an AutoCAD/Civil 3D install that has the Civil DLLs (AeccDbMgd) ---
# Авто-определение в .csproj берёт старшую версию AutoCAD, но обычный AutoCAD
# (без Civil 3D) не содержит AeccDbMgd/AecBaseMgd. Здесь ищем версию с Civil 3D.
if (-not $AcadPath) {
    $acadVersions = 2027..2015 | ForEach-Object { "C:\Program Files\Autodesk\AutoCAD $_" }
    $AcadPath = $acadVersions | Where-Object {
        (Test-Path "$_\acdbmgd.dll") -and
        ((Test-Path "$_\C3D\AeccDbMgd.dll") -or (Test-Path "$_\AeccDbMgd.dll"))
    } | Select-Object -First 1
}
$acadProp = @()
if ($AcadPath) { $acadProp = @("-p:AcadPathResolved=$AcadPath") }

function Step { Write-Host "`n> $args" -ForegroundColor Cyan  }
function Ok   { Write-Host "  OK: $args" -ForegroundColor Green }
function Fail { Write-Host "  FAIL: $args" -ForegroundColor Red; exit 1 }
function Info { Write-Host "  ..: $args" -ForegroundColor Gray  }

if ($AcadPath) { Info "AutoCAD (Civil 3D) DLLs from: $AcadPath" } else { Info "AutoCAD path auto-resolved by MSBuild" }

# Step 1: build net8
Step "Build net8.0-windows..."
dotnet build "$root\KartogrammaPlugin.csproj" -c $Config -f net8.0-windows --nologo @acadProp
if ($LASTEXITCODE -ne 0) { Fail "net8 build failed" }
Ok "net8.0-windows done"

# Step 2: build net48
Step "Build net48..."
dotnet build "$root\KartogrammaPlugin.csproj" -c $Config -f net48 --nologo @acadProp 2>&1 | Out-Null
if ($LASTEXITCODE -eq 0) { Ok "net48 done" } else { Info "net48 skipped (no Civil 3D DLLs)" }

# Step 3: assemble dist\bundle
Step "Assembling dist\bundle..."

$distBundle = "$root\dist\bundle"
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
    Info "net48 DLL not found - installer will only support Civil 3D 2025+"
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
