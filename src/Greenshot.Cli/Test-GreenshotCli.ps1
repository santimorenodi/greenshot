<#
.SYNOPSIS
  Checks every option of greenshot-cli against test windows (a small WinForms window and two Notepads).

.DESCRIPTION
  Windows PowerShell 5.1 or PowerShell 7 on Windows, run it from a normal desktop session. It opens a few windows
  (test forms named GSTEST-*, two Notepads), takes screenshots of them with greenshot-cli, checks the results (sizes,
  pixel colors, JSON, exit codes, foreground window) and closes only the windows/processes it started.
  While it runs, do not touch the keyboard and mouse: the focus checks would report your changes.

  Nothing here needs Greenshot to be installed or running. Exit code = number of failed checks.

.PARAMETER Exe
  Path to greenshot-cli.exe, default is the Release build next to this script.
#>
param(
    [string]$Exe = (Join-Path $PSScriptRoot 'bin\Release\net480\greenshot-cli.exe')
)

$ErrorActionPreference = 'Stop'
if (-not (Test-Path $Exe)) { throw "greenshot-cli.exe not found at $Exe, build it first: dotnet build src\Greenshot.Cli -c Release" }
$Exe = (Resolve-Path $Exe).Path

Add-Type -AssemblyName System.Drawing
Add-Type -AssemblyName System.Windows.Forms
Add-Type -AssemblyName Microsoft.VisualBasic
Add-Type @'
using System;
using System.Runtime.InteropServices;
public static class GsFocus {
    private delegate void WinEventProc(IntPtr hook, uint evt, IntPtr hwnd, int idObject, int idChild, uint thread, uint time);
    [DllImport("user32.dll")] private static extern IntPtr SetWinEventHook(uint min, uint max, IntPtr mod, WinEventProc proc, uint pid, uint tid, uint flags);
    [DllImport("user32.dll")] private static extern bool UnhookWinEvent(IntPtr hook);
    private static IntPtr hook;
    private static WinEventProc proc = OnEvent;
    public static int Changes;
    public static string Last = "";
    private static void OnEvent(IntPtr h, uint e, IntPtr hwnd, int o, int c, uint t, uint tm) { Changes++; Last = "0x" + hwnd.ToInt64().ToString("x"); }
    // EVENT_SYSTEM_FOREGROUND = 3, WINEVENT_OUTOFCONTEXT = 0 (needs a message loop on this thread)
    public static void Start() { Changes = 0; Last = ""; hook = SetWinEventHook(3, 3, IntPtr.Zero, proc, 0, 0, 0); }
    public static void Stop() { if (hook != IntPtr.Zero) { UnhookWinEvent(hook); hook = IntPtr.Zero; } }
}
public static class GsWin {
    [DllImport("user32.dll")] public static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] public static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);
    [DllImport("user32.dll")] public static extern bool IsIconic(IntPtr hWnd);
    [DllImport("user32.dll")] public static extern bool PostMessage(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);
}
'@

$work = Join-Path ([IO.Path]::GetTempPath()) ("gscli-test-" + [guid]::NewGuid().ToString('N').Substring(0, 8))
New-Item -ItemType Directory -Path $work | Out-Null
$script:passed = 0
$script:failed = 0
$script:skipped = 0
$script:startedPids = New-Object System.Collections.Generic.List[int]
$script:openedWindows = New-Object System.Collections.Generic.List[string]

function Check([string]$Name, [bool]$Condition, [string]$Detail = '') {
    if ($Condition) { $script:passed++; Write-Host "  PASS  $Name" -ForegroundColor Green }
    else { $script:failed++; Write-Host "  FAIL  $Name  $Detail" -ForegroundColor Red }
}
function Skip([string]$Name, [string]$Reason) { $script:skipped++; Write-Host "  SKIP  $Name  ($Reason)" -ForegroundColor Yellow }

# runs greenshot-cli, returns stdout lines, stderr text and exit code
function GsCli {
    $psi = New-Object System.Diagnostics.ProcessStartInfo
    $psi.FileName = $Exe
    $psi.UseShellExecute = $false
    $psi.RedirectStandardOutput = $true
    $psi.RedirectStandardError = $true
    $psi.CreateNoWindow = $true
    $psi.StandardOutputEncoding = [Text.Encoding]::UTF8
    $psi.StandardErrorEncoding = [Text.Encoding]::UTF8
    # an array such as 10,10,40,40 is one comma separated argument
    $flat = @($args | ForEach-Object { if ($_ -is [array]) { $_ -join ',' } else { "$_" } })
    $psi.Arguments = ($flat | ForEach-Object { if ($_ -match '[\s"]') { '"' + ($_ -replace '"', '\"') + '"' } else { $_ } }) -join ' '
    $p = [System.Diagnostics.Process]::Start($psi)
    $out = $p.StandardOutput.ReadToEnd()
    $err = $p.StandardError.ReadToEnd()
    $p.WaitForExit()
    [pscustomobject]@{ Out = $out.Trim(); Err = $err.Trim(); Code = $p.ExitCode }
}
# like GsCli, but counts foreground window changes while the tool runs (the hook needs this thread to pump messages)
function GsCliWatched {
    [GsFocus]::Start()
    $psi = New-Object System.Diagnostics.ProcessStartInfo
    $psi.FileName = $Exe
    $psi.UseShellExecute = $false
    $psi.RedirectStandardOutput = $true
    $psi.RedirectStandardError = $true
    $psi.CreateNoWindow = $true
    $flat = @($args | ForEach-Object { if ($_ -is [array]) { $_ -join ',' } else { "$_" } })
    $psi.Arguments = ($flat | ForEach-Object { if ($_ -match '[\s"]') { '"' + ($_ -replace '"', '\"') + '"' } else { $_ } }) -join ' '
    $p = [System.Diagnostics.Process]::Start($psi)
    $outTask = $p.StandardOutput.ReadToEndAsync(); $errTask = $p.StandardError.ReadToEndAsync()
    while (-not $p.HasExited) { [System.Windows.Forms.Application]::DoEvents(); Start-Sleep -Milliseconds 10 }
    $end = (Get-Date).AddMilliseconds(400)
    while ((Get-Date) -lt $end) { [System.Windows.Forms.Application]::DoEvents(); Start-Sleep -Milliseconds 10 }
    [GsFocus]::Stop()
    [pscustomobject]@{ Out = $outTask.Result.Trim(); Err = $errTask.Result.Trim(); Code = $p.ExitCode; FocusChanges = [GsFocus]::Changes; LastFocus = [GsFocus]::Last }
}
function CliJson { $r = GsCli @args; if ($r.Code -ne 0) { throw "greenshot-cli $($args -join ' ') failed: $($r.Err)" }; $r.Out | ConvertFrom-Json }

