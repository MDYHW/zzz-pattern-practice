param(
    [ValidateSet('install', 'dev', 'typecheck', 'test', 'check', 'build', 'test:e2e', 'preview', 'browsers', 'research:assets', 'research:timing', 'research:windows', 'research:check', 'experiment:build', 'experiment:check', 'experiment:records', 'experiment:media', 'lane:build', 'lane:check')]
    [string]$Task = 'check',
    [Parameter(ValueFromRemainingArguments = $true)]
    [string[]]$TaskArgs
)
$ErrorActionPreference = 'Stop'
$nodeCommand = Get-Command node -ErrorAction SilentlyContinue
$nodePath = if ($nodeCommand) { $nodeCommand.Source } else { $null }
$nodeVersion = if ($nodePath) { [version]((& $nodePath --version).TrimStart('v')) } else { [version]'0.0.0' }
if ($nodeVersion -lt [version]'24.19.0' -or $nodeVersion.Major -ne 24) {
    $bundledNode = Join-Path $env:USERPROFILE '.cache\codex-runtimes\codex-primary-runtime\dependencies\node\bin\node.exe'
    if (-not (Test-Path -LiteralPath $bundledNode)) { throw 'Node 24.19 이상(24.x)을 설치하거나 Codex 런타임을 확인하세요.' }
    $nodePath = $bundledNode
}
$npmCommand = Get-Command npm.cmd -ErrorAction SilentlyContinue
$userNpm = Join-Path $env:APPDATA 'npm\npm.cmd'
if (Test-Path -LiteralPath $userNpm) {
    $npmPath = $userNpm
} elseif ($npmCommand) {
    $npmPath = $npmCommand.Source
} else {
    throw 'npm 11을 찾지 못했습니다. 기존 npm 설치를 확인하세요.'
}
$npmCli = Join-Path (Split-Path -Parent $npmPath) 'node_modules\npm\bin\npm-cli.js'
if (-not (Test-Path -LiteralPath $npmCli)) { throw 'npm CLI 경로를 확인하세요.' }
$env:PATH = "$(Split-Path -Parent $nodePath);$(Split-Path -Parent $npmPath);$env:PATH"
Push-Location $PSScriptRoot
try {
    Write-Host "Project: $PSScriptRoot"
    Write-Host "Node: $(& $nodePath --version), npm: $(& $nodePath $npmCli --version)"
    if ($Task -eq 'install') { & $nodePath $npmCli ci @TaskArgs } else { & $nodePath $npmCli run $Task -- @TaskArgs }
    $resultCode = $LASTEXITCODE
} finally {
    Pop-Location
}
exit $resultCode
