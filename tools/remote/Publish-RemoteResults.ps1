# Imported by the launcher; publication resumes without rerunning tests.
function Publish-VeloSimResults {
    param([object]$State,[string]$RunRoot,[switch]$NonInteractive)
    $checkout=$State.checkout;Assert-RunDirectory $checkout
    $report=Join-Path $RunRoot 'report';Assert-RunDirectory $report
    $logs=Join-Path $RunRoot 'logs';Assert-RunDirectory $logs
    $statePath=Join-Path $RunRoot 'state.json'
    $identity=Invoke-RunCommand git @('var','GIT_AUTHOR_IDENT') $checkout -AllowFailure
    if($identity.Code -ne 0){
        if($NonInteractive){throw 'ACTION_REQUIRED: set Git user.name and user.email in the isolated checkout, then resume.'}
        $name=Read-Host 'Git author name for this checkout';$email=Read-Host 'Git author email for this checkout'
        if(-not$name.Trim()-or-not$email.Trim()){throw 'ACTION_REQUIRED: author identity missing'}
        Invoke-RunCommand git @('config','user.name',$name) $checkout|Out-Null
        Invoke-RunCommand git @('config','user.email',$email) $checkout|Out-Null
    }
    if(-not$State.prepared){
        if((Invoke-RunCommand git @('status','--porcelain') $checkout).Out.Trim()){throw 'Unknown checkout changes: preserve them and choose a fresh run root'}
        Invoke-RunCommand git @('switch','-c',$State.branch) $checkout|Out-Null
        $reports=Join-Path $checkout 'reports';Assert-RunDirectory $reports -Create
        $remote=Join-Path $reports 'remote';Assert-RunDirectory $remote -Create
        $destination=Join-Path $remote $State.id;Assert-RunDirectory $destination -Create
        $allowed=@('summary.json','native-report.json','native-coverage.stdout.log','stryker.out.log','stryker.err.log','mutation-counts.json','run.json')
        foreach($file in Get-ChildItem -LiteralPath $report -File){
            if($file.Name -notin $allowed){throw 'Unknown report file preserved; refusing to publish unrecognized data'}
            if($file.Length -gt 2MB){throw 'Report exceeds the small-summary upload limit; raw data stays local'}
            Copy-Item -LiteralPath $file.FullName -Destination $destination
        }
        $prompt=Join-Path $checkout 'prompt.md'
        if(-not(Test-Path -LiteralPath $prompt -PathType Leaf)){throw 'Required prompt.md missing'}
        $previous=[IO.File]::ReadAllText($prompt)
        [IO.File]::WriteAllText($prompt,"# Remote test results $($State.id)`n`nSource revision: $($State.sourceCommit). See reports/remote/$($State.id)/. Raw artifacts remain local. These test observations do not close requirements or establish a CI result.`n`n"+$previous)
        Invoke-RunCommand git @('add','--','prompt.md',('reports/remote/'+$State.id)) $checkout|Out-Null
        $State.prepared=$true;Write-RunJson $statePath $State
    }
    if(-not$State.committed){
        $names=(Invoke-RunCommand git @('diff','--cached','--name-only') $checkout).Out -split '\r?\n'
        foreach($file in $names){if($file -and $file -ne 'prompt.md' -and -not$file.StartsWith('reports/remote/'+$State.id+'/')){throw 'Unknown staged files preserved; refusing to include them in the results commit'}}
        $message="Record VeloSim remote test results $($State.id) [skip ci]"
        $staged=Invoke-RunCommand git @('diff','--cached','--quiet') $checkout -AllowFailure
        if($staged.Code -eq 0){
            # Recover a crash after commit succeeded but before state was saved.
            $subject=(Invoke-RunCommand git @('log','-1','--format=%s') $checkout).Out.Trim()
            if($subject -ne $message){throw 'No staged results and no matching completed commit: inspect the preserved checkpoint'}
        }elseif($staged.Code -eq 1){
            $guard={param($directory)
                $pending=(Invoke-RunCommand git @('diff','--cached','--name-only') $directory).Out -split '\r?\n'
                foreach($file in $pending){if($file -and $file -ne 'prompt.md' -and -not$file.StartsWith('reports/remote/'+$State.id+'/')){throw 'Unknown staged files preserved; retry refused'}}
            }
            Invoke-GitWithRecovery @('commit','-m',$message) $checkout (Join-Path $logs 'git-commit') -NonInteractive:$NonInteractive -BeforeAttempt $guard|Out-Null
        }else{throw 'Could not inspect staged results'}
        $State.committed=$true;$State.resultCommit=(Invoke-RunCommand git @('rev-parse','HEAD') $checkout).Out.Trim();Write-RunJson $statePath $State
    }
    if((Invoke-RunCommand git @('rev-parse','HEAD') $checkout).Out.Trim() -ne $State.resultCommit){throw 'Checkout HEAD changed after the results commit; refusing to push unknown work'}
    Invoke-GitWithRecovery @('push','--set-upstream','origin',($State.resultCommit+':refs/heads/'+$State.branch)) $checkout (Join-Path $logs 'git-push') -NonInteractive:$NonInteractive|Out-Null
    $remote=Invoke-RunCommand git @('ls-remote','origin',('refs/heads/'+$State.branch)) $checkout
    if(($remote.Out -split '\s+')[0] -ne $State.resultCommit){throw 'Remote SHA mismatch; local results commit is preserved'}
    $State.pushed=$true;Write-RunJson $statePath $State
    Write-Host "Results committed and remote SHA verified: $($State.resultCommit) on $($State.branch)"
}
