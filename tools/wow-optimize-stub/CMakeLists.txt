cmake_minimum_required(VERSION 3.25)
project(server_sync_stub LANGUAGES CXX)

if(NOT CMAKE_SIZEOF_VOID_P EQUAL 4)
    message(FATAL_ERROR "wow-optimize is 32-bit: configure with -A Win32")
endif()

set(CMAKE_MSVC_RUNTIME_LIBRARY "MultiThreaded")
add_library(server_sync_stub STATIC server_sync_stub.cpp)
target_compile_options(server_sync_stub PRIVATE /Zl)
