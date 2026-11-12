@echo off
rem Builds delirium_anim.dll (64-bit) for the Unity editor: GTA IV's clips (Hero, the get-ups), read at run time
rem from your own GTA IV. From D:\delirium repo\CS-Delirium\anim (Rust) against the Delirium source in
rem D:\delirium repo\delirium-physics-0.7.0, with the build-only Rust in D:\NDBuild\rust (installed there if it's
rem missing). Output: Assets\Plugins\x86_64\delirium_anim.dll. Close Unity first if it has the old one loaded.
setlocal EnableExtensions
set "HERE=%~dp0"
set "SRC=D:\delirium repo\delirium-physics-0.7.0"
set "ANIM=D:\delirium repo\CS-Delirium\anim"
if not defined ND_WORK set "ND_WORK=D:\NDBuild"
set "LOG=%HERE%build-anim-log.txt"
echo delirium_anim x64 build %DATE% %TIME% > "%LOG%"
if not exist "%SRC%\crates\delirium-data\Cargo.toml" (echo Delirium source not found in %SRC%& goto fail)
if not exist "%ANIM%\Cargo.toml" (echo delirium_anim source not found in %ANIM%& goto fail)
rem the MSVC linker: any Visual Studio (2022, 2026, or the Build Tools) with "Desktop development with C++"
set "VSWHERE=%ProgramFiles(x86)%\Microsoft Visual Studio\Installer\vswhere.exe"
if not exist "%VSWHERE%" (echo No Visual Studio found ^(vswhere.exe is missing^).& goto novs)
set "VSDIR="
for /f "usebackq tokens=*" %%i in (`"%VSWHERE%" -latest -all -prerelease -products * -requires Microsoft.VisualStudio.Component.VC.Tools.x86.x64 -property installationPath`) do set "VSDIR=%%i"
echo vswhere: "%VSDIR%" >> "%LOG%"
"%VSWHERE%" -all -prerelease -products * -property displayName >> "%LOG%" 2>&1
if not defined VSDIR (echo Your Visual Studio has no C++ tools.& goto novs)
if not exist "%VSDIR%\VC\Auxiliary\Build\vcvars64.bat" (echo vcvars64.bat is missing in %VSDIR%& goto novs)
call "%VSDIR%\VC\Auxiliary\Build\vcvars64.bat" >> "%LOG%" 2>&1
where link.exe >> "%LOG%" 2>&1 || (echo Visual Studio is there, but its linker ^(link.exe^) isn't.& goto novs)
set "RUSTUP_HOME=%ND_WORK%\rust\rustup"
set "CARGO_HOME=%ND_WORK%\rust\cargo"
set "PATH=%CARGO_HOME%\bin;%PATH%"
set "RUSTUP_TOOLCHAIN="
if not exist "%CARGO_HOME%\bin\rustc.exe" (
  echo Installing a build-only Rust ^(MSVC^) into %ND_WORK%\rust ...
  if not exist "%ND_WORK%\rust" mkdir "%ND_WORK%\rust"
  curl -sSfL -o "%ND_WORK%\rust\rustup-init.exe" "https://static.rust-lang.org/rustup/dist/x86_64-pc-windows-msvc/rustup-init.exe" || (echo Rust download failed& goto fail)
  "%ND_WORK%\rust\rustup-init.exe" -y --no-modify-path --profile minimal --default-toolchain stable-x86_64-pc-windows-msvc >> "%LOG%" 2>&1 || (echo Rust install failed& goto fail)
)
"%CARGO_HOME%\bin\rustup.exe" target add x86_64-pc-windows-msvc >> "%LOG%" 2>&1 || (echo rustup target add failed: see build-anim-log.txt& goto fail)
if not exist "%ANIM%\Cargo.lock" copy /y "%SRC%\Cargo.lock" "%ANIM%\Cargo.lock" >nul
echo Building delirium_anim.dll for 64-bit...
"%CARGO_HOME%\bin\cargo.exe" build --release --manifest-path "%ANIM%\Cargo.toml" --target x86_64-pc-windows-msvc --target-dir "%ND_WORK%\delirium-x64" >> "%LOG%" 2>&1
if errorlevel 1 (echo cargo build failed: see build-anim-log.txt& goto fail)
if not exist "%HERE%Assets\Plugins\x86_64" mkdir "%HERE%Assets\Plugins\x86_64"
copy /y "%ND_WORK%\delirium-x64\x86_64-pc-windows-msvc\release\delirium_anim.dll" "%HERE%Assets\Plugins\x86_64\delirium_anim.dll" >nul || (echo copy failed ^(is Unity open with the old one loaded? close it and run again^)& goto fail)
echo OK: Assets\Plugins\x86_64\delirium_anim.dll
echo OK >> "%LOG%"
if /i not "%~1"=="nopause" pause
exit /b 0
:novs
echo.
echo Open "Visual Studio Installer", press Modify on your Visual Studio ^(2026 is fine^), tick
echo "Desktop development with C++", install it, then run this again.
:fail
echo FAILED >> "%LOG%"
echo FAILED - send build-anim-log.txt
if /i not "%~1"=="nopause" pause
exit /b 1
