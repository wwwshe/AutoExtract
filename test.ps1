$ErrorActionPreference = 'Stop'
$env:MSBuildEnableWorkloadResolver = 'false'
Push-Location $PSScriptRoot
try {
    dotnet run --project tests/AutoExtract.Tests -c Release
    if ($LASTEXITCODE -ne 0) { throw '핵심 테스트 실패' }
    dotnet run --project tests/AutoExtract.UiSmoke -c Release
    if ($LASTEXITCODE -ne 0) { throw 'WPF 렌더 테스트 실패' }
} finally { Pop-Location }
