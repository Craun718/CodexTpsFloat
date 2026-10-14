param([switch]$Portable)
$ErrorActionPreference = 'Stop'
$taskRoot = $PSScriptRoot
if ($Portable) {
    dotnet publish (Join-Path $taskRoot 'src/CodexTpsFloat.csproj') -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:EnableCompressionInSingleFile=true -o (Join-Path $taskRoot 'app')
} else {
    dotnet publish (Join-Path $taskRoot 'src/CodexTpsFloat.csproj') -c Release -o (Join-Path $taskRoot 'app')
}
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
