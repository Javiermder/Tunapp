$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.IO.Compression.FileSystem
Add-Type -AssemblyName System.IO.Compression
$projectRoot = Split-Path -Parent $PSScriptRoot
$delivery = Join-Path $projectRoot 'artifacts\entrega'
$temporaryZip = Join-Path $delivery ('Tunapp-iOS-' + [Guid]::NewGuid().ToString('N') + '.zip')
$archive = [IO.Compression.ZipFile]::Open($temporaryZip, [IO.Compression.ZipArchiveMode]::Create)
try {
    $files = @(Get-ChildItem -LiteralPath $projectRoot -File | Where-Object { $_.Extension -in '.cs', '.xaml', '.csproj' })
    foreach ($folder in @('Platforms', 'Resources', 'src\Tunapp.Core', 'src\Tunapp.Presentation')) {
        $files += Get-ChildItem -LiteralPath (Join-Path $projectRoot $folder) -Recurse -File |
            Where-Object { $_.FullName -notmatch '\\(bin|obj)\\' }
    }
    foreach ($file in $files) {
        $relative = $file.FullName.Substring($projectRoot.Length + 1).Replace('\', '/')
        [IO.Compression.ZipFileExtensions]::CreateEntryFromFile($archive, $file.FullName, "Tunapp/$relative") | Out-Null
    }
    [IO.Compression.ZipFileExtensions]::CreateEntryFromFile($archive, (Join-Path $PSScriptRoot 'publicar-ios.sh'), 'Tunapp/publicar-ios.sh') | Out-Null
    [IO.Compression.ZipFileExtensions]::CreateEntryFromFile($archive, (Join-Path $delivery 'LEEME-iOS.txt'), 'Tunapp/LEEME-iOS.txt') | Out-Null
} finally { $archive.Dispose() }
Move-Item -LiteralPath $temporaryZip -Destination (Join-Path $delivery 'Tunapp-iOS-fuentes.zip') -Force
Write-Output 'Paquete iOS actualizado: incluye Core, Presentation y recursos; no es un IPA instalable.'
