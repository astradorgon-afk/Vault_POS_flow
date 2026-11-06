<#
.SYNOPSIS
    Publishes the Windows MAUI register and builds a per-user x64 installer.
.EXAMPLE
    .\scripts\build-desktop-installer.ps1
.EXAMPLE
    .\scripts\build-desktop-installer.ps1 -WebView2Installer C:\Downloads\MicrosoftEdgeWebview2Setup.exe
#>
[CmdletBinding()]
param(
    [string]$InnoCompiler,
    [string]$WebView2Installer
)

$ErrorActionPreference = 'Stop'
$root = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
$project = Join-Path $root 'src\Pos.Client\Pos.Client.csproj'
$installerScript = Join-Path $root 'build\installer\VaultFlow.iss'
$output = Join-Path $root 'artifacts\installer'
$publish = Join-Path $output 'publish'

[xml]$projectXml = Get-Content -LiteralPath $project
$version = [string]$projectXml.Project.PropertyGroup.ApplicationDisplayVersion
if ($version -notmatch '^\d+\.\d+(\.\d+){0,2}$') {
    throw "ApplicationDisplayVersion must be a numeric version: '$version'."
}

if (-not $InnoCompiler) {
    $command = Get-Command ISCC.exe -ErrorAction SilentlyContinue
    if ($command) { $InnoCompiler = $command.Source }
}
if (-not $InnoCompiler) {
    foreach ($candidate in @(
        "$env:LOCALAPPDATA\Programs\Inno Setup 6\ISCC.exe",
        "${env:ProgramFiles(x86)}\Inno Setup 6\ISCC.exe",
        "$env:ProgramFiles\Inno Setup 6\ISCC.exe"
    )) {
        if (Test-Path -LiteralPath $candidate -PathType Leaf) {
            $InnoCompiler = $candidate
            break
        }
    }
}
if (-not $InnoCompiler -or -not (Test-Path -LiteralPath $InnoCompiler -PathType Leaf)) {
    throw 'Inno Setup 6 compiler (ISCC.exe) was not found. Install Inno Setup 6 or pass -InnoCompiler.'
}

New-Item -ItemType Directory -Path $output -Force | Out-Null
if (-not $WebView2Installer) {
    $WebView2Installer = Join-Path $output 'MicrosoftEdgeWebview2Setup.exe'
    if (-not (Test-Path -LiteralPath $WebView2Installer -PathType Leaf)) {
        Write-Host 'Downloading the Microsoft Edge WebView2 Evergreen bootstrapper...'
        Invoke-WebRequest -UseBasicParsing -Uri 'https://go.microsoft.com/fwlink/p/?LinkId=2124703' -OutFile $WebView2Installer
    }
}
if (-not (Test-Path -LiteralPath $WebView2Installer -PathType Leaf)) {
    throw "WebView2 installer not found: $WebView2Installer"
}
$signature = Get-AuthenticodeSignature -LiteralPath $WebView2Installer
if ($signature.Status -ne 'Valid' -or $signature.SignerCertificate.Subject -notmatch '(^|,\s*)O=Microsoft Corporation(,|$)') {
    throw "WebView2 installer must have a valid Microsoft signature: $WebView2Installer"
}

Write-Host "Publishing VaultFlow POS $version for Windows x64..."
if (Test-Path -LiteralPath $publish) {
    $resolvedPublish = (Resolve-Path -LiteralPath $publish).Path
    if ($resolvedPublish -ne (Join-Path $root 'artifacts\installer\publish')) {
        throw "Refusing to remove unexpected publish directory: $resolvedPublish"
    }
    Remove-Item -LiteralPath $resolvedPublish -Recurse -Force
}
& dotnet publish $project --configuration Release --framework net10.0-windows10.0.19041.0 `
    --runtime win-x64 --self-contained true --output $publish `
    -p:RuntimeIdentifierOverride=win-x64 -p:WindowsPackageType=None `
    -p:WindowsAppSDKSelfContained=true -p:UseMonoRuntime=false
if ($LASTEXITCODE -ne 0) { throw "dotnet publish failed with exit code $LASTEXITCODE." }
if (-not (Test-Path -LiteralPath (Join-Path $publish 'Pos.Client.exe') -PathType Leaf)) {
    throw 'The Windows publish did not produce Pos.Client.exe.'
}

Write-Host 'Compiling the installer...'
& $InnoCompiler "/DAppVersion=$version" "/DPublishDir=$publish" "/DOutputDir=$output" `
    "/DWebView2Installer=$WebView2Installer" $installerScript
if ($LASTEXITCODE -ne 0) { throw "Inno Setup failed with exit code $LASTEXITCODE." }

$setup = Join-Path $output "VaultFlow-POS-Setup-$version-win-x64.exe"
if (-not (Test-Path -LiteralPath $setup -PathType Leaf)) {
    throw "Installer was not created at $setup"
}
Write-Host "Installer: $setup"
