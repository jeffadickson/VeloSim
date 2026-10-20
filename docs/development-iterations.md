# C# / native development cycle

Requested: 200 substantive engineering iterations; independent simulation and tooling AI peer reviews every 10; local commits every 25; full builds/tests every 50. Focused compilation/tests also run when needed to validate changes. A parameterized test row is not counted as an iteration. No push is authorized in this cycle.

The previous uncommitted Surelog integration fixes are preserved in the first local checkpoint. Historical Python stays as a compatibility reference; active helper commands are being ported to compiled C#.

## Implemented iterations

| Iteration | Concrete change |
|---|---|
|001|Pin .NET SDK and shared deterministic .NET10 project settings.|
|002|Add NUnit test project, Coverlet collector and pinned ReportGenerator manifest.|
|003|Create root CMake/Ninja build for native IR and scheduler shared libraries.|
|004|Define and expose the typed native C ABI header.|
|005|Add compiled CTest ABI guards, atomic-drive and parallel NBA regressions.|
|006|Join native worker threads safely during exception unwinding.|
|007|Reject multibit reset inputs for scalar-reset native operations.|
|008|Implement compiled C# build/test orchestration with bounded jobs and subprocess deadlines.|
|009|Implement separate LLVM native and Coverlet managed coverage, with HTML reports.|
|010|Replace Makefile Python entrypoints with compiled C# commands and add NativeAOT publishing.|
|011|Validate immutable four-state words including 64-bit masks.|
|012|Parse strict IR signals/schema and reject duplicate/unknown JSON fields.|
|013|Validate process references, supported opcodes and destination ownership.|
|014|Write deterministic IR without reflection for NativeAOT compatibility.|
|015|Lower normalized UHDM counter processes with initially unknown SV logic.|
|016|Own native engines through generated P/Invoke and SafeHandle disposal.|
|017|Validate atomic batched drives, width matching and worker bounds before native calls.|
|018|Implement compiled simulation CLI with trace and reset-release controls.|
|019|Run external tools with argument lists, concurrent output reads and deadlines.|
|020|Elaborate HDL through a validated Surelog adapter with artifact uniqueness checks.|
|021|Orchestrate real Surelog/UHDM normalization and strict lowering into persistent IR.|
|022|Port slang AST elaboration with structured diagnostics and temporary-file cleanup.|
|023|Expose HDL compilation through the compiled asynchronous CLI.|
|024|Parse monotonic four-state traces and compare values, widths and lengths.|
|025|Run native regression cases with fresh engine ownership for every worker configuration.|

## Peer reviews and validation

- 010: independent simulation and tooling reviewers inspected the foundation. Fixed scheduler allocation exception ownership, added genuinely parallel native tests, fixed standalone native coverage builds, propagated instrumentation to UHDM tools, included JSON coverage and moved initialization under error handling. Native CTest 2/2 passes; C# build helper compiles without warnings. Core 011–015 focused NUnit 5/5 passes.
- 020: two reviewers identified reset assertion/schema inconsistencies and subprocess cancellation/drain deadlines. See `reviews/checkpoint020.md`; fixes precede local checkpoint025. Focused NUnit10/10 passed before the added regressions.
- 025: all020 findings fixed; NUnit16/16 and native CTest2/2 pass. Real C# compile→simulate runs301 cycles with reset then300 increments, final8-bit count44. Upgraded Coverlet to10.1.0 and Test SDK18.5.1 for .NET10 after finding the older collector emitted empty managed reports. Build helper rejects empty managed coverage. Actual combined report contains both VeloSim managed code and native sources; combined line96.5%, branch69.6% (not a managed-only percentage). LLVM native line93.2%.

## Local commit checkpoints

025: compiled helper foundation and frontend/native C# port, including prior local Surelog fixes. Pending050,075,100,125,150,175,200.

## Full build checkpoints

Pending 050, 100, 150, 200. Full validation includes managed tests, compiled native tests, coverage and NativeAOT publish/smoke where supported. Coverage percentages will be recorded from actual reports, separately for managed and native code.
