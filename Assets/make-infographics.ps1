# Infographic generator — the house pattern from AutoCommand II: a plain
# script under Assets\ that renders the repo's diagrams to PNG, so they can
# be regenerated (retinted, re-worded, re-fonted) without any design tool.
# No dependencies beyond GDI+ (System.Drawing), which ships with Windows.
#
#   .\Assets\make-infographics.ps1     -> Assets\infographic-*.png
#
# The rain panes are DRAWN (not screenshotted): nothing from anyone's
# desktop ever lands in these images.

$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Drawing

$outDir = Split-Path -Parent $MyInvocation.MyCommand.Path
$rng = New-Object System.Random(1337)   # deterministic art

# ---- palette (mirrors matrix_rain.json defaults) -------------------------
$bg      = [System.Drawing.Color]::FromArgb(255, 5, 8, 7)
$panel   = [System.Drawing.Color]::FromArgb(255, 10, 18, 12)
$dim     = [System.Drawing.Color]::FromArgb(255, 14, 92, 31)      # ambient/backdrop
$trail   = [System.Drawing.Color]::FromArgb(255, 0, 255, 65)      # falling wake
$head    = [System.Drawing.Color]::FromArgb(255, 232, 255, 232)   # lead glyph
$blocked = [System.Drawing.Color]::FromArgb(255, 255, 51, 51)
$blockedHead = [System.Drawing.Color]::FromArgb(255, 255, 221, 221)
$label   = [System.Drawing.Color]::FromArgb(255, 127, 255, 159)
$text    = [System.Drawing.Color]::FromArgb(255, 168, 190, 174)
$muted   = [System.Drawing.Color]::FromArgb(255, 96, 118, 100)

$consolas  = 'Consolas'
$gothic    = 'MS Gothic'   # half-width katakana coverage

$katakana = 0xFF66..0xFF9D | ForEach-Object { [char]$_ }
# PS 5.1 has no char ranges - build the latin/digit set from code points
$alphabet = New-Object System.Collections.Generic.List[char]
foreach ($r in @(@(65, 90), @(97, 122), @(48, 57))) {
    for ($i = $r[0]; $i -le $r[1]; $i++) { $alphabet.Add([char]$i) }
}

function New-Canvas([int]$w, [int]$h) {
    $bmp = New-Object System.Drawing.Bitmap($w, $h)
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $g.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::AntiAlias
    $g.TextRenderingHint = [System.Drawing.Text.TextRenderingHint]::AntiAliasGridFit
    $g.Clear($bg)
    return @{ Bmp = $bmp; G = $g; W = $w; H = $h }
}

function Brush($color, [double]$alpha = 1.0) {
    New-Object System.Drawing.SolidBrush([System.Drawing.Color]::FromArgb([int](255 * $alpha), $color))
}
function Pen($color, [double]$alpha = 1.0, [float]$width = 1.0) {
    New-Object System.Drawing.Pen((Brush $color $alpha), $width)
}
function Txt($g, [string]$s, [string]$font, [float]$size, $color, [float]$x, [float]$y, [double]$alpha = 1.0, [string]$style = 'Regular') {
    $f = New-Object System.Drawing.Font($font, $size, [System.Drawing.FontStyle]::$style)
    $g.DrawString($s, $f, (Brush $color $alpha), $x, $y)
    $f.Dispose()
}
function GlowTxt($g, [string]$s, [string]$font, [float]$size, $color, [float]$x, [float]$y, [string]$style = 'Bold') {
    # soft bloom: a ring of low-alpha copies behind the crisp glyph
    $f = New-Object System.Drawing.Font($font, $size, [System.Drawing.FontStyle]::$style)
    foreach ($d in @(-3,-2,-1,0,1,2,3)) {
        $g.DrawString($s, $f, (Brush $color 0.10), $x + $d, $y)
        $g.DrawString($s, $f, (Brush $color 0.10), $x, $y + $d)
    }
    $g.DrawString($s, $f, (Brush $color 1.0), $x, $y)
    $f.Dispose()
}
function Box($g, [float]$x, [float]$y, [float]$w, [float]$h, $borderColor, [double]$alpha = 1.0) {
    $g.FillRectangle((Brush $panel 0.85), $x, $y, $w, $h)
    $g.DrawRectangle((Pen $borderColor $alpha 1.6), $x, $y, $w, $h)
}

