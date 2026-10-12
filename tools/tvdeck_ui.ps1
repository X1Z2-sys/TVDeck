param([string]$Steps, [string]$Out = "$env:TMPDIR\tvdeck", [switch]$RealTv)
# Drive TVDeck by UI Automation names. Steps joined by '|': "nav:<NavItem>", "click:<Button name>", "shot:<file>", "wait:<sec>", "scroll:<percent>"
Add-Type -AssemblyName UIAutomationClient, UIAutomationTypes, System.Drawing, System.Windows.Forms
Add-Type @"
using System; using System.Runtime.InteropServices;
public static class W { [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr h);
 [DllImport("user32.dll")] public static extern bool ShowWindow(IntPtr h, int cmd);
 [DllImport("user32.dll")] public static extern bool SetProcessDPIAware();
 [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr h, out RECT r);
 [DllImport("user32.dll")] public static extern bool PrintWindow(IntPtr h, IntPtr dc, uint f);
 public struct RECT { public int L, T, R, B; } }
"@
# Test only against the emulator dummy TV unless -RealTv (the user asked for a run on their TV): drop any network device
# (plain `adb disconnect` also blips the emulator), then refuse if a real one (e.g. USB) is still attached.
if (-not $RealTv) {
$devs = & adb devices | Select-Object -Skip 1 | Where-Object { $_ -match '\S' -and $_ -notmatch '^emulator-' } | ForEach-Object { ($_ -split '\s+')[0] }
foreach ($d in $devs) { if ($d -match ':') { & adb disconnect $d | Out-Null } }
$real = & adb devices | Select-Object -Skip 1 | Where-Object { $_ -match '\S' -and $_ -notmatch '^emulator-' }
if ($real) { throw "Real device attached, refusing to drive TVDeck: $real" }
}
[W]::SetProcessDPIAware() | Out-Null
$A = [System.Windows.Automation.AutomationElement]
$T = [System.Windows.Automation.TreeScope]
function Win {
  for ($i = 0; $i -lt 40; $i++) {
    $p = Get-Process TVDeck -ErrorAction SilentlyContinue | Where-Object MainWindowHandle -ne 0 | Select-Object -First 1
    if ($p) { return $A::FromHandle($p.MainWindowHandle) }
    Start-Sleep -Milliseconds 500
  }
  throw "TVDeck window not found"
}
function Find($w, $name) {
  $c = New-Object System.Windows.Automation.PropertyCondition($A::NameProperty, $name)
  for ($i = 0; $i -lt 30; $i++) { $e = $w.FindFirst($T::Descendants, $c); if ($e) { return $e }; Start-Sleep -Milliseconds 500 }
  throw "Element '$name' not found"
}
function Invoke($e) {
  $o = $null
  $walker = [System.Windows.Automation.TreeWalker]::ControlViewWalker
  for ($x = $e; $x; $x = $walker.GetParent($x)) {   # a label inside a list row: select the row
    if ($x.TryGetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern, [ref]$o)) { $o.Invoke(); return }
    if ($x.TryGetCurrentPattern([System.Windows.Automation.TogglePattern]::Pattern, [ref]$o)) { $o.Toggle(); return }
    if ($x.TryGetCurrentPattern([System.Windows.Automation.SelectionItemPattern]::Pattern, [ref]$o)) { $o.Select(); return }
  }
  throw "Element '$($e.Current.Name)' can't be invoked"
}
New-Item -ItemType Directory -Force $Out | Out-Null
if (-not (Get-Process TVDeck -ErrorAction SilentlyContinue)) {
  $app = Get-StartApps | Where-Object Name -eq 'TVDeck' | Select-Object -First 1
  Start-Process "shell:AppsFolder\$($app.AppID)"
}
$w = Win
foreach ($s in ($Steps -split '\|')) {
  $k, $v = $s -split ':', 2
  switch ($k) {
    'nav'   { Invoke (Find $w $v); Start-Sleep 2 }
    'click' {   # same name can be a label and its control: prefer the one that toggles or invokes
      Find $w $v | Out-Null
      $c = New-Object System.Windows.Automation.PropertyCondition($A::NameProperty, $v)
      $all = @($w.FindAll($T::Descendants, $c))
      $hit = $all | Where-Object { $_.GetSupportedPatterns() | Where-Object { $_.ProgrammaticName -match 'Toggle|Invoke' } } | Select-Object -First 1
      Invoke $(if ($hit) { $hit } else { $all[0] }); Start-Sleep 2
    }
    'pick'  {   # pick:<ComboBox name>=<item text prefix>
      $cb, $item = $v -split '=', 2
      (Find $w $cb).GetCurrentPattern([System.Windows.Automation.ExpandCollapsePattern]::Pattern).Expand(); Start-Sleep 1
      $e = $w.FindAll($T::Descendants, [System.Windows.Automation.Condition]::TrueCondition) | Where-Object { $_.Current.Name -like "$item*" } | Select-Object -First 1
      if (-not $e) { throw "Item '$item' not found in '$cb'" }
      Invoke $e; Start-Sleep 1
    }
    'wait'  { Start-Sleep ([double]$v) }
    'scroll' {
      $sv = $w.FindFirst($T::Descendants, (New-Object System.Windows.Automation.PropertyCondition($A::ControlTypeProperty, [System.Windows.Automation.ControlType]::Pane)))
      $c = New-Object System.Windows.Automation.PropertyCondition($A::IsScrollPatternAvailableProperty, $true)
      $scr = $w.FindAll($T::Descendants, $c) | Select-Object -Last 1
      $p = $scr.GetCurrentPattern([System.Windows.Automation.ScrollPattern]::Pattern)
      $p.SetScrollPercent(-1, [double]$v)
      Start-Sleep 1
    }
    'shot'  {
      $h = [IntPtr]$w.Current.NativeWindowHandle
      # Alt tap lets a background process take foreground; don't minimize/restore — WinUI shows only its background until it redraws.
      [System.Windows.Forms.SendKeys]::SendWait('%'); [W]::SetForegroundWindow($h) | Out-Null; Start-Sleep -Milliseconds 1500
      $r = New-Object W+RECT; [W]::GetWindowRect($h, [ref]$r) | Out-Null
      $bmp = New-Object System.Drawing.Bitmap ($r.R - $r.L), ($r.B - $r.T)
      $g = [System.Drawing.Graphics]::FromImage($bmp)
      $g.CopyFromScreen($r.L, $r.T, 0, 0, $bmp.Size); $g.Dispose()
      $f = Join-Path $Out $v; $bmp.Save($f, [System.Drawing.Imaging.ImageFormat]::Png); $bmp.Dispose()
      "saved $f"
    }
    'dump'  {
      # Every visible text/button name, in tree order: proves content without relying on pixels.
      $w.FindAll($T::Descendants, [System.Windows.Automation.Condition]::TrueCondition) |
        Where-Object { $_.Current.Name -and $_.Current.ControlType.ProgrammaticName -match 'Text|Button|ComboBox|ToggleButton|Group|ListItem|Expander' } |
        ForEach-Object { "{0,-12} {1}" -f ($_.Current.ControlType.ProgrammaticName -replace 'ControlType\.',''), ($_.Current.Name -replace "`r?`n", ' / ') } |
        Select-Object -First ([int]$v)
    }
  }
}
