<#
.SYNOPSIS
  Renders the BWOS icon from tools\icon\icon.html with headless Microsoft Edge, then writes
  assets\bwos.ico (16–256 px), assets\bwos.png and ui\img\bwos.png. Needs only Edge and
  Windows. Run after changing the design; the output is committed.
#>
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Drawing
$root = Split-Path -Parent $PSScriptRoot
$assets = Join-Path $root 'assets'
New-Item -ItemType Directory -Force $assets | Out-Null

$edge = @("${env:ProgramFiles(x86)}\Microsoft\Edge\Application\msedge.exe", "$env:ProgramFiles\Microsoft\Edge\Application\msedge.exe") |
    Where-Object { Test-Path $_ } | Select-Object -First 1
if (-not $edge) { throw 'Microsoft Edge not found' }

$tmp = Join-Path ([IO.Path]::GetTempPath()) ('bwos-icon-' + [Guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory $tmp | Out-Null
$page = 'file:///' + ((Join-Path $PSScriptRoot 'icon\icon.html') -replace '\\', '/')

function Render([string]$query, [string]$out) {
    $args = @('--headless=new', '--disable-gpu', '--hide-scrollbars', '--default-background-color=00000000',
        '--force-device-scale-factor=1', '--window-size=1024,1024', "--user-data-dir=`"$tmp\profile`"",
        '--virtual-time-budget=3000', "--screenshot=`"$out`"", "`"$page$query`"")
    Start-Process $edge -ArgumentList $args -Wait -WindowStyle Hidden
    if (-not (Test-Path $out)) { throw "Edge didn't render $out" }
}

function Resize([System.Drawing.Image]$src, [int]$size) {
    $bmp = New-Object System.Drawing.Bitmap $size, $size
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $g.InterpolationMode = 'HighQualityBicubic'
    $g.PixelOffsetMode = 'HighQuality'
    $g.CompositingQuality = 'HighQuality'
    $g.SmoothingMode = 'AntiAlias'
    $g.Clear([System.Drawing.Color]::Transparent)
    $g.DrawImage($src, 0, 0, $size, $size)
    $g.Dispose()
    return $bmp
}

function Get-DibEntry([System.Drawing.Bitmap]$bmp) {
    $s = $bmp.Width
    $rect = New-Object System.Drawing.Rectangle 0, 0, $s, $s
    $data = $bmp.LockBits($rect, 'ReadOnly', [System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
    $pixels = New-Object byte[] ($s * $s * 4)
    [Runtime.InteropServices.Marshal]::Copy($data.Scan0, $pixels, 0, $pixels.Length)
    $bmp.UnlockBits($data)
    $ms = New-Object System.IO.MemoryStream
    $w = New-Object System.IO.BinaryWriter $ms
    # BITMAPINFOHEADER: height counts the colour image and the AND mask.
    $w.Write([UInt32]40); $w.Write([Int32]$s); $w.Write([Int32]($s * 2)); $w.Write([UInt16]1); $w.Write([UInt16]32)
    $w.Write([UInt32]0); $w.Write([UInt32]0); $w.Write([Int32]0); $w.Write([Int32]0); $w.Write([UInt32]0); $w.Write([UInt32]0)
    for ($y = $s - 1; $y -ge 0; $y--) { $w.Write($pixels, $y * $s * 4, $s * 4) }   # bottom-up BGRA
    $maskRow = [int]([Math]::Ceiling($s / 32.0) * 4)
    $w.Write((New-Object byte[] ($maskRow * $s)))                                  # AND mask unused (alpha)
    $w.Flush()
    return , $ms.ToArray()   # the comma keeps it a byte[] (PowerShell would unroll it)
}

try {
    Render '' "$tmp\full.png"
    Render '?small' "$tmp\small.png"
    $full = [System.Drawing.Image]::FromFile("$tmp\full.png")
    $small = [System.Drawing.Image]::FromFile("$tmp\small.png")

    # Tiny sizes use the simplified drawing so the ring and play stay crisp.
    # Entries up to 128 px are classic 32-bit DIBs (read by every loader); 256 px is PNG.
    $sizes = 16, 20, 24, 32, 40, 48, 64, 128, 256
    $pngs = @()
    foreach ($s in $sizes) {
        $bmp = Resize $(if ($s -le 32) { $small } else { $full }) $s
        if ($s -ge 256) {
            $ms = New-Object System.IO.MemoryStream
            $bmp.Save($ms, [System.Drawing.Imaging.ImageFormat]::Png)
            $pngs += , $ms.ToArray()
        } else {
            $pngs += , (Get-DibEntry $bmp)
        }
        $bmp.Dispose()
    }
    foreach ($target in (Join-Path $assets 'bwos.png'), (Join-Path $root 'ui\img\bwos.png')) {
        $bmp = Resize $full 512
        $bmp.Save($target, [System.Drawing.Imaging.ImageFormat]::Png)
        $bmp.Dispose()
    }
    $full.Dispose(); $small.Dispose()

    # ICO container with PNG-compressed entries (supported since Windows Vista)
    $out = New-Object System.IO.MemoryStream
    $w = New-Object System.IO.BinaryWriter $out
    $w.Write([UInt16]0); $w.Write([UInt16]1); $w.Write([UInt16]$sizes.Count)
    $offset = 6 + 16 * $sizes.Count
    for ($i = 0; $i -lt $sizes.Count; $i++) {
        $s = $sizes[$i]; $len = $pngs[$i].Length
        $w.Write([byte]($(if ($s -ge 256) { 0 } else { $s })))
        $w.Write([byte]($(if ($s -ge 256) { 0 } else { $s })))
        $w.Write([byte]0); $w.Write([byte]0)
        $w.Write([UInt16]1); $w.Write([UInt16]32)
        $w.Write([UInt32]$len); $w.Write([UInt32]$offset)
        $offset += $len
    }
    foreach ($p in $pngs) { $w.Write($p) }
    [System.IO.File]::WriteAllBytes((Join-Path $assets 'bwos.ico'), $out.ToArray())
    Write-Host "Wrote $assets\bwos.ico, bwos.png and ui\img\bwos.png"
}
finally {
    Get-CimInstance Win32_Process -Filter "Name='msedge.exe'" | Where-Object { $_.CommandLine -like "*$tmp*" } |
        ForEach-Object { try { Stop-Process -Id $_.ProcessId -Force } catch {} }
    Start-Sleep -Milliseconds 300
    try { [IO.Directory]::Delete($tmp, $true) } catch {}
}
