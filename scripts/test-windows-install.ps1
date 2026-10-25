param([Parameter(Mandatory=$true)][string]$Version)
$ErrorActionPreference = 'Stop'
$root = Join-Path ([IO.Path]::GetTempPath()) ('journal-next-install-' + [Guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $root | Out-Null
New-Item -ItemType Directory -Force artifacts/installer-logs | Out-Null
$profile = Join-Path $env:LOCALAPPDATA 'JournalWorkflow'
$legacy = Join-Path $env:LOCALAPPDATA 'Journal'
if ((Test-Path $profile) -or (Test-Path $legacy)) { throw 'Install smoke must run on a disposable user profile.' }
New-Item -ItemType Directory -Path $legacy | Out-Null
$sentinel = Join-Path $legacy 'legacy-sentinel.txt'
[IO.File]::WriteAllText($sentinel, 'Previous Journal profile must remain unchanged.')
$legacyHash = (Get-FileHash $sentinel).Hash
$setup = @(Get-ChildItem artifacts/release -Filter '*-OfflineSetup.exe')
if ($setup.Count -ne 1) { throw 'Expected one Next offline installer.' }
$registry = 'HKCU:/Software/Microsoft/Windows/CurrentVersion/Uninstall/DenisPapusha.JournalWorkflow'
$first = Join-Path $root 'Первый Next {app}'
function Stop-Installed([string]$directory) {
    Get-CimInstance Win32_Process | Where-Object { $_.ExecutablePath -and $_.ExecutablePath.StartsWith($directory, [StringComparison]::OrdinalIgnoreCase) } |
        ForEach-Object { Stop-Process -Id $_.ProcessId -Force -ErrorAction SilentlyContinue }
}
function Wait-InstalledApp([string]$directory) {
    $exe = Join-Path $directory 'current/Jornal.Workflow.Desktop.exe'
    $deadline = [DateTime]::UtcNow.AddSeconds(90)
    do {
        $process = Get-Process 'Jornal.Workflow.Desktop' -ErrorAction SilentlyContinue | Where-Object { $_.Path -eq $exe } | Select-Object -First 1
        if ($process -and $process.MainWindowHandle -ne [IntPtr]::Zero) { return $process }
        Start-Sleep -Milliseconds 500
    } while ([DateTime]::UtcNow -lt $deadline)
    throw "Installed application did not open: $directory"
}
try {
    $freshLog = Join-Path (Get-Location) 'artifacts/installer-logs/fresh-install.log'
    $arguments = @('/VERYSILENT','/SUPPRESSMSGBOXES','/NORESTART','/SP-',('/JOURNALNEXTDIR="' + $first + '"'),('/LOG="' + $freshLog + '"'))
    $install = Start-Process $setup[0].FullName -ArgumentList $arguments -PassThru
    if (-not $install.WaitForExit(180000)) { throw 'Offline setup timed out.' }
    if ($install.ExitCode -notin @(0,3010)) { Get-Content $freshLog | Write-Host; throw "Offline setup failed: $($install.ExitCode)" }
    $registered = Get-ItemProperty $registry
    if ($registered.InstallLocation.TrimEnd('\') -ne $first) { throw 'Wrong Next installation registry.' }
    $appPath = Join-Path $first 'current/Jornal.Workflow.Desktop.exe'
    if (-not (Test-Path $appPath)) { throw 'Next EXE is missing after installation.' }
    if (Test-Path (Join-Path $first 'current/Jornal.Desktop.exe')) { throw 'Legacy application was installed.' }
    if (-not (Get-Process 'Jornal.Workflow.Desktop' -ErrorAction SilentlyContinue | Where-Object { $_.Path -eq $appPath })) { $app = Start-Process $appPath -PassThru }
    $null = Wait-InstalledApp $first
    $hostPath = Join-Path $first 'current/AccessHost/Jornal.AccessHost.exe'
    & $hostPath --probe
    if ($LASTEXITCODE -ne 0) { throw 'Installed x86 host did not start.' }
    $fixture = Join-Path $root 'synthetic.mdb'
    & "$env:WINDIR/SysWOW64/WindowsPowerShell/v1.0/powershell.exe" -NoProfile -File tests/Fixtures/New-AccessFixture.ps1 -Destination $fixture -RequireProvider
    if ($LASTEXITCODE -ne 0) { throw 'Synthetic Jet fixture was not created.' }
    & dotnet run --project tools/Journal.Next.InstallProbe -c Release --no-restore -- $hostPath $fixture
    if ($LASTEXITCODE -ne 0) { throw 'Installed host price-read probe failed.' }
    # Run the existing import contract against the real host from this installed release.
    $testHost = Join-Path (Get-Location) 'tests/Jornal.Workflow.Tests/bin/Release/net10.0/AccessHost'
    New-Item -ItemType Directory -Force -Path $testHost | Out-Null
    Copy-Item (Join-Path $first 'current/AccessHost/*') $testHost -Recurse -Force
    $env:WORKFLOW_LEGACY_MDB = $fixture
    $env:WORKFLOW_TEST_ACCESS_HOST = $hostPath
    & dotnet test tests/Jornal.Workflow.Tests/Jornal.Workflow.Tests.csproj -c Release --no-build --no-restore --filter 'FullyQualifiedName~RealMdbCopyLoadsPricesWithoutChangingItsBytes|FullyQualifiedName~WorkflowNativeMdbTests'
    if ($LASTEXITCODE -ne 0) { throw 'Installed MDB import or new database lifecycle failed.' }
    Remove-Item Env:WORKFLOW_LEGACY_MDB
    Remove-Item Env:WORKFLOW_TEST_ACCESS_HOST
    Stop-Installed $first
    $uninstall = Start-Process (Join-Path $first 'Update.exe') -ArgumentList @('--uninstall','--silent') -Wait -PassThru
    if ($uninstall.ExitCode -ne 0) { throw 'Disposable Next installation did not uninstall.' }
    if (Test-Path $registry) { throw 'Next uninstall registration remains.' }
    if (Test-Path $profile) { Remove-Item $profile -Recurse -Force }

    # Baseline has the same Next package identity and production updater, but is only a disposable probe.
    $feed = Join-Path $root 'releases'; New-Item -ItemType Directory -Path $feed | Out-Null
    Copy-Item artifacts/release/*.nupkg $feed
    Copy-Item artifacts/release/releases.workflow-win-x64.json $feed
    $baseline = Join-Path $root 'baseline-publish'
    & dotnet publish tests/Fixtures/UpdateProbe/Journal.Next.UpdateProbe.csproj -c Release -r win-x64 --self-contained false '-p:Version=2026.0.0' -p:RestoreLockedMode=true -p:DebugType=None -p:DebugSymbols=false -o $baseline
    if ($LASTEXITCODE -ne 0) { throw 'Baseline publish failed.' }
    Remove-Item (Join-Path $baseline 'Jornal.Workflow.Desktop.exe') -ErrorAction SilentlyContinue
    Move-Item (Join-Path $baseline 'Journal.Next.UpdateProbe.exe') (Join-Path $baseline 'Jornal.Workflow.Desktop.exe')
    $baseRelease = Join-Path $root 'baseline-release'
    & dotnet tool run vpk pack --packId DenisPapusha.JournalWorkflow --packTitle 'Journal Next test baseline' --packVersion 2026.0.0 --packDir $baseline --mainExe Jornal.Workflow.Desktop.exe --runtime win-x64 --channel workflow-win-x64 --outputDir $baseRelease --noPortable --framework net10.0-x64-runtime,net10.0-x86-runtime
    if ($LASTEXITCODE -ne 0) { throw 'Baseline packaging failed.' }
    $baseSetup = Get-ChildItem $baseRelease -Filter '*Setup.exe' | Select-Object -First 1
    $second = Join-Path $root 'Обновлённый Next'
    $env:JOURNAL_NEXT_UPDATE_PROBE_ROOT = $root
    $process = Start-Process $baseSetup.FullName -ArgumentList @('--silent','--installto',('"' + $second + '"')) -Wait -PassThru
    if ($process.ExitCode -ne 0) { throw 'Baseline install failed.' }
    Start-Sleep -Seconds 2
    if (-not (Test-Path (Join-Path $root 'scheduled.json')) -and -not (Get-Process 'Jornal.Workflow.Desktop' -ErrorAction SilentlyContinue | Where-Object { $_.Path -eq (Join-Path $second 'current/Jornal.Workflow.Desktop.exe') })) {
        Start-Process (Join-Path $second 'current/Jornal.Workflow.Desktop.exe') | Out-Null
    }
    $deadline = [DateTime]::UtcNow.AddSeconds(120)
    while (-not (Test-Path (Join-Path $root 'scheduled.json'))) {
        if (Test-Path (Join-Path $root 'failed.txt')) { throw (Get-Content (Join-Path $root 'failed.txt') -Raw) }
        if ([DateTime]::UtcNow -gt $deadline) { throw 'Baseline update did not start.' }
        Start-Sleep -Milliseconds 500
    }
    $scheduled = Get-Content (Join-Path $root 'scheduled.json') -Raw | ConvertFrom-Json
    $updated = Wait-InstalledApp $second
    $actual = [Diagnostics.FileVersionInfo]::GetVersionInfo((Join-Path $second 'current/Jornal.Workflow.Desktop.dll')).ProductVersion
    if (-not $actual.StartsWith($Version)) { throw "Wrong updated version: $actual" }
    & powershell.exe -NoProfile -File scripts/assert-restored-text.ps1 -ProcessId $updated.Id -ExpectedFile (Join-Path $root 'scheduled.json')
    if ($LASTEXITCODE -ne 0) { throw 'Updated real Next UI did not restore the result.' }
    if ((Get-FileHash (Join-Path $profile 'development-workspace.json')).Hash -ne $scheduled.workspaceHash -or
        (Get-FileHash (Join-Path $profile 'preferences.json')).Hash -ne $scheduled.preferencesHash -or
        (Get-FileHash (Join-Path $profile 'update-settings.json')).Hash -ne $scheduled.updatesHash) { throw 'Next profile or data changed during update.' }
    if ((Get-FileHash $sentinel).Hash -ne $legacyHash) { throw 'Previous Journal profile changed.' }
    $postHost = Join-Path $second 'current/AccessHost/Jornal.AccessHost.exe'
    & $postHost --probe
    if ($LASTEXITCODE -ne 0) { throw 'Updated x86 host is unavailable.' }
    @{version=$Version; freshInstall=$true; installedJetRead=$true; installedWorkflowMdbLifecycle=$true; updated=$true; uiTextRestored=$true; profilesPreserved=$true; packageId='DenisPapusha.JournalWorkflow'; channel='workflow-win-x64'} |
        ConvertTo-Json | Set-Content artifacts/installer-logs/verification.json -Encoding utf8
    Write-Host 'JOURNAL_NEXT_WINDOWS_INSTALL_UPDATE_OK'
} finally {
    Stop-Installed $first
    if (Get-Variable second -ErrorAction SilentlyContinue) { Stop-Installed $second }
    if (Test-Path Env:JOURNAL_NEXT_UPDATE_PROBE_ROOT) { Remove-Item Env:JOURNAL_NEXT_UPDATE_PROBE_ROOT }
    if (Test-Path Env:WORKFLOW_LEGACY_MDB) { Remove-Item Env:WORKFLOW_LEGACY_MDB }
    if (Test-Path Env:WORKFLOW_TEST_ACCESS_HOST) { Remove-Item Env:WORKFLOW_TEST_ACCESS_HOST }
    if (Test-Path (Join-Path $root 'failed.txt')) { Get-Content (Join-Path $root 'failed.txt') | Write-Host }
}
