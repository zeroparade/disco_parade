param(
    [string] $GameDir = "",
    [string] $ConfigPath = "",
    [switch] $Gui,
    [switch] $SkipVoiceDownload
)

$ErrorActionPreference = "Stop"
$ProgressPreference = "Continue"

$ScriptRoot = Split-Path -Parent $MyInvocation.MyCommand.Path
$LogPath = Join-Path $ScriptRoot "install.log"
$script:ProgressForm = $null
$script:ProgressTitleLabel = $null
$script:ProgressStatusLabel = $null
$script:ProgressBar = $null

function Write-InstallLog {
    param([string] $Message)
    $line = "[{0:yyyy-MM-dd HH:mm:ss}] {1}" -f (Get-Date), $Message
    Add-Content -LiteralPath $LogPath -Value $line -Encoding UTF8
    if (-not $Gui) { Write-Host $Message }
}

function Show-InstallerMessage {
    param(
        [string] $Message,
        [string] $Title = "ZERO PARADES Voice Override",
        [string] $Icon = "Information"
    )
    if ($Gui) {
        Add-Type -AssemblyName System.Windows.Forms
        [System.Windows.Forms.MessageBox]::Show($Message, $Title, "OK", $Icon) | Out-Null
    } else {
        Write-Host ""
        Write-Host $Message
    }
}

function Format-ByteSize {
    param([Int64] $Bytes)
    if ($Bytes -ge 1GB) { return "{0:N1} GB" -f ($Bytes / 1GB) }
    if ($Bytes -ge 1MB) { return "{0:N1} MB" -f ($Bytes / 1MB) }
    if ($Bytes -ge 1KB) { return "{0:N1} KB" -f ($Bytes / 1KB) }
    return "$Bytes B"
}

function Ensure-ProgressWindow {
    if (-not $Gui) { return }
    Add-Type -AssemblyName System.Windows.Forms
    Add-Type -AssemblyName System.Drawing

    if ($script:ProgressForm -ne $null -and -not $script:ProgressForm.IsDisposed) { return }

    $form = New-Object System.Windows.Forms.Form
    $form.Text = "ZERO PARADES Voice Override"
    $form.Width = 520
    $form.Height = 155
    $form.StartPosition = "CenterScreen"
    $form.FormBorderStyle = "FixedDialog"
    $form.MaximizeBox = $false
    $form.MinimizeBox = $false
    $form.ControlBox = $false
    $form.ShowInTaskbar = $true

    $titleLabel = New-Object System.Windows.Forms.Label
    $titleLabel.Left = 18
    $titleLabel.Top = 16
    $titleLabel.Width = 470
    $titleLabel.Height = 22
    $titleLabel.Font = New-Object System.Drawing.Font("Segoe UI", 10, [System.Drawing.FontStyle]::Bold)

    $statusLabel = New-Object System.Windows.Forms.Label
    $statusLabel.Left = 18
    $statusLabel.Top = 44
    $statusLabel.Width = 470
    $statusLabel.Height = 22
    $statusLabel.Font = New-Object System.Drawing.Font("Segoe UI", 9)

    $bar = New-Object System.Windows.Forms.ProgressBar
    $bar.Left = 18
    $bar.Top = 76
    $bar.Width = 470
    $bar.Height = 24
    $bar.Minimum = 0
    $bar.Maximum = 100

    $form.Controls.Add($titleLabel)
    $form.Controls.Add($statusLabel)
    $form.Controls.Add($bar)
    $form.Show()
    [System.Windows.Forms.Application]::DoEvents()

    $script:ProgressForm = $form
    $script:ProgressTitleLabel = $titleLabel
    $script:ProgressStatusLabel = $statusLabel
    $script:ProgressBar = $bar
}

function Set-InstallerProgress {
    param(
        [string] $Title,
        [string] $Status = "",
        [int] $Percent = 0,
        [switch] $Marquee
    )

    if ($Gui) {
        Ensure-ProgressWindow
        $script:ProgressTitleLabel.Text = $Title
        $script:ProgressStatusLabel.Text = $Status
        if ($Marquee) {
            $script:ProgressBar.Style = "Marquee"
            $script:ProgressBar.MarqueeAnimationSpeed = 35
        } else {
            $script:ProgressBar.Style = "Continuous"
            $script:ProgressBar.MarqueeAnimationSpeed = 0
            $script:ProgressBar.Value = [Math]::Max(0, [Math]::Min(100, $Percent))
        }
        $script:ProgressForm.Refresh()
        [System.Windows.Forms.Application]::DoEvents()
    } else {
        $clampedPercent = [Math]::Max(0, [Math]::Min(100, $Percent))
        Write-Progress -Activity $Title -Status $Status -PercentComplete $clampedPercent
    }
}

function Complete-InstallerProgress {
    param([string] $Title)
    if (-not $Gui) {
        Write-Progress -Activity $Title -Completed
    }
}

