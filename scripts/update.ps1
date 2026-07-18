param(
    [string]$Configuration = "Debug",
    [ValidateSet("x64", "ARM64")]
    [string]$Platform = "x64",
    # Where the built ShareX is installed so it survives repo rebuilds and can start with Windows.
    # Per-user path, so no elevation is needed and the machine-wide Program Files install is left alone.
    [string]$InstallDirectory = (Join-Path $env:LOCALAPPDATA "Programs\ShareX")
)

$ErrorActionPreference = "Stop"

$repoRoot = Split-Path -Parent $PSScriptRoot
$projectPath = Join-Path $repoRoot "ShareX\ShareX.csproj"
$runtimeIdentifier = if ($Platform -eq "ARM64") { "win-arm64" } else { "win-x64" }
$outputDirectory = Join-Path $repoRoot "ShareX\bin\$Configuration\$runtimeIdentifier"
$buildExecutablePath = Join-Path $outputDirectory "ShareX.exe"
$installExecutablePath = Join-Path $InstallDirectory "ShareX.exe"

# --- 1. Force close every running ShareX so nothing locks the build or the install target ---------

function Get-ShareXProcesses {
    return @(Get-Process -Name "ShareX" -ErrorAction SilentlyContinue)
}

$running = Get-ShareXProcesses
if ($running.Count -gt 0) {
    Write-Host "Closing $($running.Count) running ShareX process(es)..."

    # Try a graceful exit first (lets ShareX flush its settings), using whichever exe is already on disk.
    $exitLauncher = @($running | ForEach-Object { $_.Path } | Where-Object { $_ } | Select-Object -First 1)
    if ($exitLauncher.Count -eq 0 -and (Test-Path -LiteralPath $installExecutablePath)) {
        $exitLauncher = @($installExecutablePath)
    }
    if ($exitLauncher.Count -gt 0) {
        try {
            Start-Process -FilePath $exitLauncher[0] -ArgumentList "-ExitShareX" -WindowStyle Hidden -Wait
        }
        catch {
            Write-Warning "ShareX did not accept the exit command: $($_.Exception.Message)"
        }
    }

    $deadline = [DateTime]::UtcNow.AddSeconds(8)
    do {
        Start-Sleep -Milliseconds 250
        $running = Get-ShareXProcesses
    } while ($running.Count -gt 0 -and [DateTime]::UtcNow -lt $deadline)

    if ($running.Count -gt 0) {
        Write-Host "Force stopping $($running.Count) remaining ShareX process(es)..."
        $running | ForEach-Object { Stop-Process -Id $_.Id -Force -ErrorAction SilentlyContinue }
        $running | ForEach-Object { Wait-Process -Id $_.Id -Timeout 5 -ErrorAction SilentlyContinue }
    }
}

# --- 2. Build --------------------------------------------------------------------------------------

Write-Host "Building ShareX ($Configuration, $Platform)..."
& dotnet build $projectPath -c $Configuration "-p:Platform=$Platform" --nologo
if ($LASTEXITCODE -ne 0) {
    throw "The ShareX build failed (exit code $LASTEXITCODE)."
}

if (-not (Test-Path -LiteralPath $buildExecutablePath)) {
    throw "The ShareX build completed without producing $buildExecutablePath."
}

# --- 3. Ensure FFmpeg sits beside the build so the video tools work -------------------------------

$buildFFmpegPath = Join-Path $outputDirectory "ffmpeg.exe"
if (-not (Test-Path -LiteralPath $buildFFmpegPath)) {
    $systemFFmpeg = Get-Command ffmpeg.exe -CommandType Application -ErrorAction SilentlyContinue | Select-Object -First 1
    if ($null -eq $systemFFmpeg) {
        throw "FFmpeg was not found beside ShareX.exe or on PATH. Configure a custom FFmpeg path before testing video tools."
    }

    Write-Host "Copying FFmpeg from $($systemFFmpeg.Source)..."
    Copy-Item -LiteralPath $systemFFmpeg.Source -Destination $buildFFmpegPath -Force
}

# --- 4. Deploy the fresh build to the stable install directory ------------------------------------

Write-Host "Installing to $InstallDirectory ..."
if (-not (Test-Path -LiteralPath $InstallDirectory)) {
    New-Item -ItemType Directory -Path $InstallDirectory -Force | Out-Null
}

# robocopy /MIR mirrors the build output into the install dir (adds new files, removes stale ones).
& robocopy $outputDirectory $InstallDirectory /MIR /NFL /NDL /NJH /NJS /NP /R:3 /W:1 | Out-Null
# robocopy exit codes 0-7 are success (bit flags); >= 8 means a real failure.
if ($LASTEXITCODE -ge 8) {
    throw "Copying the build to $InstallDirectory failed (robocopy exit code $LASTEXITCODE)."
}
$global:LASTEXITCODE = 0

if (-not (Test-Path -LiteralPath $installExecutablePath)) {
    throw "The install completed without producing $installExecutablePath."
}

# --- 5. Make it start with Windows (per-user Startup shortcut, launched silently to the tray) -----

$startupDirectory = [System.Environment]::GetFolderPath('Startup')
$shortcutPath = Join-Path $startupDirectory "ShareX.lnk"
Write-Host "Registering startup shortcut: $shortcutPath -> $installExecutablePath"

$shell = New-Object -ComObject WScript.Shell
$shortcut = $shell.CreateShortcut($shortcutPath)
$shortcut.TargetPath = $installExecutablePath
$shortcut.Arguments = "-silent"
$shortcut.WorkingDirectory = $InstallDirectory
$shortcut.IconLocation = $installExecutablePath
$shortcut.Description = "ShareX (dev build)"
$shortcut.Save()

# --- 6. Start the freshly installed build ---------------------------------------------------------

$process = Start-Process -FilePath $installExecutablePath -WorkingDirectory $InstallDirectory -PassThru
Write-Host "Started ShareX PID $($process.Id): $installExecutablePath"
Write-Host "Done. This build will now launch on sign-in via the Startup shortcut."
