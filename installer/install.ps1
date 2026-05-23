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
    Set-IniValue -Path $voiceConfig -Section "Profiles" -Key "OverrideProfile" -Value "male"
    Set-IniValue -Path $voiceConfig -Section "Profiles" -Key "MaleOverrideRoot" -Value "voice-overrides"
    Set-IniValue -Path $voiceConfig -Section "Profiles" -Key "FemaleOverrideRoot" -Value "voice-overrides-female"
    Set-IniValue -Path $voiceConfig -Section "Runtime" -Key "OverrideEnabled" -Value "true"
    Set-IniValue -Path $voiceConfig -Section "Runtime" -Key "OriginalVoiceEnabledWhenOverrideExists" -Value "false"
    Set-IniValue -Path $voiceConfig -Section "Runtime" -Key "AllowOriginalVoiceWhenOverrideFails" -Value "true"
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
        } elseif ($base -match "^\d{3,}_(.+)$") {
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
    try {
        $response = $request.GetResponse()
        $totalBytes = [Int64]$response.ContentLength
        $inputStream = $response.GetResponseStream()
        $outputStream = [System.IO.File]::Create($Destination)
        $buffer = New-Object byte[] (1024 * 1024)
        $downloaded = [Int64]0
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

    Download-File -Url $url -Destination $archive -DisplayName "BepInEx IL2CPP x64"
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

    $name = [string]$Pack.name
    if ([string]::IsNullOrWhiteSpace($name)) { $name = "voice-pack" }

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

    Download-File -Url $url -Destination $archive -DisplayName "$name voice pack"
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
        foreach ($pack in @($config.voicePacks)) {
            Install-VoicePack -Pack $pack -GameRoot $GameDir
        }
    }

    $maleCount = @(Get-ChildItem -LiteralPath (Join-Path $GameDir "BepInEx/voice-overrides") -Filter "*.wav" -File -Recurse -ErrorAction SilentlyContinue).Count
    $femaleCount = @(Get-ChildItem -LiteralPath (Join-Path $GameDir "BepInEx/voice-overrides-female") -Filter "*.wav" -File -Recurse -ErrorAction SilentlyContinue).Count

    $message = "Installed ZERO PARADES Voice Override.`n`nMale voices: $maleCount`nFemale voices: $femaleCount`n`nF1 toggles overrides. F2 switches voice profile."
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
