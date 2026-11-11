[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)][int]$AppProcessId,
    [Parameter(Mandatory = $true)][string]$BackupPath,
    [Parameter(Mandatory = $true)][ValidateSet('Exercise', 'Restart')][string]$Mode,
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
    Invoke-Control (Control MainManageBackup)
    $window = Control BackupConfirmationFirst
    Wait-Until { (Control CancelBackupAction).Current.HasKeyboardFocus } 'first default cancel focus'
    Require ((Value (Control FrozenBackupPath)) -ceq $BackupPath) 'First confirmation shows a different backup.'
    return $window
}
function Second-Dialog {
    $null = First-Dialog
    Invoke-Control (Control ConfirmBackupAction)
    $null = Control BackupConfirmationSecond
    Wait-Until { (Control CancelBackupAction).Current.HasKeyboardFocus } 'second default cancel focus'
    Require ((Value (Control BackupNameInput)) -ceq '') 'Permanent confirmation was prefilled.'
    Require (-not (Control ConfirmBackupAction).Current.IsEnabled) 'Empty name enabled permanent deletion.'
}

try {
    Wait-Until { Main-Ready } 'startup initialization'
    Invoke-Control (Find-Control Name '手动模式')
    Require ((Value (Control MainBackupPath)) -ceq $BackupPath) 'Result did not recover the exact backup path.'
    $verification = (Control MainVerification).Current.Name
    if ($Mode -eq 'Exercise') {
        Require ((Control MainVerifyData).Current.IsEnabled) 'Unverified result cannot be verified.'
        Require (-not (Control MainManageBackup).Current.IsEnabled) 'Unverified result permits deletion.'
        Invoke-Control (Control MainVerifyData)
        Wait-Until { (Control MainManageBackup).Current.IsEnabled } 'user verification persisted'
        $combo = Control MainBackupMode
        ([Windows.Automation.ExpandCollapsePattern]$combo.GetCurrentPattern([Windows.Automation.ExpandCollapsePattern]::Pattern)).Expand()
        $choice = $combo.FindFirst($descendants, [Windows.Automation.PropertyCondition]::new([Windows.Automation.AutomationElement]::NameProperty, '永久删除（双确认）'))
        Require ($null -ne $choice) 'Explicit permanent mode is unavailable.'
        ([Windows.Automation.SelectionItemPattern]$choice.GetCurrentPattern([Windows.Automation.SelectionItemPattern]::Pattern)).Select()
        ([Windows.Automation.ExpandCollapsePattern]$combo.GetCurrentPattern([Windows.Automation.ExpandCollapsePattern]::Pattern)).Collapse()
        $null = First-Dialog
        Invoke-Control (Control CancelBackupAction)
        Wait-Until { $null -eq (Find-Control Id BackupConfirmationFirst) -and (Control MainManageBackup).Current.IsEnabled } 'first cancellation completed'
        Require ([IO.Directory]::Exists($BackupPath)) 'First cancellation removed the backup.'
        Second-Dialog
        $input = Control BackupNameInput
        ([Windows.Automation.ValuePattern]$input.GetCurrentPattern([Windows.Automation.ValuePattern]::Pattern)).SetValue('wrong-name')
        Require (-not (Control ConfirmBackupAction).Current.IsEnabled) 'Wrong name enabled permanent deletion.'
        Invoke-Control (Control CancelBackupAction)
        Wait-Until { $null -eq (Find-Control Id BackupConfirmationSecond) -and (Control MainManageBackup).Current.IsEnabled } 'second cancellation completed'
        Require ([IO.Directory]::Exists($BackupPath)) 'Second cancellation removed the backup.'
        Second-Dialog
        $input = Control BackupNameInput
        ([Windows.Automation.ValuePattern]$input.GetCurrentPattern([Windows.Automation.ValuePattern]::Pattern)).SetValue([IO.Path]::GetFileName($BackupPath))
        Wait-Until { (Control ConfirmBackupAction).Current.IsEnabled } 'exact name accepted'
        Save-Screenshot ($Screenshot -replace '\.png$', '-second.png') 'BackupConfirmationSecond'
        Invoke-Control (Control ConfirmBackupAction)
        Wait-Until { $null -eq (Find-Control Id BackupConfirmationSecond) -and (Main-Ready) -and -not [IO.Directory]::Exists($BackupPath) } 'authorized deletion ended'
    }
    Require (-not (Control MainManageBackup).Current.IsEnabled) 'Deleted backup remains actionable in the result.'
    $manualStatus = (Control MainTransactionStatus).Current.Name
    Save-Screenshot $Screenshot
    Invoke-Control (Find-Control Name '⏩ 一键迁移')
    $tab = Find-Control Name '已迁移'
    Require ($null -ne $tab) 'Migrated tab is absent.'
    ([Windows.Automation.SelectionItemPattern]$tab.GetCurrentPattern([Windows.Automation.SelectionItemPattern]::Pattern)).Select()
    Require ((Value (Control TaskBackupPath)) -ceq $BackupPath) 'Task card lost the recorded backup path.'
    Require (-not (Control TaskManageBackup).Current.IsEnabled) 'Refreshed task permits deletion of an already deleted backup.'
    [ordered]@{ mode = $Mode; processId = $AppProcessId; backupPath = $BackupPath; verification = $verification;
        manualStatus = $manualStatus; taskManageEnabled = (Control TaskManageBackup).Current.IsEnabled; screenshot = $Screenshot;
        firstCancelPreserved = ($Mode -eq 'Exercise'); secondCancelPreserved = ($Mode -eq 'Exercise'); defaultCancelFocused = ($Mode -eq 'Exercise') } | ConvertTo-Json -Compress
    exit 0
} catch {
    [Console]::Error.WriteLine(($_ | Out-String) + $_.ScriptStackTrace)
    exit 1
}
