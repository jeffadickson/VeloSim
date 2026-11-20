# Standalone Windows campaign: download, run for 12 hours, commit and publish

This workflow runs on a Windows machine without an assistant connection. Windows
PowerShell7.4+ orchestrates an Ubuntu24.04 WSL2 environment. Native builds and large
outputs stay on the Linux filesystem. It runs four LLVM/libFuzzer domains with
sanitizers, managed tests with Coverlet, and an established Stryker.NET campaign.
The source is frozen at the downloaded Git commit. It does not install Surelog/UHDM
or claim HDL frontend conformance from these runtime/IR tests.

The12-hour budget applies to native testing; preparation and coverage replay take
additional time. Stryker runs once over the configured five managed source files,
under the same12-hour limit, and may finish earlier. It does not repeat identical
mutants merely to fill time. A timed-out or unfinished campaign is reported as
incomplete. Surviving mutants and uncovered code remain visible in the results.

## 1. Install the Windows prerequisites

Use x64 Windows10/11 with virtualization enabled, WSL2, preferably8logical processors,
32GB RAM and20GB free disk. Keep the machine plugged in, awake and connected during
setup/publication. The scripts do not change sleep or protection settings.
ARM64 NativeAOT publication is not qualified by this package.

In an **administrator PowerShell** window, check/reuse installed tools:

```powershell
if (-not (Get-Command winget -ErrorAction SilentlyContinue)) {
    throw 'Install Microsoft App Installer from Microsoft Store first.'
}
foreach ($package in @(
    @{ Tool='git'; Id='Git.Git' },
    @{ Tool='gh'; Id='GitHub.cli' },
    @{ Tool='pwsh'; Id='Microsoft.PowerShell' }
)) {
    if (-not (Get-Command $package.Tool -ErrorAction SilentlyContinue)) {
        winget install --exact --id $package.Id --source winget
        if ($LASTEXITCODE -ne 0) { throw "Installation needs attention: $($package.Id)" }
    }
}
wsl --list --verbose
# If Ubuntu-24.04 is absent:
wsl --install -d Ubuntu-24.04
```

Restart Windows if requested. Open Ubuntu once with `wsl -d Ubuntu-24.04` and
finish its Linux username/password setup. Confirm it uses WSL2 with
`wsl --list --verbose`. If necessary, run `wsl --set-version Ubuntu-24.04 2`.
Then open a **new PowerShell7 window** so installed tools are on its PATH.
The remaining steps normally run as your regular Windows user.

Optional preauthorization reduces the chance of an end-of-run pause:

```powershell
gh auth login --hostname github.com --web
gh auth setup-git
```

Never paste tokens into the script, command arguments, reports or this chat.
Git identity may come from your existing configuration; otherwise publication
asks for a name/email in the isolated checkout. Existing signing policy is preserved.

## 2. Download the complete launcher

The files below must be from the same published ref. Use a new bootstrap directory
if an earlier download exists; the command refuses to overwrite unknown files.

```powershell
$profileRoot = $env:USERPROFILE
if (-not (Test-Path -LiteralPath $profileRoot -PathType Container)) { throw 'Profile directory missing' }
$bootstrap = Join-Path $profileRoot 'VeloSimBootstrap'
if (-not (Test-Path -LiteralPath $bootstrap -PathType Container)) {
    New-Item -ItemType Directory -Path $bootstrap | Out-Null
}
$ref = 'procedural-types-arrays'
$base = "https://raw.githubusercontent.com/jeffadickson/VeloSim/$ref/tools/remote"
foreach ($name in @('Remote.Common.ps1','Publish-RemoteResults.ps1','Start-VeloSimCampaign.ps1','Run-LinuxCampaign.ps1')) {
    $file = Join-Path $bootstrap $name
    if (Test-Path -LiteralPath $file) { throw "Existing file preserved: $file. Choose a new bootstrap directory." }
    Invoke-WebRequest "$base/$name" -OutFile $file -ErrorAction Stop
    Get-FileHash -LiteralPath $file -Algorithm SHA256
}
```

Review the downloaded files before running them. If your machine's policy permits
running these reviewed files, remove their download tags individually:

```powershell
if (-not (Test-Path -LiteralPath $bootstrap -PathType Container)) { throw 'Bootstrap directory missing' }
foreach ($name in @('Remote.Common.ps1','Publish-RemoteResults.ps1','Start-VeloSimCampaign.ps1','Run-LinuxCampaign.ps1')) {
    $file = Join-Path $bootstrap $name
    if (-not (Test-Path -LiteralPath $file -PathType Leaf)) { throw "Downloaded file missing: $file" }
    Unblock-File -LiteralPath $file
}
```

Do not disable protection or machine-wide execution policy. Corporate restrictions
may require your administrator's approval. The scripts record actual command
failures in logs and stop rather than claiming unavailable tools succeeded.

## 3. Optional short qualification on the remote machine

`-InstallPrerequisites` explicitly authorizes Ubuntu package installation and
installation of the repository-pinned .NET SDK if missing. Existing compatible
tools and cached packages are reused. No custom Python automation is installed.

```powershell
if (-not (Test-Path -LiteralPath $bootstrap -PathType Container)) { throw 'Bootstrap directory missing' }
if (-not (Test-Path -LiteralPath $env:USERPROFILE -PathType Container)) { throw 'Profile directory missing' }
$smokeRoot = Join-Path $env:USERPROFILE ('VeloSimSmoke-' + [DateTime]::UtcNow.ToString('yyyyMMdd-HHmmss'))
& (Join-Path $bootstrap 'Start-VeloSimCampaign.ps1') -Root $smokeRoot -Smoke -InstallPrerequisites
```

