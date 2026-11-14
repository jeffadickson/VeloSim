BeforeAll {
    $verify=Join-Path (Split-Path -Parent $PSScriptRoot) 'scripts/Test-SchematicBundle.ps1'
    $publish=Join-Path (Split-Path -Parent $PSScriptRoot) 'scripts/Publish-Schematic.ps1'
    function New-BundleFixture([string]$Root,[string]$Runtime='win-x64'){
        if(!(Test-Path -LiteralPath (Split-Path -Parent $Root) -PathType Container)){throw 'Missing fixture parent'}
        New-Item -ItemType Directory -Path $Root|Out-Null
        $suffix=if($Runtime -eq 'win-x64'){'.exe'}else{''}
        $files=@('wwwroot/index.html',"SchematicLab.WebHost$suffix","mcp/SchematicLab.Mcp$suffix",'Launch-Schematic.ps1','Test-SchematicBundle.ps1','README.txt')
        $entries=@(foreach($relative in $files){
            $path=Join-Path $Root $relative;$parent=Split-Path -Parent $path
            if(!(Test-Path -LiteralPath $parent -PathType Container)){New-Item -ItemType Directory -Path $parent|Out-Null}
            [IO.File]::WriteAllText($path,"fixture: $relative")
            @{path=$relative;sha256=(Get-FileHash -LiteralPath $path).Hash.ToLowerInvariant();bytes=(Get-Item -LiteralPath $path).Length}
        })
        @{schema='velosim.schematic.bundle.v1';package_kind='editor_only';framework='net11.0';runtime=$Runtime;self_contained=$true;files=$entries}|ConvertTo-Json -Depth 5|Set-Content -LiteralPath (Join-Path $Root schematic-bundle.json)
    }
    function Change-Manifest([string]$Root,[scriptblock]$Change){
        $path=Join-Path $Root schematic-bundle.json
        $data=Get-Content -LiteralPath $path -Raw|ConvertFrom-Json
        & $Change $data
        $data|ConvertTo-Json -Depth 5|Set-Content -LiteralPath $path
    }
}

Describe 'Standalone distribution integrity and refusal boundaries' {
    BeforeEach {
        $bundle=Join-Path $TestDrive ([guid]::NewGuid().ToString('N'))
        New-BundleFixture $bundle
    }

    It 'accepts an intact manifest and the required entry points' {
        & $verify -Directory $bundle|Should -Match '^Verified '
    }
    It 'checks Linux bundle identities without claiming runtime execution' {
        $linux=Join-Path $TestDrive ([guid]::NewGuid().ToString('N'))
        New-BundleFixture $linux 'linux-x64'
        & $verify -Directory $linux|Should -Match '^Verified '
    }
    It 'rejects a changed recorded file' {
        [IO.File]::WriteAllText((Join-Path $bundle README.txt),'corruption')
        {& $verify -Directory $bundle}|Should -Throw '*hash mismatch*'
    }
    It 'rejects a missing recorded file' {
        Remove-Item -LiteralPath (Join-Path $bundle README.txt)
        {& $verify -Directory $bundle}|Should -Throw '*Missing or escaping*'
    }
    It 'rejects an unrecorded executable dependency' {
        [IO.File]::WriteAllText((Join-Path $bundle Unexpected.dll),'unexpected')
        {& $verify -Directory $bundle}|Should -Throw '*Unrecorded*'
    }
    It 'rejects duplicate manifest entries' {
        Change-Manifest $bundle {param($data) $data.files+=@($data.files[0])}
        {& $verify -Directory $bundle}|Should -Throw '*duplicate*'
    }
    It 'rejects unsafe relative entry <Path>' -TestCases @(
        @{Path='../outside'},@{Path='/absolute'},@{Path='wwwroot/../README.txt'},
        @{Path='C:/Windows/win.ini'},@{Path='a:b'},@{Path='wwwroot\index.html'}
    ) {
        param($Path)
        Change-Manifest $bundle {param($data) $data.files[0].path=$Path}
        {& $verify -Directory $bundle}|Should -Throw
    }
    It 'rejects unsupported runtime metadata' {
        Change-Manifest $bundle {param($data) $data.runtime='unknown'}
        {& $verify -Directory $bundle}|Should -Throw '*Unsupported*'
    }
    It 'rejects an omitted required executable even when all remaining hashes match' {
        Change-Manifest $bundle {param($data) $data.files=@($data.files|Where-Object path -ne 'mcp/SchematicLab.Mcp.exe')}
        Remove-Item -LiteralPath (Join-Path $bundle 'mcp/SchematicLab.Mcp.exe')
        {& $verify -Directory $bundle}|Should -Throw '*Required bundle entry*'
    }
    It 'refuses to overwrite an existing publication directory' {
        {& $publish -OutputDirectory $bundle}|Should -Throw '*fresh output*'
    }
    It 'refuses a simulation publication without its solver before building' {
        $fresh=Join-Path $TestDrive ([guid]::NewGuid().ToString('N'))
        {& $publish -OutputDirectory $fresh}|Should -Throw '*requires SolverPath*'
        Test-Path -LiteralPath $fresh|Should -BeFalse
    }
    It 'rejects a simulation manifest without recorded solver bytes' {
        Change-Manifest $bundle {param($data) $data.package_kind='simulation'}
        {& $verify -Directory $bundle}|Should -Throw '*solver*'
    }
    It 'accepts simulation only when the solver record matches the recorded executable' {
        $directory=Join-Path $bundle solver
        New-Item -ItemType Directory -Path $directory|Out-Null
        $solverFixturePath=Join-Path $directory circuit-simulator.exe
        [IO.File]::WriteAllText($solverFixturePath,'fixture-not-a-runtime-execution')
        $hash=(Get-FileHash -LiteralPath $solverFixturePath).Hash.ToLowerInvariant()
        Change-Manifest $bundle {param($data)
            $data.package_kind='simulation'
            $data.files+=@{path='solver/circuit-simulator.exe';sha256=$hash;bytes=(Get-Item -LiteralPath $solverFixturePath).Length}
            $data|Add-Member solver @{file='solver/circuit-simulator.exe';sha256=$hash;declared_source_commit=('a'*40)}
        }
        & $verify -Directory $bundle|Should -Match '^Verified '
        Change-Manifest $bundle {param($data) $data.solver.sha256='b'*64}
        {& $verify -Directory $bundle}|Should -Throw '*Solver provenance hash mismatch*'
    }
}
