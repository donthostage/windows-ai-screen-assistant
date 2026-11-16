[CmdletBinding()]
param(
    [Parameter(Mandatory)] [string] $PublishedDirectory,
    [Parameter(Mandatory)] [string] $OutputPath,
    [Parameter(Mandatory)] [ValidatePattern('^\d+\.\d+\.\d+\.\d+$')] [string] $Version,
    [string] $Publisher = 'CN=ScreenAssistant Development',
    [string] $CertificateThumbprint,
    [string] $TimestampUrl
)

$ErrorActionPreference = 'Stop'
$source = (Resolve-Path -LiteralPath $PublishedDirectory).Path
$output = $ExecutionContext.SessionState.Path.GetUnresolvedProviderPathFromPSPath($OutputPath)
$fileName = [IO.Path]::GetFileName($output)
if ([IO.Path]::GetExtension($output) -ne '.msix' -or (Test-Path -LiteralPath $output)) {
    throw 'OutputPath must name a new .msix file.'
}
foreach ($file in @('ScreenAssistant.exe', 'ScreenAssistant.dll', 'coreclr.dll')) {
    if (!(Test-Path -LiteralPath (Join-Path $source $file) -PathType Leaf)) {
        throw "Missing $file in the self-contained publish directory."
    }
}

$sdkRoot = Join-Path ${env:ProgramFiles(x86)} 'Windows Kits/10/bin'
$makeAppx = Get-ChildItem -Path "$sdkRoot/*/x64/makeappx.exe" -File |
    Sort-Object { [version]$_.Directory.Parent.Name } -Descending | Select-Object -First 1
if (!$makeAppx) { throw 'Install Windows SDK with the MSIX Packaging Tools.' }

$temp = [IO.Directory]::CreateTempSubdirectory('screenassistant-msix-').FullName
$stage = New-Item -ItemType Directory -Path (Join-Path $temp 'package')
$package = Join-Path $temp $fileName
try {
    Get-ChildItem -LiteralPath $source | Copy-Item -Destination $stage.FullName -Recurse
    $assets = New-Item -ItemType Directory -Path (Join-Path $stage.FullName 'Assets')
    Add-Type -AssemblyName System.Drawing
    $icon = [Drawing.Icon]::new((Join-Path $PSScriptRoot '../src/ScreenAssistant/app.ico'), 256, 256)
    try {
        $bitmap = $icon.ToBitmap()
        try {
            foreach ($size in @(44, 50, 150)) {
                $logo = [Drawing.Bitmap]::new($bitmap, $size, $size)
                try { $logo.Save((Join-Path $assets.FullName "Logo$size.png"), [Drawing.Imaging.ImageFormat]::Png) }
                finally { $logo.Dispose() }
            }
        } finally { $bitmap.Dispose() }
    } finally { $icon.Dispose() }

    $publisherXml = [Security.SecurityElement]::Escape($Publisher)
    @"
<?xml version="1.0" encoding="utf-8"?>
<Package xmlns="http://schemas.microsoft.com/appx/manifest/foundation/windows10"
         xmlns:uap="http://schemas.microsoft.com/appx/manifest/uap/windows10"
         xmlns:rescap="http://schemas.microsoft.com/appx/manifest/foundation/windows10/restrictedcapabilities"
         IgnorableNamespaces="uap rescap">
  <Identity Name="ScreenAssistant.Desktop" Publisher="$publisherXml" Version="$Version" ProcessorArchitecture="x64" />
  <Properties>
    <DisplayName>Screen Assistant</DisplayName>
    <PublisherDisplayName>Screen Assistant</PublisherDisplayName>
    <Logo>Assets\Logo50.png</Logo>
  </Properties>
  <Dependencies>
    <TargetDeviceFamily Name="Windows.Desktop" MinVersion="10.0.19041.0" MaxVersionTested="10.0.26100.0" />
  </Dependencies>
  <Resources><Resource Language="ru-ru" /></Resources>
  <Applications>
    <Application Id="ScreenAssistant" Executable="ScreenAssistant.exe" EntryPoint="Windows.FullTrustApplication">
      <uap:VisualElements DisplayName="Screen Assistant" Description="Screen Assistant"
          BackgroundColor="transparent" Square150x150Logo="Assets\Logo150.png" Square44x44Logo="Assets\Logo44.png" />
    </Application>
  </Applications>
  <Capabilities><rescap:Capability Name="runFullTrust" /></Capabilities>
</Package>
"@ | Set-Content -LiteralPath (Join-Path $stage.FullName 'AppxManifest.xml') -Encoding utf8

    & $makeAppx.FullName pack /d $stage.FullName /p $package
    if ($LASTEXITCODE -ne 0) { throw 'MakeAppx failed.' }

    if ($CertificateThumbprint) {
        $signTool = Join-Path $makeAppx.Directory.FullName 'signtool.exe'
        $sign = @('sign', '/fd', 'SHA256', '/sha1', $CertificateThumbprint)
        if ($TimestampUrl) { $sign += @('/tr', $TimestampUrl, '/td', 'SHA256') }
        & $signTool @sign $package
        if ($LASTEXITCODE -ne 0) { throw 'SignTool failed.' }
    }

    [IO.File]::Move($package, $output)
    Get-Item -LiteralPath $output
}
finally {
    $resolvedTemp = (Resolve-Path -LiteralPath $temp).Path
    if ($resolvedTemp -eq $temp -and (Split-Path -Leaf $temp).StartsWith('screenassistant-msix-')) {
        Remove-Item -LiteralPath $resolvedTemp -Recurse -Force
    }
}
