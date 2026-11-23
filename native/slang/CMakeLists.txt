cmake_minimum_required(VERSION 3.20)
project(velosim_slang_frontend LANGUAGES CXX)
set(CMAKE_CXX_STANDARD 20)
set(CMAKE_CXX_STANDARD_REQUIRED ON)

find_package(slang CONFIG REQUIRED)

add_executable(velosim-slang-lower velosim_slang_lower.cpp)
target_link_libraries(velosim-slang-lower PRIVATE slang::slang)
if(TARGET velosim_options)
  target_link_libraries(velosim-slang-lower PRIVATE velosim_options)
endif()
install(TARGETS velosim-slang-lower RUNTIME DESTINATION bin)
