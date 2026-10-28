# Optional comparative qualification, deliberately separate from the exported
# runtime. A compiler installation does not imply that hosts have slang linked.
option(VELOSIM_SVINT_QUALIFICATION "Build the pinned SVInt comparison and independent .NET oracle" OFF)
if(VELOSIM_SVINT_QUALIFICATION)
  find_package(slang 12.1.0 EXACT CONFIG REQUIRED)
  find_program(VELOSIM_DOTNET_EXECUTABLE dotnet REQUIRED)
  set(numeric_dir "${CMAKE_BINARY_DIR}/numeric-qualification")
  set(generator "${numeric_dir}/generator/GenerateNumericCorpus${CMAKE_EXECUTABLE_SUFFIX}")
  set(corpus "${numeric_dir}/numeric-corpus.tsv")
  add_custom_command(OUTPUT "${generator}"
    COMMAND "${CMAKE_COMMAND}" -E make_directory "${numeric_dir}/generator"
    COMMAND "${VELOSIM_DOTNET_EXECUTABLE}" publish "${PROJECT_SOURCE_DIR}/tools/GenerateNumericCorpus.cs"
      -c Release -o "${numeric_dir}/generator" -p:PublishAot=true
    DEPENDS "${PROJECT_SOURCE_DIR}/tools/GenerateNumericCorpus.cs" "${PROJECT_SOURCE_DIR}/global.json"
    VERBATIM)
  add_custom_command(OUTPUT "${corpus}"
    COMMAND "${CMAKE_COMMAND}" -E rm -f "${corpus}"
    COMMAND "${generator}" "${corpus}"
    DEPENDS "${generator}"
    VERBATIM)
  add_custom_target(velosim_numeric_oracle DEPENDS "${corpus}")
  add_executable(velosim_svint_qualification "${PROJECT_SOURCE_DIR}/tests/native/svint_qualification.cpp")
  target_link_libraries(velosim_svint_qualification PRIVATE slang::slang velosim_options)
  add_dependencies(velosim_svint_qualification velosim_numeric_oracle)
  add_executable(velosim_svint_simd_tests "${PROJECT_SOURCE_DIR}/tests/native/svint_simd_tests.cpp")
  target_link_libraries(velosim_svint_simd_tests PRIVATE slang::slang velosim_options)
  velosim_mutation_target(velosim_svint_simd_tests)
  add_executable(velosim_svint_simd_benchmark "${PROJECT_SOURCE_DIR}/tests/native/svint_simd_benchmark.cpp")
  target_link_libraries(velosim_svint_simd_benchmark PRIVATE slang::slang velosim_options)
  target_compile_definitions(velosim_svint_simd_benchmark PRIVATE
    "VELOSIM_BENCH_INSTRUMENTED=$<OR:$<BOOL:${VELOSIM_COVERAGE}>,$<BOOL:${VELOSIM_SANITIZERS}>>")
  option(VELOSIM_SVINT_FUZZING "Build libFuzzer differential checks for the experimental SIMD adapter" OFF)
  if(VELOSIM_SVINT_FUZZING)
    if(NOT CMAKE_CXX_COMPILER_ID MATCHES "Clang")
      message(FATAL_ERROR "SVInt differential libFuzzer target requires Clang")
    endif()
    add_executable(velosim_svint_simd_fuzz "${PROJECT_SOURCE_DIR}/tests/native/svint_simd_fuzz.cpp")
    target_link_libraries(velosim_svint_simd_fuzz PRIVATE slang::slang)
    target_compile_options(velosim_svint_simd_fuzz PRIVATE -fsanitize=fuzzer,address,undefined -fno-omit-frame-pointer)
    target_link_options(velosim_svint_simd_fuzz PRIVATE -fsanitize=fuzzer,address,undefined)
  endif()
  if(BUILD_TESTING)
    add_test(NAME svint_qualification COMMAND velosim_svint_qualification "${corpus}")
    add_test(NAME svint_simd COMMAND velosim_svint_simd_tests)
  endif()
endif()
