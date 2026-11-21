[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)][int]$AppProcessId,
    [string]$BackupPath,
    [Parameter(Mandatory = $true)][ValidateSet('Exercise', 'Recycle', 'Restart', 'Startup', 'ExitComplete', 'ExitButton', 'ExitX')][string]$Mode,
    [Parameter(Mandatory = $true)][string]$Screenshot
)
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName UIAutomationClient, UIAutomationTypes, System.Drawing
Add-Type @'
using System;
using System.Runtime.InteropServices;
public static class WindowCapture {
    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool PrintWindow(IntPtr window, IntPtr dc, uint flags);
    [DllImport("user32.dll")]
    public static extern IntPtr SendMessage(IntPtr window, uint message, IntPtr wParam, IntPtr lParam);
}
'@
$OutputEncoding = [Console]::OutputEncoding = [Text.UTF8Encoding]::new($false)
$desktop = [Windows.Automation.AutomationElement]::RootElement
$descendants = [Windows.Automation.TreeScope]::Descendants
$processCondition = [Windows.Automation.PropertyCondition]::new([Windows.Automation.AutomationElement]::ProcessIdProperty, $AppProcessId)

function Find-Control([string]$Property, [string]$Value) {
    $propertyId = if ($Property -eq 'Id') { [Windows.Automation.AutomationElement]::AutomationIdProperty } else { [Windows.Automation.AutomationElement]::NameProperty }
    $condition = [Windows.Automation.AndCondition]::new($processCondition, [Windows.Automation.PropertyCondition]::new($propertyId, $Value))
    $desktop.FindFirst($descendants, $condition)
}
function Wait-Until([scriptblock]$Check, [string]$Description) {
    $watch = [Diagnostics.Stopwatch]::StartNew()
    do {
        if (& $Check) { return }
        if ((Get-Process -Id $AppProcessId -ErrorAction SilentlyContinue) -eq $null) { throw "Test app exited while waiting for $Description" }
        Start-Sleep -Milliseconds 50
    } while ($watch.Elapsed.TotalSeconds -lt 10)
    throw "Timed out waiting for $Description"
}
function Control([string]$Id) {
    Wait-Until { $null -ne (Find-Control Id $Id) } "control $Id"
    Find-Control Id $Id
}
function Invoke-Control($Element) {
    ([Windows.Automation.InvokePattern]$Element.GetCurrentPattern([Windows.Automation.InvokePattern]::Pattern)).Invoke()
}
function Value($Element) {
    ([Windows.Automation.ValuePattern]$Element.GetCurrentPattern([Windows.Automation.ValuePattern]::Pattern)).Current.Value
}
function Require([bool]$Condition, [string]$ErrorText) { if (-not $Condition) { throw $ErrorText } }
function Main-Ready {
    $button = Find-Control Name '手动模式'
    if ($null -eq $button -or -not $button.Current.IsEnabled) { return $false }
    return $true
}
function Save-Screenshot([string]$Path, [string]$WindowId = 'MigrationMainWindow') {
    $window = Control $WindowId
    $bounds = $window.Current.BoundingRectangle
    Require ($bounds.Width -gt 0 -and $bounds.Height -gt 0) 'Test window has no visible bounds.'
    Require (-not [IO.File]::Exists($Path)) 'Screenshot already exists.'
    $bitmap = [Drawing.Bitmap]::new([int]$bounds.Width, [int]$bounds.Height)
    $graphics = [Drawing.Graphics]::FromImage($bitmap)
    try {
        $dc = $graphics.GetHdc()
        try { Require ([WindowCapture]::PrintWindow([IntPtr]$window.Current.NativeWindowHandle, $dc, 2)) 'Actual window rendering failed.' }
        finally { $graphics.ReleaseHdc($dc) }
        $bitmap.Save($Path, [Drawing.Imaging.ImageFormat]::Png)
    } finally { $graphics.Dispose(); $bitmap.Dispose() }
}
function First-Dialog {
    Invoke-Control (Control ManageVerifiedBackup)
    $window = Control BackupConfirmationFirst
    Wait-Until { (Control CancelBackupAction).Current.HasKeyboardFocus } 'first default cancel focus'
    Require ((Value (Control FrozenBackupPath)) -ceq $BackupPath) 'First confirmation shows a different backup.'
    return $window
}
function Second-Dialog([bool]$Permanent = $true) {
    $null = First-Dialog
    Invoke-Control (Control ConfirmBackupAction)
    $null = Control BackupConfirmationSecond
    Wait-Until { (Control CancelBackupAction).Current.HasKeyboardFocus } 'second default cancel focus'
    if ($Permanent) {
        Require ((Value (Control BackupNameInput)) -ceq '') 'Permanent confirmation was prefilled.'
        Require (-not (Control ConfirmBackupAction).Current.IsEnabled) 'Empty name enabled permanent deletion.'
    }
}

