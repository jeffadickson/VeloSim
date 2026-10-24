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
