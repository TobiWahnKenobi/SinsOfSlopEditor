# Packages Sins of Slop Editor for distribution: a self-contained single-file build for 64-bit Windows
# (no .NET install needed) plus a readme, zipped into dist\SinsOfSlopEditor-<version>-win-x64.zip.
#
#   pwsh build\publish.ps1

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$project = Join-Path $root 'src\SinsEditor.App\SinsEditor.App.csproj'
$version = ([xml](Get-Content $project)).Project.PropertyGroup.Version | Where-Object { $_ } | Select-Object -First 1
$name = "SinsOfSlopEditor-$version-win-x64"
$out = Join-Path $root "dist\$name"
$zip = Join-Path $root "dist\$name.zip"

if (Test-Path $out) { Remove-Item $out -Recurse -Force }
if (Test-Path $zip) { Remove-Item $zip -Force }

dotnet publish $project -c Release -r win-x64 --self-contained true -o $out `
    -p:PublishSingleFile=true `
    -p:IncludeNativeLibrariesForSelfExtract=true `
    -p:EnableCompressionInSingleFile=true `
    -p:DebugType=none
if ($LASTEXITCODE -ne 0) { throw "dotnet publish failed." }

(Get-Content (Join-Path $PSScriptRoot 'README.txt') -Raw) `
    -replace '\{VERSION\}', $version -replace '\{YEAR\}', (Get-Date).Year |
    Set-Content (Join-Path $out 'README.txt') -Encoding utf8

# Licences: GPL for the editor, the logos' terms, and the third-party notices with their full texts.
foreach ($file in 'LICENSE', 'LICENSE-ASSETS.md', 'THIRD-PARTY-NOTICES.md') { Copy-Item (Join-Path $root $file) $out }
Copy-Item (Join-Path $root 'licenses') (Join-Path $out 'licenses') -Recurse

Compress-Archive -Path (Join-Path $out '*') -DestinationPath $zip
Get-ChildItem $out | Select-Object Name, @{ n = 'MB'; e = { [math]::Round($_.Length / 1MB, 1) } } | Format-Table -AutoSize
"Package: $zip ($([math]::Round((Get-Item $zip).Length / 1MB, 1)) MB)"