function ImageSize([string]$Path) { $b = New-Object System.Drawing.Bitmap $Path; try { @($b.Width, $b.Height) } finally { $b.Dispose() } }
function Pixel([string]$Path, [int]$X, [int]$Y) { $b = New-Object System.Drawing.Bitmap $Path; try { $b.GetPixel($X, $Y) } finally { $b.Dispose() } }
# the red square starts at client 10,10: white just before, red on the first pixel, in x and y
function RedEdgeExact([string]$Path, [int]$X0, [int]$Y0) {
    $b = New-Object System.Drawing.Bitmap $Path
    try {
        if ($X0 -lt 1 -or $Y0 -lt 1 -or $X0 + 6 -ge $b.Width -or $Y0 + 6 -ge $b.Height) { return $false }
        $left = $b.GetPixel($X0 - 1, $Y0 + 5); $inside = $b.GetPixel($X0, $Y0 + 5)
        $above = $b.GetPixel($X0 + 5, $Y0 - 1); $below = $b.GetPixel($X0 + 5, $Y0)
        (IsColor $left 255 255 255 6) -and (IsColor $inside 255 0 0 6) -and (IsColor $above 255 255 255 6) -and (IsColor $below 255 0 0 6)
    } finally { $b.Dispose() }
}
function DarkPixels([string]$Path, [int]$X, [int]$Y, [int]$W, [int]$H) {
    $b = New-Object System.Drawing.Bitmap $Path
    try {
        $n = 0
        for ($yy = $Y; $yy -lt [math]::Min($Y + $H, $b.Height); $yy++) { for ($xx = $X; $xx -lt [math]::Min($X + $W, $b.Width); $xx++) { if ($b.GetPixel($xx, $yy).R -lt 110) { $n++ } } }
        $n
    } finally { $b.Dispose() }
}
function IsColor($Pixel, [int]$R, [int]$G, [int]$B, [int]$Tolerance = 12) {
    [math]::Abs($Pixel.R - $R) -le $Tolerance -and [math]::Abs($Pixel.G - $G) -le $Tolerance -and [math]::Abs($Pixel.B - $B) -le $Tolerance
}
function NewImage([string]$Path, [int]$W, [int]$H, [System.Drawing.Color]$Color) {
    $b = New-Object System.Drawing.Bitmap $W, $H
    $g = [System.Drawing.Graphics]::FromImage($b); $g.Clear($Color); $g.Dispose()
    $b.Save($Path, [System.Drawing.Imaging.ImageFormat]::Png); $b.Dispose()
}
function FileHash([string]$Path) { (Get-FileHash $Path -Algorithm SHA256).Hash }

# --- test window: white client area, red square at client 10,10 (40x40), blue square at 100,50 (40x40), a combo box
$formScript = @'
param([string]$Title, [int]$X = 100, [int]$Y = 100, [switch]$Popup)
Add-Type -AssemblyName System.Windows.Forms
Add-Type @"
using System.Runtime.InteropServices;
public static class GsDpi { [DllImport("user32.dll")] public static extern bool SetProcessDpiAwarenessContext(System.IntPtr value); }
"@
[void][GsDpi]::SetProcessDpiAwarenessContext([IntPtr](-4))
[System.Windows.Forms.Application]::EnableVisualStyles()
$f = New-Object System.Windows.Forms.Form
$f.Text = $Title
$f.StartPosition = 'Manual'
$f.Location = New-Object System.Drawing.Point $X, $Y
$f.ClientSize = New-Object System.Drawing.Size 420, 300
$f.BackColor = [System.Drawing.Color]::White
$red = New-Object System.Windows.Forms.Panel; $red.BackColor = [System.Drawing.Color]::FromArgb(255, 0, 0); $red.Location = '10,10'; $red.Size = '40,40'; $f.Controls.Add($red)
$blue = New-Object System.Windows.Forms.Panel; $blue.BackColor = [System.Drawing.Color]::FromArgb(0, 0, 255); $blue.Location = '100,50'; $blue.Size = '40,40'; $f.Controls.Add($blue)
$combo = New-Object System.Windows.Forms.ComboBox; $combo.Location = '10,120'; $combo.Width = 200; $combo.DropDownStyle = 'DropDownList'; $combo.DropDownHeight = 120
1..8 | ForEach-Object { [void]$combo.Items.Add("Item number $_") }
$f.Controls.Add($combo)
if ($Popup) {
    # a context menu is a window of its own (popup, tool window, not activatable) like the menus of Unreal or Chrome
    $menu = New-Object System.Windows.Forms.ContextMenuStrip
    $menu.AutoClose = $false
    1..5 | ForEach-Object { [void]$menu.Items.Add("Menu entry $_") }
    $t = New-Object System.Windows.Forms.Timer; $t.Interval = 1500
    $t.Add_Tick({ $t.Stop(); $menu.Show($f, (New-Object System.Drawing.Point 230, 100)) })
    $f.Add_Shown({ $t.Start() })
}
[System.Windows.Forms.Application]::Run($f)
'@
$formFile = Join-Path $work 'testform.ps1'
Set-Content -Path $formFile -Value $formScript -Encoding UTF8

