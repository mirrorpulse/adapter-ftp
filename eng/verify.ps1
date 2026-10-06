[CmdletBinding()]
param()
$ErrorActionPreference = 'Stop'
& pwsh -NoProfile -File (Join-Path $PSScriptRoot 'restore-adapter-sdk.ps1')
if ($LASTEXITCODE -ne 0) { throw 'Fixed SDK verification failed.' }
$projects = @('src/MirrorPulse.Adapter.Ftp.Worker/MirrorPulse.Adapter.Ftp.Worker.csproj',
    'tests/MirrorPulse.Adapter.Ftp.Worker.Tests/MirrorPulse.Adapter.Ftp.Worker.Tests.csproj',
    'tools/MirrorPulse.Adapter.Ftp.Conformance/MirrorPulse.Adapter.Ftp.Conformance.csproj')
foreach ($project in $projects) {
    & dotnet restore $project --locked-mode
    if ($LASTEXITCODE -ne 0) { throw 'Locked restore failed.' }
    & dotnet build $project -c Release --no-restore
    if ($LASTEXITCODE -ne 0) { throw 'Release build failed.' }
    & dotnet format $project --no-restore --verify-no-changes
    if ($LASTEXITCODE -ne 0) { throw 'Formatting failed.' }
}
& dotnet test $projects[1] -c Release --no-build --no-restore --logger 'trx;LogFileName=ftp-v2.trx' --results-directory artifacts/test-results
if ($LASTEXITCODE -ne 0) { throw 'Actual FTP/FTPS conformance failed.' }
[xml]$trx = Get-Content -LiteralPath artifacts/test-results/ftp-v2.trx -Raw
$counts = $trx.TestRun.ResultSummary.Counters
if ($counts.total -ne 23 -or $counts.executed -ne 23 -or $counts.passed -ne 23 -or $counts.notExecuted -ne 0) {
    throw 'All FTP/FTPS source cases must execute without skips.'
}
& pwsh -NoProfile -File (Join-Path $PSScriptRoot 'verify-adapter-version.ps1')
if ($LASTEXITCODE -ne 0) { throw 'Provider version policy verification failed.' }
& pwsh -NoProfile -File (Join-Path $PSScriptRoot 'verify-adapter-publishing.ps1')
if ($LASTEXITCODE -ne 0) { throw 'Provider publication policy verification failed.' }
