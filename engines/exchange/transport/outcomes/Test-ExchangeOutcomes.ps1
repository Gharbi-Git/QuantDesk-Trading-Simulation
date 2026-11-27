param([Parameter(Mandatory=$true)][string]$RepoRoot)
$ErrorActionPreference = 'Stop'
$repo = (Resolve-Path -LiteralPath $RepoRoot).Path
$prefix = "$HOME\QuantDeskBuild\grpc-probe-build\vcpkg_installed"
$build = "$HOME\QuantDeskBuild\exchange-outcomes-transport-build"
if (-not $env:VCPKG_ROOT) { $env:VCPKG_ROOT = 'C:\Program Files\Microsoft Visual Studio\18\Community\VC\vcpkg' }
$toolchain = Join-Path $env:VCPKG_ROOT 'scripts\buildsystems\vcpkg.cmake'
foreach ($path in @($toolchain, "$prefix\x64-windows\share\grpc", "$prefix\x64-windows\tools\protobuf\protoc.exe")) {
    if (-not (Test-Path -LiteralPath $path)) { throw "Missing existing dependency: $path. Do not reinstall automatically." }
}
foreach ($command in @('cmake','ctest','dotnet')) { Get-Command $command -ErrorAction Stop | Out-Null }
cmake -S $PSScriptRoot -B $build -A x64 "-DQD_REPO_ROOT=$repo" "-DCMAKE_TOOLCHAIN_FILE=$toolchain" "-DVCPKG_INSTALLED_DIR=$prefix" -DVCPKG_MANIFEST_MODE=ON -DVCPKG_MANIFEST_INSTALL=OFF -DVCPKG_TARGET_TRIPLET=x64-windows -DVCPKG_HOST_TRIPLET=x64-windows
if ($LASTEXITCODE -ne 0) { throw 'Configuration failed.' }
cmake --build $build --config Debug --target qd_exchange_outcomes qd_outcomes_adapter_tests
if ($LASTEXITCODE -ne 0) { throw 'Compilation failed.' }
ctest --test-dir $build -C Debug --output-on-failure
if ($LASTEXITCODE -ne 0) { throw 'Native adapter test failed.' }
dotnet run --project "$PSScriptRoot\smoke\QuantDesk.Exchange.OutcomeSmoke.csproj" --configuration Debug -- "$build\Debug\qd_exchange_outcomes.exe"
if ($LASTEXITCODE -ne 0) { throw 'V2 cross-language test failed.' }
