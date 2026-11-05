param([switch]$Publish,[switch]$PackageOnly)
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
        [xml]$properties=Get-Content Directory.Build.props
        $version=$properties.Project.PropertyGroup.Version
        $publishDirectory=Join-Path $repoRoot "artifacts\release-v$version"
        $publishExe=Join-Path $publishDirectory 'FolderNest.exe'
        $publishingProcess=Get-Process FolderNest -ErrorAction SilentlyContinue | Where-Object {$_.Path -eq $publishExe}
        if($publishingProcess){throw 'Close the staged release executable before publishing over it'}
        & $sdk publish src/FolderNest.Desktop -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:DebugType=None -o $publishDirectory
        if($LASTEXITCODE -ne 0){throw 'Publish failed'}
        $smokeData=[System.IO.Directory]::CreateTempSubdirectory('FolderNest-smoke-').FullName
        try {
            $smoke=Start-Process -FilePath $publishExe -ArgumentList @('--smoke-test','--data',('"'+$smokeData+'"')) -WindowStyle Hidden -PassThru
            if(-not $smoke.WaitForExit(30000)){$smoke.Kill();throw 'Portable smoke test timed out'}
            if($smoke.ExitCode -ne 0){throw ('Portable smoke test failed: '+$smoke.ExitCode)}
        } finally {
            $resolvedSmoke=[System.IO.Path]::GetFullPath($smokeData)
            $resolvedTemp=[System.IO.Path]::GetFullPath([System.IO.Path]::GetTempPath())
            if($resolvedSmoke.StartsWith($resolvedTemp,[StringComparison]::OrdinalIgnoreCase) -and (Split-Path $resolvedSmoke -Leaf).StartsWith('FolderNest-smoke-')){
                Remove-Item -LiteralPath $resolvedSmoke -Recurse -Force
            }
        }
        Copy-Item README.md,CHANGELOG.md,LICENSE -Destination $publishDirectory
        Copy-Item docs -Destination $publishDirectory -Recurse -Force
        Copy-Item examples -Destination $publishDirectory -Recurse -Force
        # Explicit allowlist: a locally launched application may have personal data beside its EXE.
        # Never include local databases, logs, configuration or SDK files in the distribution.
        if(-not $PackageOnly){
            $product=Join-Path $repoRoot '产品\FolderNest'
            $running=Get-Process FolderNest -ErrorAction SilentlyContinue | Where-Object {$_.Path -eq (Join-Path $product 'FolderNest.exe')}
            if($running){throw 'Close the product FolderNest.exe before updating it'}
            New-Item -ItemType Directory -Path $product -Force | Out-Null
            Copy-Item -LiteralPath @($publishExe,(Join-Path $publishDirectory 'README.md'),(Join-Path $publishDirectory 'CHANGELOG.md'),(Join-Path $publishDirectory 'LICENSE'),(Join-Path $publishDirectory 'docs'),(Join-Path $publishDirectory 'examples')) -Destination $product -Recurse -Force
            Write-Host "Portable EXE: $product\FolderNest.exe"
        }
        $packageDirectory=Join-Path $repoRoot '发布包'
        New-Item -ItemType Directory -Path $packageDirectory -Force | Out-Null
        $package=Join-Path $packageDirectory "FolderNest-Portable-v$version-win-x64.zip"
        Compress-Archive -LiteralPath @($publishExe,(Join-Path $publishDirectory 'README.md'),(Join-Path $publishDirectory 'CHANGELOG.md'),(Join-Path $publishDirectory 'LICENSE'),(Join-Path $publishDirectory 'docs'),(Join-Path $publishDirectory 'examples')) -DestinationPath $package -Force
        $hash=(Get-FileHash -LiteralPath $package -Algorithm SHA256).Hash.ToLowerInvariant()
        [System.IO.File]::WriteAllText($package+'.sha256',$hash+'  '+[System.IO.Path]::GetFileName($package)+[Environment]::NewLine)
        Write-Host "Staged EXE: $publishExe"
        Write-Host "Portable ZIP: $package"
    }
} finally { Pop-Location }
