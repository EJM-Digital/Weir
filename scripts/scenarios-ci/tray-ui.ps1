<#
    The tray as a person sees it, through Windows UI Automation: the icon in the notification area, its menu and its balloons. One
    command per run, one JSON document on standard output, so the scenarios (windows-tray.mjs) own every wait and this script owns none.

        tray-ui.ps1 session
        tray-ui.ps1 icons
        tray-ui.ps1 show-hidden
        tray-ui.ps1 hover-icons
        tray-ui.ps1 open-menu -TrayProcessId <pid> [-Via taskbar|message]
        tray-ui.ps1 menu -TrayProcessId <pid>
        tray-ui.ps1 invoke -TrayProcessId <pid> -Item <text>
        tray-ui.ps1 click-item -TrayProcessId <pid> -Item <text>
        tray-ui.ps1 close-menu -TrayProcessId <pid>
        tray-ui.ps1 click-icon -Clicks single|double
        tray-ui.ps1 click-balloon -TrayProcessId <pid>
        tray-ui.ps1 window -TrayProcessId <pid>
        tray-ui.ps1 window-set -TrayProcessId <pid> -Value <text>
        tray-ui.ps1 window-press -TrayProcessId <pid> -Item <button or option>
        tray-ui.ps1 balloons
        tray-ui.ps1 find-text -Contains <text>
        tray-ui.ps1 dump [-Depth <n>] [-Root desktop|taskbar]

    It acts on the desktop of the session it runs in, so it is meant for the clean GitHub-hosted Windows runner (and refuses to move the
    mouse anywhere else).
#>
param(
    [Parameter(Mandatory, Position = 0)]
    [ValidateSet('session', 'icons', 'show-hidden', 'hover-icons', 'open-menu', 'menu', 'invoke', 'click-item', 'close-menu', 'click-icon', 'click-balloon', 'window', 'window-set', 'window-press', 'balloons', 'find-text', 'dump')]
    [string] $Command,
    [int] $TrayProcessId = 0,
    [string] $Item = '',
    [string] $Contains = '',
    [string] $Value = '',
    [ValidateSet('single', 'double')]
    [string] $Clicks = 'single',
    [ValidateSet('taskbar', 'message')]
    [string] $Via = 'taskbar',
    [int] $Depth = 4,
    [ValidateSet('desktop', 'taskbar')]
    [string] $Root = 'desktop'
)

$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName UIAutomationClient, UIAutomationTypes, System.Windows.Forms

Add-Type -TypeDefinition @'
using System;
using System.Runtime.InteropServices;
using System.Text;

public static class TrayUiNative
{
    public delegate bool EnumWindowsProc(IntPtr window, IntPtr parameter);

    [DllImport("user32.dll")] public static extern bool EnumWindows(EnumWindowsProc callback, IntPtr parameter);
    [DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(IntPtr window, out uint processId);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] public static extern int GetClassName(IntPtr window, StringBuilder text, int capacity);
    [DllImport("user32.dll")] public static extern bool PostMessage(IntPtr window, uint message, IntPtr wParam, IntPtr lParam);
    [DllImport("user32.dll")] public static extern bool SetCursorPos(int x, int y);
    [DllImport("user32.dll")] public static extern void mouse_event(uint flags, uint dx, uint dy, uint data, UIntPtr extra);
    [DllImport("kernel32.dll")] public static extern bool ProcessIdToSessionId(uint processId, out uint sessionId);
}
'@

$automation = [System.Windows.Automation.AutomationElement]
$tree = [System.Windows.Automation.TreeScope]

function Write-Json($value) { ConvertTo-Json -Compress -Depth 8 -InputObject $value }

function Get-Children($element) { $element.FindAll($tree::Children, [System.Windows.Automation.Condition]::TrueCondition) }

function Get-Rectangle($element) {
    $box = $element.Current.BoundingRectangle
    if ([double]::IsInfinity($box.X)) { return $null }
    [pscustomobject]@{ x = [int]$box.X; y = [int]$box.Y; width = [int]$box.Width; height = [int]$box.Height }
}

function Get-Summary($element) {
    $current = $element.Current
    [pscustomobject]@{
        name        = $current.Name
        type        = $current.ControlType.ProgrammaticName -replace '^ControlType\.', ''
        className   = $current.ClassName
        automationId = $current.AutomationId
        processId   = $current.ProcessId
        enabled     = $current.IsEnabled
        offscreen   = $current.IsOffscreen
        bounds      = Get-Rectangle $element
    }
}

