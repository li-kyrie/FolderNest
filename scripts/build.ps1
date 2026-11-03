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
    & $sdk run --project tests/FolderNest.Desktop.Tests -c Release --no-build
    if($LASTEXITCODE -ne 0){throw 'Desktop component tests failed'}
    if($Publish){
        & $sdk publish src/FolderNest.Desktop -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:DebugType=None -o artifacts/portable
        if($LASTEXITCODE -ne 0){throw 'Publish failed'}
        $smokeData=[System.IO.Directory]::CreateTempSubdirectory('FolderNest-smoke-').FullName
        try {
            $smoke=Start-Process -FilePath (Join-Path $repoRoot 'artifacts/portable/FolderNest.exe') -ArgumentList @('--smoke-test','--data',('"'+$smokeData+'"')) -WindowStyle Hidden -PassThru
            if(-not $smoke.WaitForExit(30000)){$smoke.Kill();throw 'Portable smoke test timed out'}
            if($smoke.ExitCode -ne 0){throw ('Portable smoke test failed: '+$smoke.ExitCode)}
        } finally {
            $resolvedSmoke=[System.IO.Path]::GetFullPath($smokeData)
            $resolvedTemp=[System.IO.Path]::GetFullPath([System.IO.Path]::GetTempPath())
            if($resolvedSmoke.StartsWith($resolvedTemp,[StringComparison]::OrdinalIgnoreCase) -and (Split-Path $resolvedSmoke -Leaf).StartsWith('FolderNest-smoke-')){
                Remove-Item -LiteralPath $resolvedSmoke -Recurse -Force
            }
        }
        Copy-Item README.md,CHANGELOG.md,LICENSE -Destination artifacts/portable
        Copy-Item docs -Destination artifacts/portable -Recurse -Force
        # Explicit allowlist: a locally launched application may have personal data beside its EXE.
        # Never include local databases, logs, configuration or SDK files in the distribution.
        [xml]$properties=Get-Content Directory.Build.props
        $version=$properties.Project.PropertyGroup.Version
        $running=Get-Process FolderNest -ErrorAction SilentlyContinue | Where-Object {$_.Path -eq (Join-Path $repoRoot 'FolderNest.exe')}
        if($running){throw 'Close the root FolderNest.exe before updating the portable application'}
        Copy-Item artifacts/portable/FolderNest.exe -Destination (Join-Path $repoRoot 'FolderNest.exe') -Force
        $package=Join-Path (Split-Path $repoRoot -Parent) "FolderNest-Portable-v$version-win-x64.zip"
        Compress-Archive -LiteralPath @('artifacts/portable/FolderNest.exe','artifacts/portable/README.md','artifacts/portable/CHANGELOG.md','artifacts/portable/LICENSE','artifacts/portable/docs') -DestinationPath $package -Force
        Write-Host "Portable EXE: $repoRoot\FolderNest.exe"
        Write-Host "Portable ZIP: $package"
    }
} finally { Pop-Location }