# A mock rain pane: PID band, frozen backdrop, leads with log-fading wakes,
# one blocked column. Used by the overview sheet.
function Draw-RainPane($g, [float]$x, [float]$y, [float]$w, [float]$h) {
    $g.FillRectangle((Brush $panel 0.9), $x, $y, $w, $h)
    $g.DrawRectangle((Pen $trail 0.8 1.4), $x, $y, $w, $h)

    $cellW = 26.0; $cellH = 30.0
    $bandH = 96.0
    $cols = [int](($w - 16) / $cellW)
    $rows = [int](($h - $bandH - 12) / $cellH)

    # frozen backdrop
    for ($c = 0; $c -lt $cols; $c++) {
        for ($r = 0; $r -lt $rows; $r++) {
            if ($rng.NextDouble() -lt 0.88) {
                $ch = $katakana[$rng.Next($katakana.Count)]
                $f = New-Object System.Drawing.Font($gothic, 14)
                $g.DrawString([string]$ch, $f, (Brush $dim 1.0),
                    $x + 8 + $c * $cellW, $y + $bandH + 4 + $r * $cellH)
                $f.Dispose()
            }
        }
    }

    # PID label band (vertical digits, last column red = blocked)
    for ($c = 0; $c -lt $cols; $c++) {
        $pidDigits = (1000 + $rng.Next(9000)).ToString().ToCharArray()
        $isRed = ($c -eq $cols - 3)
        for ($i = 0; $i -lt $pidDigits.Length; $i++) {
            Txt $g ([string]$pidDigits[$i]) $consolas 11 ($(if ($isRed) { $blocked } else { $label })) `
                ($x + 8 + $c * $cellW + 5) ($y + 6 + $i * 16)
        }
    }

    # active streams: lead + logarithmically fading wake above it
    $wakeAlphas = @(0.92, 0.90, 0.87, 0.84, 0.78, 0.68, 0.50, 0.28)  # log hold, then plunge
    $streams = @(
        @{ Col = 1;      HeadRow = $rows - 6; Red = $false },
        @{ Col = 4;      HeadRow = $rows - 11; Red = $false },
        @{ Col = $cols - 3; HeadRow = $rows - 4; Red = $true },   # blocked, still trying
        @{ Col = $cols - 1; HeadRow = $rows - 9; Red = $false }
    )
    foreach ($s in $streams) {
        $gx = $x + 8 + $s.Col * $cellW
        $gy = $y + $bandH + 4 + $s.HeadRow * $cellH
        $cWake  = $(if ($s.Red) { $blocked } else { $trail })
        $cHead  = $(if ($s.Red) { $blockedHead } else { $head })
        for ($i = 0; $i -lt $wakeAlphas.Count; $i++) {
            $ch = $katakana[$rng.Next($katakana.Count)]
            $f = New-Object System.Drawing.Font($gothic, 15, [System.Drawing.FontStyle]::Bold)
            $g.DrawString([string]$ch, $f, (Brush $cWake $wakeAlphas[$i]),
                $gx, $gy - ($i + 1) * $cellH)
            $f.Dispose()
        }
        $leadCh = $katakana[$rng.Next($katakana.Count)]
        GlowTxt $g ([string]$leadCh) $gothic 17 $cHead $gx ($gy - 2)
    }
}

# ===========================================================================
# 1) Overview — "the rain is your machine"
# ===========================================================================
$c = New-Canvas 1560 980
$g = $c.G

Txt $g 'AUTOCOMMAND MATRIX' $consolas 44 $head 60 34 -style 'Bold'
Txt $g 'the process monitor as rain  -  holiday edition' $consolas 20 $trail 62 96
Txt $g 'one column per process PID  |  every falling glyph a transmit  |  blocked PIDs rain red' $consolas 15 $muted 62 132

Draw-RainPane $g 60 190 560 760

# edge-anchor annotation arrow along the pane's right edge
$edgePen = Pen $label 0.85 2.2
$edgePen.EndCap = [System.Drawing.Drawing2D.LineCap]::ArrowAnchor
$g.DrawLine($edgePen, 636, 900, 636, 210)
Txt $g 'busiest' $consolas 13 $label 646 880
Txt $g 'PID' $consolas 13 $label 646 896
Txt $g 'quiet' $consolas 13 $muted 646 214
Txt $g 'new ids slide in from this edge' $consolas 12 $muted 560 168

# annotation blocks on the right
$notes = @(
    @{ Title = 'a column appears'; Body = 'a process PID opened a tracked connection (Sysmon event, or the boot baseline snapshot)'; Y = 200; Red = $false },
    @{ Title = 'letters fall'; Body = 'that PID transmitted packets - one falling glyph per transmit, capped at fallRowsPerSecond'; Y = 330; Red = $false },
    @{ Title = 'the lead blooms, the wake fades in log'; Body = 'only the lead glyph is brightest (subtle zLift, faint shadow, soft glow); behind it, brightness holds high then plunges - and resolves into the frozen backdrop glyph beneath'; Y = 460; Red = $false },
    @{ Title = 'a column rains red'; Body = 'the firewall inventory says that PID is blocked - and each fresh connection attempt still rolls a red glyph'; Y = 620; Red = $true },
    @{ Title = 'the backdrop never changes on its own'; Body = 'the ambient field is frozen; the only edits are real ones - characters the falling wakes etch in as they pass'; Y = 750; Red = $false }
)
foreach ($n in $notes) {
    $accent = $(if ($n.Red) { $blocked } else { $trail })
    $g.DrawRectangle((Pen $accent 0.9 1.6), 740, $n.Y, 740, 100)
    $g.FillRectangle((Brush $panel 0.55), 741, $n.Y + 1, 739, 99)
    Txt $g $n.Title $consolas 18 $accent 764 ($n.Y + 14) -style 'Bold'
    $f = New-Object System.Drawing.Font($consolas, 13)
    $g.DrawString($n.Body, $f, (Brush $text 1.0), (New-Object System.Drawing.RectangleF(764, ($n.Y + 46), 696, 48)))
    $f.Dispose()
    $lead = Pen $accent 0.5 1.2
    $lead.DashStyle = [System.Drawing.Drawing2D.DashStyle]::Dash
    $g.DrawLine($lead, 740, $n.Y + 50, 630, $n.Y + 50)
}

$c.Bmp.Save((Join-Path $outDir 'infographic-overview.png'), [System.Drawing.Imaging.ImageFormat]::Png)
$c.G.Dispose(); $c.Bmp.Dispose()

# ===========================================================================
# 2) Architecture — "one engine, many faces"
# ===========================================================================
$c = New-Canvas 1360 820
$g = $c.G

Txt $g 'ONE ENGINE, MANY FACES' $consolas 36 $head 60 34 -style 'Bold'
Txt $g 'the overlay reuses the UI-free core libraries of AutoCommand II through a pinned git submodule' $consolas 16 $trail 62 92

# engine box (top)
$g.DrawRectangle((Pen $trail 1.0 2.0), 240, 150, 880, 190)
$g.FillRectangle((Brush $panel 0.7), 241, 151, 879, 189)
Txt $g 'AutoCommand II core libraries' $consolas 22 $head 270 172 -style 'Bold'
Txt $g 'github.com/dparksports/autocommand-ii   (external\  -  git submodule, pinned)' $consolas 13 $muted 272 208
Txt $g 'SysmonWatcherService' $consolas 15 $label 276 250
Txt $g 'RawSocketSnifferService' $consolas 15 $label 276 282
Txt $g 'FirewallService' $consolas 15 $label 276 314
Txt $g 'new/changed rows (push)' $consolas 12 $text 640 250
Txt $g 'per-packet TX/RX counters' $consolas 12 $text 640 282
Txt $g 'blocked IP/app inventory' $consolas 12 $text 640 314

# faces row (bottom)
$faces = @(
    @{ X = 100;  Title = 'AutoCommand (WPF)';  Body = 'the main app - 14 tabs,'; Body2 = 'DataGrid rows' },
    @{ X = 530;  Title = 'AutoCommand Matrix'; Body = 'this repo - the rain,'; Body2 = 'same engines, new face'; Hot = $true },
    @{ X = 960;  Title = 'AgentHost (MCP)';    Body = 'headless AI access,'; Body2 = 'same cores' }
)
foreach ($fbox in $faces) {
    $accent = $(if ($fbox.Hot) { $head } else { $trail })
    $g.DrawRectangle((Pen $accent 1.0 2.0), $fbox.X, 500, 300, 150)
    $g.FillRectangle((Brush $panel 0.7), $fbox.X + 1, 501, 299, 149)
    Txt $g $fbox.Title $consolas 17 $accent ($fbox.X + 22) 522 -style 'Bold'
    Txt $g $fbox.Body $consolas 13 $text ($fbox.X + 22) 556
    Txt $g $fbox.Body2 $consolas 13 $text ($fbox.X + 22) 578
}

# arrows engine -> faces
foreach ($fx in @(250, 680, 1110)) {
    $p = Pen $trail 0.8 2.0
    $p.EndCap = [System.Drawing.Drawing2D.LineCap]::ArrowAnchor
    $g.DrawLine($p, 680, 340, $fx, 496)
}
Txt $g 'plain ProjectReferences - the same consumption pattern the WPF app and AgentHost use' $consolas 13 $muted 300 420
Txt $g 'bump the submodule to pick up core changes' $consolas 13 $muted 300 444

$c.Bmp.Save((Join-Path $outDir 'infographic-architecture.png'), [System.Drawing.Imaging.ImageFormat]::Png)
$c.G.Dispose(); $c.Bmp.Dispose()

# ===========================================================================
# 3) Config — "make it yours"
# ===========================================================================
$c = New-Canvas 1420 940
$g = $c.G

Txt $g 'MAKE IT YOURS' $consolas 36 $head 60 34 -style 'Bold'
Txt $g 'matrix_rain.json beside the exe - edit it while it rains; every knob hot-applies on save' $consolas 16 $trail 62 92

# JSON block (left)
$g.DrawRectangle((Pen $trail 0.8 1.4), 60, 150, 560, 740)
$g.FillRectangle((Brush $panel 0.85), 61, 151, 559, 739)
$json = @(
    '{',
    '  "dock": "right",      // left|right|full|manual',
    '  "widthPx": 280,       "opacity": 0.92,',
    '  "fontFamily": "Consolas",',
    '  "fontFile": "",       // or your own .ttf/.otf',
    '  "fontSizePx": 18,     "charset": "katakana-latin",',
    '  "colors": {',
    '    "head": "#E8FFE8",  "trail": "#00FF41",',
    '    "blocked": "#FF3333",',
    '    "label": "#7FFF9F", "ambient": "#0E5C1F"',
    '  },',
    '  "lettersPerTransmit": 1.0,',
    '  "fallRowsPerSecond": 25,   // twice slower',
    '  "wakeFadeSeconds": 2.0,    // log fade',
    '  "zLift": 1.06,       // subtle elevation',
    '  "bloom": true,       "ambient": true,',
    '  "ambientDriftSeconds": 0,  // frozen',
    '  "maxColumns": 64, "showLabels": true',
    '}'
)
for ($i = 0; $i -lt $json.Count; $i++) {
    Txt $g $json[$i] $consolas 14 $text 84 (170 + $i * 30)
}

# right side: swatches + effects
$rx = 680
Txt $g 'colors' $consolas 17 $head $rx 160 -style 'Bold'
$swatches = @(
    @{ C = $head;     N = 'head #E8FFE8' },
    @{ C = $trail;    N = 'trail #00FF41' },
    @{ C = $blocked;  N = 'blocked #FF3333' },
    @{ C = $label;    N = 'label #7FFF9F' },
    @{ C = $dim;      N = 'ambient #0E5C1F' }
)
for ($i = 0; $i -lt $swatches.Count; $i++) {
    $g.FillRectangle((Brush $swatches[$i].C 1.0), ($rx + ($i % 3) * 240), (196 + [int][Math]::Floor($i / 3) * 66), 40, 40)
    Txt $g $swatches[$i].N $consolas 12 $text ($rx + ($i % 3) * 240 + 48) (208 + [int][Math]::Floor($i / 3) * 66)
}

Txt $g 'zLift - how far the lead floats' $consolas 17 $head $rx 350 -style 'Bold'
$lifts = @(
    @{ S = 22; N = '1.0 flat' },
    @{ S = 24; N = '1.06 subtle (default)' },
    @{ S = 34; N = '1.5 dramatic' }
)
for ($i = 0; $i -lt $lifts.Count; $i++) {
    $x0 = $rx + $i * 240
    GlowTxt $g ([string]$alphabet[$rng.Next($alphabet.Count)]) $gothic ([float]$lifts[$i].S) $head ($x0 + 8) 388
    Txt $g $lifts[$i].N $consolas 12 $text $x0 440
}

Txt $g 'wakeFadeSeconds - the log tail' $consolas 17 $head $rx 500 -style 'Bold'
$trailsCfg = @('0.5s', '2s (default)', '5s')
for ($i = 0; $i -lt 3; $i++) {
    $x0 = $rx + $i * 240
    $depth = @(4, 7, 11)[$i]
    for ($r = 0; $r -lt $depth; $r++) {
        $a = [Math]::Max(0.08, 0.92 * [Math]::Log(1 + 8 * (1 - $r / $depth)) / [Math]::Log(9))
        $ch = $katakana[$rng.Next($katakana.Count)]
        $f = New-Object System.Drawing.Font($gothic, 14, [System.Drawing.FontStyle]::Bold)
        $g.DrawString([string]$ch, $f, (Brush $trail $a), $x0 + 14, (538 + $r * 24))
        $f.Dispose()
    }
    Txt $g $trailsCfg[$i] $consolas 12 $text $x0 (540 + $depth * 24 + 6)
}

Txt $g 'dock - where it lives' $consolas 17 $head $rx 780 -style 'Bold'
$docks = @('left', 'right (default)', 'full')
for ($i = 0; $i -lt 3; $i++) {
    $x0 = $rx + $i * 240
    $g.DrawRectangle((Pen $text 0.8 1.4), $x0, 818, 130, 84)
    if ($i -eq 0)   { $g.FillRectangle((Brush $trail 0.55), $x0 + 2, 820, 22, 80) }
    if ($i -eq 1)   { $g.FillRectangle((Brush $trail 0.55), $x0 + 106, 820, 22, 80) }
    if ($i -eq 2)   { $g.FillRectangle((Brush $trail 0.30), $x0 + 2, 820, 126, 80) }
    Txt $g $docks[$i] $consolas 12 $text $x0 908
}

$c.Bmp.Save((Join-Path $outDir 'infographic-config.png'), [System.Drawing.Imaging.ImageFormat]::Png)
$c.G.Dispose(); $c.Bmp.Dispose()

Write-Output "generated: $outDir\infographic-overview.png, infographic-architecture.png, infographic-config.png"
