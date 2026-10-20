DOTNET ?= dotnet
.DEFAULT_GOAL := build
JOBS ?= 4
BUILD_DLL := tools/VeloSim.Build/bin/Release/net10.0/VeloSim.Build.dll
NATIVE_BUILD_TOOL := artifacts/publish/VeloSim.Build/VeloSim.Build
BUILD_SOURCES := $(wildcard tools/VeloSim.Build/*.cs tools/VeloSim.Build/*.csproj) Directory.Build.props global.json
.PHONY: build test coverage native-coverage publish
$(BUILD_DLL): $(BUILD_SOURCES)
	$(DOTNET) build tools/VeloSim.Build -c Release -m:$(JOBS)
build test coverage native-coverage publish: $(BUILD_DLL)
	@if [ -x "$(NATIVE_BUILD_TOOL)" ] && [ ! "$(BUILD_DLL)" -nt "$(NATIVE_BUILD_TOOL)" ]; then \
	  VELOSIM_JOBS=$(JOBS) "$(NATIVE_BUILD_TOOL)" $@; \
	else VELOSIM_JOBS=$(JOBS) $(DOTNET) "$(BUILD_DLL)" $@; fi