# --- the session ----------------------------------------------------------------------------------------------------------------

function Get-SessionFacts {
    $own = [uint32]0
    [void][TrayUiNative]::ProcessIdToSessionId([uint32]$PID, [ref]$own)
    $explorer = @(Get-Process -Name explorer -ErrorAction SilentlyContinue | Where-Object { $_.SessionId -eq $own })
    $taskbar = $automation::RootElement.FindFirst($tree::Children, (New-Object System.Windows.Automation.PropertyCondition($automation::ClassNameProperty, 'Shell_TrayWnd')))
    [pscustomobject]@{
        sessionId       = [int]$own
        userInteractive = [Environment]::UserInteractive
        explorerRuns    = $explorer.Count -gt 0
        taskbarFound    = $null -ne $taskbar
        user            = [Environment]::UserName
        os              = [Environment]::OSVersion.VersionString
        screen          = "$([System.Windows.Forms.SystemInformation]::PrimaryMonitorSize.Width)x$([System.Windows.Forms.SystemInformation]::PrimaryMonitorSize.Height)"
    }
}

# --- the notification area ------------------------------------------------------------------------------------------------------

function Find-TaskbarRoots {
    $names = 'Shell_TrayWnd', 'Shell_SecondaryTrayWnd', 'NotifyIconOverflowWindow', 'TopLevelWindowForOverflowXamlIsland'
    foreach ($child in (Get-Children $automation::RootElement)) {
        if ($names -contains $child.Current.ClassName) { $child }
    }
}

function Get-Buttons($root) {
    $buttons = New-Object System.Windows.Automation.PropertyCondition($automation::ControlTypeProperty, [System.Windows.Automation.ControlType]::Button)
    $root.FindAll($tree::Descendants, $buttons)
}

# The buttons of the notification area, and of the overflow when it is open, with where each one is.
function Get-NotificationButtons {
    foreach ($root in (Find-TaskbarRoots)) {
        $where = $root.Current.ClassName
        foreach ($button in (Get-Buttons $root)) {
            $summary = Get-Summary $button
            $summary | Add-Member -NotePropertyName area -NotePropertyValue $where
            $summary
        }
    }
}

function Find-TrayIcon {
    @(Get-NotificationButtons | Where-Object { $_.name -like 'Weir*' })
}

# It moves the real mouse and sends real keys, so it does so only where nobody else is using the desktop.
function Assert-ThrowawayDesktop {
    if ($env:GITHUB_ACTIONS -ne 'true' -or $env:RUNNER_ENVIRONMENT -ne 'github-hosted') { throw 'This moves the mouse and sends keys, so it runs only on a GitHub-hosted runner.' }
}

function Click-At($x, $y, [switch] $Right) {
    Assert-ThrowawayDesktop
    [void][TrayUiNative]::SetCursorPos($x, $y)
    $down, $up = if ($Right) { 0x0008, 0x0010 } else { 0x0002, 0x0004 }
    [TrayUiNative]::mouse_event($down, 0, 0, 0, [UIntPtr]::Zero)
    [TrayUiNative]::mouse_event($up, 0, 0, 0, [UIntPtr]::Zero)
}

function Get-Centre($bounds) { @(($bounds.x + [int]($bounds.width / 2)), ($bounds.y + [int]($bounds.height / 2))) }

# A tray that was ended by force leaves its icon where it was until the pointer passes over it. Passing over every Weir icon makes Windows drop
# the ones whose program is gone; the one that is alive stays.
function Move-OverIcons {
    Assert-ThrowawayDesktop
    $moved = 0
    foreach ($icon in @(Find-TrayIcon)) {
        if (-not $icon.bounds) { continue }
        $x, $y = Get-Centre $icon.bounds
        [void][TrayUiNative]::SetCursorPos($x, $y)
        [void][TrayUiNative]::SetCursorPos($x + 3, $y + 3)
        $moved++
    }
    $moved
}