function StartForm([string]$Title, [int]$X, [int]$Y, [switch]$Popup) {
    $argList = @('-NoProfile', '-STA', '-ExecutionPolicy', 'Bypass', '-File', $formFile, '-Title', $Title, '-X', $X, '-Y', $Y)
    if ($Popup) { $argList += '-Popup' }
    $proc = Start-Process powershell -ArgumentList $argList -PassThru -WindowStyle Normal
    $script:startedPids.Add($proc.Id)
    $deadline = (Get-Date).AddSeconds(30)
    while ((Get-Date) -lt $deadline) {
        $w = (CliJson list --json).windows | Where-Object { $_.title -eq $Title } | Select-Object -First 1
        if ($w) { Start-Sleep -Milliseconds 700; return (CliJson list --json).windows | Where-Object { $_.title -eq $Title } | Select-Object -First 1 }
        Start-Sleep -Milliseconds 300
    }
    throw "test window $Title did not appear"
}

function Cleanup {
    # windows the script opened in programs that were already running (Notepad keeps one process): ask them to close
    foreach ($h in $script:openedWindows) { [void][GsWin]::PostMessage([IntPtr][Convert]::ToInt64($h, 16), 0x10, [IntPtr]::Zero, [IntPtr]::Zero) }
    foreach ($id in $script:startedPids) { try { Stop-Process -Id $id -Force -ErrorAction SilentlyContinue } catch { } }
    Start-Sleep -Milliseconds 300
    try { Remove-Item -Recurse -Force $work -ErrorAction SilentlyContinue } catch { }
}

