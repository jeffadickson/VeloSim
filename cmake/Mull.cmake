# Optional maintained LLVM mutation tooling. Ordinary native/SDK builds do not
# load a compiler plugin or acquire the tool as a runtime dependency.
set(VELOSIM_MULL_PLUGIN "" CACHE FILEPATH "Pinned Mull LLVM18 compiler plugin (optional)")
set(VELOSIM_MULL_RUNNER "" CACHE FILEPATH "Matching mull-runner-18 executable (optional)")
if(VELOSIM_MULL_PLUGIN)
  if(NOT CMAKE_CXX_COMPILER_ID MATCHES "Clang" OR
     CMAKE_CXX_COMPILER_VERSION VERSION_LESS 18 OR NOT CMAKE_CXX_COMPILER_VERSION VERSION_LESS 19)
    message(FATAL_ERROR "The pinned Mull18 integration requires Clang18")
  endif()
  if(NOT EXISTS "${VELOSIM_MULL_PLUGIN}")
    message(FATAL_ERROR "Mull compiler plugin does not exist")
  endif()
  if(VELOSIM_MULL_RUNNER AND NOT EXISTS "${VELOSIM_MULL_RUNNER}")
    message(FATAL_ERROR "Mull runner does not exist")
  endif()
endif()

function(velosim_mutation_target target)
  if(NOT VELOSIM_MULL_PLUGIN)
    return()
  endif()
  set(config "${PROJECT_SOURCE_DIR}/verification/mull-rtl.yml")
  target_compile_options(${target} PRIVATE "-fpass-plugin=${VELOSIM_MULL_PLUGIN}" -g -grecord-command-line)
  # Make the configuration explicit for both Ninja compilation and execution;
  # preserve a configured compiler launcher after the environment wrapper.
  set(launcher "${CMAKE_COMMAND}" -E env "MULL_CONFIG=${config}")
  if(CMAKE_CXX_COMPILER_LAUNCHER)
    list(APPEND launcher ${CMAKE_CXX_COMPILER_LAUNCHER})
  endif()
  set_property(TARGET ${target} PROPERTY CXX_COMPILER_LAUNCHER "${launcher}")
  get_target_property(inputs ${target} SOURCES)
  foreach(input IN LISTS inputs)
    set_property(SOURCE "${input}" APPEND PROPERTY OBJECT_DEPENDS "${config}")
  endforeach()
  if(VELOSIM_MULL_RUNNER)
    set(report_dir "${CMAKE_BINARY_DIR}/mutations/${target}")
    add_custom_target(${target}_mutations
      COMMAND "${CMAKE_COMMAND}" -E make_directory "${report_dir}"
      COMMAND "${CMAKE_COMMAND}" -E env "MULL_CONFIG=${config}"
        "${VELOSIM_MULL_RUNNER}" -reporters=Elements -report-dir "${report_dir}"
        -report-name "${target}" "$<TARGET_FILE:${target}>"
      DEPENDS ${target}
      WORKING_DIRECTORY "${PROJECT_SOURCE_DIR}"
      VERBATIM)
  endif()
endfunction()
