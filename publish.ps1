param([string]$Output = (Join-Path $PSScriptRoot 'artifacts\publish\win-x64'))
$ErrorActionPreference = 'Stop'
$env:MSBuildEnableWorkloadResolver = 'false'
dotnet publish (Join-Path $PSScriptRoot 'src\AutoExtract.App\AutoExtract.App.csproj') -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -o $Output
if ($LASTEXITCODE -ne 0) { throw 'Release 빌드 실패' }
Get-FileHash -LiteralPath (Join-Path $Output 'AutoExtract.exe') -Algorithm SHA256
