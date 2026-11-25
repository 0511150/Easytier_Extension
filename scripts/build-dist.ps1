$ErrorActionPreference = "Stop"

$Root = Split-Path -Parent $PSScriptRoot
$LocalOffline = "C:\Users\admin\AppData\Local\Packages\Claude_pzs8sxrjxfjjc\LocalCache\Local\EasytierExtBuild"
$LegacyCoreSrc = "C:\Users\admin\AppData\Local\Temp\claude\3b77ca5fb4cc6fe1\5c502608-3145-4885-b6e9-2c94e0df5bb4\scratchpad\root1\core"

$RestoreProps = @()
if (Test-Path "$LocalOffline\dotnet\dotnet.exe") {
    $env:DOTNET_ROOT = "$LocalOffline\dotnet"
    $env:DOTNET_CLI_HOME = "$LocalOffline\home"
    $env:NUGET_PACKAGES = "$LocalOffline\nuget"
    $Dotnet = "$LocalOffline\dotnet\dotnet.exe"
    $RestoreProps += "-p:RestoreSources=$LocalOffline\nuget"
    Write-Host "Using local offline .NET SDK & NuGet cache."
}
else {
    $DotnetCmd = Get-Command dotnet -ErrorAction Stop
    $Dotnet = $DotnetCmd.Source
    Write-Host "Using system .NET SDK: $Dotnet"
}

$env:DOTNET_NOLOGO = "1"
$env:DOTNET_SKIP_FIRST_TIME_EXPERIENCE = "1"

$Dist = Join-Path $Root "dist"
$TempCoreBackup = Join-Path ([System.IO.Path]::GetTempPath()) ("et_core_backup_" + [guid]::NewGuid().ToString("N"))
$TempSmokeRoot = Join-Path ([System.IO.Path]::GetTempPath()) ("et_smoke_root_" + [guid]::NewGuid().ToString("N"))

try {
    Write-Host "[1/6] Running unit & core integration tests..."
    & $Dotnet test (Join-Path $Root "tests\EasyTierExt.Tests\EasyTierExt.Tests.csproj") -c Release -p:Platform=x64 @RestoreProps --filter "FullyQualifiedName!~PublishedScreenShareSmokeTest"
    if ($LASTEXITCODE -ne 0) { throw "Tests failed!" }

    Write-Host "[2/6] Preserving core binaries / legacy config & cleaning dist directory..."
    $AppDataConfig = Join-Path $env:APPDATA "Easytier_With_Extension\config"
    if ((Test-Path (Join-Path $Dist "config\client.json")) -and !(Test-Path (Join-Path $AppDataConfig "client.json"))) {
        New-Item -ItemType Directory -Path $AppDataConfig -Force | Out-Null
        Copy-Item (Join-Path $Dist "config\*") $AppDataConfig -Force
    }

    $CoreSrc = $null
    if ($env:EASYTIER_CORE_SRC -and (Test-Path $env:EASYTIER_CORE_SRC)) {
        $CoreSrc = $env:EASYTIER_CORE_SRC
    }
    elseif (Test-Path $LegacyCoreSrc) {
        $CoreSrc = $LegacyCoreSrc
    }
    elseif (Test-Path (Join-Path $Dist "core")) {
        New-Item -ItemType Directory -Path $TempCoreBackup -Force | Out-Null
        Copy-Item (Join-Path $Dist "core\*") $TempCoreBackup -Force
        $CoreSrc = $TempCoreBackup
    }
    else {
        throw "EasyTier core binaries not found. Set EASYTIER_CORE_SRC or place binaries in dist\core."
    }

    if (Test-Path $Dist) { Remove-Item $Dist -Recurse -Force }
    New-Item -ItemType Directory -Path $Dist | Out-Null

    Write-Host "[3/6] Publishing EasyTierExt.App (Self-Contained + ReadyToRun)..."
    $VersionProps = @()
    if ($env:APP_VERSION -and $env:APP_VERSION.Trim()) {
        $CleanVer = $env:APP_VERSION.Trim().TrimStart('v', 'V')
        $VersionProps += "-p:Version=$CleanVer"
    }
    & $Dotnet publish (Join-Path $Root "src\EasyTierExt.App\EasyTierExt.App.csproj") `
        -c Release -r win-x64 -p:Platform=x64 --self-contained true -p:PublishReadyToRun=true @RestoreProps @VersionProps -o $Dist
    if ($LASTEXITCODE -ne 0) { throw "App publish failed!" }

    Write-Host "[4/6] Copying EasyTier core binaries..."
    $DistCore = Join-Path $Dist "core"
    New-Item -ItemType Directory -Path $DistCore | Out-Null
    Copy-Item "$CoreSrc\*" $DistCore -Force

    Write-Host "[5/6] Publishing ScreenShare plugin to temp smoke directory (excluded from dist)..."
    $SmokePluginDir = Join-Path $TempSmokeRoot "plugins\screen-share"
    New-Item -ItemType Directory -Path $SmokePluginDir -Force | Out-Null
    & $Dotnet publish (Join-Path $Root "plugins\ScreenShare\ScreenShare.csproj") `
        -c Release -r win-x64 -p:Platform=x64 --self-contained true -p:PublishReadyToRun=true @RestoreProps -o $SmokePluginDir
    if ($LASTEXITCODE -ne 0) { throw "ScreenShare publish failed!" }
    Copy-Item (Join-Path $Root "plugins\ScreenShare\manifest.json") (Join-Path $SmokePluginDir "manifest.json") -Force

    Write-Host "[6/6] Running full test suite including published ScreenShare.exe smoke test..."
    $env:EASYTIER_SMOKE_PLUGIN_ROOT = $TempSmokeRoot
    & $Dotnet test (Join-Path $Root "tests\EasyTierExt.Tests\EasyTierExt.Tests.csproj") -c Release -p:Platform=x64 --no-build
    if ($LASTEXITCODE -ne 0) { throw "Post-publish verification tests failed!" }

    if ((Test-Path (Join-Path $Dist "plugins")) -or (Test-Path (Join-Path $Dist "market"))) {
        throw "dist directory must only contain main program and core binaries (found plugins or market directory)."
    }

    Write-Host "Build & packaging completed successfully (main program only)!"
    Write-Host "Output: $Dist"
}
finally {
    Remove-Item Env:EASYTIER_SMOKE_PLUGIN_ROOT -ErrorAction SilentlyContinue
    if (Test-Path $TempCoreBackup) {
        Remove-Item $TempCoreBackup -Recurse -Force -ErrorAction SilentlyContinue
    }
    if (Test-Path $TempSmokeRoot) {
        Remove-Item $TempSmokeRoot -Recurse -Force -ErrorAction SilentlyContinue
    }
}