function Close-InstallerProgress {
    if ($Gui -and $script:ProgressForm -ne $null -and -not $script:ProgressForm.IsDisposed) {
        $script:ProgressForm.Close()
        $script:ProgressForm.Dispose()
    }
    $script:ProgressForm = $null
    $script:ProgressTitleLabel = $null
    $script:ProgressStatusLabel = $null
    $script:ProgressBar = $null
}

function Find-GameDirectory {
    param([string] $ExplicitGameDir)

    if (-not [string]::IsNullOrWhiteSpace($ExplicitGameDir)) {
        return [System.IO.Path]::GetFullPath($ExplicitGameDir)
    }

    return [System.IO.Path]::GetFullPath($ScriptRoot)
}

function Read-InstallerConfig {
    param([string] $PathValue)
    if ([string]::IsNullOrWhiteSpace($PathValue)) {
        $PathValue = Join-Path $ScriptRoot "installer-config.json"
    }
    if (-not (Test-Path -LiteralPath $PathValue)) {
        return [pscustomobject]@{
            modName = "ZERO PARADES Voice Override"
            disableBepInExConsole = $true
            bepInEx = [pscustomobject]@{
                enabled = $true
                url = "https://builds.bepinex.dev/projects/bepinex_be/755/BepInEx-Unity.IL2CPP-win-x64-6.0.0-be.755%2B3fab71a.zip"
            }
            voicePacks = @()
        }
    }
    return (Get-Content -LiteralPath $PathValue -Raw -Encoding UTF8 | ConvertFrom-Json)
}

function Get-EnabledVoicePacks {
    param([object] $Config)

    if ($null -eq $Config.voicePacks) { return @() }
    return @($Config.voicePacks | Where-Object {
        -not ($_.PSObject.Properties.Name -contains "enabled") -or [bool]$_.enabled
    })
}

function Get-VoicePackDisplayName {
    param([object] $Pack)

    $displayName = ""
    if ($Pack.PSObject.Properties.Name -contains "displayName") {
        $displayName = [string]$Pack.displayName
    }
    if ([string]::IsNullOrWhiteSpace($displayName)) {
        $displayName = [string]$Pack.name
    }
    if ([string]::IsNullOrWhiteSpace($displayName)) {
        $displayName = "voice pack"
    }
    return $displayName
}

function Test-VoicePackSelectedByDefault {
    param([object] $Pack)

    if ($Pack.PSObject.Properties.Name -contains "selectedByDefault") {
        return [bool]$Pack.selectedByDefault
    }
    return $true
}

function Get-VoicePackName {
    param([object] $Pack)

    $name = [string]$Pack.name
    if ([string]::IsNullOrWhiteSpace($name)) { return "voice-pack" }
    return $name
}

function Get-VoicePackUpdateUrl {
    param([object] $Pack)

    if ($Pack.PSObject.Properties.Name -contains "updateUrl") {
        $updateUrl = [string]$Pack.updateUrl
        if (-not [string]::IsNullOrWhiteSpace($updateUrl)) { return $updateUrl }
    }
    return [string]$Pack.url
}

function Get-VoicePackStatePath {
    param([string] $GameRoot)
    return Join-Path $GameRoot "BepInEx/config/spore.zeroparades.voicepacks.json"
}

function Set-ObjectProperty {
    param(
        [object] $Object,
        [string] $Name,
        [object] $Value
    )

    if ($Object.PSObject.Properties.Name -contains $Name) {
        $Object.PSObject.Properties[$Name].Value = $Value
    } else {
        $Object | Add-Member -NotePropertyName $Name -NotePropertyValue $Value
    }
}

function Read-VoicePackState {
    param([string] $GameRoot)

    $path = Get-VoicePackStatePath -GameRoot $GameRoot
    if (Test-Path -LiteralPath $path) {
        try {
            $state = Get-Content -LiteralPath $path -Raw -Encoding UTF8 | ConvertFrom-Json
            if ($null -eq $state) { throw "empty state" }
            if (-not ($state.PSObject.Properties.Name -contains "packs") -or $null -eq $state.packs) {
                Set-ObjectProperty -Object $state -Name "packs" -Value ([pscustomobject]@{})
            }
            return $state
        } catch {
            Write-InstallLog "Voice pack state could not be read; starting fresh: $($_.Exception.Message)"
        }
    }

    return [pscustomobject]@{
        schemaVersion = 1
        updatedAt = ""
        packs = [pscustomobject]@{}
    }
}

function Save-VoicePackState {
    param(
        [string] $GameRoot,
        [object] $State
    )

    $path = Get-VoicePackStatePath -GameRoot $GameRoot
    New-Item -ItemType Directory -Force -Path (Split-Path -Parent $path) | Out-Null
    Set-ObjectProperty -Object $State -Name "schemaVersion" -Value 1
    Set-ObjectProperty -Object $State -Name "updatedAt" -Value ((Get-Date).ToUniversalTime().ToString("o"))
    if (-not ($State.PSObject.Properties.Name -contains "packs") -or $null -eq $State.packs) {
        Set-ObjectProperty -Object $State -Name "packs" -Value ([pscustomobject]@{})
    }
    $State | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath $path -Encoding UTF8
}

