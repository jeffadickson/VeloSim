DOTNET ?= dotnet
.DEFAULT_GOAL := build
JOBS ?= 4
BUILD_DLL := tools/VeloSim.Build/bin/Release/net10.0/VeloSim.Build.dll
NATIVE_BUILD_TOOL := artifacts/publish/VeloSim.Build/VeloSim.Build
NATIVE_MUTATION_TOOL := artifacts/publish/BlockingMutationProbe/BlockingMutationProbe
BUILD_SOURCES := $(wildcard tools/VeloSim.Build/*.cs tools/VeloSim.Build/*.csproj) Directory.Build.props Directory.Build.targets global.json
.PHONY: build test coverage native-coverage benchmark publish bundle doctor coverage-check coverage-current
.PHONY: mutation-probe requirements-docs
$(NATIVE_MUTATION_TOOL): tools/BlockingMutationProbe.cs $(wildcard src/VeloSim.Core/*.cs) src/VeloSim.Core/VeloSim.csproj Directory.Build.props Directory.Build.targets global.json
	$(DOTNET) publish tools/BlockingMutationProbe.cs -c Release -r linux-x64 -p:PublishAot=true -p:VeloSimNativeJobs=$(JOBS) -o artifacts/publish/BlockingMutationProbe
mutation-probe: $(NATIVE_MUTATION_TOOL)
	"$(NATIVE_MUTATION_TOOL)" "$(CURDIR)"
requirements-docs:
	uv tool run --from strictdoc==0.30.4 strictdoc export requirements --config strictdoc_config.py --output-dir artifacts/requirements-200/strictdoc --no-parallelization
$(BUILD_DLL): $(BUILD_SOURCES)
	$(DOTNET) build tools/VeloSim.Build -c Release -m:$(JOBS)
build test coverage native-coverage benchmark publish bundle doctor coverage-check coverage-current: $(BUILD_DLL)
	@use_native=true; for source in $(BUILD_SOURCES); do \
	  if [ "$$source" -nt "$(NATIVE_BUILD_TOOL)" ]; then use_native=false; fi; done; \
	if [ "$@" != publish ] && [ "$@" != bundle ] && [ -x "$(NATIVE_BUILD_TOOL)" ] && [ "$$use_native" = true ]; then \
	  VELOSIM_JOBS=$(JOBS) "$(NATIVE_BUILD_TOOL)" $@; \
	else VELOSIM_JOBS=$(JOBS) $(DOTNET) "$(BUILD_DLL)" $@; fi

.PHONY: combinational-mutation-probe combinational-qualification-tool
combinational-qualification-tool:
	$(DOTNET) publish tools/CombinationalQualification.cs -c Release -r linux-x64 -p:PublishAot=true -p:VeloSimNativeJobs=$(JOBS) -o artifacts/publish/CombinationalQualification
combinational-mutation-probe:
	$(DOTNET) publish tools/CombinationalMutationProbe.cs -c Release -r linux-x64 -p:PublishAot=true -p:VeloSimNativeJobs=$(JOBS) -o artifacts/publish/CombinationalMutationProbe
	"artifacts/publish/CombinationalMutationProbe/CombinationalMutationProbe" "$(CURDIR)"
.PHONY: function-mutation-probe
function-mutation-probe:
	$(DOTNET) publish tools/FunctionMutationProbe.cs -c Release -r linux-x64 -p:PublishAot=true -p:VeloSimNativeJobs=$(JOBS) -o artifacts/publish/FunctionMutationProbe
	"artifacts/publish/FunctionMutationProbe/FunctionMutationProbe" "$(CURDIR)"

.PHONY: surelog-tagfix
.PHONY: runtime-fuzz-tool runtime-fuzz-smoke
.PHONY: remote-script-tests
.PHONY: queue-mutation-probe
CONTAINER_MUTATION_TOOL := artifacts/publish/ContainerEventMutationProbe/ContainerEventMutationProbe
$(CONTAINER_MUTATION_TOOL): tools/ContainerEventMutationProbe.cs tools/ContainerEventMutationProbe.packages.lock.json Directory.Build.props Directory.Build.targets global.json
	$(DOTNET) publish tools/ContainerEventMutationProbe.cs -c Release -r linux-x64 -p:PublishAot=true -p:VeloSimNativeJobs=$(JOBS) -o artifacts/publish/ContainerEventMutationProbe
queue-mutation-probe: $(CONTAINER_MUTATION_TOOL)
	"$(CONTAINER_MUTATION_TOOL)" "$(CURDIR)" queue
remote-script-tests:
	pwsh -NoProfile -File tools/remote/Test-RemoteScripts.ps1
FUZZ_TOOL := artifacts/publish/RuntimeFuzzCampaign/RuntimeFuzzCampaign
FUZZ_OUTPUT ?=
$(FUZZ_TOOL): tools/RuntimeFuzzCampaign.cs tools/RuntimeFuzzCampaign.packages.lock.json Directory.Build.props Directory.Build.targets global.json
	$(DOTNET) publish tools/RuntimeFuzzCampaign.cs -c Release -r linux-x64 -p:PublishAot=true -p:VeloSimNativeJobs=$(JOBS) -o artifacts/publish/RuntimeFuzzCampaign
runtime-fuzz-tool: $(FUZZ_TOOL)
runtime-fuzz-smoke: runtime-fuzz-tool
	@test -n "$(FUZZ_OUTPUT)" || { echo 'Set FUZZ_OUTPUT to a fresh path with an existing parent'; exit 2; }
	"$(FUZZ_TOOL)" "$(CURDIR)" "$(FUZZ_OUTPUT)" 30
# Explicit pinned checkout: this target never modifies the installed frontend.
SURELOG_SOURCE ?=
SURELOG_TAGFIX_BUILD ?= artifacts/surelog-tagfix
surelog-tagfix:
	cmake -S tools/surelog-tagfix -B "$(SURELOG_TAGFIX_BUILD)" -G Ninja -DCMAKE_BUILD_TYPE=Release -DCMAKE_PREFIX_PATH=/opt/velosim -DSURELOG_SOURCE="$(SURELOG_SOURCE)"
	cmake --build "$(SURELOG_TAGFIX_BUILD)" --parallel $(JOBS)
