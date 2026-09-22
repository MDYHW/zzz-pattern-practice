param([string]$OutputDirectory = (Join-Path $PSScriptRoot '..\..\.local\experiment-build'))
$ErrorActionPreference = 'Stop'
$compiler = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'
if (-not (Test-Path -LiteralPath $compiler)) { throw '.NET Framework 4.x C# compiler is required.' }
$destination = [IO.Path]::GetFullPath($OutputDirectory)
$localRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..\..\.local'))
if (-not $destination.StartsWith($localRoot + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase)) { throw 'Build output must stay under repository .local.' }
New-Item -ItemType Directory -Path $destination -Force | Out-Null
$sources = @('ExperimentCore.cs', 'ExperimentProfile.cs', 'ExperimentPlan.cs', 'NativeInput.cs', 'NoticeDetection.cs', 'NoticeExperiment.cs', 'NoticeForm.cs', 'TrialCorrectionForm.cs', 'ExperimentSettings.cs', 'ObsRecording.cs', 'ObsRecordingTests.cs', 'TrialResults.cs', 'TrialResultsTests.cs', 'NoticeTests.cs', 'Program.cs') | ForEach-Object { Join-Path $PSScriptRoot $_ }
$exe = Join-Path $destination 'ControlExperiment.exe'
& $compiler /nologo /codepage:65001 /target:exe /platform:x64 /optimize+ /warn:4 "/out:$exe" "/win32manifest:$(Join-Path $PSScriptRoot 'app.manifest')" /r:System.dll /r:System.Core.dll /r:System.Drawing.dll /r:System.Windows.Forms.dll /r:System.Web.Extensions.dll /r:System.Security.dll $sources
if ($LASTEXITCODE -ne 0) { throw 'C# build failed.' }
Write-Output $exe
