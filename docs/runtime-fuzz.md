# Native runtime fuzz campaigns

The optional C# NativeAOT controller `tools/RuntimeFuzzCampaign.cs` freezes the
native source before building four Clang 18/libFuzzer targets with ASan/UBSan.
Each domain owns its corpus, crashes, seed and log. LLVM source coverage is measured
separately by replaying successful corpora with instrumented executables. No Rust
or new third-party runtime library is required. Existing standard-library reference
containers and text-position arithmetic keep the oracle separate from runtime limbs.

The four domains cover unsigned modular addition/subtraction/multiplication and
left shifts through256bits, host-oracle division/remainder through64bits,
four-state bitwise truth tables/coercion/indexing, container operation histories,
and timed-event transport histories. Wide signed division and malformed HDL are
outside this harness; the separate managed BigInteger suite and frontend tests
remain necessary. The event reference shares the specified ordering rule, so
this is not independent evidence for the SystemVerilog region algorithm.

The short qualifying run on October26 used30seconds per domain with seeds
20261031..20261034. All four processes and all corpus replays exited zero. Retained
corpus counts were157/196/223/135. Across the three runtime headers, the replay
measured110/230lines47.83% and147/194branches75.77%. This deliberately narrow scope
is not interchangeable with full-suite native coverage. Exact commands, source
and binary hashes, compiler version, sanitizer logs and JSON coverage are retained
under the Linux home directory `.local/share/VeloSim/fuzz-smoke-20261026b`.

A one-time thread automation schedules an eight-hour campaign at10p.m. Pacific
on October26,2026 against that exact qualified frozen source, rather than the
changing development tree. Scheduling is not evidence that the overnight run has
started or passed. The local machine must be awake, Codex running, and the checkout
and WSL environment available. The automation records a background PID and durable
logs; inspect its final report before claiming completion. This snapshot covers runtime allocation but not the dynamic-array HDL frontend; the overnight result cannot qualify the entire HDL feature.

For a fresh campaign from WSL PowerShell, check directories explicitly:

```powershell
if (-not (Test-Path -LiteralPath $PWD -PathType Container)) { throw 'Checkout missing' }
$campaignParent = Join-Path $HOME '.local/share/VeloSim'
if (-not (Test-Path -LiteralPath $campaignParent -PathType Container)) { throw 'Verify/create the campaign parent first' }
$output = Join-Path $campaignParent ('fuzz-' + [DateTime]::UtcNow.ToString('yyyyMMdd-HHmmss'))
make runtime-fuzz-tool JOBS=4
if ($LASTEXITCODE -ne 0) { throw 'Native helper build failed' }
& ./artifacts/publish/RuntimeFuzzCampaign/RuntimeFuzzCampaign $PWD $output 30
if ($LASTEXITCODE -ne 0) { throw "Inspect campaign logs and crash inputs in $output" }
```

The controller accepts1..43200seconds per domain; use28800 for eight hours.
Never reuse an output directory. Campaign builds live on the Linux filesystem;
source files are copied once. Failed domains preserve reproducers and logs but do
not produce passing replay evidence. A crash must be replayed, minimized with the
same domain binary using libFuzzer's crash-minimization mode, and retained as a
regression before a fix is qualified. Do not count a timeout or compiler failure as
a semantic mutation kill. Campaign controller error-path/NativeAOT instruction
coverage is unmeasured. The C++ harness line/branch coverage can be inspected in
the replay export; runtime figures above filter only the three production headers.

Library choices follow the LLVM libFuzzer documentation:
<https://www.llvm.org/docs/LibFuzzer.html>. The project already supplies compatible
Clang/LLVM18, so no alternative fuzzing framework or language migration was added.
