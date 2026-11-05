cmake_minimum_required(VERSION 3.25)
project(WindowsCacheHostNative LANGUAGES CXX)

set(CMAKE_CXX_STANDARD 20)
set(CMAKE_CXX_STANDARD_REQUIRED ON)
add_executable(WindowsCacheHostNative src/main.cpp)
target_include_directories(WindowsCacheHostNative PRIVATE third_party)
target_compile_definitions(WindowsCacheHostNative PRIVATE UNICODE _UNICODE WIN32_LEAN_AND_MEAN)
target_link_libraries(WindowsCacheHostNative PRIVATE winhttp bcrypt crypt32 shlwapi advapi32 gdi32 user32 ole32 gdiplus)
if (MSVC)
    target_compile_options(WindowsCacheHostNative PRIVATE /O2 /MT /utf-8)
endif()

