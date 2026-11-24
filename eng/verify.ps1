[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

function Invoke-DotNet {
    & dotnet @args
    if ($LASTEXITCODE -ne 0) { throw "dotnet failed with exit code $LASTEXITCODE." }
}

Push-Location (Split-Path $PSScriptRoot -Parent)
try {
    Invoke-DotNet restore KingbaseConnector.slnx
    Invoke-DotNet build KingbaseConnector.slnx -c Release --no-restore
    Invoke-DotNet test KingbaseConnector.slnx -c Release --no-build --no-restore
    Invoke-DotNet pack src/KingbaseConnector -c Release --no-build --no-restore -o artifacts/packages

    # A fresh package cache prevents a previous build of the same version from satisfying these checks.
    $consumerCache = Join-Path (Get-Location) "artifacts/consumers/$([Guid]::NewGuid().ToString('N'))"
    $consumer = 'tests/KingbaseConnector.ConsumerTests/KingbaseConnector.ConsumerTests.csproj'
    $assets = @(
        @('netstandard2.0', 'net8.0', '.NETStandard,Version=v2.0'),
        @('netstandard2.1', 'net8.0', '.NETStandard,Version=v2.1'),
        @('net8.0', 'net8.0', '.NETCoreApp,Version=v8.0'),
        @('net9.0', 'net9.0', '.NETCoreApp,Version=v9.0'),
        @('net10.0', 'net10.0', '.NETCoreApp,Version=v10.0')
    )
    foreach ($entry in $assets) {
        $buildProperties = @("-p:TargetFramework=$($entry[1])", "-p:KingbaseAsset=$($entry[0])",
            "-p:BaseIntermediateOutputPath=$consumerCache/$($entry[0])/obj/",
            "-p:OutputPath=$consumerCache/$($entry[0])/bin/",
            "-p:RestorePackagesPath=$consumerCache/packages")
        Invoke-DotNet restore $consumer @buildProperties "-p:RestoreAdditionalProjectSources=$(Join-Path (Get-Location) 'artifacts/packages')"
        Invoke-DotNet build $consumer -c Release --no-restore @buildProperties
        $consumerDll = Join-Path $consumerCache "$($entry[0])/bin/KingbaseConnector.ConsumerTests.dll"
        Invoke-DotNet $consumerDll $entry[2]
    }
}
finally { Pop-Location }