function Get-VoicePackStateEntry {
    param(
        [object] $State,
        [string] $Name
    )

    if ($null -eq $State -or $null -eq $State.packs) { return $null }
    if ($State.packs.PSObject.Properties.Name -contains $Name) {
        return $State.packs.PSObject.Properties[$Name].Value
    }
    return $null
}

function Get-RemoteVoicePackMetadata {
    param(
        [string] $Url,
        [string] $DisplayName
    )

    if ([string]::IsNullOrWhiteSpace($Url) -or $Url.Contains("YOUR_NAME/YOUR_REPO")) {
        return [pscustomobject]@{ ok = $false; error = "no URL configured" }
    }

    [Net.ServicePointManager]::SecurityProtocol = [Net.SecurityProtocolType]::Tls12
    $request = $null
    $response = $null
    try {
        $request = [System.Net.HttpWebRequest]::Create($Url)
        $request.Method = "HEAD"
        $request.UserAgent = "ZeroParadesVoiceOverrideInstaller/1.0"
        $request.AllowAutoRedirect = $true
        $request.Timeout = 15000
        $request.ReadWriteTimeout = 30000
        $response = $request.GetResponse()
        $lastModified = ""
        try {
            if ($response.LastModified -gt [DateTime]::MinValue) {
                $lastModified = $response.LastModified.ToUniversalTime().ToString("o")
            }
        } catch { }

        return [pscustomobject]@{
            ok = $true
            url = $Url
            etag = [string]$response.Headers["ETag"]
            lastModified = $lastModified
            contentLength = [Int64]$response.ContentLength
            checkedAt = (Get-Date).ToUniversalTime().ToString("o")
        }
    } catch {
        Write-InstallLog "Voice pack update check unavailable for '$DisplayName': $($_.Exception.GetType().Name): $($_.Exception.Message)"
        return [pscustomobject]@{
            ok = $false
            url = $Url
            error = "$($_.Exception.GetType().Name): $($_.Exception.Message)"
            checkedAt = (Get-Date).ToUniversalTime().ToString("o")
        }
    } finally {
        if ($response -ne $null) { $response.Dispose() }
    }
}

function Test-RemoteMetadataChanged {
    param(
        [object] $Remote,
        [object] $Installed
    )

    if ($null -eq $Remote -or $null -eq $Installed -or $Remote.ok -ne $true) { return $false }

    $remoteEtag = [string]$Remote.etag
    $installedEtag = [string]$Installed.etag
    if ((-not [string]::IsNullOrWhiteSpace($remoteEtag)) -and
        (-not [string]::IsNullOrWhiteSpace($installedEtag)) -and
        ($remoteEtag -ne $installedEtag)) {
        return $true
    }

    $remoteLength = [Int64]$Remote.contentLength
    $installedLength = [Int64]$Installed.contentLength
    if ($remoteLength -gt 0 -and $installedLength -gt 0 -and $remoteLength -ne $installedLength) {
        return $true
    }

    $remoteModified = [string]$Remote.lastModified
    $installedModified = [string]$Installed.lastModified
    if ((-not [string]::IsNullOrWhiteSpace($remoteModified)) -and
        (-not [string]::IsNullOrWhiteSpace($installedModified)) -and
        ($remoteModified -ne $installedModified)) {
        return $true
    }

    return $false
}

function Get-VoicePackInstallStatus {
    param(
        [object] $Pack,
        [string] $GameRoot,
        [object] $State
    )

    $name = Get-VoicePackName -Pack $Pack
    $displayName = Get-VoicePackDisplayName -Pack $Pack
    $destinationRelative = [string]$Pack.destination
    $destination = if ([string]::IsNullOrWhiteSpace($destinationRelative)) { "" } else { Join-Path $GameRoot $destinationRelative }
    $wavCount = 0
    if (-not [string]::IsNullOrWhiteSpace($destination) -and (Test-Path -LiteralPath $destination)) {
        $wavCount = @(Get-ChildItem -LiteralPath $destination -Filter "*.wav" -File -Recurse -ErrorAction SilentlyContinue).Count
    }

    $installed = Get-VoicePackStateEntry -State $State -Name $name
    $remote = Get-RemoteVoicePackMetadata -Url (Get-VoicePackUpdateUrl -Pack $Pack) -DisplayName $displayName

    $status = "not-installed"
    $statusText = "not installed"
    if ($wavCount -gt 0) {
        if ($remote.ok -ne $true) {
            $status = "check-unavailable"
            $statusText = "installed, update check unavailable, $wavCount wavs"
        } elseif ($null -eq $installed) {
            $status = "untracked"
            $statusText = "installed but not tracked yet, $wavCount wavs"
        } elseif (Test-RemoteMetadataChanged -Remote $remote -Installed $installed) {
            $status = "update-available"
            $statusText = "update available, $wavCount installed wavs"
        } else {
            $status = "current"
            $statusText = "up to date, $wavCount wavs"
        }
    }

    return [pscustomobject]@{
        name = $name
        displayName = $displayName
        status = $status
        statusText = $statusText
        wavCount = $wavCount
        remote = $remote
        installed = $installed
    }
}

