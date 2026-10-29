# Local CI — the house release path from AutoCommand II: everything the
# GitHub Actions workflow does, run from this machine instead (no runner,
# no queue; use it when you want the release the moment you tag).
#
#   .\release.ps1              build, verify, zip, checksum, gh release
#
# Not elevated on purpose: publish lands in .\publish (bin\Release may be
# locked by a running instance), and gh carries its own auth.

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $MyInvocation.MyCommand.Path
Set-Location $root

# ---- version (single source of truth: <Version> in the csproj) ----------
$csproj = Get-Content AutoCommand.Matrix.csproj -Raw
if ($csproj -notmatch '<Version>([\d.]+)</Version>') { throw "no <Version> in AutoCommand.Matrix.csproj" }
$ver = $Matches[1]
$tag = "v$ver"
Write-Host "== AutoCommand.Matrix $ver ($tag)"

# ---- publish (self-contained win-x64, into .\publish) -------------------
if (Test-Path publish) { Remove-Item publish -Recurse -Force }
dotnet publish AutoCommand.Matrix.csproj -c Release -r win-x64 --self-contained -o publish
if ($LASTEXITCODE -ne 0) { throw "publish failed" }

# ---- the tag must equal the shipped version -----------------------------
# (CI appends +<commit sha> to the informational version; compare the
# semver part only.)
$actual = (Get-Item publish/AutoCommand.Matrix.exe).VersionInfo.ProductVersion.Split('+')[0]
if ($actual -ne $ver) { throw "csproj says $ver but the exe says $actual - bump <Version>?" }
Write-Host "== version verified: $actual"

# ---- package + checksums ------------------------------------------------
$zip = "AutoCommand-Matrix-$tag-win-x64.zip"
Compress-Archive -Path publish/* -DestinationPath $zip -Force
$hash = (Get-FileHash $zip -Algorithm SHA256).Hash
"$hash  $zip" | Out-File SHA256SUMS.txt -Encoding ascii
Write-Host "== packaged $zip ($([math]::Round((Get-Item $zip).Length / 1MB, 1)) MB)"

# ---- release -------------------------------------------------------------
if (-not (Test-Path "release_notes_$ver.md")) { throw "no release_notes_$ver.md - write the notes first" }
gh release create $tag $zip SHA256SUMS.txt --title "AutoCommand Matrix $ver" --notes-file "release_notes_$ver.md"
if ($LASTEXITCODE -ne 0) { throw "gh release create failed (tag pushed? exists already?)" }
Write-Host "== released: https://github.com/dparksports/autocommand-matrix/releases/tag/$tag"
