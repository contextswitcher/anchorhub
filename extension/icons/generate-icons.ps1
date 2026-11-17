# Regenerate the colour-coded icons used by AnchorHub.
# Palette mixes Paul Tol's bright + vibrant qualitative schemes (all are
# colour-blind safe).
#
# Shape encodes the lookup phase (and helps colour-blind users on top of
# the hue cue):
#   - filled circle (full size) — a result is in (green = exact match,
#     teal = related, orange = daemon disconnected)
#   - hollow red ring (full size) — lookup completed, no OneNote match
#   - small filled gray square (centred inside the same 16×16 canvas) —
#     lookup in progress; the smaller-than-result footprint means the
#     "round thing enlarges" from the same centre when the result lands
#
# Run from repo root:
#   powershell -ExecutionPolicy Bypass -File extension\icons\generate-icons.ps1

Add-Type -AssemblyName System.Drawing

$here = Split-Path -Parent $MyInvocation.MyCommand.Definition

# Filled circle, anti-aliased, 1px inset so the AA edge has room inside
# the canvas.
function Make-FilledCircle {
    param([int]$Size, [string]$HexColor, [string]$OutPath)

    $bmp = New-Object System.Drawing.Bitmap($Size, $Size)
    $g   = [System.Drawing.Graphics]::FromImage($bmp)
    $g.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::AntiAlias
    $g.Clear([System.Drawing.Color]::Transparent)

    $color = [System.Drawing.ColorTranslator]::FromHtml($HexColor)
    $brush = New-Object System.Drawing.SolidBrush($color)

    $inset = 1
    $rect  = New-Object System.Drawing.RectangleF(
        [float]$inset, [float]$inset,
        [float]($Size - 2*$inset), [float]($Size - 2*$inset))
    $g.FillEllipse($brush, $rect)

    $brush.Dispose()
    $g.Dispose()
    $bmp.Save($OutPath, [System.Drawing.Imaging.ImageFormat]::Png)
    $bmp.Dispose()
}

# Hollow ring (transparent fill, coloured outline). Border width scales
# with canvas size so the stroke stays visually proportional at 32/48.
function Make-HollowRing {
    param([int]$Size, [string]$HexColor, [string]$OutPath)

    $bmp = New-Object System.Drawing.Bitmap($Size, $Size)
    $g   = [System.Drawing.Graphics]::FromImage($bmp)
    $g.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::AntiAlias
    $g.Clear([System.Drawing.Color]::Transparent)

    $color  = [System.Drawing.ColorTranslator]::FromHtml($HexColor)
    # ~12.5% of canvas width: 2px at 16, 4px at 32, 6px at 48.
    $stroke = [Math]::Max(2, [int]($Size / 8))
    $pen    = New-Object System.Drawing.Pen($color, [float]$stroke)

    # Inset by half the stroke width so the outer edge of the stroke
    # touches the canvas edge (matches the filled-circle outer radius).
    $inset = $stroke / 2.0
    $rect  = New-Object System.Drawing.RectangleF(
        [float]$inset, [float]$inset,
        [float]($Size - 2*$inset), [float]($Size - 2*$inset))
    $g.DrawEllipse($pen, $rect)

    $pen.Dispose()
    $g.Dispose()
    $bmp.Save($OutPath, [System.Drawing.Imaging.ImageFormat]::Png)
    $bmp.Dispose()
}

# Centred filled square at a fraction of the canvas. Used for the
# "searching" state at 50% so results visually "enlarge" around the
# same centre when the lookup completes.
function Make-CenteredSquare {
    param([int]$Size, [string]$HexColor, [float]$Ratio, [string]$OutPath)

    $bmp = New-Object System.Drawing.Bitmap($Size, $Size)
    $g   = [System.Drawing.Graphics]::FromImage($bmp)
    $g.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::AntiAlias
    $g.Clear([System.Drawing.Color]::Transparent)

    $color = [System.Drawing.ColorTranslator]::FromHtml($HexColor)
    $brush = New-Object System.Drawing.SolidBrush($color)

    $inner  = [int]($Size * $Ratio)
    $offset = [int](($Size - $inner) / 2)
    $rect   = New-Object System.Drawing.RectangleF(
        [float]$offset, [float]$offset,
        [float]$inner,  [float]$inner)
    $g.FillRectangle($brush, $rect)

    $brush.Dispose()
    $g.Dispose()
    $bmp.Save($OutPath, [System.Drawing.Imaging.ImageFormat]::Png)
    $bmp.Dispose()
}

