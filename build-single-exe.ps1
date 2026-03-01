$ErrorActionPreference = 'Stop'

$repoRoot = Split-Path -Parent $MyInvocation.MyCommand.Path
Set-Location $repoRoot

$solutionPath = Join-Path $repoRoot 'SalsaNOW.sln'
$toolsDir = Join-Path $repoRoot '.tools'
$nugetExe = Join-Path $toolsDir 'nuget.exe'

if (-not (Test-Path $toolsDir)) {
    New-Item -ItemType Directory -Path $toolsDir | Out-Null
}

if (-not (Test-Path $nugetExe)) {
    Write-Host 'Downloading nuget.exe...'
    Invoke-WebRequest -Uri 'https://dist.nuget.org/win-x86-commandline/latest/nuget.exe' -OutFile $nugetExe
}

Write-Host 'Restoring legacy packages.config dependencies...'
& $nugetExe restore $solutionPath -PackagesDirectory (Join-Path $repoRoot 'packages') -NonInteractive -Verbosity minimal
if ($LASTEXITCODE -ne 0) {
    throw 'NuGet restore failed.'
}

$msbuildCandidates = @(
    'C:\Program Files (x86)\Microsoft Visual Studio\2022\BuildTools\MSBuild\Current\bin\MSBuild.exe',
    'C:\Program Files\Microsoft Visual Studio\2022\BuildTools\MSBuild\Current\bin\MSBuild.exe'
)

$msbuild = $msbuildCandidates | Where-Object { Test-Path $_ } | Select-Object -First 1
if (-not $msbuild) {
    $msbuildCmd = Get-Command msbuild -ErrorAction SilentlyContinue
    if ($msbuildCmd) {
        $msbuild = $msbuildCmd.Source
    }
}

if (-not $msbuild) {
    throw 'MSBuild.exe not found. Install Visual Studio Build Tools 2022 with .NET desktop build tools workload.'
}

Write-Host "Building Release with: $msbuild"
& $msbuild $solutionPath '/p:Configuration=Release' '/p:Platform=Any CPU' '/v:minimal'
if ($LASTEXITCODE -ne 0) {
    throw 'MSBuild failed.'
}

$outputExe = Join-Path $repoRoot 'SalsaNOW\bin\Release\SalsaNOW.exe'
if (Test-Path $outputExe) {
    Write-Host "Built EXE: $outputExe"
} else {
    throw 'Build finished but SalsaNOW.exe was not found in expected output folder.'
}
