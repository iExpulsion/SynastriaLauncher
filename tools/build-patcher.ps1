#Requires -Version 7
<#
.SYNOPSIS
    Builds SynastriaLauncher.dll, the launcher's in-game DLL (the in-memory patches), and PatcherHost.exe for the tests.
.DESCRIPTION
    Compiles native/SynastriaLauncher with CMake (32-bit), stamped with the launcher's version. The DLL lands in src/SynastriaLauncher.Core/Patching/Native,
    where the next launcher build embeds it; PatcherHost.exe stays in artifacts/patcher/build/Release, where the
    memory parity tests look for it. Needs Visual Studio with the C++ desktop workload.
#>
$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot
$source = Join-Path $root 'native/SynastriaLauncher'
$version = ([xml](Get-Content (Join-Path $root 'src/SynastriaLauncher.App/SynastriaLauncher.App.csproj'))).Project.PropertyGroup.Version |
    Where-Object { $_ } | Select-Object -First 1
$build = Join-Path $root 'artifacts/patcher/build'
$out = Join-Path $root 'src/SynastriaLauncher.Core/Patching/Native'

$cmake = (Get-Command cmake -ErrorAction SilentlyContinue)?.Source
if (-not $cmake) {
    $vswhere = "${env:ProgramFiles(x86)}\Microsoft Visual Studio\Installer\vswhere.exe"
    $cmake = & $vswhere -latest -find 'Common7/IDE/CommonExtensions/Microsoft/CMake/CMake/bin/cmake.exe' | Select-Object -First 1
}
if (-not $cmake) { throw 'CMake not found: install Visual Studio with the C++ desktop workload.' }

& $cmake -S $source -B $build -A Win32 "-DLAUNCHER_VERSION=$version"
if ($LASTEXITCODE) { throw 'CMake configure failed' }
& $cmake --build $build --config Release
if ($LASTEXITCODE) { throw 'Build failed' }

New-Item -ItemType Directory -Force -Path $out | Out-Null
Copy-Item (Join-Path $build 'Release/SynastriaLauncher.dll') $out -Force
Get-ChildItem $out | ForEach-Object { '{0,-24} {1,10:N0} bytes  {2}' -f $_.Name, $_.Length, (Get-FileHash $_.FullName).Hash.ToLower() }
