Set-StrictMode -Version 2
$ErrorActionPreference = 'Stop'

$SolutionRoot = Resolve-Path "$PSScriptRoot/.."

Write-Output 'git submodule update --recursive --init'
git submodule update --recursive --init

&"$PSScriptRoot\build.ps1" -Configuration Release