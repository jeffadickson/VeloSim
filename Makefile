DOTNET ?= dotnet
.DEFAULT_GOAL := build
JOBS ?= 4
BUILD_DLL := tools/VeloSim.Build/bin/Release/net10.0/VeloSim.Build.dll
NATIVE_BUILD_TOOL := artifacts/publish/VeloSim.Build/VeloSim.Build
BUILD_SOURCES := $(wildcard tools/VeloSim.Build/*.cs tools/VeloSim.Build/*.csproj) Directory.Build.props global.json
.PHONY: build test coverage native-coverage benchmark publish bundle doctor coverage-check
$(BUILD_DLL): $(BUILD_SOURCES)
	$(DOTNET) build tools/VeloSim.Build -c Release -m:$(JOBS)
build test coverage native-coverage benchmark publish bundle doctor coverage-check: $(BUILD_DLL)
	@use_native=true; for source in $(BUILD_SOURCES); do \
	  if [ "$$source" -nt "$(NATIVE_BUILD_TOOL)" ]; then use_native=false; fi; done; \
	if [ "$@" != publish ] && [ "$@" != bundle ] && [ -x "$(NATIVE_BUILD_TOOL)" ] && [ "$$use_native" = true ]; then \
	  VELOSIM_JOBS=$(JOBS) "$(NATIVE_BUILD_TOOL)" $@; \
	else VELOSIM_JOBS=$(JOBS) $(DOTNET) "$(BUILD_DLL)" $@; fi
