# Downloads eval datasets into data/datasets/ (gitignored — no dataset files are ever
# committed; see README licensing section). Also stages a 10-image smoke set.
#
# SROIE (primary, English): community mirror of the ICDAR 2019 competition data.
# CORD (secondary, line items): W2 — v2 lives on Hugging Face as parquet
#   (naver-clova-ix/cord-v2); wiring it up belongs to the eval milestone.

$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
$datasets = Join-Path $root 'data/datasets'
New-Item -ItemType Directory -Force $datasets | Out-Null

$sroie = Join-Path $datasets 'sroie'
if (-not (Test-Path $sroie)) {
    git clone --depth 1 https://github.com/zzzDavid/ICDAR-2019-SROIE $sroie
} else {
    Write-Host "sroie: already present, skipping clone"
}

# Smoke set: first 10 receipt images, flat-copied for `docextract extract data/smoke`.
$smoke = Join-Path $root 'data/smoke'
New-Item -ItemType Directory -Force $smoke | Out-Null
$images = Get-ChildItem $sroie -Recurse -Include *.jpg, *.jpeg, *.png -File |
    Sort-Object FullName | Select-Object -First 10
if ($images.Count -eq 0) { throw "no images found under $sroie — mirror layout changed?" }
$images | Copy-Item -Destination $smoke -Force
Write-Host "smoke set: $($images.Count) images -> $smoke"