# Ship's anchor drawn in a 100x100 design box, scaled to the canvas.
# $Ratio shrinks it around the centre (searching state), $Stroke is the
# line width in design units (thin = "no match").
function Make-Anchor {
    param([int]$Size, [string]$HexColor, [float]$Ratio, [float]$Stroke, [string]$OutPath)

    $bmp = New-Object System.Drawing.Bitmap($Size, $Size)
    $g   = [System.Drawing.Graphics]::FromImage($bmp)
    $g.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::AntiAlias
    $g.Clear([System.Drawing.Color]::Transparent)

    $scale = ($Size - 2) / 100.0 * $Ratio          # 1px margin at full size
    $off   = ($Size - 100 * $scale) / 2.0
    $g.TranslateTransform([float]$off, [float]$off)
    $g.ScaleTransform([float]$scale, [float]$scale)

    $color = [System.Drawing.ColorTranslator]::FromHtml($HexColor)
    $pen   = New-Object System.Drawing.Pen($color, $Stroke)
    $pen.StartCap = [System.Drawing.Drawing2D.LineCap]::Round
    $pen.EndCap   = [System.Drawing.Drawing2D.LineCap]::Round
    $brush = New-Object System.Drawing.SolidBrush($color)

    $g.DrawEllipse($pen, 41.0, 6.0, 18.0, 18.0)        # ring
    $g.DrawLine($pen, 50, 24, 50, 88)                  # shaft
    $g.DrawLine($pen, 34, 38, 66, 38)                  # stock
    $g.DrawArc($pen, 18.0, 24.0, 64.0, 64.0, 0.0, 180.0)  # arms
    $left  = [System.Drawing.PointF[]]@(
        (New-Object System.Drawing.PointF(18, 42)),
        (New-Object System.Drawing.PointF(6, 58)),
        (New-Object System.Drawing.PointF(30, 58)))
    $right = [System.Drawing.PointF[]]@(
        (New-Object System.Drawing.PointF(82, 42)),
        (New-Object System.Drawing.PointF(70, 58)),
        (New-Object System.Drawing.PointF(94, 58)))
    $g.FillPolygon($brush, $left)                      # flukes
    $g.FillPolygon($brush, $right)

    $brush.Dispose(); $pen.Dispose(); $g.Dispose()
    $bmp.Save($OutPath, [System.Drawing.Imaging.ImageFormat]::Png)
    $bmp.Dispose()
}

# Result states — full-size solid anchor, colour = kind of result.
$filledStates = @{
    'green'  = '#44BB99'  # exact match — Tol light mint
    'host'   = '#AAAA00'  # host-only / related — Tol light olive
    'yellow' = '#EE7733'  # not connected — Tol vibrant orange
}
foreach ($name in $filledStates.Keys) {
    foreach ($size in 16, 32, 48) {
        $out = Join-Path $here "$name-$size.png"
        Make-Anchor -Size $size -HexColor $filledStates[$name] -Ratio 1.0 -Stroke 13 -OutPath $out
        Write-Host "wrote $out  anchor  $($filledStates[$name])"
    }
}

# No match — thin gray anchor (same gray as searching; weight and size
# distinguish the lookup phase). Keeps link-dense pages quiet.
foreach ($size in 16, 32, 48) {
    $out = Join-Path $here "notfound-$size.png"
    Make-Anchor -Size $size -HexColor '#BBBBBB' -Ratio 1.0 -Stroke 8 -OutPath $out
    Write-Host "wrote $out  thin anchor  #BBBBBB"
}

# Searching — small solid gray anchor, centred; enlarges when the result lands.
foreach ($size in 16, 32, 48) {
    $out = Join-Path $here "default-$size.png"
    Make-Anchor -Size $size -HexColor '#BBBBBB' -Ratio 0.6 -Stroke 14 -OutPath $out
    Write-Host "wrote $out  small anchor  #BBBBBB"
}

# Brand icon (extension manager, about:addons) — Tol bright blue.
foreach ($size in 16, 32, 48, 96, 128) {
    $out = Join-Path $here "anchor-$size.png"
    Make-Anchor -Size $size -HexColor '#4477AA' -Ratio 1.0 -Stroke 13 -OutPath $out
    Write-Host "wrote $out  brand anchor  #4477AA"
}