Smoke mode runs30seconds per native domain and a narrow Stryker allocation/shape
campaign, with a30-minute mutation deadline. Downloads/builds and mutation testing
take longer than30seconds. It publishes a separate small results commit, just as
the full run does. An optional `-PrepareOnly` checks WSL/checkout preparation but
does not qualify builds/tests; use a distinct root for the actual run.

## 4. Run the 12-hour campaign

```powershell
if (-not (Test-Path -LiteralPath $bootstrap -PathType Container)) { throw 'Bootstrap directory missing' }
if (-not (Test-Path -LiteralPath $env:USERPROFILE -PathType Container)) { throw 'Profile directory missing' }
$runRoot = Join-Path $env:USERPROFILE ('VeloSim12h-' + [DateTime]::UtcNow.ToString('yyyyMMdd-HHmmss'))
& (Join-Path $bootstrap 'Start-VeloSimCampaign.ps1') -Root $runRoot -Hours 12 -MutationConcurrency 2 -InstallPrerequisites
```

Leave that PowerShell window open. No Codex app or connection to this chat is
required. Four native workers use isolated corpora. A single Stryker invocation
uses two concurrent mutation workers by default. Builds are bounded to four jobs;
NUnit/Coverlet jobs sharing outputs are serialized. The scripts never infer a
speedup from those settings.

Linux setup uses the Microsoft Ubuntu package repository for PowerShell,
Ubuntu's Clang/LLVM18/CMake/Ninja packages, the official .NET installer for the
exact `global.json` SDK if required, and pinned .NET local tools. Setup on a clean
machine requires Internet/package access. A failed setup pauses with logs; do not
label it a completed test run. Use a fresh root when restarting an incomplete
setup; previous files are preserved.

## 5. Results, authentication and resuming publication

At the end, the launcher prepares a new `results/remote-<UTC-id>` branch. It commits
small summaries plus an updated root `prompt.md`, then attempts a normal push.
The commit message contains `[skip ci]`; it neither changes workflow policy nor
dispatches CI. It verifies the remote branch SHA after a successful push.

Raw corpora, profiles, SDK/tool files and binaries remain in the WSL run directory
recorded by `state.json`. Only small report files under `reports/remote/<id>/` and
the handoff update are staged. The development/default branch is not merged or
force-pushed. Failed tests can still produce a clearly labeled results commit.

If commit/signing or GitHub authentication fails, the launcher pauses and preserves
the staged reports and any existing local commit. Authorize in another PowerShell
window, fix any identity/signing/network issue in the log, then press Enter to retry
only that Git operation. Press Q to stop safely. Later:

```powershell
if (-not (Test-Path -LiteralPath $bootstrap -PathType Container)) { throw 'Bootstrap directory missing' }
if (-not (Test-Path -LiteralPath $runRoot -PathType Container)) { throw 'Set $runRoot to the saved run directory' }
gh auth login --hostname github.com --web
gh auth setup-git
& (Join-Path $bootstrap 'Start-VeloSimCampaign.ps1') -Root $runRoot -ResumePublish
```

`-ResumePublish` never reruns12hours of tests. With `-NonInteractive`, a required
action produces a friendly saved-state message and exit2 instead of waiting for
input; resume interactively when available. Test outcomes are retained even if
publication remains unauthorized. Unexpected failures are logged and handled;
this does not promise every network, installer or test will succeed.

Check `state.json`, `logs/`, `report/summary.json`, `report/native-report.json`,
`report/native-coverage.stdout.log` and `report/mutation-counts.json`. JSON source
and executable hashes identify what actually ran. Review surviving mutations,
crashes and incomplete jobs rather than interpreting process exit0 as correctness.

## Verification and limitations

Local qualification includes PowerShell parsing, Pester process/directory/failure
checks, and a real commit/push/resume against a disposable local bare Git repository.
Pester covers the common/publisher functions; Windows WSL installation on a clean
machine, an expired live GitHub credential, twelve-hour completion and all launcher
error branches are separate/unmeasured scenarios. The Linux worker smoke result
and exact local metrics are recorded in the repository handoff after execution.
No twelve-hour run is claimed merely because the scripts exist.

Primary setup references: [Microsoft WSL](https://learn.microsoft.com/windows/wsl/install),
[PowerShell on Ubuntu](https://learn.microsoft.com/powershell/scripting/install/install-ubuntu),
[pinned .NET installation](https://learn.microsoft.com/dotnet/core/tools/dotnet-install-script),
[LLVM libFuzzer](https://www.llvm.org/docs/LibFuzzer.html), and
[Stryker.NET configuration](https://stryker-mutator.io/docs/stryker-net/configuration/).

Local script qualification:17Pester checks pass with zero failures/skips. Common
and publisher functions measure173/195commands88.72% and99/106lines93.40%.
Pester does not provide a branch-coverage figure in this report. Launcher, installer
and orchestration coverage is not included in those percentages. Linux worker
smokes completed their selected managed/native/mutation paths; no12-hour duration
is claimed. Reproduce script checks from a checkout using PowerShell7:

```powershell
if (-not (Test-Path -LiteralPath $PWD -PathType Container)) { throw 'Checkout missing' }
$checks = Join-Path $PWD 'tools/remote/Test-RemoteScripts.ps1'
if (-not (Test-Path -LiteralPath $checks -PathType Leaf)) { throw 'Script checks missing' }
& $checks -InstallPrerequisites
```
