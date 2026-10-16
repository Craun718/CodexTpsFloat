param(
    [string]$Runtime = "win-x64",
    [switch]$Portable
)
$ErrorActionPreference = 'Stop'
$taskRoot = $PSScriptRoot
$arguments = @(
    'publish', (Join-Path $taskRoot 'src/App/CodexTpsFloat.App.csproj'),
    '-c', 'Release', '-r', $Runtime, '--self-contained', 'true',
    '-o', (Join-Path $taskRoot 'app')
)
if ($Portable) {
    # The download archive provides compression; leave assemblies uncompressed so they do not need private decompression buffers at runtime.
    $arguments += @(
        '-p:PublishSingleFile=true',
        '-p:IncludeNativeLibrariesForSelfExtract=true',
        '-p:EnableCompressionInSingleFile=false'
    )
}
dotnet @arguments
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
