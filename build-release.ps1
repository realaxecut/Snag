# Builds Snag's installer, and with -Publish turns it into a GitHub release that Snag's
# built-in updater (Settings > Snag) will offer to everyone.
#
#   .\build-release.ps1                                  # dist\Snag-Setup-<version>.exe + SHA256SUMS.txt
#   .\build-release.ps1 -Publish -Notes "- Fixed X`n- Added Y"
#
# The version comes from <Version> in Snag\Snag.csproj: bump it before publishing a new release.
# Needs the .NET 10 SDK and Inno Setup 6.7+ (winget install JRSoftware.InnoSetup); -Publish also needs
# git and the GitHub CLI, signed in (gh auth login).
param(
    [switch]$Publish,
    [string]$Notes = ''
)
$ErrorActionPreference = 'Stop'
Set-Location $PSScriptRoot
$repo = 'realaxecut/Snag'

[xml]$project = Get-Content 'Snag\Snag.csproj'
$version = @($project.Project.PropertyGroup.Version | Where-Object { $_ })[0]
if ($version -notmatch '^\d+\.\d+\.\d+$') { throw "Snag.csproj needs a <Version> like 1.2.3 (found '$version')." }

$iscc = @(
    "$env:LOCALAPPDATA\Programs\Inno Setup 6\ISCC.exe",
    "${env:ProgramFiles(x86)}\Inno Setup 6\ISCC.exe",
    "$env:ProgramFiles\Inno Setup 6\ISCC.exe"
) | Where-Object { Test-Path $_ } | Select-Object -First 1
if (-not $iscc) { throw 'Inno Setup 6 is needed: winget install JRSoftware.InnoSetup' }

if ($Publish) {
    if (git status --porcelain) { throw 'Commit your changes before publishing a release.' }
    if (gh release view "v$version" --repo $repo 2>$null) { throw "v$version is already released. Bump <Version> in Snag\Snag.csproj first." }
}

Write-Host "Building Snag $version..."
# Self-contained, so the installed app doesn't need the .NET runtime on the PC. Built in a short temp
# path because Inno Setup can't read files whose full path is over 260 characters.
$appDir = Join-Path $env:TEMP 'snag-release\app'
if (Test-Path $appDir) { Remove-Item $appDir -Recurse -Force }
dotnet publish Snag -c Release -r win-x64 --self-contained true -p:PublishSingleFile=false -p:DebugType=none -o $appDir
if ($LASTEXITCODE) { throw 'dotnet publish failed.' }

New-Item -ItemType Directory -Force dist | Out-Null
$setup = "Snag-Setup-$version.exe"
& $iscc /Qp "/DAppVersion=$version" "/DAppDir=$appDir" installer\Snag.iss
if ($LASTEXITCODE) { throw 'Inno Setup failed.' }

# Snag's updater refuses an installer whose SHA-256 isn't listed here.
$hash = (Get-FileHash "dist\$setup" -Algorithm SHA256).Hash.ToLowerInvariant()
Set-Content dist\SHA256SUMS.txt "$hash  $setup" -Encoding ascii
Write-Host "Built dist\$setup ($([math]::Round((Get-Item "dist\$setup").Length / 1MB, 1)) MB)"

if ($Publish) {
    git push origin HEAD
    if ($LASTEXITCODE) { throw 'git push failed.' }
    if (-not $Notes) { $Notes = "Snag $version" }
    gh release create "v$version" "dist\$setup" dist\SHA256SUMS.txt --repo $repo --target (git rev-parse HEAD) --title "Snag $version" --notes $Notes
    if ($LASTEXITCODE) { throw 'Creating the GitHub release failed.' }
    Write-Host "Released: https://github.com/$repo/releases/tag/v$version"
}
