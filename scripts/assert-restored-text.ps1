param([int]$ProcessId, [string]$ExpectedFile)
$ErrorActionPreference = 'Stop'
$ExpectedText = (Get-Content -LiteralPath $ExpectedFile -Raw -Encoding UTF8 | ConvertFrom-Json).text
Add-Type -AssemblyName UIAutomationClient
Add-Type -AssemblyName UIAutomationTypes
$deadline = [DateTime]::UtcNow.AddSeconds(60)
while ([DateTime]::UtcNow -lt $deadline) {
    $app = Get-Process -Id $ProcessId -ErrorAction SilentlyContinue
    if (-not $app) { throw 'Updated Next application exited.' }
    $app.Refresh()
    if ($app.MainWindowHandle -ne [IntPtr]::Zero) {
        $window = [System.Windows.Automation.AutomationElement]::FromHandle($app.MainWindowHandle)
        $condition = New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::ControlTypeProperty, [System.Windows.Automation.ControlType]::Edit)
        $edits = $window.FindAll([System.Windows.Automation.TreeScope]::Descendants, $condition)
        foreach ($edit in $edits) {
            $pattern = $null
            if ($edit.TryGetCurrentPattern([System.Windows.Automation.ValuePattern]::Pattern, [ref]$pattern)) {
                if ($pattern.Current.Value.Replace("`r`n", "`n").Contains($ExpectedText.Replace("`r`n", "`n").TrimEnd())) {
                    Write-Host 'NEXT_RESTORED_TEXT_OK'; exit 0
                }
            }
        }
    }
    Start-Sleep -Milliseconds 500
}
throw 'Restored result text was not found in the real installed application UI.'
