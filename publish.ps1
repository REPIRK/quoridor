# Builds a single self-contained Quoridor.exe that runs without .NET installed.
# Output: publish\Quoridor.exe

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $MyInvocation.MyCommand.Path

dotnet publish (Join-Path $root 'src\Quoridor.App\Quoridor.App.csproj') `
    -c Release `
    -r win-x64 `
    --self-contained true `
    -p:PublishSingleFile=true `
    -p:IncludeNativeLibrariesForSelfExtract=true `
    -p:EnableCompressionInSingleFile=true `
    -p:DebugType=none `
    -o (Join-Path $root 'publish')

$exe = Join-Path $root 'publish\Quoridor.exe'
if (Test-Path $exe) {
    $size = [math]::Round((Get-Item $exe).Length / 1MB, 1)
    Write-Host ""
    Write-Host "Ready: $exe ($size MB)"
}
