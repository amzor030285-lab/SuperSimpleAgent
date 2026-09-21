@echo off
setlocal EnableExtensions

set ROOT=%~dp0

echo === [1/3] Product build + staging ===
call "%ROOT%build.bat"
if errorlevel 1 (
  echo.
  echo RESULT: BUILD FAILED - stopping before tests.
  exit /b 1
)

echo.
echo === [2/3] Test projects build + packaging into Tests-Build ===
call "%ROOT%build-tests.bat"
if errorlevel 1 (
  echo.
  echo RESULT: TEST BUILD FAILED - stopping before tests.
  exit /b 1
)

echo.
echo === [3/3] Running test suites ===
call "%ROOT%run-tests.bat" %~1
if errorlevel 1 (
  echo.
  echo RESULT: TESTS FAILED - see Tests\logs\ for details.
  exit /b 1
)

echo.
echo RESULT: ALL GREEN - build + tests OK.
exit /b 0
