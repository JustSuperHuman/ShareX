param(
    [string]$Configuration = "Debug",
    [ValidateSet("x64", "ARM64")]
    [string]$Platform = "x64"
)

$ErrorActionPreference = "Stop"

$repoRoot = Split-Path -Parent $PSScriptRoot
$projectPath = Join-Path $repoRoot "ShareX\ShareX.csproj"
$runtimeIdentifier = if ($Platform -eq "ARM64") { "win-arm64" } else { "win-x64" }
$outputDirectory = Join-Path $repoRoot "ShareX\bin\$Configuration\$runtimeIdentifier"
$executablePath = Join-Path $outputDirectory "ShareX.exe"

function Get-RepositoryShareXProcesses {
    if (-not (Test-Path -LiteralPath $executablePath)) {
        return @()
    }

    $normalizedExecutablePath = [System.IO.Path]::GetFullPath($executablePath)

    return @(Get-Process -Name "ShareX" -ErrorAction SilentlyContinue |
        Where-Object {
            try {
                $_.Path -and
                [System.IO.Path]::GetFullPath($_.Path).Equals(
                    $normalizedExecutablePath,
                    [System.StringComparison]::OrdinalIgnoreCase)
            }
            catch {
                $false
            }
        })
}

$runningProcesses = @(Get-RepositoryShareXProcesses)
if ($runningProcesses.Count -gt 0) {
    Write-Host "Closing the repository ShareX build..."

    try {
        Start-Process -FilePath $executablePath -ArgumentList "-ExitShareX" -WindowStyle Hidden -Wait
    }
    catch {
        Write-Warning "ShareX did not accept the exit command: $($_.Exception.Message)"
    }

    $deadline = [DateTime]::UtcNow.AddSeconds(10)
    do {
        Start-Sleep -Milliseconds 250
        $runningProcesses = @(Get-RepositoryShareXProcesses)
    } while ($runningProcesses.Count -gt 0 -and [DateTime]::UtcNow -lt $deadline)

    if ($runningProcesses.Count -gt 0) {
        Write-Host "Stopping the remaining repository ShareX process..."
        $runningProcesses | ForEach-Object { Stop-Process -Id $_.Id -Force }
        $runningProcesses | ForEach-Object { Wait-Process -Id $_.Id -Timeout 5 -ErrorAction SilentlyContinue }
    }
}

Write-Host "Building ShareX ($Configuration, $Platform)..."
& dotnet build $projectPath -c $Configuration "-p:Platform=$Platform" --nologo
if ($LASTEXITCODE -ne 0) {
    exit $LASTEXITCODE
}

$ffmpegPath = Join-Path $outputDirectory "ffmpeg.exe"
if (-not (Test-Path -LiteralPath $ffmpegPath)) {
    $systemFFmpeg = Get-Command ffmpeg.exe -CommandType Application -ErrorAction SilentlyContinue | Select-Object -First 1
    if ($null -eq $systemFFmpeg) {
        throw "FFmpeg was not found beside ShareX.exe or on PATH. Configure a custom FFmpeg path before testing video tools."
    }

    Write-Host "Copying FFmpeg from $($systemFFmpeg.Source)..."
    Copy-Item -LiteralPath $systemFFmpeg.Source -Destination $ffmpegPath -Force
}

if (-not (Test-Path -LiteralPath $executablePath)) {
    throw "The ShareX build completed without producing $executablePath."
}

$process = Start-Process -FilePath $executablePath -WorkingDirectory $outputDirectory -PassThru
Write-Host "Started ShareX PID $($process.Id): $executablePath"