try {
    Write-Host "greenshot-cli: $Exe"
    Write-Host "work folder:   $work`n"

    # ------------------------------------------------------------------ help
    Write-Host '[help]'
    $help = (GsCli help).Out
    foreach ($word in '--window-pid', '--window-exe', '--client', '--include-popups', '--no-activate', '--restore-behind', '--settle',
                      '--preview', '--preview-width', '--preview-max', '--json', 'combine', '--hstack', '--vstack', '--gap-color',
                      '--labels', '--grid', '--anchor', '--label', 'diff', '--threshold', '--min-area', 'physical pixels') {
        Check "help mentions $word" ($help.Contains($word))
    }
    Check 'every new argument has an example' ((([regex]::Matches($help, 'example: greenshot-cli')).Count) -ge 9)

    # ------------------------------------------------------------------ test windows
    $a = StartForm 'GSTEST-A' 100 100 -Popup
    Write-Host "  test window A: $($a.hwnd) pid=$($a.pid)"

    # ------------------------------------------------------------------ list --json + DPI
    Write-Host "`n[list --json, DPI]"
    $list = CliJson list --json
    Check 'json has monitors' ($list.monitors.Count -ge 1 -and $null -ne $list.monitors[0].width)
    foreach ($field in 'hwnd', 'title', 'pid', 'exe', 'rect', 'client', 'minimized', 'visible') {
        Check "window has $field" ($null -ne $a.PSObject.Properties[$field])
    }
    Check 'exe is the full path of powershell' ($a.exe -like '*powershell.exe')
    Check 'client is inside rect' ($a.client.x -ge $a.rect.x -and $a.client.y -ge $a.rect.y -and $a.client.width -le $a.rect.width)
    Check 'client is 420x300 physical pixels' ($a.client.width -eq 420 -and $a.client.height -eq 300) "got $($a.client.width)x$($a.client.height)"

    # per-monitor aware helper process reads the same rectangles with plain Win32 calls
    $probe = @"
Add-Type @'
using System; using System.Runtime.InteropServices;
public static class P {
  [StructLayout(LayoutKind.Sequential)] public struct RECT { public int L, T, R, B; }
  [StructLayout(LayoutKind.Sequential)] public struct PT { public int X, Y; }
  [DllImport("user32.dll")] public static extern bool SetProcessDpiAwarenessContext(IntPtr v);
  [DllImport("user32.dll")] public static extern bool GetClientRect(IntPtr h, out RECT r);
  [DllImport("user32.dll")] public static extern bool ClientToScreen(IntPtr h, ref PT p);
}
'@
[void][P]::SetProcessDpiAwarenessContext([IntPtr](-4))
`$h = [IntPtr]$($a.hwnd.Replace('0x', '0x'))
`$r = New-Object P+RECT; [void][P]::GetClientRect(`$h, [ref]`$r)
`$p = New-Object P+PT; [void][P]::ClientToScreen(`$h, [ref]`$p)
"`$(`$p.X),`$(`$p.Y),`$(`$r.R - `$r.L),`$(`$r.B - `$r.T)"
"@
    $probeFile = Join-Path $work 'probe.ps1'
    Set-Content $probeFile $probe -Encoding UTF8
    $probeOut = (& powershell -NoProfile -ExecutionPolicy Bypass -File $probeFile | Select-Object -Last 1)
    $expected = "$($a.client.x),$($a.client.y),$($a.client.width),$($a.client.height)"
    Check 'client rect equals what a per-monitor DPI aware Win32 script gets' ($probeOut -eq $expected) "script $probeOut, list --json $expected"

    # ------------------------------------------------------------------ capture json, relative region, client
    Write-Host "`n[capture: json, region, client]"
    $full = Join-Path $work 'a-full.png'
    $j = CliJson capture --window-pid $a.pid --window 'GSTEST-A' --json -o $full
    Check 'json: path, width, height' ($j.path -eq $full -and $j.width -gt 0 -and $j.height -gt 0)
    Check 'json: hwnd and pid of the window' ($j.hwnd -eq $a.hwnd -and $j.pid -eq $a.pid)
    Check 'json: rect and mode' ($j.rect.width -eq $j.width -and $j.rect.height -eq $j.height -and $j.mode)
    Check 'json is a single line' (((GsCli capture --window-pid $a.pid --json -o (Join-Path $work 'one.png')).Out -split "`n").Count -eq 1)
    Check 'text output unchanged without --json' ((GsCli capture --window-pid $a.pid -o (Join-Path $work 'text.png')).Out -match '(?s)^saved: .*\r?\nsize: \d+x\d+$')

    $client = Join-Path $work 'a-client.png'
    $j = CliJson capture --window-pid $a.pid --client --json -o $client
    Check '--client image is exactly the client area' ($j.width -eq $a.client.width -and $j.height -eq $a.client.height) "got $($j.width)x$($j.height)"
    Check '--client rect matches list' ($j.rect.x -eq $a.client.x -and $j.rect.y -eq $a.client.y)
    Check 'client (20,20) is the red square' (IsColor (Pixel $client 20 20) 255 0 0)
    Check 'client (120,70) is the blue square' (IsColor (Pixel $client 120 70) 0 0 255)

    foreach ($mode in 'aero', 'gdi', 'screen') {
        $edge = Join-Path $work "edge-$mode.png"
        $null = GsCli capture --window-pid $a.pid --mode $mode --client -o $edge
        Check "--client --mode ${mode}: pixel exact (red square starts at 10,10)" (RedEdgeExact $edge 10 10)
        $edge2 = Join-Path $work "edge-region-$mode.png"
        $null = GsCli capture --window-pid $a.pid --mode $mode --client --region 5, 5, 100, 100 -o $edge2
        Check "--client --region --mode ${mode}: pixel exact (red square starts at 5,5)" (RedEdgeExact $edge2 5 5)
    }
    $red = Join-Path $work 'a-red.png'
    $j = CliJson capture --window-pid $a.pid --client --region 10,10,40,40 --json -o $red
    Check '--client --region gives the 40x40 red square' ($j.width -eq 40 -and $j.height -eq 40 -and (IsColor (Pixel $red 20 20) 255 0 0) -and (IsColor (Pixel $red 1 1) 255 0 0))
    Check '--region rect is in screen pixels' ($j.rect.x -eq $a.client.x + 10 -and $j.rect.y -eq $a.client.y + 10)

    $blue = Join-Path $work 'a-blue.png'
    $dx = $a.client.x - $a.rect.x; $dy = $a.client.y - $a.rect.y
    $j = CliJson capture --window-pid $a.pid --region ($dx + 100), ($dy + 50), 40, 40 --json -o $blue
    Check '--region relative to the window (not the client)' ($j.width -eq 40 -and (IsColor (Pixel $blue 20 20) 0 0 255) -and (IsColor (Pixel $blue 1 1) 0 0 255))
    $r = GsCli capture --window-pid $a.pid --region "$($a.rect.width),0,10,10" -o (Join-Path $work 'nope.png')
    Check '--region touching only the right edge of the window: clean error, no stack trace' ($r.Code -eq 1 -and $r.Err -like 'error:*outside*' -and $r.Err -notlike '*   at *')
    $r = GsCli capture --window-pid $a.pid --region 5000, 5000, 10, 10 -o (Join-Path $work 'nope.png')
    Check '--region outside the window is an error' ($r.Code -eq 1 -and $r.Err -like 'error:*outside*')
    $r = GsCli capture --client -o (Join-Path $work 'nope.png')
    Check '--client without a window is an error' ($r.Code -eq 1 -and $r.Err -like 'error:*need a window*')

    # existing behaviour: screen region in physical pixels, title only
    $screen = Join-Path $work 'screen.png'
    $j = CliJson capture --region 0, 0, 200, 100 --json -o $screen
    Check 'screen --region is still absolute' ($j.width -eq 200 -and $j.height -eq 100 -and $null -eq $j.hwnd)
    $r = GsCli capture --window 'GSTEST-A' -o (Join-Path $work 'title.png')
    Check '--window with a title alone still works' ($r.Code -eq 0)
    $r = GsCli capture --window 'GSTEST-A' --window 'GSTEST-A' -o (Join-Path $work 'x.png')
    Check 'the same target twice is still an error' ($r.Code -eq 1 -and $r.Err -like 'error: use only one of*')
    $r = GsCli list --whatever
    Check 'list still ignores unknown arguments (with a warning)' ($r.Code -eq 0 -and $r.Out -like '*monitor 0*')
    $r = GsCli capture --window-pid $a.pid --preview (Join-Path $work 'p.webp') -o (Join-Path $work 'prev-first.png')
    Check 'bad preview extension fails before anything is written' ($r.Code -eq 1 -and -not (Test-Path (Join-Path $work 'prev-first.png')))

    # ------------------------------------------------------------------ popups
    Write-Host "`n[popups]"
    Start-Sleep -Milliseconds 1500   # the menu of test window A opens itself
    $withoutPopups = Join-Path $work 'popup-without.png'
    $withPopups = Join-Path $work 'popup-with.png'
    # a plain capture raises the window (as it always did), so the focus is looked at around the --include-popups capture only
    $null = CliJson capture --window-pid $a.pid --json -o $withoutPopups
    Start-Sleep -Milliseconds 500
    $fg0 = [GsWin]::GetForegroundWindow()
    $j = CliJson capture --window-pid $a.pid --include-popups --json -o $withPopups
    $fg1 = [GsWin]::GetForegroundWindow()
    Check '--include-popups does not change the foreground window' ($fg0 -eq $fg1)
    $sizeWithout = ImageSize $withoutPopups; $sizeWith = ImageSize $withPopups
    Check 'the open menu makes the image grow only with --include-popups' ($sizeWith[0] -gt $sizeWithout[0] -and $sizeWith[1] -gt $sizeWithout[1]) "sizes $($sizeWithout -join 'x') / $($sizeWith -join 'x')"
    # the menu is at client 230,100 of the window: it has dark text there, the image without popups does not
    $menuX = $a.client.x - $a.rect.x + 250; $menuY = $a.client.y - $a.rect.y + 105
    $darkWith = DarkPixels $withPopups $menuX $menuY 150 90
    $darkWithout = DarkPixels $withoutPopups $menuX $menuY 150 90
    Check 'the menu text is drawn at its real position' ($darkWith -gt 40 -and $darkWithout -eq 0) "dark pixels with popups $darkWith, without $darkWithout"

    # ------------------------------------------------------------------ preview
    Write-Host "`n[preview]"
    $prevPath = Join-Path $work 'a-preview.png'
    $j = CliJson capture --window-pid $a.pid --client -o (Join-Path $work 'a-big.png') --preview $prevPath --preview-width 200 --json
    $big = ImageSize (Join-Path $work 'a-big.png'); $small = ImageSize $prevPath
    Check 'preview has the wanted width and keeps proportions' ($small[0] -eq 200 -and [math]::Abs($small[1] - 200 * $big[1] / $big[0]) -le 1)
    Check 'original keeps its full resolution' ($big[0] -eq $a.client.width)
    Check 'json lists the preview' ($j.preview -eq $prevPath -and $j.preview_width -eq 200)
    $null = GsCli capture --window-pid $a.pid --client -o (Join-Path $work 'a-big2.png') --preview (Join-Path $work 'a-max.png') --preview-max 100
    $m = ImageSize (Join-Path $work 'a-max.png')
    Check '--preview-max limits the longest side' ([math]::Max($m[0], $m[1]) -eq 100)
    $null = GsCli edit (Join-Path $work 'a-big.png') -o (Join-Path $work 'edit-prev.png') --preview (Join-Path $work 'edit-prev-small.png') --preview-width 80 --rect 5, 5, 20, 20
    Check 'edit --preview works too' ((ImageSize (Join-Path $work 'edit-prev-small.png'))[0] -eq 80)
    $r = GsCli capture --window-pid $a.pid --preview-width 100 -o (Join-Path $work 'x.png')
    Check '--preview-width without --preview is an error' ($r.Code -eq 1)

    # ------------------------------------------------------------------ combine, grid, anchors, labels, diff (synthetic images)
    Write-Host "`n[combine, grid, anchors, diff]"
    $i1 = Join-Path $work 'i1.png'; $i2 = Join-Path $work 'i2.png'; $i3 = Join-Path $work 'i3.png'
    NewImage $i1 400 300 ([System.Drawing.Color]::FromArgb(230, 230, 230))
    NewImage $i2 400 300 ([System.Drawing.Color]::FromArgb(200, 220, 255))
    NewImage $i3 200 100 ([System.Drawing.Color]::FromArgb(255, 230, 200))
    $out = Join-Path $work 'h.png'
    $j = CliJson combine $i1 $i2 -o $out --hstack --gap 10 --gap-color '#FF0000' --json
    Check 'hstack: width = sum + gap' ($j.width -eq 810 -and $j.height -eq 300)
    Check 'hstack: gap has the gap color' (IsColor (Pixel $out 405 100) 255 0 0)
    $j = CliJson combine $i1 $i2 -o (Join-Path $work 'v.png') --vstack --gap 4 --json
    Check 'vstack: height = sum + gap' ($j.width -eq 400 -and $j.height -eq 604)
    $j = CliJson combine $i1 $i2 $i3 -o (Join-Path $work 'g.png') --grid 2 --gap 0 --json
    Check 'grid 2 columns, 3 images: rows are as high as their highest tile' ($j.width -eq 800 -and $j.height -eq 400) "got $($j.width)x$($j.height)"
    $j = CliJson combine $i1 $i2 -o (Join-Path $work 'w.png') --hstack --gap 0 --width 100 --json
    Check '--width scales every tile keeping the proportions' ($j.width -eq 200 -and $j.height -eq 75)
    $plain = Join-Path $work 'plain.png'; $labeled = Join-Path $work 'labeled.png'
    $null = GsCli combine $i1 $i2 -o $plain --hstack --gap 0
    $j = CliJson combine $i1 $i2 -o $labeled --hstack --gap 0 --labels 'Before' 'After' --json
    $d = CliJson diff $plain $labeled --json -o (Join-Path $work 'labels-diff.png') --min-area 1
    $labelsAtTiles = @($d.rects | Where-Object { $_.x -lt 60 -and $_.y -lt 60 }).Count -ge 1 -and @($d.rects | Where-Object { $_.x -ge 400 -and $_.x -lt 460 -and $_.y -lt 60 }).Count -ge 1
    Check '--labels are drawn in the top left corner of each tile' $labelsAtTiles "diff rects: $(($d.rects | ForEach-Object { "$($_.x),$($_.y)" }) -join ' ')"
    Check 'combined image can be annotated with edit' ((GsCli edit $labeled -o (Join-Path $work 'annotated.png') --rect 20, 100, 100, 50).Code -eq 0)
    $r = GsCli combine $i1 -o (Join-Path $work 'x.png')
    Check 'combine needs two images' ($r.Code -eq 1)

    # grid
    $before = FileHash $i1
    $gridOut = Join-Path $work 'grid.png'
    $j = CliJson edit $i1 --grid 50 -o $gridOut --json
    $d = CliJson diff $i1 $gridOut --json -o (Join-Path $work 'grid-diff.png')
    Check '--grid writes a copy with lines and coordinates' ($j.width -eq 400 -and $d.changed_pixels -gt 1000)
    Check '--grid does not touch the original' ($before -eq (FileHash $i1))
    $null = GsCli edit $i1 --grid 50
    Check '--grid without -o makes NAME.grid.png' ((Test-Path (Join-Path $work 'i1.grid.png')) -and $before -eq (FileHash $i1))

    # anchors
    $anchorBase = Join-Path $work 'anchor.png'
    NewImage $anchorBase 600 400 ([System.Drawing.Color]::White)
    function AnchorRect([string]$Anchor, [string[]]$Extra) {
        $o = Join-Path $work ("anchor-$Anchor.png")
        $null = GsCli edit $anchorBase -o $o --color '#000000' --font-size 20 --anchor $Anchor @Extra
        (CliJson diff $anchorBase $o --json -o (Join-Path $work ("anchor-$Anchor-diff.png")) --min-area 1 --merge 40).rects | Select-Object -First 1
    }
    $r = AnchorRect 'bottom-left' @('--text', '10,10', 'Hello')
    Check 'anchor bottom-left: text 10 px from the left and bottom' ($r.x -le 24 -and ($r.y + $r.height) -ge 360 -and ($r.y + $r.height) -le 398) "rect $($r.x),$($r.y),$($r.width),$($r.height)"
    $r = AnchorRect 'bottom-right' @('--text', '10,10', 'Hello')
    Check 'anchor bottom-right: text in the bottom right corner' (($r.x + $r.width) -ge 560 -and ($r.x + $r.width) -le 598 -and ($r.y + $r.height) -ge 360) "rect $($r.x),$($r.y),$($r.width),$($r.height)"
    $r = AnchorRect 'top-right' @('--text', '10,10', 'Hello')
    Check 'anchor top-right: text in the top right corner' (($r.x + $r.width) -ge 560 -and $r.y -le 24) "rect $($r.x),$($r.y),$($r.width),$($r.height)"
    $r = AnchorRect 'center' @('--text', '0,0', 'Hello')
    Check 'anchor center: text around the middle' ($r.x -gt 220 -and ($r.x + $r.width) -lt 380 -and $r.y -gt 160 -and ($r.y + $r.height) -lt 240) "rect $($r.x),$($r.y),$($r.width),$($r.height)"
    $r = AnchorRect 'bottom' @('--rect', '0,10,200,30')
    Check 'anchor bottom: box centered horizontally, 10 px from the bottom' ($r.x -ge 195 -and $r.x -le 205 -and ($r.y + $r.height) -ge 358) "rect $($r.x),$($r.y),$($r.width),$($r.height)"
    $r = GsCli edit $anchorBase -o (Join-Path $work 'x.png') --anchor middle --text 1, 1 'x'
    Check 'unknown anchor is an error' ($r.Code -eq 1)

    # --label glued to a shape
    $labelOut = Join-Path $work 'label.png'
    $null = GsCli edit $anchorBase -o $labelOut --color '#E53935' --thickness 3 --rect 100, 150, 200, 100 --label 'Broken'
    $d = CliJson diff $anchorBase $labelOut --json -o (Join-Path $work 'label-diff.png') --min-area 1 --merge 40
    $box = $d.rects | Select-Object -First 1
    Check '--label sits above the top left corner of the shape' ($box.y -lt 150 -and $box.y -gt 100 -and $box.x -le 102) "rect $($box.x),$($box.y),$($box.width),$($box.height)"
    Check '--label is filled with the color of the shape' (IsColor (Pixel $labelOut 103 ($box.y + 2)) 229 57 53 24)
    $r = GsCli edit $anchorBase -o (Join-Path $work 'x.png') --label 'no shape'
    Check '--label without a shape is an error' ($r.Code -eq 1)
    $r = GsCli edit $anchorBase -o (Join-Path $work 'x.png') --rect 300, 200, 100, 50 --crop 200, 100, 300, 200 --label 'after crop'
    Check '--label after --crop is an error (positions changed)' ($r.Code -eq 1)
    $stepOut = Join-Path $work 'step-label.png'
    $null = GsCli edit $anchorBase -o $stepOut --step 300, 200 --label 'Step'
    $sd = CliJson diff $anchorBase $stepOut --json -o (Join-Path $work 'step-diff.png') --min-area 1 --merge 60
    $sbox = $sd.rects | Select-Object -First 1
    Check '--label after --step takes the color of the circle, not of the number' ((Pixel $stepOut 302 ($sbox.y + 2)).R -gt 100 -and (Pixel $stepOut 302 ($sbox.y + 2)).G -lt 60) "pixel $((Pixel $stepOut 302 ($sbox.y + 2)))"

    # diff
    $same = CliJson diff $i1 $i1 --json -o (Join-Path $work 'same-diff.png')
    Check 'diff of identical images: no rectangles' ($same.rects.Count -eq 0 -and $same.changed_pixels -eq 0)
    $changed = Join-Path $work 'changed.png'
    $null = GsCli edit $i1 -o $changed --fill '#FF0000' --color '#FF0000' --rect 100, 80, 60, 40 --rect 300, 200, 50, 50
    $marked = Join-Path $work 'marked.png'
    $d = CliJson diff $i1 $changed --json -o $marked
    Check 'diff finds the two changed areas' ($d.rects.Count -eq 2) "rects: $($d.rects.Count)"
    $first = $d.rects | Where-Object { $_.x -lt 200 } | Select-Object -First 1
    Check 'diff rectangle is where the change is' ($first.x -le 100 -and $first.x -ge 90 -and $first.y -le 80 -and ($first.x + $first.width) -ge 160) "rect $($first.x),$($first.y),$($first.width),$($first.height)"
    Check 'diff marks the changes on the AFTER image' ((ImageSize $marked)[0] -eq 400 -and (IsColor (Pixel $marked 5 5) 230 230 230))
    $d2 = CliJson diff $i1 $changed --json -o (Join-Path $work 'min.png') --min-area 5000
    Check '--min-area drops small areas' ($d2.rects.Count -eq 0) "rects: $($d2.rects.Count)"
    $r = GsCli diff $i1 $i3 -o (Join-Path $work 'x.png')
    Check 'diff of different sizes is an error' ($r.Code -eq 1 -and $r.Err -like 'error:*same size*')
    Check 'diff text output lists the rectangles' ((GsCli diff $i1 $changed -o (Join-Path $work 't.png')).Out -match 'rect: \d+,\d+,\d+,\d+')

    # ------------------------------------------------------------------ several windows with the same title: two Notepads
    Write-Host "`n[two Notepad windows: --window-pid, --window-exe]"
    $runningNotepad = @(Get-Process -Name notepad -ErrorAction SilentlyContinue)
    if ($runningNotepad.Count -gt 0) {
        Skip 'two Notepad windows' 'Notepad is already running, its windows would share the process and the test could close your own windows'
    }
    else {
        $known = @((CliJson list --json).windows | ForEach-Object { $_.hwnd })
        for ($n = 0; $n -lt 2; $n++) { $null = Start-Process (Join-Path $env:WINDIR 'System32\notepad.exe') -PassThru; Start-Sleep -Milliseconds 2500 }
        $notepads = @((CliJson list --json).windows | Where-Object { $_.exe -like '*notepad.exe' -and $known -notcontains $_.hwnd })
        # Notepad was not running before, so its processes are ours
        foreach ($np in Get-Process -Name notepad -ErrorAction SilentlyContinue) { $script:startedPids.Add($np.Id) }
        if ($notepads.Count -lt 2) { Skip 'two Notepad windows' "only $($notepads.Count) new Notepad window(s) appeared" }
        elseif (($notepads.pid | Select-Object -Unique).Count -lt 2) { Skip 'Notepad windows distinguished by pid' 'both windows belong to one process on this Windows version (the two test forms below cover it)' }
        else {
            $p1 = $notepads[0]; $p2 = $notepads[1]
            $r = GsCli capture --window-exe '*\notepad.exe' -o (Join-Path $work 'np.png')
            Check 'several matches with --window-exe: error listing them' ($r.Code -eq 1 -and $r.Err -like "*$($p1.hwnd)*" -and $r.Err -like "*$($p2.hwnd)*") $r.Err
            $j = CliJson capture --window-pid $p2.pid --json -o (Join-Path $work 'np2.png')
            Check '--window-pid picks the window of that process' ($j.pid -eq $p2.pid -and $j.hwnd -eq $p2.hwnd)
            $j = CliJson capture --window-exe '*notepad.exe' --window-pid $p1.pid --json -o (Join-Path $work 'np1.png')
            Check '--window-exe and --window-pid combined' ($j.pid -eq $p1.pid)
            $r = GsCli capture --window-pid $p1.pid --window-exe '*\nothing-here.exe' -o (Join-Path $work 'np.png')
            Check 'filters that do not match are an error' ($r.Code -eq 1 -and $r.Err -like '*no window matches*')
        }
    }

    # ------------------------------------------------------------------ no focus stealing
    Write-Host "`n[--no-activate]"
    $b = StartForm 'GSTEST-B' 700 100
    [Microsoft.VisualBasic.Interaction]::AppActivate($b.pid)
    Start-Sleep -Milliseconds 800
    $r = GsCli capture --window 'GSTEST' --window-exe '*\powershell.exe' -o (Join-Path $work 'x.png')
    Check 'two windows match with --window-exe: error listing both, never picks one silently' ($r.Code -eq 1 -and $r.Err -like '*2 windows match*' -and $r.Err -like "*$($a.hwnd)*" -and $r.Err -like "*$($b.hwnd)*") $r.Err
    $r = GsCli capture --window 'GSTEST' -o (Join-Path $work 'x.png')
    Check 'two windows with only --window: old behaviour, warning and the first one' ($r.Code -eq 0 -and $r.Err -like 'warning:*2 windows match*')
    # two windows of the same exe, told apart by process id
    $j = CliJson capture --window-pid $b.pid --json -o (Join-Path $work 'pid-b.png')
    Check '--window-pid picks the window of that process (two windows, same exe)' ($j.pid -eq $b.pid -and $j.hwnd -eq $b.hwnd)
    $j = CliJson capture --window-exe '*\powershell.exe' --window-pid $a.pid --json -o (Join-Path $work 'pid-a.png')
    Check '--window-exe together with --window-pid' ($j.pid -eq $a.pid -and $j.hwnd -eq $a.hwnd)
    $j = CliJson capture --window 'GSTEST-B' --window-exe '*\powershell.exe' --json -o (Join-Path $work 'title-exe.png')
    Check '--window title together with --window-exe' ($j.hwnd -eq $b.hwnd)
    $r = GsCli capture --window-pid $a.pid --window 'GSTEST-B' -o (Join-Path $work 'x.png')
    Check 'title of another window with --window-pid: no match, error' ($r.Code -eq 1 -and $r.Err -like '*no window matches*')
    foreach ($mode in 'aero', 'aerotransparent', 'gdi', 'screen', 'auto') {
        $fgBefore = [GsWin]::GetForegroundWindow(); $mouseBefore = [System.Windows.Forms.Cursor]::Position
        $r = GsCliWatched capture --window-pid $a.pid --mode $mode --no-activate -o (Join-Path $work "noact-$mode.png")
        $fgAfter = [GsWin]::GetForegroundWindow(); $mouseAfter = [System.Windows.Forms.Cursor]::Position
        Check "--no-activate --mode ${mode}: captured, no foreground change at all, mouse unchanged" ($r.Code -eq 0 -and $r.FocusChanges -eq 0 -and $fgBefore -eq $fgAfter -and $mouseBefore -eq $mouseAfter -and $fgBefore -ne [IntPtr]::Zero) "code $($r.Code) focus changes $($r.FocusChanges) (last $($r.LastFocus)) $($r.Err)"
    }
    $r = GsCliWatched capture --window-pid $a.pid --include-popups -o (Join-Path $work 'noact-popups.png')
    Check '--include-popups: no foreground change at all' ($r.Code -eq 0 -and $r.FocusChanges -eq 0) "focus changes $($r.FocusChanges) (last $($r.LastFocus))"
    $noact = Join-Path $work 'noact-aero.png'
    Check '--no-activate aero shows the window content (red square)' ($true -and (IsColor (Pixel $noact ($a.client.x - $a.rect.x + 20) ($a.client.y - $a.rect.y + 20)) 255 0 0 40))

    # a maximized window: negative coordinates, borders outside of the screen
    [void][GsWin]::ShowWindow([IntPtr][Convert]::ToInt64($b.hwnd, 16), 3)   # SW_MAXIMIZE
    Start-Sleep -Milliseconds 1200
    foreach ($mode in 'aero', 'gdi') {
        $maxOut = Join-Path $work "maximized-$mode.png"
        $null = GsCli capture --window-pid $b.pid --mode $mode --no-activate --client -o $maxOut
        Check "maximized window, --client --mode ${mode}: pixel exact" (RedEdgeExact $maxOut 10 10)
    }
    [void][GsWin]::ShowWindow([IntPtr][Convert]::ToInt64($b.hwnd, 16), 9)   # SW_RESTORE
    Start-Sleep -Milliseconds 600
    [Microsoft.VisualBasic.Interaction]::AppActivate($b.pid)
    Start-Sleep -Milliseconds 500

    # ------------------------------------------------------------------ minimized windows
    Write-Host "`n[minimized: --no-activate error, --restore-behind]"
    [void][GsWin]::ShowWindow([IntPtr][Convert]::ToInt64($a.hwnd, 16), 6)   # SW_MINIMIZE
    Start-Sleep -Milliseconds 800
    [Microsoft.VisualBasic.Interaction]::AppActivate($b.pid)
    Start-Sleep -Milliseconds 500
    Check 'test window A is minimized' ((CliJson list --json).windows | Where-Object { $_.hwnd -eq $a.hwnd }).minimized
    $r = GsCli capture --window-pid $a.pid --no-activate -o (Join-Path $work 'min-fail.png')
    Check '--no-activate with a minimized window: clear error, window stays minimized' ($r.Code -eq 1 -and $r.Err -like '*minimized*--restore-behind*' -and [GsWin]::IsIconic([IntPtr][Convert]::ToInt64($a.hwnd, 16)))
    $fgBefore = [GsWin]::GetForegroundWindow()
    $restored = Join-Path $work 'restored.png'
    $r = GsCli capture --window-pid $a.pid --restore-behind --settle 800 --json -o $restored
    $fgAfter = [GsWin]::GetForegroundWindow()
    Check '--restore-behind captures the minimized window' ($r.Code -eq 0) "$($r.Err)"
    if ($r.Code -eq 0) {
        $rj = $r.Out | ConvertFrom-Json
        $red = $false
        $bmp = New-Object System.Drawing.Bitmap $restored
        for ($y = 0; $y -lt $bmp.Height -and -not $red; $y += 4) { for ($x = 0; $x -lt $bmp.Width; $x += 4) { $p = $bmp.GetPixel($x, $y); if ($p.R -gt 240 -and $p.G -lt 20 -and $p.B -lt 20) { $red = $true; break } } }
        $bmp.Dispose()
        Check '--restore-behind image has the window content (red square, not black)' $red
        Check '--restore-behind reports PrintWindow' ($rj.mode -eq 'printwindow')
    }
    Check '--restore-behind does not change the foreground window' ($fgBefore -eq $fgAfter)
    # again, watched by the foreground hook, with the client area and a region (read while the window is restored)
    [void][GsWin]::ShowWindow([IntPtr][Convert]::ToInt64($a.hwnd, 16), 6)
    Start-Sleep -Milliseconds 800
    [Microsoft.VisualBasic.Interaction]::AppActivate($b.pid)
    Start-Sleep -Milliseconds 500
    $rc = Join-Path $work 'restored-client.png'
    $r = GsCliWatched capture --window-pid $a.pid --restore-behind --settle 800 --client --json -o $rc
    Check '--restore-behind --client: client area of the window, no foreground change' ($r.Code -eq 0 -and $r.FocusChanges -eq 0 -and (RedEdgeExact $rc 10 10)) "code $($r.Code) focus changes $($r.FocusChanges) $($r.Err)"
    Start-Sleep -Milliseconds 500
    $rr = Join-Path $work 'restored-region.png'
    $r = GsCli capture --window-pid $a.pid --restore-behind --settle 800 --client --region 5, 5, 100, 100 -o $rr
    Check '--restore-behind --client --region: pixel exact' ($r.Code -eq 0 -and (RedEdgeExact $rr 5 5)) "code $($r.Code) $($r.Err)"
    Check '--restore-behind minimizes the window again' ([GsWin]::IsIconic([IntPtr][Convert]::ToInt64($a.hwnd, 16)))
    [void][GsWin]::ShowWindow([IntPtr][Convert]::ToInt64($a.hwnd, 16), 4)   # SW_SHOWNOACTIVATE, for the next checks

    # ------------------------------------------------------------------ screen mode warns about windows in front
    Write-Host "`n[--mode screen: covered window warning]"
    $c = StartForm 'GSTEST-C' 100 100   # sits exactly over window A
    $r = GsCli capture --window-pid $a.pid --mode screen --no-activate -o (Join-Path $work 'covered.png')
    Check 'screen mode names the window that covers the target' ($r.Code -eq 0 -and $r.Err -like '*covered by*GSTEST-C*') $r.Err

    Write-Host "`n$($script:passed) passed, $($script:failed) failed, $($script:skipped) skipped"
}
finally {
    Cleanup
}
exit $script:failed
