param([string[]]$Editions = @('Studio', 'Server', 'Client'))
# The first start asks for the interface language in a window of its own. This clicks the first answer in each edition (through UI Automation, so it needs
# a desktop session) and checks that the program is still there afterwards and shows its main window. Choosing used to close the program, because the
# question was the only window open at that moment.
. "$PSScriptRoot/common.ps1"
Add-Type -AssemblyName UIAutomationClient, UIAutomationTypes
$exe = Join-Path $repoRoot 'src/TriAsr.App/bin/x64/Release/net10.0-windows/win-x64/TriAsr.App.exe'
if (-not (Test-Path -LiteralPath $exe)) { throw 'Build Release x64 first.' }
$priorRoot = $env:TRIASR_DATA_ROOT; $priorEdition = $env:TRIASR_EDITION; $priorModel = $env:TRIASR_MODEL_ROOT; $priorLanguage = $env:TRIASR_LANGUAGE; $priorDotnet = $env:DOTNET_ROOT
$env:DOTNET_ROOT = Split-Path $script:Dotnet -Parent
$env:TRIASR_MODEL_ROOT = $null
$env:TRIASR_LANGUAGE = $null
$results = @()
try {
    foreach ($edition in $Editions) {
        $root = Join-Path $repoRoot ('artifacts/smoke/language-' + $edition.ToLowerInvariant() + '-' + [guid]::NewGuid().ToString('N'))
        Write-SmokeSettings $root $edition
        $env:TRIASR_EDITION = $edition.ToLowerInvariant()
        $env:TRIASR_DATA_ROOT = $root
        $process = Start-Process -FilePath $exe -PassThru
        try {
            # 1. the question appears
            $deadline = (Get-Date).AddSeconds(60); $chooser = [IntPtr]::Zero
            while ((Get-Date) -lt $deadline -and -not $process.HasExited) {
                $process.Refresh()
                if ($process.MainWindowHandle -ne [IntPtr]::Zero) { $chooser = $process.MainWindowHandle; break }
                Start-Sleep -Milliseconds 300
            }
            if ($chooser -eq [IntPtr]::Zero) { throw "$edition showed no language question (exited: $($process.HasExited))." }
            $element = [System.Windows.Automation.AutomationElement]::FromHandle($chooser)
            $isButton = New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::ControlTypeProperty, [System.Windows.Automation.ControlType]::Button)
            $buttons = $element.FindAll([System.Windows.Automation.TreeScope]::Descendants, $isButton)
            if ($buttons.Count -lt 5) { throw "$edition offered $($buttons.Count) languages instead of 5." }
            $buttons[0].GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke()
            # 2. the program is still running and opens its main window
            $deadline = (Get-Date).AddSeconds(90); $main = $null
            $ownedByProcess = New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::ProcessIdProperty, $process.Id)
            while ((Get-Date) -lt $deadline -and -not $process.HasExited) {
                $names = @([System.Windows.Automation.AutomationElement]::RootElement.FindAll([System.Windows.Automation.TreeScope]::Children, $ownedByProcess) | ForEach-Object { $_.Current.Name })
                $main = $names | Where-Object { $_ -match "^Mockingbird $edition .+ v\d" } | Select-Object -First 1
                if ($main) { break }
                Start-Sleep -Milliseconds 300
            }
            if (-not $main) { throw "$edition did not open its main window after the language was chosen (exited: $($process.HasExited))." }
            $results += [pscustomobject]@{ edition = $edition; mainWindow = $main }
        } finally { if (-not $process.HasExited) { Stop-Process -Id $process.Id } }
    }
    $results | ConvertTo-Json
} finally {
    $env:TRIASR_DATA_ROOT = $priorRoot; $env:TRIASR_EDITION = $priorEdition; $env:TRIASR_MODEL_ROOT = $priorModel; $env:TRIASR_LANGUAGE = $priorLanguage; $env:DOTNET_ROOT = $priorDotnet
}
