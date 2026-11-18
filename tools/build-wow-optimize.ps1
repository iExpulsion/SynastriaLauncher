#Requires -Version 7
<#
.SYNOPSIS
    Builds wow-optimize's wow_optimize.dll from a pinned release for the launcher to ship.
.DESCRIPTION
    Only the wow_optimize target is built: not the version.dll proxy or the wow_loader injector, which the launcher
    never installs (the DLL Loader patch loads the DLL instead). The DLL and its optional .sym land in
    src/SynastriaLauncher.Core/Mods/Bundled/wow-optimize, where the next launcher build embeds them.
    Needs git, CMake and Visual Studio with the C++ desktop workload (32-bit tools).
    To move to a newer wow-optimize, review its changes, then update $Commit and $Version (and the version in
    mods.json) and run this again.

    Since v3.23.0's version bump every hook calls WowOpt_RegisterServerSyncHook, defined in
    src/core/server_sync_compat.cpp, which was never published (the official releases' "ServerSyncCompat", which
    keeps a server's integrity check from noticing the hooks). tools/wow-optimize-stub supplies an empty definition,
    built as a static library and linked in; without the real file wow-optimize's CMakeLists leaves
    HAVE_SERVER_SYNC_COMPAT off, so nothing is hidden and nothing closed is shipped. The release archives aren't used:
    Windows Defender flagged v3.23.1's Release.7z (its injector and proxy) as Trojan:Win32/Suschil!rfn.
#>
param(
    [string]$Commit = 'd0a9f35fc1759917641cf991e511af7fb205cf6f',
    [string]$Version = 'v3.23.1'
)

$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot
$work = Join-Path $root 'artifacts/wow-optimize'
$out = Join-Path $root 'src/SynastriaLauncher.Core/Mods/Bundled/wow-optimize'

$cmake = (Get-Command cmake -ErrorAction SilentlyContinue)?.Source
if (-not $cmake) {
    $vswhere = "${env:ProgramFiles(x86)}\Microsoft Visual Studio\Installer\vswhere.exe"
    $cmake = & $vswhere -latest -find 'Common7/IDE/CommonExtensions/Microsoft/CMake/CMake/bin/cmake.exe' | Select-Object -First 1
}
if (-not $cmake) { throw 'CMake not found: install Visual Studio with the C++ desktop workload.' }

function Get-Pinned([string]$Folder, [string]$Url, [string]$Sha) {
    if (-not (Test-Path (Join-Path $Folder '.git'))) {
        New-Item -ItemType Directory -Force -Path $Folder | Out-Null
        git -C $Folder init -q
        git -C $Folder remote add origin $Url
    }
    git -C $Folder fetch -q --depth 1 origin $Sha
    git -C $Folder checkout -q --force FETCH_HEAD
    git -C $Folder clean -q -fdx
    if ((git -C $Folder rev-parse HEAD) -ne $Sha) { throw "Checked out the wrong commit of $Url" }
}

$source = Join-Path $work 'src'
Get-Pinned $source 'https://github.com/suprepupre/wow-optimize.git' $Commit
if (Test-Path (Join-Path $source 'src/core/server_sync_compat.cpp')) {
    throw 'This wow-optimize commit has server_sync_compat.cpp: review it before building, the stub is no longer the whole story.'
}

# Fresh build folders each time: FetchContent dependencies are pinned per commit and never updated in place.
$stubBuild = Join-Path $work 'stub'
$build = Join-Path $work 'build'
foreach ($folder in $stubBuild, $build) {
    if (Test-Path $folder) { Remove-Item -LiteralPath $folder -Recurse -Force }
}

& $cmake -S (Join-Path $PSScriptRoot 'wow-optimize-stub') -B $stubBuild -A Win32
if ($LASTEXITCODE) { throw 'Stub configure failed' }
& $cmake --build $stubBuild --config Release
if ($LASTEXITCODE) { throw 'Stub build failed' }
$stubLib = (Resolve-Path (Join-Path $stubBuild 'Release/server_sync_stub.lib')).Path

& $cmake -S $source -B $build -A Win32 "-DCMAKE_SHARED_LINKER_FLAGS=`"$stubLib`""
if ($LASTEXITCODE) { throw 'CMake configure failed' }
& $cmake --build $build --config Release --target wow_optimize
if ($LASTEXITCODE) { throw 'Build failed' }

New-Item -ItemType Directory -Force -Path $out | Out-Null
Copy-Item (Join-Path $build 'Release/wow_optimize.dll') $out -Force
$sym = Join-Path $build 'Release/wow_optimize.sym'
if (Test-Path $sym) { Copy-Item $sym $out -Force }
Set-Content -Path (Join-Path $out 'BUILD.txt') -NoNewline `
    -Value "suprepupre/wow-optimize $Version, commit $Commit, with tools/wow-optimize-stub"
Get-ChildItem $out | ForEach-Object { '{0,-20} {1,10:N0} bytes  {2}' -f $_.Name, $_.Length, (Get-FileHash $_.FullName).Hash.ToLower() }
