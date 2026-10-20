param([string]$DotnetPath = 'dotnet')

$ErrorActionPreference = 'Stop'
$projectRoot = Split-Path -Parent $PSScriptRoot
$project = Join-Path $projectRoot 'FintechAutoSiteConfiguration.FunctionApp.csproj'
$publishDirectory = Join-Path $projectRoot 'bin\Release\net10.0\publish'
$artifactDirectory = Join-Path $projectRoot 'artifacts'
$package = Join-Path $artifactDirectory 'fintech-autosite-functionapp.zip'

if ($DotnetPath -eq 'dotnet' -and !(Get-Command dotnet -ErrorAction SilentlyContinue)) {
    $bundledDotnet = Join-Path $env:USERPROFILE '.dotnet10\dotnet.exe'
    if (Test-Path -LiteralPath $bundledDotnet) { $DotnetPath = $bundledDotnet }
}

& $DotnetPath publish $project --configuration Release --output $publishDirectory --nologo
if ($LASTEXITCODE -ne 0) { throw 'Release publish failed.' }

foreach ($requiredFile in @('host.json', 'functions.metadata', 'worker.config.json', 'extensions.json', '.azurefunctions/Microsoft.Azure.Functions.Worker.Extensions.dll')) {
    if (!(Test-Path -LiteralPath (Join-Path $publishDirectory $requiredFile))) {
        throw "Publish output is missing $requiredFile."
    }
}
$metadata = Get-Content -LiteralPath (Join-Path $publishDirectory 'functions.metadata') -Raw | ConvertFrom-Json
$functions = @($metadata)
$binding = @($functions[0].bindings | Where-Object type -eq 'timerTrigger')
if ($functions.Count -ne 1 -or $functions[0].name -ne 'AutoSiteConfigurationTimer' -or
    $binding.Count -ne 1 -or $binding[0].schedule -ne '0 */5 * * * *' -or
    $binding[0].runOnStartup -ne $false -or $binding[0].useMonitor -ne $true) {
    throw 'Published function metadata does not match the five-minute timer contract.'
}
$privateFiles = @(Get-ChildItem -LiteralPath $publishDirectory -Recurse -File |
    Where-Object { $_.Name -like 'local.settings*.json' -or $_.Name -like 'azure-app-settings*.json' -or $_.Name -like 'appsettings*.json' })
if ($privateFiles.Count -gt 0) { throw 'Settings files must not be included in the deployment package.' }

New-Item -ItemType Directory -Path $artifactDirectory -Force | Out-Null
if (Test-Path -LiteralPath $package) { Remove-Item -LiteralPath $package }
Add-Type -AssemblyName System.IO.Compression.FileSystem
# ZipFile includes hidden .azurefunctions host dependencies, with host.json at the archive root.
[IO.Compression.ZipFile]::CreateFromDirectory($publishDirectory, $package)
Write-Output "Deployment package: $package"