# Opens the hidden icons when Weir's icon is not among the visible ones, unless they are open already.
function Show-Overflow {
    if (Find-TaskbarRoots | Where-Object { $_.Current.ClassName -eq 'TopLevelWindowForOverflowXamlIsland' }) { return $false }
    $chevron = Get-NotificationButtons | Where-Object { $_.name -match 'Show Hidden Icons|Notification Chevron|Hidden icons' } | Select-Object -First 1
    if (-not $chevron -or -not $chevron.bounds) { return $false }
    $x, $y = Get-Centre $chevron.bounds
    Click-At $x $y
    $true
}

# --- the menu -------------------------------------------------------------------------------------------------------------------

function Find-Menu([int] $owner) {
    foreach ($child in (Get-Children $automation::RootElement)) {
        if ($owner -gt 0 -and $child.Current.ProcessId -ne $owner) { continue }
        if ($child.Current.ControlType -eq [System.Windows.Automation.ControlType]::Menu) { return $child }
        $menu = $child.FindFirst($tree::Descendants, (New-Object System.Windows.Automation.PropertyCondition($automation::ControlTypeProperty, [System.Windows.Automation.ControlType]::Menu)))
        if ($menu) { return $menu }
    }
    $null
}

function Get-MenuItems($menu) {
    $items = New-Object System.Windows.Automation.PropertyCondition($automation::ControlTypeProperty, [System.Windows.Automation.ControlType]::MenuItem)
    foreach ($item in $menu.FindAll($tree::Descendants, $items)) {
        $summary = Get-Summary $item
        $toggle = $null
        $pattern = $null
        if ($item.TryGetCurrentPattern([System.Windows.Automation.TogglePattern]::Pattern, [ref]$pattern)) { $toggle = $pattern.Current.ToggleState.ToString() }
        $summary | Add-Member -NotePropertyName toggle -NotePropertyValue $toggle
        $summary
    }
}

# The tray's hidden window receives the messages the notification area sends for its icon: a right click (WM_RBUTTONUP, 0x205) opens the
# menu, and a click on the balloon (NIN_BALLOONUSERCLICK, 0x405) is the click on a notification. Sending the same message does what the
# click does, for a desktop whose taskbar cannot be reached or whose notifications are not shown.
function Send-NotifyIconMessage([int] $owner, [int] $mouseMessage) {
    Assert-ThrowawayDesktop
    $windows = New-Object System.Collections.Generic.List[IntPtr]
    $callback = [TrayUiNative+EnumWindowsProc] {
        param($window, $parameter)
        $process = [uint32]0
        [void][TrayUiNative]::GetWindowThreadProcessId($window, [ref]$process)
        if ($process -eq $owner) { $windows.Add($window) }
        $true
    }
    [void][TrayUiNative]::EnumWindows($callback, [IntPtr]::Zero)
    $sent = 0
    foreach ($window in $windows) {
        $name = New-Object System.Text.StringBuilder 256
        [void][TrayUiNative]::GetClassName($window, $name, 256)
        if ($name.ToString() -like 'WindowsForms10.Window*') {
            # WM_USER + 1024 is the callback message of the notification icon; its lParam says what happened to the icon.
            if ([TrayUiNative]::PostMessage($window, 0x0800, [IntPtr]1, [IntPtr]$mouseMessage)) { $sent++ }
        }
    }
    $sent
}

# --- a window of the tray (the question about the port) ----------------------------------------------------------------------------

function Find-Window([int] $owner) {
    foreach ($child in (Get-Children $automation::RootElement)) {
        if ($child.Current.ProcessId -eq $owner -and $child.Current.ControlType -eq [System.Windows.Automation.ControlType]::Window) { return $child }
    }
    $null
}

function Find-All($window, $type) {
    $window.FindAll($tree::Descendants, (New-Object System.Windows.Automation.PropertyCondition($automation::ControlTypeProperty, $type)))
}

function Get-WindowFacts($window) {
    $pattern = $null
    [pscustomobject]@{
        title   = $window.Current.Name
        texts   = @(Find-All $window ([System.Windows.Automation.ControlType]::Text) | ForEach-Object { $_.Current.Name } | Where-Object { $_ })
        edits   = @(Find-All $window ([System.Windows.Automation.ControlType]::Edit) | ForEach-Object { if ($_.TryGetCurrentPattern([System.Windows.Automation.ValuePattern]::Pattern, [ref]$pattern)) { $pattern.Current.Value } else { '' } })
        buttons = @(Find-All $window ([System.Windows.Automation.ControlType]::Button) | ForEach-Object { [pscustomobject]@{ name = $_.Current.Name; enabled = $_.Current.IsEnabled } })
        options = @(Find-All $window ([System.Windows.Automation.ControlType]::RadioButton) | ForEach-Object { if ($_.TryGetCurrentPattern([System.Windows.Automation.SelectionItemPattern]::Pattern, [ref]$pattern)) { [pscustomobject]@{ name = $_.Current.Name; enabled = $_.Current.IsEnabled; selected = $pattern.Current.IsSelected } } })
    }
}

