$ErrorActionPreference = 'Stop'
$projectPath = $PSScriptRoot
$sourcesPath = Join-Path $projectPath 'src'
$exePath = Join-Path $projectPath 'AnbyPet.exe'
$spritePath = Join-Path $projectPath 'anby-desktop-pet-v1.png'
$petFullPath = Join-Path $projectPath 'anby-burger-halfbody-v2-mirror.png'
$petEmptyPath = Join-Path $projectPath 'assets\anby-burger-empty.png'
$capturePath = Join-Path $projectPath 'third_party\PresentMon\PresentMon-2.6.0-x64.exe'
$captureNotice = Join-Path $projectPath 'third_party\PresentMon\LICENSE.txt'
$captureDigest = 'B2A706BC6AD475749E3B7E3409263AA1E6906D45BDCF993F6DBC0F660188F1AF'
$compilerPath = Join-Path $env:windir 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'
if (-not (Test-Path -LiteralPath $compilerPath -PathType Leaf)) {
    throw 'The Windows .NET Framework x64 C# compiler was not found.'
}
if ((Get-FileHash -LiteralPath $capturePath).Hash -ne $captureDigest) {
    throw 'PresentMon does not match the pinned official 2.6.0 release.'
}

Add-Type -AssemblyName System.Drawing
$iconPath = Join-Path $projectPath ('.anby-icon-' + [guid]::NewGuid().ToString('N') + '.ico')
$sprite = [System.Drawing.Bitmap]::new($spritePath)
try {
    $layers = @(
        foreach ($size in @(16, 24, 32, 48, 64)) {
            $picture = [System.Drawing.Bitmap]::new($size, $size, [System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
            $graphics = [System.Drawing.Graphics]::FromImage($picture)
            $stream = [System.IO.MemoryStream]::new()
            try {
                $graphics.InterpolationMode = [System.Drawing.Drawing2D.InterpolationMode]::HighQualityBicubic
                $rectangle = [System.Drawing.Rectangle]::new(0, 0, $size, $size)
                $graphics.DrawImage($sprite, $rectangle, 278, 6, 478, 478, [System.Drawing.GraphicsUnit]::Pixel)
                $picture.Save($stream, [System.Drawing.Imaging.ImageFormat]::Png)
                [pscustomobject]@{ Size = $size; Bytes = $stream.ToArray() }
            } finally {
                $stream.Dispose()
                $graphics.Dispose()
                $picture.Dispose()
            }
        }
    )
    $writer = [System.IO.BinaryWriter]::new([System.IO.File]::Open($iconPath, [System.IO.FileMode]::CreateNew))
    try {
        $writer.Write([uint16]0)
        $writer.Write([uint16]1)
        $writer.Write([uint16]$layers.Count)
        $offset = 6 + 16 * $layers.Count
        foreach ($layer in $layers) {
            $writer.Write([byte]$layer.Size)
            $writer.Write([byte]$layer.Size)
            $writer.Write([byte]0)
            $writer.Write([byte]0)
            $writer.Write([uint16]1)
            $writer.Write([uint16]32)
            $writer.Write([uint32]$layer.Bytes.Length)
            $writer.Write([uint32]$offset)
            $offset += $layer.Bytes.Length
        }
        foreach ($layer in $layers) { $writer.Write([byte[]]$layer.Bytes) }
    } finally { $writer.Dispose() }

    $manifestPath = Join-Path $sourcesPath 'app.manifest'
    $sources = @(Get-ChildItem -LiteralPath $sourcesPath -Filter '*.cs' -File | ForEach-Object FullName)
    & $compilerPath /nologo /target:winexe /platform:x64 /optimize+ /warnaserror+ /codepage:65001 `
        "/out:$exePath" "/win32manifest:$manifestPath" "/win32icon:$iconPath" `
        "/resource:$petFullPath,AnbyPet.PetFull.png" "/resource:$petEmptyPath,AnbyPet.PetEmpty.png" "/resource:$iconPath,AnbyPet.Tray.ico" `
        "/resource:$capturePath,AnbyPet.PresentMon.exe" "/resource:$captureNotice,AnbyPet.PresentMon.LICENSE.txt" `
        /reference:System.dll /reference:System.Core.dll /reference:System.Drawing.dll `
        /reference:System.Windows.Forms.dll /reference:System.Web.Extensions.dll /reference:System.Management.dll @sources
    if ($LASTEXITCODE -ne 0) { throw "Compilation failed with code $LASTEXITCODE." }
    Get-Item -LiteralPath $exePath | Select-Object FullName,Length
} finally {
    $sprite.Dispose()
    if (Test-Path -LiteralPath $iconPath -PathType Leaf) { Remove-Item -LiteralPath $iconPath }
}
