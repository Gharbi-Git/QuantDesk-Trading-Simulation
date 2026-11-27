param([Parameter(Mandatory=$true)][string]$RepoRoot)
$ErrorActionPreference = 'Stop'
$repo = (Resolve-Path -LiteralPath $RepoRoot).Path
$prefix = "$HOME\QuantDeskBuild\grpc-probe-build\vcpkg_installed"
$build = "$HOME\QuantDeskBuild\exchange-trading-repo-build"
if (-not $env:VCPKG_ROOT) { throw 'VCPKG_ROOT is not set. Use your previous VS developer terminal.' }
$toolchain = Join-Path $env:VCPKG_ROOT 'scripts\buildsystems\vcpkg.cmake'
foreach ($path in @($toolchain, "$prefix\x64-windows\share\grpc", "$prefix\x64-windows\tools\protobuf\protoc.exe", "$repo\engines\exchange\CMakeLists.txt")) {
    if (-not (Test-Path -LiteralPath $path)) { throw "Missing: $path" }
}
cmake -S $PSScriptRoot -B $build -A x64 `
    "-DQD_REPO_ROOT=$repo" `
    "-DCMAKE_TOOLCHAIN_FILE=$toolchain" `
    "-DVCPKG_INSTALLED_DIR=$prefix" `
    -DVCPKG_MANIFEST_MODE=ON `
    -DVCPKG_MANIFEST_INSTALL=OFF `
    -DVCPKG_TARGET_TRIPLET=x64-windows `
    -DVCPKG_HOST_TRIPLET=x64-windows
if ($LASTEXITCODE -ne 0) { throw 'Configuration failed. Do not reinstall dependencies.' }
cmake --build $build --config Debug --target qd_exchange_trading qd_exchange_adapter_tests
if ($LASTEXITCODE -ne 0) { throw 'Compilation failed.' }
ctest --test-dir $build -C Debug -R '^exchange_(trading_loopback|adapter)$' --output-on-failure
if ($LASTEXITCODE -ne 0) { throw 'Test failed.' }
