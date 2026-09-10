param([string]$Configuration = "Release")

$ErrorActionPreference = "Stop"
$root = Split-Path -Parent $MyInvocation.MyCommand.Path

# Locate MSBuild via vswhere (falls back to a known path).
$vswhere = "${env:ProgramFiles(x86)}\Microsoft Visual Studio\Installer\vswhere.exe"
$msbuild = $null
if (Test-Path $vswhere) {
    $vs = & $vswhere -latest -products * -requires Microsoft.Component.MSBuild -property installationPath
    if ($vs) { $msbuild = Join-Path $vs "MSBuild\Current\Bin\amd64\MSBuild.exe" }
}
if (-not $msbuild -or -not (Test-Path $msbuild)) {
    $msbuild = "C:\Program Files\Microsoft Visual Studio\2022\Community\MSBuild\Current\Bin\amd64\MSBuild.exe"
}

# 1. Restore C++ NuGet packages.
$nuget = Join-Path $root "tools\nuget.exe"
if (-not (Test-Path $nuget)) {
    New-Item -ItemType Directory -Force (Split-Path $nuget) | Out-Null
    Invoke-WebRequest -Uri "https://dist.nuget.org/win-x86-commandline/latest/nuget.exe" -OutFile $nuget
}
& $nuget restore (Join-Path $root "src\WebcamSwitcher.Source\WebcamSwitcher.Source.vcxproj") -SolutionDirectory $root -NonInteractive
if ($LASTEXITCODE -ne 0) { throw "nuget restore failed" }

# 2. Build the C++ media source.
& $msbuild (Join-Path $root "src\WebcamSwitcher.Source\WebcamSwitcher.Source.vcxproj") /p:Configuration=$Configuration /p:Platform=x64 /m /v:m /nologo
if ($LASTEXITCODE -ne 0) { throw "C++ build failed" }

# 3. Publish the WPF app (self-contained, no .NET runtime required).
$appOut = Join-Path $root "artifacts\app"
& dotnet publish (Join-Path $root "src\WebcamSwitcher.App\WebcamSwitcher.App.csproj") -c $Configuration -r win-x64 --self-contained true -o $appOut
if ($LASTEXITCODE -ne 0) { throw "dotnet publish failed" }

# 4. Copy the media source DLL next to the app.
Copy-Item (Join-Path $root "src\WebcamSwitcher.Source\x64\$Configuration\WebcamSwitcher.Source.dll") $appOut -Force

Write-Host "Build complete. App: $appOut"
