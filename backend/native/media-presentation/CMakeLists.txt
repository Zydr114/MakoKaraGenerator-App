cmake_minimum_required(VERSION 3.24)
project(mkg_presentation C)
set(CMAKE_C_STANDARD 17)
set(CMAKE_C_STANDARD_REQUIRED ON)
set(MAKO_PRESENTATION_LINUX_DEVKIT_DIR "" CACHE PATH "Optional pinned LGPL FFmpeg 9.0.2 Linux x64 devkit directory")
if(WIN32)
  if(NOT MINGW OR NOT CMAKE_C_COMPILER_ID STREQUAL "Clang" OR NOT CMAKE_SIZEOF_VOID_P EQUAL 8)
    message(FATAL_ERROR "Use a native Windows x64 llvm-mingw toolchain (see README.md).")
  endif()
  include(windows_devkit.cmake)
elseif(NOT "${MAKO_PRESENTATION_LINUX_DEVKIT_DIR}" STREQUAL "")
  include(linux_devkit.cmake)
else()
  find_package(PkgConfig REQUIRED)
  pkg_check_modules(AV REQUIRED IMPORTED_TARGET libavformat>=63 libavcodec>=63 libavutil>=61)
endif()

function(add_presentation_executable name source)
  add_executable(${name} ${source})
  target_compile_options(${name} PRIVATE -Wall -Wextra -fstack-protector-strong)
  if(WIN32)
    target_compile_definitions(${name} PRIVATE WIN32_LEAN_AND_MEAN NOMINMAX _WIN32_WINNT=0x0A00)
    target_include_directories(${name} SYSTEM PRIVATE "${ffmpeg_devkit}/include")
    target_link_options(${name} PRIVATE -municode -static -Wl,--dependent-load-flag=0x800)
    set_target_properties(${name} PROPERTIES RUNTIME_OUTPUT_DIRECTORY "${windows_tool_dir}/$<0:>")
  elseif(NOT "${MAKO_PRESENTATION_LINUX_DEVKIT_DIR}" STREQUAL "")
    target_include_directories(${name} SYSTEM PRIVATE "${ffmpeg_linux_devkit}/include")
    target_link_libraries(${name} PRIVATE
      "${linux_tool_dir}/libavformat.so.63"
      "${linux_tool_dir}/libavcodec.so.63"
      "${linux_tool_dir}/libavutil.so.61"
      ${CMAKE_DL_LIBS}
    )
    target_link_options(${name} PRIVATE -Wl,--disable-new-dtags)
    set_target_properties(${name} PROPERTIES
      RUNTIME_OUTPUT_DIRECTORY "${linux_tool_dir}/$<0:>"
      BUILD_RPATH "\$ORIGIN"
      INSTALL_RPATH "\$ORIGIN"
      BUILD_WITH_INSTALL_RPATH TRUE
    )
  else()
    target_link_libraries(${name} PRIVATE PkgConfig::AV ${CMAKE_DL_LIBS})
  endif()
endfunction()

add_presentation_executable(mkg-presentation mkg_presentation.c)
# Root must build against a pinned controlled bundle and publish its complete manifest.

include(CTest)
if(BUILD_TESTING)
  add_presentation_executable(mkg-presentation-side-data-tests tests/mkg_presentation_side_data_tests.c)
  add_test(NAME mkg-presentation-side-data-tests COMMAND mkg-presentation-side-data-tests)
  if(WIN32)
    add_presentation_executable(mkg-presentation-windows-io-tests tests/mkg_presentation_windows_io_tests.c)
    add_test(NAME mkg-presentation-windows-io-tests COMMAND mkg-presentation-windows-io-tests)
    find_package(Python3 3.11 REQUIRED COMPONENTS Interpreter)
    add_test(NAME mkg-presentation-windows-wire-tests COMMAND "${Python3_EXECUTABLE}"
      "${CMAKE_CURRENT_SOURCE_DIR}/tests/windows_wire_tests.py" "$<TARGET_FILE:mkg-presentation>")
    set_tests_properties(mkg-presentation-windows-wire-tests PROPERTIES TIMEOUT 45)
  else()
    add_presentation_executable(mkg-presentation-closure-tests tests/mkg_presentation_closure_tests.c)
    add_test(NAME mkg-presentation-closure-tests COMMAND mkg-presentation-closure-tests)
  endif()
endif()