function Test-VoicePackDownloadDefault {
    param(
        [object] $Pack,
        [object] $Status
    )

    if ($Status.status -eq "current" -or $Status.status -eq "check-unavailable") {
        return $false
    }
    if ($Status.status -eq "update-available" -or $Status.status -eq "untracked") {
        return $true
    }
    return (Test-VoicePackSelectedByDefault -Pack $Pack)
}

function Select-VoicePacksForDownload {
    param(
        [object[]] $VoicePacks,
        [string] $GameRoot
    )

    if ($VoicePacks.Count -eq 0) { return @() }

    Write-Host ""
    Write-Host "Checking voice pack update metadata..."
    $state = Read-VoicePackState -GameRoot $GameRoot

    Write-Host ""
    Write-Host "Choose voice packs to download or update. Press Enter to keep the default."
    Write-Host "You can rerun Install.bat later to add packs."

    $selected = New-Object System.Collections.Generic.List[object]
    foreach ($pack in $VoicePacks) {
        $displayName = Get-VoicePackDisplayName -Pack $pack
        $status = Get-VoicePackInstallStatus -Pack $pack -GameRoot $GameRoot -State $state
        $selectedByDefault = Test-VoicePackDownloadDefault -Pack $pack -Status $status
        $suffix = if ($selectedByDefault) { "[Y/n]" } else { "[y/N]" }
        $answer = Read-Host "Download $displayName ($($status.statusText)) $suffix"

        $wanted = $selectedByDefault
        if (-not [string]::IsNullOrWhiteSpace($answer)) {
            $wanted = ($answer.Trim() -match "^(y|yes)$")
        }

        if ($wanted) {
            $selected.Add($pack) | Out-Null
        }
    }

    if ($selected.Count -eq 0) {
        Write-InstallLog "No voice packs selected for download."
    } else {
        $selectedNames = @($selected.ToArray() | ForEach-Object { Get-VoicePackDisplayName -Pack $_ })
        Write-InstallLog "Selected voice packs: $($selectedNames -join ', ')"
    }

    return @($selected.ToArray())
}

function Copy-DirectoryContents {
    param(
        [string] $Source,
        [string] $Destination
    )
    if (-not (Test-Path -LiteralPath $Source)) { return }
    New-Item -ItemType Directory -Force -Path $Destination | Out-Null
    Get-ChildItem -LiteralPath $Source -Force | ForEach-Object {
        $target = Join-Path $Destination $_.Name
        if ($_.PSIsContainer) {
            Copy-DirectoryContents -Source $_.FullName -Destination $target
        } else {
            New-Item -ItemType Directory -Force -Path (Split-Path -Parent $target) | Out-Null
            Copy-Item -LiteralPath $_.FullName -Destination $target -Force
        }
    }
}

