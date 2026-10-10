param([switch]$Publish)
$ErrorActionPreference='Stop'
$repoRoot=Split-Path -Parent $PSScriptRoot
$localSdk=Join-Path $repoRoot '.tools\dotnet\dotnet.exe'
$sdk=if(Test-Path -LiteralPath $localSdk){$localSdk}else{'dotnet'}
Push-Location $repoRoot
try {
    & $sdk build FolderNest.slnx -c Release
    if($LASTEXITCODE -ne 0){throw 'Build failed'}
    & $sdk run --project tests/FolderNest.Tests -c Release --no-build
    if($LASTEXITCODE -ne 0){throw 'Behavioral tests failed'}
    if($Publish){
        & $sdk publish src/FolderNest.Desktop -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:DebugType=None -o artifacts/portable
        if($LASTEXITCODE -ne 0){throw 'Publish failed'}
        Copy-Item README.md,LICENSE -Destination artifacts/portable
        Copy-Item docs -Destination artifacts/portable -Recurse -Force
        # Explicit allowlist: a locally launched preview may have data-v2 beside its EXE.
        # Never include local databases, logs, configuration or SDK files in the distribution.
        Compress-Archive -LiteralPath @('artifacts/portable/FolderNest.exe','artifacts/portable/README.md','artifacts/portable/LICENSE','artifacts/portable/docs') -DestinationPath 'D:\FolderNest-Framework-Preview-win-x64.zip' -Force
    }
} finally { Pop-Location }
