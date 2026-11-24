cmake_minimum_required(VERSION 3.25)
project(SynastriaLauncher LANGUAGES CXX RC)
set(LAUNCHER_VERSION "0.0.0" CACHE STRING "The launcher's version, stamped into the DLL")

if(NOT CMAKE_SIZEOF_VOID_P EQUAL 4)
    message(FATAL_ERROR "WowExt.exe is 32-bit: configure with -A Win32")
endif()

set(CMAKE_CXX_STANDARD 20)
set(CMAKE_CXX_STANDARD_REQUIRED ON)
set(CMAKE_MSVC_RUNTIME_LIBRARY "MultiThreaded$<$<CONFIG:Debug>:Debug>")
add_compile_options(/W4 /WX /permissive- /utf-8)

add_library(patcher_engine STATIC
    src/Engine.cpp
    src/Patch.cpp
    src/SimplePatches.cpp
    src/GraphicsLimits.cpp
    src/HdPortraits.cpp
    src/ShortNumbers.cpp
    src/DamageColors.cpp
    src/NameplateSort.cpp
    src/NameplateStack.cpp
    src/GroundTargets.cpp
    src/CursorOptimizations.cpp
    src/InteractKey.cpp
    src/AutoLogin.cpp)

string(REPLACE "." "," LAUNCHER_VERSION_COMMAS "${LAUNCHER_VERSION}")
configure_file(src/Version.rc.in "${CMAKE_CURRENT_BINARY_DIR}/Version.rc" @ONLY)
add_library(SynastriaLauncher SHARED src/Dll.cpp "${CMAKE_CURRENT_BINARY_DIR}/Version.rc")
target_link_libraries(SynastriaLauncher PRIVATE patcher_engine shell32)

add_library(image_file STATIC src/ImageFile.cpp)

add_executable(PatcherHost src/Host.cpp)
target_link_libraries(PatcherHost PRIVATE patcher_engine image_file)

add_executable(PatcherTests test/NativeTests.cpp test/NameplateTests.cpp test/VisualTests.cpp test/GroundTests.cpp test/CursorTests.cpp test/InteractTests.cpp test/AutoLoginTests.cpp)
target_link_libraries(PatcherTests PRIVATE patcher_engine image_file)
