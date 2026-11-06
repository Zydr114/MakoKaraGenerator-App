cmake_minimum_required(VERSION 3.20)
project(mkg_media_envelope LANGUAGES C)

if(NOT CMAKE_SYSTEM_NAME STREQUAL "Linux" OR NOT CMAKE_SYSTEM_PROCESSOR MATCHES "^(x86_64|amd64|AMD64)$")
  message(FATAL_ERROR "The media envelope ABI currently supports only Linux x64")
endif()
find_package(PkgConfig REQUIRED)
pkg_check_modules(SYSTEMD REQUIRED IMPORTED_TARGET libsystemd)
find_package(Threads REQUIRED)

add_library(mkg_media_envelope SHARED mkg-envelope-bridge.c)
target_compile_features(mkg_media_envelope PRIVATE c_std_11)
target_compile_options(mkg_media_envelope PRIVATE -Wall -Wextra -Werror)
target_link_libraries(mkg_media_envelope PRIVATE PkgConfig::SYSTEMD Threads::Threads)

add_executable(mkg-media-exec-gate mkg-media-exec-gate.c)
target_compile_features(mkg-media-exec-gate PRIVATE c_std_11)
target_compile_options(mkg-media-exec-gate PRIVATE -Wall -Wextra -Werror)

# Both build output and installed output are internal immutable tool inputs.
# Hash these bytes AND their resolved shared-library closure into MediaToolIdentity.
install(TARGETS mkg_media_envelope mkg-media-exec-gate
  LIBRARY DESTINATION .
  RUNTIME DESTINATION .)

include(CTest)
if(BUILD_TESTING)
  add_executable(mkg-envelope-exit-tests tests/mkg-envelope-exit-tests.c)
  target_compile_features(mkg-envelope-exit-tests PRIVATE c_std_11)
  target_compile_options(mkg-envelope-exit-tests PRIVATE -Wall -Wextra -Werror)
  target_link_libraries(mkg-envelope-exit-tests PRIVATE PkgConfig::SYSTEMD Threads::Threads)
  add_test(NAME mkg-envelope-exit-proc-tests COMMAND mkg-envelope-exit-tests proc)
  add_test(NAME mkg-envelope-exit-kill-tests COMMAND mkg-envelope-exit-tests kill)
endif()

if(BUILD_TESTING)
  add_executable(mkg-envelope-snapshot-tests tests/mkg-envelope-snapshot-tests.c)
  target_compile_features(mkg-envelope-snapshot-tests PRIVATE c_std_11)
  target_compile_options(mkg-envelope-snapshot-tests PRIVATE -Wall -Wextra -Werror)
  target_link_libraries(mkg-envelope-snapshot-tests PRIVATE PkgConfig::SYSTEMD Threads::Threads)
  add_test(NAME mkg-envelope-snapshot-tests COMMAND mkg-envelope-snapshot-tests)
endif()

if(BUILD_TESTING)
  add_executable(mkg-envelope-oom-history-tests tests/mkg-envelope-oom-history-tests.c)
  target_compile_features(mkg-envelope-oom-history-tests PRIVATE c_std_11)
  target_compile_options(mkg-envelope-oom-history-tests PRIVATE -Wall -Wextra -Werror)
  target_link_libraries(mkg-envelope-oom-history-tests PRIVATE PkgConfig::SYSTEMD Threads::Threads)
  add_test(NAME mkg-envelope-oom-history-tests COMMAND mkg-envelope-oom-history-tests)
endif()

if(BUILD_TESTING)
  add_executable(mkg-envelope-observer-tests tests/mkg-envelope-observer-tests.c)
  target_compile_features(mkg-envelope-observer-tests PRIVATE c_std_11)
  target_compile_options(mkg-envelope-observer-tests PRIVATE -Wall -Wextra -Werror)
  target_link_libraries(mkg-envelope-observer-tests PRIVATE PkgConfig::SYSTEMD Threads::Threads)
  add_test(NAME mkg-envelope-observer-tests COMMAND mkg-envelope-observer-tests)
endif()