function Flatten-ExtractedVoiceSubfolder {
    param(
        [string] $Destination,
        [string] $Subfolder
    )

    if ([string]::IsNullOrWhiteSpace($Subfolder)) { return }

    $candidate = Join-Path $Destination $Subfolder
    if (-not (Test-Path -LiteralPath $candidate -PathType Container)) { return }

    $resolvedDestination = [System.IO.Path]::GetFullPath($Destination)
    $resolvedCandidate = [System.IO.Path]::GetFullPath($candidate)
    $prefix = $resolvedDestination.TrimEnd('\') + '\'
    if (-not $resolvedCandidate.StartsWith($prefix, [System.StringComparison]::OrdinalIgnoreCase)) {
        throw "Refusing to flatten folder outside destination: $resolvedCandidate"
    }

    Write-InstallLog "Flattening extracted voice folder $Subfolder"
    Copy-DirectoryContents -Source $resolvedCandidate -Destination $resolvedDestination
    Remove-Item -LiteralPath $resolvedCandidate -Recurse -Force
}

function Set-IniValue {
    param(
        [string] $Path,
        [string] $Section,
        [string] $Key,
        [string] $Value
    )

    $lines = New-Object System.Collections.Generic.List[string]
    if (Test-Path -LiteralPath $Path) {
        foreach ($line in (Get-Content -LiteralPath $Path -Encoding UTF8)) {
            $lines.Add($line)
        }
    }

    $sectionHeader = "[$Section]"
    $sectionStart = -1
    for ($i = 0; $i -lt $lines.Count; $i++) {
        if ($lines[$i].Trim() -ieq $sectionHeader) {
            $sectionStart = $i
            break
        }
    }

    if ($sectionStart -lt 0) {
        if ($lines.Count -gt 0 -and $lines[$lines.Count - 1].Trim() -ne "") {
            $lines.Add("")
        }
        $lines.Add($sectionHeader)
        $lines.Add("$Key = $Value")
        Set-Content -LiteralPath $Path -Value $lines -Encoding UTF8
        return
    }

    $insertAt = $lines.Count
    for ($i = $sectionStart + 1; $i -lt $lines.Count; $i++) {
        if ($lines[$i].Trim().StartsWith("[") -and $lines[$i].Trim().EndsWith("]")) {
            $insertAt = $i
            break
        }
        if ($lines[$i] -match "^\s*$([regex]::Escape($Key))\s*=") {
            $lines[$i] = "$Key = $Value"
            Set-Content -LiteralPath $Path -Value $lines -Encoding UTF8
            return
        }
    }

    $lines.Insert($insertAt, "$Key = $Value")
    Set-Content -LiteralPath $Path -Value $lines -Encoding UTF8
}

function Ensure-PluginConfig {
    param([string] $GameRoot)
    $configDir = Join-Path $GameRoot "BepInEx/config"
    New-Item -ItemType Directory -Force -Path $configDir | Out-Null

    $bepInExConfig = Join-Path $configDir "BepInEx.cfg"
    Set-IniValue -Path $bepInExConfig -Section "Logging.Console" -Key "Enabled" -Value "false"
    Set-IniValue -Path $bepInExConfig -Section "Logging.Disk" -Key "Enabled" -Value "true"

    $voiceConfig = Join-Path $configDir "spore.zeroparades.voiceoverride.cfg"
    Set-IniValue -Path $voiceConfig -Section "Hotkeys" -Key "ToggleOverrideKey" -Value "F1"
    Set-IniValue -Path $voiceConfig -Section "Hotkeys" -Key "CycleProfileKey" -Value "F2"
    Set-IniValue -Path $voiceConfig -Section "Hotkeys" -Key "ToggleExtraVoicesKey" -Value "F3"
    Set-IniValue -Path $voiceConfig -Section "Hotkeys" -Key "ToggleNarratorMissingVoicesKey" -Value "F4"
    Set-IniValue -Path $voiceConfig -Section "Hotkeys" -Key "ReportLatestDialogueKey" -Value "F10"
    Set-IniValue -Path $voiceConfig -Section "Hotkeys" -Key "ToggleVoicePackUpdateToastsKey" -Value "F11"
    Set-IniValue -Path $voiceConfig -Section "Profiles" -Key "OverrideProfile" -Value "male"
    Set-IniValue -Path $voiceConfig -Section "Profiles" -Key "MaleOverrideRoot" -Value "voice-overrides"
    Set-IniValue -Path $voiceConfig -Section "Profiles" -Key "FemaleOverrideRoot" -Value "voice-overrides-female"
    Set-IniValue -Path $voiceConfig -Section "Profiles" -Key "ExtraOverrideRoot" -Value "voice-override-extras"
    Set-IniValue -Path $voiceConfig -Section "Profiles" -Key "NarratorOverrideRoot" -Value "voice-override-narrator"
    Set-IniValue -Path $voiceConfig -Section "Runtime" -Key "OverrideEnabled" -Value "true"
    Set-IniValue -Path $voiceConfig -Section "Runtime" -Key "ExtraVoicesEnabled" -Value "true"
    Set-IniValue -Path $voiceConfig -Section "Runtime" -Key "NarratorMissingVoicesEnabled" -Value "false"
    Set-IniValue -Path $voiceConfig -Section "Runtime" -Key "OriginalVoiceEnabledWhenOverrideExists" -Value "false"
    Set-IniValue -Path $voiceConfig -Section "Runtime" -Key "AllowOriginalVoiceWhenOverrideFails" -Value "true"
    Set-IniValue -Path $voiceConfig -Section "Runtime" -Key "VoicePackUpdateToastsEnabled" -Value "true"
    Set-IniValue -Path $voiceConfig -Section "Runtime" -Key "VoicePackUpdateToastRepeatMinutes" -Value "30"
}

function Normalize-VoiceFileNames {
    param([string] $Directory)
    if (-not (Test-Path -LiteralPath $Directory)) { return 0 }
    $renamed = 0
    Get-ChildItem -LiteralPath $Directory -Filter "*.wav" -File -Recurse | ForEach-Object {
        $base = [System.IO.Path]::GetFileNameWithoutExtension($_.Name)
        $marker = $base.IndexOf("__BNK", [System.StringComparison]::OrdinalIgnoreCase)
        if ($marker -gt 0) {
            $dialogueId = $base.Substring(0, $marker)
            if (-not [string]::IsNullOrWhiteSpace($dialogueId)) {
                $target = Join-Path $_.DirectoryName ($dialogueId + $_.Extension.ToLowerInvariant())
                if ($target -ine $_.FullName) {
                    if (Test-Path -LiteralPath $target) {
                        Remove-Item -LiteralPath $_.FullName -Force
                    } else {
                        Move-Item -LiteralPath $_.FullName -Destination $target -Force
                    }
                    $script:RenamedVoiceFiles++
                    $renamed++
                }
            }
        } elseif ($base -match "^\d{5,}_(.+)$") {
            $dialogueId = $Matches[1]
            if (-not [string]::IsNullOrWhiteSpace($dialogueId)) {
                $target = Join-Path $_.DirectoryName ($dialogueId + $_.Extension.ToLowerInvariant())
                if ($target -ine $_.FullName) {
                    if (Test-Path -LiteralPath $target) {
                        Remove-Item -LiteralPath $_.FullName -Force
                    } else {
                        Move-Item -LiteralPath $_.FullName -Destination $target -Force
                    }
                    $script:RenamedVoiceFiles++
                    $renamed++
                }
            }
        }
    }
    return $renamed
}

function Download-File {
    param(
        [string] $Url,
        [string] $Destination,
        [string] $DisplayName = "voice pack"
    )
    Write-InstallLog "Downloading $Url"
    $parent = Split-Path -Parent $Destination
    New-Item -ItemType Directory -Force -Path $parent | Out-Null

    [Net.ServicePointManager]::SecurityProtocol = [Net.SecurityProtocolType]::Tls12
    $activity = "Downloading $DisplayName"
    Set-InstallerProgress -Title $activity -Status "Connecting..." -Percent 0

    $request = [System.Net.HttpWebRequest]::Create($Url)
    $request.UserAgent = "ZeroParadesVoiceOverrideInstaller/1.0"
    $request.AllowAutoRedirect = $true
    $request.Timeout = 30000
    $request.ReadWriteTimeout = 300000

    $response = $null
    $inputStream = $null
    $outputStream = $null
    $downloaded = [Int64]0
    $etag = ""
    $lastModified = ""
    $contentLength = [Int64]-1
    try {
        $response = $request.GetResponse()
        $etag = [string]$response.Headers["ETag"]
        try {
            if ($response.LastModified -gt [DateTime]::MinValue) {
                $lastModified = $response.LastModified.ToUniversalTime().ToString("o")
            }
        } catch { }
        $contentLength = [Int64]$response.ContentLength
        $totalBytes = $contentLength
        $inputStream = $response.GetResponseStream()
        $outputStream = [System.IO.File]::Create($Destination)
        $buffer = New-Object byte[] (1024 * 1024)
        $startedAt = Get-Date
        $lastUpdate = Get-Date "2000-01-01"

        while (($read = $inputStream.Read($buffer, 0, $buffer.Length)) -gt 0) {
            $outputStream.Write($buffer, 0, $read)
            $downloaded += $read
            $now = Get-Date
            if (($now - $lastUpdate).TotalMilliseconds -lt 250 -and $totalBytes -gt 0 -and $downloaded -lt $totalBytes) {
                continue
            }

            $elapsedSeconds = [Math]::Max(0.1, ($now - $startedAt).TotalSeconds)
            $speed = [Int64]($downloaded / $elapsedSeconds)
            if ($totalBytes -gt 0) {
                $percent = [int][Math]::Floor(($downloaded * 100.0) / $totalBytes)
                $status = "{0} of {1} ({2}/s)" -f (Format-ByteSize $downloaded), (Format-ByteSize $totalBytes), (Format-ByteSize $speed)
                Set-InstallerProgress -Title $activity -Status $status -Percent $percent
            } else {
                $status = "{0} downloaded ({1}/s)" -f (Format-ByteSize $downloaded), (Format-ByteSize $speed)
                Set-InstallerProgress -Title $activity -Status $status -Percent 0 -Marquee
            }
            $lastUpdate = $now
        }
    } finally {
        if ($outputStream -ne $null) { $outputStream.Dispose() }
        if ($inputStream -ne $null) { $inputStream.Dispose() }
        if ($response -ne $null) { $response.Dispose() }
    }

    Set-InstallerProgress -Title $activity -Status "Download complete: $(Format-ByteSize ((Get-Item -LiteralPath $Destination).Length))" -Percent 100
    Complete-InstallerProgress -Title $activity
    return [pscustomobject]@{
        ok = $true
        url = $Url
        etag = $etag
        lastModified = $lastModified
        contentLength = $contentLength
        downloadedBytes = $downloaded
        downloadedAt = (Get-Date).ToUniversalTime().ToString("o")
    }
}

function Update-VoicePackState {
    param(
        [string] $GameRoot,
        [object] $Pack,
        [object] $DownloadMetadata,
        [int] $WavCount,
        [int] $RenamedCount
    )

    $name = Get-VoicePackName -Pack $Pack
    $state = Read-VoicePackState -GameRoot $GameRoot
    $metadata = [pscustomobject]@{
        name = $name
        displayName = Get-VoicePackDisplayName -Pack $Pack
        destination = [string]$Pack.destination
        url = [string]$Pack.url
        updateUrl = Get-VoicePackUpdateUrl -Pack $Pack
        installedAt = (Get-Date).ToUniversalTime().ToString("o")
        etag = [string]$DownloadMetadata.etag
        lastModified = [string]$DownloadMetadata.lastModified
        contentLength = [Int64]$DownloadMetadata.contentLength
        downloadedBytes = [Int64]$DownloadMetadata.downloadedBytes
        wavCount = $WavCount
        renamedCount = $RenamedCount
    }
    Set-ObjectProperty -Object $state.packs -Name $name -Value $metadata
    Save-VoicePackState -GameRoot $GameRoot -State $state
    Write-InstallLog "Recorded voice pack state for '$name' ($WavCount wav files)."
}

function Install-BepInExIfMissing {
    param(
        [object] $Config,
        [string] $GameRoot
    )

    $bepInExConfig = $Config.bepInEx
    if ($null -eq $bepInExConfig -or $bepInExConfig.enabled -eq $false) {
        Write-InstallLog "BepInEx auto-install disabled."
        return
    }

    $requiredPaths = @(
        "winhttp.dll",
        "doorstop_config.ini",
        "dotnet/coreclr.dll",
        "BepInEx/core/BepInEx.Unity.IL2CPP.dll"
    )
    $missing = @($requiredPaths | Where-Object { -not (Test-Path -LiteralPath (Join-Path $GameRoot $_)) })
    if ($missing.Count -eq 0) {
        Write-InstallLog "BepInEx already installed."
        return
    }

    $url = [string]$bepInExConfig.url
    if ([string]::IsNullOrWhiteSpace($url)) {
        throw "BepInEx is missing and installer-config.json has no bepInEx.url."
    }

    Write-InstallLog "BepInEx missing: $($missing -join ', ')"
    $archive = Join-Path $GameRoot "_bepinex.download.zip"
    if (Test-Path -LiteralPath $archive) {
        Remove-Item -LiteralPath $archive -Force
    }

    $null = Download-File -Url $url -Destination $archive -DisplayName "BepInEx IL2CPP x64"
    Set-InstallerProgress -Title "Installing BepInEx" -Status "Extracting BepInEx into the game folder..." -Percent 100
    Expand-Archive -LiteralPath $archive -DestinationPath $GameRoot -Force
    Remove-Item -LiteralPath $archive -Force -ErrorAction SilentlyContinue

    $stillMissing = @($requiredPaths | Where-Object { -not (Test-Path -LiteralPath (Join-Path $GameRoot $_)) })
    if ($stillMissing.Count -gt 0) {
        throw "BepInEx download/extract completed, but required files are still missing: $($stillMissing -join ', ')"
    }

    Write-InstallLog "BepInEx installed."
}

function Install-VoicePack {
    param(
        [object] $Pack,
        [string] $GameRoot
    )

    if ($Pack.PSObject.Properties.Name -contains "enabled" -and -not [bool]$Pack.enabled) {
        Write-InstallLog "Skipping disabled voice pack: $($Pack.name)"
        return
    }

    $name = Get-VoicePackName -Pack $Pack

    $url = [string]$Pack.url
    $required = $false
    if ($Pack.PSObject.Properties.Name -contains "required") { $required = [bool]$Pack.required }
    if ([string]::IsNullOrWhiteSpace($url) -or $url.Contains("YOUR_NAME/YOUR_REPO")) {
        if ($required) {
            throw "Required voice pack '$name' has no real download URL in installer-config.json."
        }
        Write-InstallLog "Skipping voice pack '$name': no URL configured."
        return
    }

    $destinationRelative = [string]$Pack.destination
    if ([string]::IsNullOrWhiteSpace($destinationRelative)) {
        throw "Voice pack '$name' has no destination configured."
    }

    $destination = Join-Path $GameRoot $destinationRelative
    New-Item -ItemType Directory -Force -Path $destination | Out-Null

    $safeName = $name -replace '[^\w.-]', '_'
    $archive = Join-Path $destination ("_" + $safeName + ".download.zip")
    if (Test-Path -LiteralPath $archive) {
        Remove-Item -LiteralPath $archive -Force
    }

    $downloadMetadata = Download-File -Url $url -Destination $archive -DisplayName "$name voice pack"
    Set-InstallerProgress -Title "Installing $name voice pack" -Status "Extracting into $destinationRelative..." -Percent 100
    Expand-Archive -LiteralPath $archive -DestinationPath $destination -Force
    Remove-Item -LiteralPath $archive -Force -ErrorAction SilentlyContinue

    $archiveSubfolder = [string]$Pack.archiveSubfolder
    if (-not [string]::IsNullOrWhiteSpace($archiveSubfolder)) {
        Flatten-ExtractedVoiceSubfolder -Destination $destination -Subfolder $archiveSubfolder
    } else {
        $destinationLeaf = Split-Path -Leaf $destinationRelative
        Flatten-ExtractedVoiceSubfolder -Destination $destination -Subfolder $destinationLeaf
        Flatten-ExtractedVoiceSubfolder -Destination $destination -Subfolder $destinationRelative
    }

    Set-InstallerProgress -Title "Installing $name voice pack" -Status "Checking filenames..." -Percent 100
    $script:RenamedVoiceFiles = 0
    Normalize-VoiceFileNames -Directory $destination | Out-Null
    $wavCount = @(Get-ChildItem -LiteralPath $destination -Filter "*.wav" -File -Recurse).Count
    Update-VoicePackState -GameRoot $GameRoot -Pack $Pack -DownloadMetadata $downloadMetadata -WavCount $wavCount -RenamedCount $script:RenamedVoiceFiles
    Write-InstallLog "Installed voice pack '$name' to '$destinationRelative' ($wavCount wav files, $script:RenamedVoiceFiles renamed)."
}

try {
    Clear-Content -LiteralPath $LogPath -ErrorAction SilentlyContinue
    Write-InstallLog "Installer started."
    $config = Read-InstallerConfig -PathValue $ConfigPath

    $GameDir = Find-GameDirectory -ExplicitGameDir $GameDir
    if (-not (Test-Path -LiteralPath (Join-Path $GameDir "ZeroParades.exe"))) {
        throw "Selected folder does not contain ZeroParades.exe: $GameDir"
    }

    Write-InstallLog "Installing to $GameDir"
    if ($Gui) {
        Show-InstallerMessage -Message "The installer will now set up BepInEx if needed, install the mod, and download the voice packs. This can take several minutes; wait for the success message before launching the game."
    }

    Install-BepInExIfMissing -Config $config -GameRoot $GameDir

    New-Item -ItemType Directory -Force -Path (Join-Path $GameDir "BepInEx/plugins") | Out-Null
    New-Item -ItemType Directory -Force -Path (Join-Path $GameDir "BepInEx/voice-overrides") | Out-Null
    New-Item -ItemType Directory -Force -Path (Join-Path $GameDir "BepInEx/voice-overrides-female") | Out-Null
    New-Item -ItemType Directory -Force -Path (Join-Path $GameDir "BepInEx/voice-override-extras") | Out-Null
    New-Item -ItemType Directory -Force -Path (Join-Path $GameDir "BepInEx/voice-override-narrator") | Out-Null

    $pluginPath = Join-Path $GameDir "BepInEx/plugins/ZeroParadesVoiceOverride.dll"
    if (-not (Test-Path -LiteralPath $pluginPath)) {
        $loosePlugin = Join-Path $ScriptRoot "ZeroParadesVoiceOverride.dll"
        if (Test-Path -LiteralPath $loosePlugin) {
            Copy-Item -LiteralPath $loosePlugin -Destination $pluginPath -Force
        } else {
            throw "ZeroParadesVoiceOverride.dll was not found. Extract the ZIP into the folder that contains ZeroParades.exe and keep the BepInEx folder from the ZIP."
        }
    }

    if ($config.disableBepInExConsole -ne $false) {
        Ensure-PluginConfig -GameRoot $GameDir
        Write-InstallLog "Configured BepInEx console hidden and plugin hotkeys."
    }

    if (-not $SkipVoiceDownload) {
        $selectedVoicePacks = Select-VoicePacksForDownload -VoicePacks (Get-EnabledVoicePacks -Config $config) -GameRoot $GameDir
        foreach ($pack in $selectedVoicePacks) {
            Install-VoicePack -Pack $pack -GameRoot $GameDir
        }
    } else {
        Write-InstallLog "Voice pack downloads skipped by command line."
    }

    $maleCount = @(Get-ChildItem -LiteralPath (Join-Path $GameDir "BepInEx/voice-overrides") -Filter "*.wav" -File -Recurse -ErrorAction SilentlyContinue).Count
    $femaleCount = @(Get-ChildItem -LiteralPath (Join-Path $GameDir "BepInEx/voice-overrides-female") -Filter "*.wav" -File -Recurse -ErrorAction SilentlyContinue).Count
    $extraCount = @(Get-ChildItem -LiteralPath (Join-Path $GameDir "BepInEx/voice-override-extras") -Filter "*.wav" -File -Recurse -ErrorAction SilentlyContinue).Count
    $narratorCount = @(Get-ChildItem -LiteralPath (Join-Path $GameDir "BepInEx/voice-override-narrator") -Filter "*.wav" -File -Recurse -ErrorAction SilentlyContinue).Count

    $message = "Installed ZERO PARADES Voice Override.`n`nMale voices: $maleCount`nFemale voices: $femaleCount`nExtra character voices: $extraCount`nNarrator missing voices: $narratorCount`n`nF1 cycles presets. F2 cycles redub off/male/female. F3 toggles extras. F4 toggles narrator missing VO. F10 reports latest dialogue. F11 toggles update toasts. F12 toggles debug toasts."
    Write-InstallLog $message.Replace("`n", " ")
    Close-InstallerProgress
    Show-InstallerMessage -Message $message
    exit 0
} catch {
    $errorMessage = "Install failed: $($_.Exception.Message)`n`nLog: $LogPath"
    Write-InstallLog $errorMessage.Replace("`n", " ")
    Close-InstallerProgress
    Show-InstallerMessage -Message $errorMessage -Icon "Error"
    exit 1
}