function Answer-Defer([bool]$Accept) {
    Wait-Until { $null -ne (Find-Control Name '尚未验证：保留备份继续？') } 'pending-verification prompt'
    $prompt = Find-Control Name '尚未验证：保留备份继续？'
    $no = $prompt.FindFirst($descendants, [Windows.Automation.PropertyCondition]::new([Windows.Automation.AutomationElement]::AutomationIdProperty, '7'))
    Require ($null -ne $no -and $no.Current.HasKeyboardFocus) 'Deferral must default to No.'
    $button = $prompt.FindFirst($descendants, [Windows.Automation.PropertyCondition]::new(
        [Windows.Automation.AutomationElement]::AutomationIdProperty, $(if ($Accept) { '6' } else { '7' })))
    Require ($null -ne $button) 'The explicit deferral response is missing.'
    Require ($button.Current.NativeWindowHandle -ne 0) 'Native confirmation button has no window handle.'
    # 原生 MessageBox 按钮在当前 UIA 提供程序中不公开 InvokePattern；BM_CLICK 触发其真实按钮动作。
    $null = [WindowCapture]::SendMessage([IntPtr]$button.Current.NativeWindowHandle, 0x00F5, [IntPtr]::Zero, [IntPtr]::Zero)
    Wait-Until { $null -eq (Find-Control Name '尚未验证：保留备份继续？') } 'deferral response'
}
function Open-Verification([string]$Entry = 'MainVerifyData') {
    Invoke-Control (Control $Entry)
    $null = Control MigrationVerificationDialog
    Wait-Until { (Control CloseMigrationVerification).Current.IsEnabled } 'verification record and current properties loaded'
    Require ((Value (Control VerificationBackupPath)) -ceq $BackupPath) 'Verification shows a different backup.'
}
function Close-Verification {
    Invoke-Control (Control CloseMigrationVerification)
    Wait-Until { $null -eq (Find-Control Id MigrationVerificationDialog) } 'verification closed'
}
function Select-BackupMode([bool]$Recycle) {
    $combo = Control VerificationBackupMode
    ([Windows.Automation.ExpandCollapsePattern]$combo.GetCurrentPattern([Windows.Automation.ExpandCollapsePattern]::Pattern)).Expand()
    $choice = $combo.FindFirst($descendants, [Windows.Automation.PropertyCondition]::new(
        [Windows.Automation.AutomationElement]::NameProperty, $(if ($Recycle) { '移入回收站（双确认）' } else { '永久删除（双确认）' })))
    Require ($null -ne $choice) 'The requested explicit backup mode is unavailable.'
    ([Windows.Automation.SelectionItemPattern]$choice.GetCurrentPattern([Windows.Automation.SelectionItemPattern]::Pattern)).Select()
    ([Windows.Automation.ExpandCollapsePattern]$combo.GetCurrentPattern([Windows.Automation.ExpandCollapsePattern]::Pattern)).Collapse()
}

function Finish-App {
    $process = Get-Process -Id $AppProcessId -ErrorAction SilentlyContinue
    if ($null -eq $process) { return }
    $window = Control MigrationMainWindow
    ([Windows.Automation.WindowPattern]$window.GetCurrentPattern([Windows.Automation.WindowPattern]::Pattern)).Close()
    Wait-Until { $null -eq (Get-Process -Id $AppProcessId -ErrorAction SilentlyContinue) -or $null -ne (Find-Control Id '7') } 'application exit or pending-verification prompt'
    if ($null -ne (Find-Control Id '7')) { Answer-Defer $true }
    Require ($process.WaitForExit(10000)) 'Application did not finish its original close action.'
}

