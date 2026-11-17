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

# Filled-circle states (match results). Colours from Paul Tol's light
# qualitative scheme — mint and olive read as visibly different hues
# (bright green-cyan vs muted yellow-green) where the previous bright
# green + teal pair was hard to tell apart at 16px. State names
# ('green', 'host', 'yellow') are historical file labels and don't
# track the rendered hue.
$filledStates = @{
    'green'  = '#44BB99'  # exact match — Tol light mint
    'host'   = '#AAAA00'  # host-only / related — Tol light olive
    'yellow' = '#EE7733'  # not connected — Tol vibrant orange (unchanged)
}
foreach ($name in $filledStates.Keys) {
    foreach ($size in 16, 32, 48) {
        $out = Join-Path $here "$name-$size.png"
        Make-FilledCircle -Size $size -HexColor $filledStates[$name] -OutPath $out
        Write-Host "wrote $out  filled circle  $($filledStates[$name])"
    }
}

# Hollow ring — no match. Same gray as the searching state — shape
# alone (hollow ring vs small filled square vs full filled circle)
# distinguishes the lookup phase. Avoids a "red sea" of negatives on
# link-dense pages where most links aren't anchored.
$ringColour = '#BBBBBB'  # Tol grey
foreach ($size in 16, 32, 48) {
    $out = Join-Path $here "notfound-$size.png"
    Make-HollowRing -Size $size -HexColor $ringColour -OutPath $out
    Write-Host "wrote $out  hollow ring    $ringColour"
}

# Searching — small centred gray square. Smaller than the result icons
# so the "round thing enlarges" once the lookup completes.
$searchColour = '#BBBBBB'  # Tol grey — neutral / in-progress
$searchRatio  = 0.5         # square edge = 50% of canvas → ~8px at 16, 16px at 32
foreach ($size in 16, 32, 48) {
    $out = Join-Path $here "default-$size.png"
    Make-CenteredSquare -Size $size -HexColor $searchColour -Ratio $searchRatio -OutPath $out
    Write-Host "wrote $out  small square   $searchColour  ratio=$searchRatio"
}
