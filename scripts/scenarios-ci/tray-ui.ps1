<#
    The tray as a person sees it, through Windows UI Automation: the icon in the notification area, its menu and its balloons. One
    command per run, one JSON document on standard output, so the scenarios (windows-tray.mjs) own every wait and this script owns none.

        tray-ui.ps1 session
        tray-ui.ps1 icons
        tray-ui.ps1 open-menu -TrayProcessId <pid> [-Via taskbar|message]
        tray-ui.ps1 menu -TrayProcessId <pid>
        tray-ui.ps1 invoke -TrayProcessId <pid> -Item <text>
        tray-ui.ps1 close-menu -TrayProcessId <pid>
        tray-ui.ps1 balloons
        tray-ui.ps1 dump [-Depth <n>] [-Root desktop|taskbar]

    It acts on the desktop of the session it runs in, so it is meant for the clean GitHub-hosted Windows runner (and refuses to move the
    mouse anywhere else).
#>
param(
    [Parameter(Mandatory, Position = 0)]
    [ValidateSet('session', 'icons', 'open-menu', 'menu', 'invoke', 'close-menu', 'balloons', 'dump')]
    [string] $Command,
    [int] $TrayProcessId = 0,
    [string] $Item = '',
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

# Opens the hidden icons when Weir's icon is not among the visible ones.
function Show-Overflow {
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

# The tray's hidden window receives the notification area's mouse messages; sending it the same message a right click on the icon makes
# opens the same menu, for a desktop whose taskbar cannot be reached.
function Open-MenuByMessage([int] $owner) {
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
            # WM_USER + 1024, the callback message of the notification icon, with WM_RBUTTONUP.
            if ([TrayUiNative]::PostMessage($window, 0x0800, [IntPtr]1, [IntPtr]0x0205)) { $sent++ }
        }
    }
    $sent
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
    'open-menu' {
        if ($TrayProcessId -le 0) { throw '-TrayProcessId is needed.' }
        if ($Via -eq 'message') {
            $sent = Open-MenuByMessage $TrayProcessId
            Write-Json ([pscustomobject]@{ via = 'message'; sent = $sent })
            break
        }
        $icon = Find-TrayIcon | Select-Object -First 1
        $opened = $false
        if (-not $icon) { $opened = Show-Overflow; $icon = Find-TrayIcon | Select-Object -First 1 }
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
    'close-menu' {
        Assert-ThrowawayDesktop
        [System.Windows.Forms.SendKeys]::SendWait('{ESC}')
        Write-Json ([pscustomobject]@{ closed = $true })
    }
    'balloons' { Write-Json @(Get-Balloons) }
    'dump' {
        $start = if ($Root -eq 'taskbar') { @(Find-TaskbarRoots) } else { @($automation::RootElement) }
        Write-Json @($start | ForEach-Object { Get-Tree $_ $Depth })
    }
}
exit 0