# --- balloons -------------------------------------------------------------------------------------------------------------------

# Every piece of text in a window that looks like a notification: the toast windows of the shell.
function Get-Balloons {
    $texts = New-Object System.Windows.Automation.PropertyCondition($automation::ControlTypeProperty, [System.Windows.Automation.ControlType]::Text)
    foreach ($child in (Get-Children $automation::RootElement)) {
        $class = $child.Current.ClassName
        $name = $child.Current.Name
        if ($class -notmatch 'ToastWindow|NotificationToast|Windows.UI.Core.CoreWindow|XamlExplorerHostIslandWindow|ControlCenterWindow' -and $name -notmatch 'notification|Weir') { continue }
        $found = @($child.FindAll($tree::Descendants, $texts) | ForEach-Object { $_.Current.Name } | Where-Object { $_ })
        if ($found.Count -gt 0) { [pscustomobject]@{ window = $name; className = $class; processId = $child.Current.ProcessId; texts = $found } }
    }
}

# Everything on the desktop whose name holds the text, with the class of the window it sits in.
function Find-Text([string] $text) {
    foreach ($top in (Get-Children $automation::RootElement)) {
        foreach ($element in $top.FindAll($tree::Descendants, [System.Windows.Automation.Condition]::TrueCondition)) {
            if ($element.Current.Name -like "*$text*") {
                $summary = Get-Summary $element
                $summary | Add-Member -NotePropertyName window -NotePropertyValue $top.Current.ClassName
                $summary | Add-Member -NotePropertyName windowName -NotePropertyValue $top.Current.Name
                $summary
            }
        }
    }
}

# --- a look at the tree, for the person reading a failed run ----------------------------------------------------------------------

function Get-Tree($element, [int] $depth) {
    $summary = Get-Summary $element
    if ($depth -gt 0) {
        $summary | Add-Member -NotePropertyName children -NotePropertyValue @(Get-Children $element | ForEach-Object { Get-Tree $_ ($depth - 1) })
    }
    $summary
}

# --- commands -------------------------------------------------------------------------------------------------------------------

