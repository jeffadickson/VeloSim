# Shared checked orchestration; importing this file never starts a campaign.
Set-StrictMode -Version Latest

function Assert-RunDirectory {
    param([Parameter(Mandatory)][string]$Path, [switch]$Create)
    if (Test-Path -LiteralPath $Path -PathType Container) { return }
    if (-not $Create) { throw "Directory missing: $Path" }
    $parent = Split-Path -Parent $Path
    if (-not (Test-Path -LiteralPath $parent -PathType Container)) { throw "Parent directory missing: $parent" }
    New-Item -ItemType Directory -Path $Path -ErrorAction Stop | Out-Null
}

function Invoke-RunCommand {
    param([Parameter(Mandatory)][string]$Tool, [string[]]$Arguments = @(),
          [Parameter(Mandatory)][string]$Directory, [int]$TimeoutSeconds = 1200,
          [string]$LogPrefix, [switch]$AllowFailure)
    Assert-RunDirectory $Directory
    $command = Get-Command $Tool -ErrorAction SilentlyContinue
    if (-not $command) { throw "Required tool missing: $Tool" }
    if ($LogPrefix) { Assert-RunDirectory (Split-Path -Parent $LogPrefix) }
    $info = [Diagnostics.ProcessStartInfo]::new($command.Source)
    $info.WorkingDirectory = $Directory
    $info.UseShellExecute = $false
    $info.CreateNoWindow = $true
    $info.RedirectStandardOutput = $true
    $info.RedirectStandardError = $true
    if ([IO.Path]::GetFileNameWithoutExtension($command.Source) -eq 'git') {
        $info.Environment['GIT_TERMINAL_PROMPT']='0'
        $info.Environment['GCM_INTERACTIVE']='Never'
    }
    foreach ($argument in $Arguments) { $info.ArgumentList.Add($argument) }
    $process = [Diagnostics.Process]::Start($info)
    try {
        $stdout = $process.StandardOutput.ReadToEndAsync()
        $stderr = $process.StandardError.ReadToEndAsync()
        $timedOut = -not $process.WaitForExit($TimeoutSeconds * 1000)
        if ($timedOut) {
            $process.Kill($true)
            $process.WaitForExit()
        }
        $result = [pscustomobject]@{ Code = $process.ExitCode; Out = $stdout.GetAwaiter().GetResult(); Err = $stderr.GetAwaiter().GetResult() }
        if ($LogPrefix) {
            [IO.File]::WriteAllText($LogPrefix + '.out.log', $result.Out)
            [IO.File]::WriteAllText($LogPrefix + '.err.log', $result.Err)
        }
        if ($timedOut) { throw "Command exceeded its checked deadline: $Tool. Partial output was retained." }
        if ($result.Code -ne 0 -and -not $AllowFailure) {
            $detail=if($LogPrefix){"See $LogPrefix.err.log"}else{$result.Err.Trim()}
            throw "Command failed (exit $($result.Code)): $Tool. $detail"
        }
        return $result
    } finally { $process.Dispose() }
}

function Invoke-GitWithRecovery {
    # Commit/signing and network authentication failures retain all local work.
    # A retry repeats only this Git command, never an already completed test run.
    param([string[]]$Arguments, [string]$Directory, [string]$LogPrefix,
          [scriptblock]$Runner = { param($a,$d,$l) Invoke-RunCommand git $a $d -LogPrefix $l -AllowFailure },
          [scriptblock]$Prompt = { param($message) Read-Host $message },
          [scriptblock]$BeforeAttempt = { param($directory) },
          [switch]$NonInteractive)
    $attempt=0
    while ($true) {
        ++$attempt
        $attemptLog=if($LogPrefix){$LogPrefix+'-attempt-'+$attempt.ToString('000')}else{''}
        & $BeforeAttempt $Directory
        $result = & $Runner $Arguments $Directory $attemptLog
        if ($result.Code -eq 0) { return $result }
        Write-Host 'Git is paused. Your source, test results and any local commit are preserved.'
        Write-Host 'Authorize GitHub with gh auth login --hostname github.com --web, or fix the identity/signing/network issue shown in the retained log.'
        if ($NonInteractive) { throw 'ACTION_REQUIRED: Git authorization or configuration; resume publication after fixing it.' }
        try { $answer = & $Prompt 'Press Enter to retry this Git step, or Q to save and exit' }
        catch { throw 'ACTION_REQUIRED: no interactive prompt is available; resume publication interactively.' }
        if($null -eq $answer){throw 'ACTION_REQUIRED: input closed; results remain local for publication resume.'}
        if ($answer -eq 'Q') { throw 'ACTION_REQUIRED: Publication paused by user; results remain local.' }
    }
}

function Write-RunJson {
    param([string]$Path, [object]$Value)
    Assert-RunDirectory (Split-Path -Parent $Path)
    $temporary=$Path+'.tmp'
    $Value | ConvertTo-Json -Depth 20 | Set-Content -LiteralPath $temporary -Encoding utf8
    [IO.File]::Move($temporary,$Path,$true)
}