try {
    Wait-Until { Main-Ready } 'startup initialization'
    if ($Mode -eq 'Startup') {
        Save-Screenshot $Screenshot
        [ordered]@{ mode = $Mode; processId = $AppProcessId; initializationCompleted = $true; screenshot = $Screenshot } | ConvertTo-Json -Compress
        exit 0
    }
    Invoke-Control (Find-Control Name '手动模式')
    Require ((Value (Control MainBackupPath)) -ceq $BackupPath) 'Result did not recover the exact backup path.'
    $verification = (Control MainVerification).Current.Name
    if ($Mode -like 'Exit*') {
        $action = {
            if ($Mode -eq 'ExitComplete') { Invoke-Control (Control CompleteMigrationResult) }
            elseif ($Mode -eq 'ExitButton') { Invoke-Control (Control CloseMigrationApplication) }
            else {
                $window = Control MigrationMainWindow
                ([Windows.Automation.WindowPattern]$window.GetCurrentPattern([Windows.Automation.WindowPattern]::Pattern)).Close()
            }
        }
        & $action
        Answer-Defer $false
        Require ((Value (Control MainBackupPath)) -ceq $BackupPath) 'Refusing deferral changed the current result.'
        Require ([IO.Directory]::Exists($BackupPath)) 'Refusing deferral removed the backup.'
        Save-Screenshot $Screenshot
        & $action
        Answer-Defer $true
        if ($Mode -eq 'ExitComplete') {
            Wait-Until { $null -eq (Find-Control Id MainVerifyData) -or (Find-Control Id MainVerifyData).Current.IsOffscreen } 'completion returned to first step'
            Finish-App
        } else {
            $process = Get-Process -Id $AppProcessId -ErrorAction SilentlyContinue
            if ($null -ne $process) { Require ($process.WaitForExit(10000)) 'Confirmed exit did not close the app.' }
        }
        Require ([IO.Directory]::Exists($BackupPath)) 'Ignoring verification removed the backup.'
        [ordered]@{ mode = $Mode; refusedStayed = $true; acceptedContinued = $true; backupRetained = $true; screenshot = $Screenshot } | ConvertTo-Json -Compress
        exit 0
    }
    Open-Verification
    Save-Screenshot ($Screenshot -replace '\.png$', '-unverified.png') 'MigrationVerificationDialog'
    $sourceName = [IO.Path]::GetFileName((Value (Control VerificationSourcePath)).TrimEnd('\', '/'))
    if ($Mode -in @('Exercise', 'Recycle')) {
        Require (-not (Control ConfirmMigrationVerification).Current.IsEnabled) 'Unchecked usage acknowledgement can verify data.'
        Require (-not (Control ManageVerifiedBackup).Current.IsEnabled) 'Unverified result permits deletion.'
        Invoke-Control (Control CloseMigrationVerification)
        Answer-Defer $false
        Require ($null -ne (Control MigrationVerificationDialog)) 'Refusing deferral closed verification.'
        Invoke-Control (Control CloseMigrationVerification)
        Answer-Defer $true
        Wait-Until { $null -eq (Find-Control Id MigrationVerificationDialog) -and (Main-Ready) } 'unverified dialog closed retaining backup'
        Open-Verification
        Require (-not (Control ManageVerifiedBackup).Current.IsEnabled) 'Deferral was incorrectly recorded as verification.'
        $ack = Control VerificationAcknowledgement
        ([Windows.Automation.TogglePattern]$ack.GetCurrentPattern([Windows.Automation.TogglePattern]::Pattern)).Toggle()
        Wait-Until { (Control ConfirmMigrationVerification).Current.IsEnabled } 'usage acknowledgement enabled verification'
        Invoke-Control (Control ConfirmMigrationVerification)
        Wait-Until { (Control ManageVerifiedBackup).Current.IsEnabled } 'user verification persisted'
        Save-Screenshot ($Screenshot -replace '\.png$', '-verification.png') 'MigrationVerificationDialog'
        Select-BackupMode ($Mode -eq 'Recycle')
        $null = First-Dialog
        Invoke-Control (Control CancelBackupAction)
        Wait-Until { $null -eq (Find-Control Id BackupConfirmationFirst) -and (Control ManageVerifiedBackup).Current.IsEnabled } 'first cancellation completed'
        Require ([IO.Directory]::Exists($BackupPath)) 'First cancellation removed the backup.'
        if ($Mode -eq 'Recycle') {
            Second-Dialog -Permanent $false
            Require ((Control ConfirmBackupAction).Current.IsEnabled) 'Recycle incorrectly requires permanent-delete text.'
            Save-Screenshot ($Screenshot -replace '\.png$', '-second.png') 'BackupConfirmationSecond'
            Invoke-Control (Control CancelBackupAction)
            Wait-Until { $null -eq (Find-Control Id BackupConfirmationSecond) -and (Control ManageVerifiedBackup).Current.IsEnabled } 'recycle cancellation completed'
            Require ([IO.Directory]::Exists($BackupPath)) 'Recycle cancellation removed the backup.'
        } else {
            Second-Dialog
            $input = Control BackupNameInput
            ([Windows.Automation.ValuePattern]$input.GetCurrentPattern([Windows.Automation.ValuePattern]::Pattern)).SetValue([IO.Path]::GetFileName($BackupPath))
            Require (-not (Control ConfirmBackupAction).Current.IsEnabled) 'Random backup suffix was accepted instead of the original name.'
            Invoke-Control (Control CancelBackupAction)
            Wait-Until { $null -eq (Find-Control Id BackupConfirmationSecond) -and (Control ManageVerifiedBackup).Current.IsEnabled } 'second cancellation completed'
            Require ([IO.Directory]::Exists($BackupPath)) 'Second cancellation removed the backup.'
            Second-Dialog
            $input = Control BackupNameInput
            ([Windows.Automation.ValuePattern]$input.GetCurrentPattern([Windows.Automation.ValuePattern]::Pattern)).SetValue($sourceName)
            Wait-Until { (Control ConfirmBackupAction).Current.IsEnabled } 'original folder name accepted'
            Save-Screenshot ($Screenshot -replace '\.png$', '-second.png') 'BackupConfirmationSecond'
            Invoke-Control (Control ConfirmBackupAction)
            Wait-Until { $null -eq (Find-Control Id BackupConfirmationSecond) -and -not (Control ManageVerifiedBackup).Current.IsEnabled -and -not [IO.Directory]::Exists($BackupPath) } 'authorized deletion ended'
        }
    } else {
        Require (-not (Control ManageVerifiedBackup).Current.IsEnabled) 'Deleted backup remains actionable after restart.'
    }
    Close-Verification
    Wait-Until { Main-Ready } 'main state refreshed'
    $manualStatus = (Control MainTransactionStatus).Current.Name
    Save-Screenshot $Screenshot
    Invoke-Control (Find-Control Name '⏩ 一键迁移')
    $tab = Find-Control Name '已迁移'
    Require ($null -ne $tab) 'Migrated tab is absent.'
    ([Windows.Automation.SelectionItemPattern]$tab.GetCurrentPattern([Windows.Automation.SelectionItemPattern]::Pattern)).Select()
    Require ((Value (Control TaskBackupPath)) -ceq $BackupPath) 'Task card lost the recorded backup path.'
    Open-Verification TaskVerifyData
    $canManage = (Control ManageVerifiedBackup).Current.IsEnabled
    Require ($canManage -eq ($Mode -eq 'Recycle')) 'Task entry does not reflect the actual retained/deleted backup.'
    Require (-not (Control ConfirmMigrationVerification).Current.IsEnabled) 'Confirmed verification was lost between entry points.'
    Close-Verification
    Finish-App
    [ordered]@{ mode = $Mode; processId = $AppProcessId; backupPath = $BackupPath; verification = $verification;
        manualStatus = $manualStatus; taskManageEnabled = $canManage; screenshot = $Screenshot;
        firstCancelPreserved = ($Mode -ne 'Restart'); secondCancelPreserved = ($Mode -ne 'Restart'); defaultCancelFocused = ($Mode -ne 'Restart') } | ConvertTo-Json -Compress
    exit 0
} catch {
    [Console]::Error.WriteLine(($_ | Out-String) + $_.ScriptStackTrace)
    exit 1
}