switch ($Command) {
    'session' { Write-Json (Get-SessionFacts) }
    'icons' { Write-Json @(Find-TrayIcon) }
    'show-hidden' { Write-Json ([pscustomobject]@{ opened = Show-Overflow }) }
    'hover-icons' { Write-Json ([pscustomobject]@{ moved = Move-OverIcons }) }
    'open-menu' {
        if ($TrayProcessId -le 0) { throw '-TrayProcessId is needed.' }
        if ($Via -eq 'message') {
            $sent = Send-NotifyIconMessage $TrayProcessId 0x0205
            Write-Json ([pscustomobject]@{ via = 'message'; sent = $sent })
            break
        }
        $opened = $false
        if (-not (Find-TrayIcon)) { $opened = Show-Overflow }
        $icons = @(Find-TrayIcon)
        if ($icons.Count -gt 1) { [void](Move-OverIcons); Write-Json ([pscustomobject]@{ via = 'taskbar'; found = $false; ghosts = $icons.Count }); break }
        $icon = $icons | Select-Object -First 1
        if (-not $icon -or -not $icon.bounds) { Write-Json ([pscustomobject]@{ via = 'taskbar'; found = $false; overflowOpened = $opened }); break }
        $x, $y = Get-Centre $icon.bounds
        Click-At $x $y -Right
        Write-Json ([pscustomobject]@{ via = 'taskbar'; found = $true; icon = $icon; overflowOpened = $opened })
    }
    'menu' {
        $menu = Find-Menu $TrayProcessId
        if (-not $menu) { Write-Json $null; break }
        Write-Json ([pscustomobject]@{ items = @(Get-MenuItems $menu) })
    }
    'invoke' {
        $menu = Find-Menu $TrayProcessId
        if (-not $menu) { throw 'No menu is open.' }
        $match = @(Get-MenuItems $menu) | Where-Object { $_.name -eq $Item }
        if (-not $match) { throw "The menu has no item '$Item'." }
        $found = $menu.FindAll($tree::Descendants, (New-Object System.Windows.Automation.PropertyCondition($automation::NameProperty, $Item))) | Where-Object { $_.Current.ControlType -eq [System.Windows.Automation.ControlType]::MenuItem } | Select-Object -First 1
        $pattern = $null
        if ($found.TryGetCurrentPattern([System.Windows.Automation.TogglePattern]::Pattern, [ref]$pattern)) { $pattern.Toggle() }
        elseif ($found.TryGetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern, [ref]$pattern)) { $pattern.Invoke() }
        else { throw "'$Item' cannot be invoked." }
        Write-Json ([pscustomobject]@{ invoked = $Item })
    }
    'click-item' {
        # A real click, for an item whose handler asks a question and does not return until it is answered: UI Automation's Invoke would wait for it.
        $menu = Find-Menu $TrayProcessId
        if (-not $menu) { throw 'No menu is open.' }
        $match = @(Get-MenuItems $menu) | Where-Object { $_.name -eq $Item -and $_.bounds } | Select-Object -First 1
        if (-not $match) { throw "The menu has no item '$Item' to click." }
        $x, $y = Get-Centre $match.bounds
        Click-At $x $y
        Write-Json ([pscustomobject]@{ clicked = $Item })
    }
    'close-menu' {
        Assert-ThrowawayDesktop
        [System.Windows.Forms.SendKeys]::SendWait('{ESC}')
        Write-Json ([pscustomobject]@{ closed = $true })
    }
    'click-icon' {
        if (-not (Find-TrayIcon)) { Show-Overflow | Out-Null }
        $icons = @(Find-TrayIcon)
        if ($icons.Count -gt 1) { [void](Move-OverIcons); Write-Json ([pscustomobject]@{ found = $false; ghosts = $icons.Count }); break }
        $icon = $icons | Select-Object -First 1
        if (-not $icon -or -not $icon.bounds) { Write-Json ([pscustomobject]@{ found = $false }); break }
        $x, $y = Get-Centre $icon.bounds
        Click-At $x $y
        if ($Clicks -eq 'double') { Click-At $x $y }
        Write-Json ([pscustomobject]@{ found = $true; clicks = $Clicks })
    }
    'click-balloon' {
        Write-Json ([pscustomobject]@{ sent = Send-NotifyIconMessage $TrayProcessId 0x0405 })
    }
    'window' {
        $window = Find-Window $TrayProcessId
        if (-not $window) { Write-Json $null; break }
        Write-Json (Get-WindowFacts $window)
    }
    'window-set' {
        $window = Find-Window $TrayProcessId
        if (-not $window) { throw 'The tray has no window open.' }
        $edit = Find-All $window ([System.Windows.Automation.ControlType]::Edit) | Select-Object -First 1
        if (-not $edit) { throw 'The window has no text box.' }
        $edit.GetCurrentPattern([System.Windows.Automation.ValuePattern]::Pattern).SetValue($Value)
        Write-Json ([pscustomobject]@{ set = $Value })
    }
    'window-press' {
        $window = Find-Window $TrayProcessId
        if (-not $window) { throw 'The tray has no window open.' }
        $found = $window.FindAll($tree::Descendants, (New-Object System.Windows.Automation.PropertyCondition($automation::NameProperty, $Item))) | Select-Object -First 1
        if (-not $found) { throw "The window has no control named '$Item'." }
        $pattern = $null
        if ($found.TryGetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern, [ref]$pattern)) { $pattern.Invoke() }
        elseif ($found.TryGetCurrentPattern([System.Windows.Automation.SelectionItemPattern]::Pattern, [ref]$pattern)) { $pattern.Select() }
        else { throw "'$Item' cannot be pressed." }
        Write-Json ([pscustomobject]@{ pressed = $Item })
    }
    'balloons' { Write-Json @(Get-Balloons) }
    'find-text' { Write-Json @(Find-Text $Contains) }
    'dump' {
        $start = if ($Root -eq 'taskbar') { @(Find-TaskbarRoots) } else { @($automation::RootElement) }
        Write-Json @($start | ForEach-Object { Get-Tree $_ $Depth })
    }
}
exit 0
