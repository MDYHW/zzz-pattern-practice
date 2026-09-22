param([string]$OutputDirectory = (Join-Path $PSScriptRoot '..\..\.local\lane-verifier'))
$ErrorActionPreference = 'Stop'
$compiler = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'
$destination = [IO.Path]::GetFullPath($OutputDirectory)
$localRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..\..\.local'))
if (-not $destination.StartsWith($localRoot + [IO.Path]::DirectorySeparatorChar,[StringComparison]::OrdinalIgnoreCase)) { throw 'Build output must stay under repository .local.' }
New-Item -ItemType Directory -Force -Path $destination | Out-Null
$shared = Join-Path $PSScriptRoot '..\experiment'
$sources = @('ExperimentCore.cs','ExperimentProfile.cs','ExperimentPlan.cs','NoticeDetection.cs') | ForEach-Object { Join-Path $shared $_ }
$sources += @('LaneNative.cs','LaneModel.cs','LaneOverlay.cs','ObservedInput.cs','LaneForm.cs','LaneTests.cs','Program.cs') | ForEach-Object { Join-Path $PSScriptRoot $_ }
$exe = Join-Path $destination 'LaneVerifier.exe'
& $compiler /nologo /codepage:65001 /target:winexe /platform:x64 /optimize+ /warn:4 "/out:$exe" "/win32manifest:$(Join-Path $shared 'app.manifest')" /r:System.dll /r:System.Core.dll /r:System.Drawing.dll /r:System.Windows.Forms.dll /r:System.Web.Extensions.dll /r:System.Security.dll $sources
if ($LASTEXITCODE -ne 0) { throw 'Lane verifier build failed.' }
New-Item -ItemType Directory -Force -Path (Join-Path $destination 'profiles') | Out-Null
Copy-Item -LiteralPath (Join-Path $shared 'profiles\vesper-execute-01.json'),(Join-Path $shared 'profiles\vesper-execute-02.json'),(Join-Path $shared 'profiles\girtablullu-stagnant-execute-01.json') -Destination (Join-Path $destination 'profiles')
Copy-Item -LiteralPath (Join-Path $shared 'notice-template.png'),(Join-Path $shared 'notice-template-execute02.png'),(Join-Path $shared 'notice-template-girtablullu.png') -Destination $destination
Copy-Item -LiteralPath (Join-Path $PSScriptRoot '사용법.md') -Destination $destination
Write-Output $exe
