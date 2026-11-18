#requires -Version 5.1
<#
.SYNOPSIS
  Initializes buildable QuantDesk projects inside the ALREADY COMMITTED monorepo.
.DESCRIPTION
  Non-destructive, rerunnable project bootstrap for the 15-Nov-2026 roadmap:
    - .NET 10: one ASP.NET Core host, Shared, Modules, three xUnit test projects
    - React/TypeScript/Vite: one application, preserving existing feature folders
    - C++20/CMake: quant-core static library, backtest and exchange executables
    - Rust: Tokio runner Cargo package and a genuine unit test
    - Python: package metadata, starter SDK, standard-library unit test
    - Docker Compose: PostgreSQL + Keycloak with separate databases/users,
      optional API/web containers, multi-stage Dockerfiles and local secrets
    - PowerShell build/check commands, initialization guide
  NO module business logic, financial features, auth integration, gRPC contracts,
  database migrations, market provider, or sandbox are claimed complete.
  NEVER removes existing source folders, recreates a Git repo, commits, or pushes.
  Only known, untouched scaffold CMake placeholders are replaced.
.EXAMPLE
  & 'C:\Initialize-QuantDesk-Projects.ps1' -RootPath 'C:\QuantDesk-Trading-Simulation'
.EXAMPLE
  & 'C:\Initialize-QuantDesk-Projects.ps1' -RootPath 'C:\QuantDesk-Trading-Simulation' -StartInfrastructure
.EXAMPLE
  & 'C:\Initialize-QuantDesk-Projects.ps1' -SkipPackageInstall -SkipBuild
#>
[CmdletBinding()]
param(
    [string]$RootPath = 'C:\QuantDesk-Trading-Simulation',
    [switch]$SkipPackageInstall,
    [switch]$SkipBuild,
    [switch]$StartInfrastructure
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$script:Root = [System.IO.Path]::GetFullPath($RootPath).TrimEnd([char[]]@('\', '/'))
$script:Created = New-Object 'System.Collections.Generic.List[string]'
$script:Kept = New-Object 'System.Collections.Generic.List[string]'
$script:Warnings = New-Object 'System.Collections.Generic.List[string]'
$script:Encoding = New-Object System.Text.UTF8Encoding($false)

function FullPath([string]$relative) { Join-Path $script:Root ($relative.Replace('/', '\')) }
function Ensure-Directory([string]$relative) {
    $path = FullPath $relative
    if (-not (Test-Path -LiteralPath $path -PathType Container)) {
        [void][System.IO.Directory]::CreateDirectory($path)
    }
    return $path
}
function Save-File {
    param([string]$Relative, [AllowEmptyString()][string]$Content)
    $path = FullPath $Relative
    if (Test-Path -LiteralPath $path -PathType Leaf) {
        [void]$script:Kept.Add($Relative)
        return $false
    }
    $dir = Split-Path -Parent $path
    if (-not (Test-Path -LiteralPath $dir)) { [void][System.IO.Directory]::CreateDirectory($dir) }
    [System.IO.File]::WriteAllText($path, $Content.Replace("`r`n", "`n"), $script:Encoding)
    [void]$script:Created.Add($Relative)
    return $true
}
function Replace-ScaffoldCMake {
    param([string]$Relative, [string]$Content)
    $path = FullPath $Relative
    if (-not (Test-Path -LiteralPath $path)) { [void](Save-File $Relative $Content); return }
    $old = [System.IO.File]::ReadAllText($path)
    if ($old.Replace("`r`n", "`n").Trim() -eq $Content.Replace("`r`n", "`n").Trim()) {
        [void]$script:Kept.Add($Relative)
    } elseif ($old.Contains('# C++20 target will be defined in its implementation PR.')) {
        [System.IO.File]::WriteAllText($path, $Content.Replace("`r`n", "`n"), $script:Encoding)
        [void]$script:Created.Add("$Relative (replaced known scaffold placeholder)")
    } else {
        [void]$script:Warnings.Add("Keeping customized $Relative; verify it declares the intended target.")
    }
}
function Run-Command {
    param([string]$Exe, [string[]]$Arguments, [string]$Directory)
    $where = if ([string]::IsNullOrWhiteSpace($Directory)) { $script:Root } else { $Directory }
    Write-Host ("`n> " + $Exe + ' ' + ($Arguments -join ' ')) -ForegroundColor DarkCyan
    Push-Location -LiteralPath $where
    try {
        & $Exe @Arguments
        if ($LASTEXITCODE -ne 0) { throw "Command failed (exit $LASTEXITCODE): $Exe $($Arguments -join ' ')" }
    } finally { Pop-Location }
}
function Need-Command([string]$name) {
    if (-not (Get-Command $name -ErrorAction SilentlyContinue)) {
        throw "Required program '$name' was not found in PATH. Open a fresh terminal and verify the toolchain."
    }
}
function New-Secret {
    $data = New-Object byte[] 24
    $rng = [System.Security.Cryptography.RandomNumberGenerator]::Create()
    try { $rng.GetBytes($data) } finally { $rng.Dispose() }
    return ([BitConverter]::ToString($data)).Replace('-', '').ToLowerInvariant()
}

# Preconditions: work IN the existing repository; never create another repository.
if (-not (Test-Path -LiteralPath (FullPath '.git'))) {
    throw "This must run in your ALREADY CLONED QuantDesk Git repository. Not found: $(FullPath '.git')"
}
$gitRoot = (& git -C $script:Root rev-parse --show-toplevel 2>$null)
if ($LASTEXITCODE -ne 0) { throw 'Git cannot read the repository. Open a Git-enabled shell.' }
$actual = [System.IO.Path]::GetFullPath(($gitRoot -join '').Trim()).TrimEnd([char[]]@('\', '/'))
if (-not [string]::Equals($actual, $script:Root, [StringComparison]::OrdinalIgnoreCase)) {
    throw "The Git root ($actual) differs from the requested project root ($($script:Root)). Aborting."
}
foreach ($required in @('apps/api/src/QuantDesk.Api','apps/api/src/QuantDesk.Shared',
                       'apps/api/src/QuantDesk.Modules','engines/quant-core',
                       'engines/backtest','engines/exchange','services/strategy-runner',
                       'apps/web/src/features','infra/compose')) {
    if (-not (Test-Path -LiteralPath (FullPath $required))) {
        throw "Existing scaffold is missing $required. Run the folder scaffold first, or verify RootPath."
    }
}
Need-Command 'dotnet'
Need-Command 'cmake'
Need-Command 'cargo'
Need-Command 'git'
if (-not $SkipPackageInstall) { Need-Command 'npm.cmd'; Need-Command 'node' }
Write-Host "`nQuantDesk PROJECT initialization: $($script:Root)" -ForegroundColor Cyan
Run-Command 'dotnet' @('--version') $script:Root
Run-Command 'cargo' @('--version') $script:Root
if (-not $SkipPackageInstall) {
    $nodeVersion = (& node --version).Trim()
    if ($LASTEXITCODE -ne 0 -or $nodeVersion -notmatch '^v26\.') {
        throw "Node.js 26.x is required by the current repository .node-version. Found '$nodeVersion'. Run 'fnm use 26' and retry."
    }
    Write-Host "Node.js $nodeVersion detected." -ForegroundColor Green
}

# -----------------------------------------------------------------------------
# 1. ASP.NET Core 10 + Onion module assembly
# -----------------------------------------------------------------------------
Write-Host "`n[1/7] .NET solution and three production assemblies" -ForegroundColor Cyan
$apiDir = FullPath 'apps/api'
$apiProjects = @(
    @{ Template='web'; Name='QuantDesk.Api' },
    @{ Template='classlib'; Name='QuantDesk.Shared' },
    @{ Template='classlib'; Name='QuantDesk.Modules' }
)
$apiWasNew = $false
foreach ($p in $apiProjects) {
    $destination = FullPath ("apps/api/src/" + $p.Name)
    $projectFile = Join-Path $destination ($p.Name + '.csproj')
    if (-not (Test-Path $projectFile)) {
        Run-Command 'dotnet' @('new', $p.Template, '--name', $p.Name,
                    '--framework','net10.0','--output',$destination,'--no-restore') $script:Root
        [void]$script:Created.Add("apps/api/src/$($p.Name)/$($p.Name).csproj")
        if ($p.Name -eq 'QuantDesk.Api') { $apiWasNew = $true }
    } else { [void]$script:Kept.Add("apps/api/src/$($p.Name)/$($p.Name).csproj") }
}
$solution = FullPath 'apps/api/QuantDesk.sln'
if (-not (Test-Path $solution)) {
    if (Test-Path (FullPath 'apps/api/QuantDesk.slnx')) {
        throw 'QuantDesk.slnx already exists. Select one solution format and migrate explicitly instead of creating two.'
    }
    # .NET 10 defaults to .slnx; the roadmap scaffold specifies a classic .sln.
    Run-Command 'dotnet' @('new','sln','--name','QuantDesk','--format','sln','--output',$apiDir) $script:Root
}
$sharedCsproj = FullPath 'apps/api/src/QuantDesk.Shared/QuantDesk.Shared.csproj'
$modulesCsproj = FullPath 'apps/api/src/QuantDesk.Modules/QuantDesk.Modules.csproj'
$hostCsproj = FullPath 'apps/api/src/QuantDesk.Api/QuantDesk.Api.csproj'
function Ensure-ProjectReference([string]$from, [string]$to, [string]$referenceName) {
    $xmlText = [System.IO.File]::ReadAllText($from)
    if (-not $xmlText.Contains($referenceName + '.csproj')) {
        Run-Command 'dotnet' @('add',$from,'reference',$to) $script:Root
    }
}
Ensure-ProjectReference $modulesCsproj $sharedCsproj 'QuantDesk.Shared'
Ensure-ProjectReference $hostCsproj $modulesCsproj 'QuantDesk.Modules'
Ensure-ProjectReference $hostCsproj $sharedCsproj 'QuantDesk.Shared'

# Replace only the untouched dotnet-new minimal API Program.cs (not user code).
$program = FullPath 'apps/api/src/QuantDesk.Api/Program.cs'
$programContent = @'
using QuantDesk.Modules;

var builder = WebApplication.CreateBuilder(args);
builder.Services.AddProblemDetails();

var app = builder.Build();
app.UseExceptionHandler();

// Bootstrap ONLY. Authentication, BFF, persistence, SignalR and real modules are QD implementation tasks.
app.MapGet("/health", () => Results.Ok(new { status = "healthy", application = "QuantDesk.Api" }));
app.MapGet("/api/meta/modules", () => Results.Ok(ModuleCatalog.All));

app.Run();

public partial class Program { }
'@
if ($apiWasNew) {
    [System.IO.File]::WriteAllText($program, $programContent.Replace("`r`n", "`n"), $script:Encoding)
} elseif ((Test-Path $program) -and (([System.IO.File]::ReadAllText($program)).Contains('app.MapGet("/", () => "Hello World!")'))) {
    [System.IO.File]::WriteAllText($program, $programContent.Replace("`r`n", "`n"), $script:Encoding)
} else {
    [void]$script:Warnings.Add('Kept existing API Program.cs. Confirm that /health and /api/meta/modules exist if desired.')
}

[void](Save-File 'apps/api/src/QuantDesk.Modules/ModuleCatalog.cs' @'
namespace QuantDesk.Modules;

// Documentation/bootstrapping metadata. These descriptors do NOT mean modules are implemented.
public sealed record ModuleDescriptor(string Code, string Name, string Owner);
public static class ModuleCatalog
{
    public static IReadOnlyList<ModuleDescriptor> All { get; } = new[]
    {
        new ModuleDescriptor("M01", "MarketData", "D1"),
        new ModuleDescriptor("M02", "Indicators", "D1"),
        new ModuleDescriptor("M03", "StrategyRegistry", "D1"),
        new ModuleDescriptor("M04", "Backtesting", "D1"),
        new ModuleDescriptor("M05", "PaperTrading", "D1"),
        new ModuleDescriptor("M06", "Matching", "D2"),
        new ModuleDescriptor("M07", "Portfolio", "D2"),
        new ModuleDescriptor("M08", "Risk", "D1"),
        new ModuleDescriptor("M09", "Analytics", "D1"),
        new ModuleDescriptor("M11", "Options", "D1"),
        new ModuleDescriptor("M12", "MarketSimulation", "D2"),
        new ModuleDescriptor("M13", "Arena", "D2"),
        new ModuleDescriptor("M14", "Administration", "D2")
    };
}
'@)
[void](Save-File 'apps/api/src/QuantDesk.Shared/BootstrapInfo.cs' @'
namespace QuantDesk.Shared;

public static class BootstrapInfo
{
    public const string ProductName = "QuantDesk";
}
'@)

# Create proper test projects, then convert generated package versions into the
# existing centralized Directory.Packages.props so restore works with CPM enabled.
Write-Host "`n[2/7] .NET test projects and central NuGet management" -ForegroundColor Cyan
$testNames = @('QuantDesk.UnitTests','QuantDesk.IntegrationTests','QuantDesk.ArchitectureTests')
foreach ($name in $testNames) {
    $location = FullPath ("apps/api/tests/" + $name)
    $projectFile = Join-Path $location ($name + '.csproj')
    if (-not (Test-Path $projectFile)) {
        Run-Command 'dotnet' @('new','xunit','--name',$name,'--framework','net10.0',
                    '--output',$location,'--no-restore') $script:Root
        [void]$script:Created.Add("apps/api/tests/$name/$name.csproj")
    }
    Ensure-ProjectReference $projectFile $modulesCsproj 'QuantDesk.Modules'
    if ($name -eq 'QuantDesk.IntegrationTests') {
        Ensure-ProjectReference $projectFile $hostCsproj 'QuantDesk.Api'
    }
}
$centralPackages = FullPath 'apps/api/Directory.Packages.props'
if (-not (Test-Path $centralPackages)) {
    throw 'Missing scaffold apps/api/Directory.Packages.props for centralized NuGet management.'
}
[xml]$centralXml = Get-Content -LiteralPath $centralPackages -Raw
$existingVersions = @{}
foreach ($entry in @($centralXml.SelectNodes('//PackageVersion'))) {
    if ($null -ne $entry -and $entry.GetAttribute('Include')) {
        $existingVersions[$entry.GetAttribute('Include')] = $entry.GetAttribute('Version')
    }
}
$requiredCentral = @{}
foreach ($name in $testNames) {
    $projectFile = FullPath ("apps/api/tests/$name/$name.csproj")
    [xml]$doc = Get-Content -LiteralPath $projectFile -Raw
    $hasChanges = $false
    foreach ($reference in @($doc.SelectNodes('//PackageReference'))) {
        if ($null -eq $reference) { continue }
        $pkg = $reference.GetAttribute('Include')
        $version = $reference.GetAttribute('Version')
        if (-not [string]::IsNullOrWhiteSpace($version)) {
            if ($requiredCentral.ContainsKey($pkg) -and $requiredCentral[$pkg] -ne $version) {
                throw "Conflicting test template versions for $pkg. Resolve the package version explicitly."
            }
            $requiredCentral[$pkg] = $version
            [void]$reference.RemoveAttribute('Version')
            $hasChanges = $true
        }
    }
    if ($hasChanges) { $doc.Save($projectFile) }
}
if ($requiredCentral.Count -gt 0) {
    $group = $centralXml.CreateElement('ItemGroup')
    foreach ($pkg in ($requiredCentral.Keys | Sort-Object)) {
        $version = $requiredCentral[$pkg]
        if ($existingVersions.ContainsKey($pkg)) {
            if ($existingVersions[$pkg] -ne $version) {
                throw "Central NuGet version mismatch for $pkg ($($existingVersions[$pkg]) vs $version). Resolve manually."
            }
            continue
        }
        $element = $centralXml.CreateElement('PackageVersion')
        $element.SetAttribute('Include', $pkg)
        $element.SetAttribute('Version', $version)
        [void]$group.AppendChild($element)
    }
    if ($group.ChildNodes.Count -gt 0) { [void]$centralXml.Project.AppendChild($group) }
    $centralXml.Save($centralPackages)
}
[void](Save-File 'apps/api/tests/QuantDesk.UnitTests/ModuleCatalogTests.cs' @'
using QuantDesk.Modules;
using Xunit;

namespace QuantDesk.UnitTests;

public sealed class ModuleCatalogTests
{
    [Fact]
    public void InitialRoadmapContainsThirteenFunctionalModules()
    {
        Assert.Equal(13, ModuleCatalog.All.Count);
        Assert.Equal(8, ModuleCatalog.All.Count(m => m.Owner == "D1"));
        Assert.Equal(5, ModuleCatalog.All.Count(m => m.Owner == "D2"));
    }
}
'@)
[void](Save-File 'apps/api/tests/QuantDesk.ArchitectureTests/ModuleDescriptorTests.cs' @'
using QuantDesk.Modules;
using Xunit;

namespace QuantDesk.ArchitectureTests;

public sealed class ModuleDescriptorTests
{
    [Fact]
    public void ModuleCodesAreUnique() =>
        Assert.Equal(ModuleCatalog.All.Count, ModuleCatalog.All.Select(m => m.Code).Distinct().Count());
}
'@)
foreach ($name in @('QuantDesk.Api','QuantDesk.Shared','QuantDesk.Modules')) {
    $project = FullPath ("apps/api/src/$name/$name.csproj")
    $already = (& dotnet sln $solution list) -join "`n"
    if ($LASTEXITCODE -ne 0) { throw 'Could not list .NET solution projects.' }
    if (-not $already.Contains("$name.csproj")) { Run-Command 'dotnet' @('sln',$solution,'add',$project) $script:Root }
}
foreach ($name in $testNames) {
    $project = FullPath ("apps/api/tests/$name/$name.csproj")
    $already = (& dotnet sln $solution list) -join "`n"
    if (-not $already.Contains("$name.csproj")) { Run-Command 'dotnet' @('sln',$solution,'add',$project) $script:Root }
}

# -----------------------------------------------------------------------------
# 3. C++20 CMake targets: real compiled smoke binaries, NOT trading logic
# -----------------------------------------------------------------------------
Write-Host "`n[3/7] C++ quant-core / backtest / exchange" -ForegroundColor Cyan
[void](Save-File 'engines/quant-core/include/quantdesk/quant/build_info.hpp' @'
#pragma once
#include <string_view>
namespace quantdesk::quant {
    std::string_view build_component_name() noexcept;
}
'@)
[void](Save-File 'engines/quant-core/src/build_info.cpp' @'
#include "quantdesk/quant/build_info.hpp"
namespace quantdesk::quant {
    std::string_view build_component_name() noexcept { return "quant-core"; }
}
'@)
[void](Save-File 'engines/quant-core/tests/unit/build_info_test.cpp' @'
#include "quantdesk/quant/build_info.hpp"
#include <string_view>
int main() {
    return quantdesk::quant::build_component_name() == std::string_view{"quant-core"} ? 0 : 1;
}
'@)
Replace-ScaffoldCMake 'engines/quant-core/CMakeLists.txt' @'
add_library(quantdesk_quant_core STATIC src/build_info.cpp)
target_include_directories(quantdesk_quant_core PUBLIC ${CMAKE_CURRENT_SOURCE_DIR}/include)
target_compile_features(quantdesk_quant_core PUBLIC cxx_std_20)
add_executable(quantdesk_quant_core_smoke tests/unit/build_info_test.cpp)
target_link_libraries(quantdesk_quant_core_smoke PRIVATE quantdesk_quant_core)
add_test(NAME quant_core_bootstrap COMMAND quantdesk_quant_core_smoke)
'@
[void](Save-File 'engines/backtest/src/main.cpp' @'
#include "quantdesk/quant/build_info.hpp"
#include <iostream>
int main() {
    std::cout << "QuantDesk backtest scaffold (no replay implemented yet), linked to "
              << quantdesk::quant::build_component_name() << '\n';
    return 0;
}
'@)
Replace-ScaffoldCMake 'engines/backtest/CMakeLists.txt' @'
add_executable(quantdesk_backtest src/main.cpp)
target_link_libraries(quantdesk_backtest PRIVATE quantdesk_quant_core)
target_compile_features(quantdesk_backtest PRIVATE cxx_std_20)
add_test(NAME backtest_bootstrap COMMAND quantdesk_backtest)
'@
[void](Save-File 'engines/exchange/src/main.cpp' @'
#include <iostream>
int main() {
    std::cout << "QuantDesk exchange scaffold (no order matching implemented yet)\n";
    return 0;
}
'@)
Replace-ScaffoldCMake 'engines/exchange/CMakeLists.txt' @'
add_executable(quantdesk_exchange src/main.cpp)
target_compile_features(quantdesk_exchange PRIVATE cxx_std_20)
add_test(NAME exchange_bootstrap COMMAND quantdesk_exchange)
'@

# -----------------------------------------------------------------------------
# 4. Rust: cargo manifest + basic Tokio runner; platform logic comes later
# -----------------------------------------------------------------------------
Write-Host "`n[4/7] Rust strategy-runner" -ForegroundColor Cyan
[void](Save-File 'services/strategy-runner/Cargo.toml' @'
[package]
name = "quantdesk-strategy-runner"
version = "0.1.0"
edition = "2024"
publish = false

[dependencies]
tokio = { version = "1", features = ["rt-multi-thread", "macros", "time", "sync"] }
serde = { version = "1", features = ["derive"] }
serde_json = "1"
'@)
[void](Save-File 'services/strategy-runner/src/lib.rs' @'
//! QuantDesk runner bootstrap. No strategy container launching or exchange routing yet.
pub fn runner_component_name() -> &'static str { "quantdesk-strategy-runner" }

#[cfg(test)]
mod tests {
    #[test]
    fn bootstrap_name_is_stable() {
        assert_eq!(super::runner_component_name(), "quantdesk-strategy-runner");
    }
}
'@)
[void](Save-File 'services/strategy-runner/src/main.rs' @'
#[tokio::main]
async fn main() {
    println!("{} scaffold - OCI supervision not implemented",
             quantdesk_strategy_runner::runner_component_name());
}
'@)

# -----------------------------------------------------------------------------
# 5. Python SDK: package baseline, without inventing a strategy protocol
# -----------------------------------------------------------------------------
Write-Host "`n[5/7] Python SDK package" -ForegroundColor Cyan
[void](Save-File 'sdk/python/pyproject.toml' @'
[build-system]
requires = ["setuptools>=75", "wheel"]
build-backend = "setuptools.build_meta"

[project]
name = "quantdesk-strategy-sdk"
version = "0.1.0"
description = "Starter Python SDK for the future versioned QuantDesk strategy protocol"
requires-python = ">=3.11"

[tool.setuptools]
packages = ["quantdesk_sdk"]
'@)
[void](Save-File 'sdk/python/quantdesk_sdk/__init__.py' @'
"""QuantDesk SDK bootstrap; API/protocol definitions require contract approval."""
__version__ = "0.1.0"
'@)
[void](Save-File 'sdk/python/tests/test_bootstrap.py' @'
import unittest
from quantdesk_sdk import __version__

class BootstrapTests(unittest.TestCase):
    def test_version(self):
        self.assertEqual(__version__, "0.1.0")

if __name__ == "__main__":
    unittest.main()
'@)

# -----------------------------------------------------------------------------
# 6. React: merge into EXISTING feature folders; never run create-vite in them
# -----------------------------------------------------------------------------
Write-Host "`n[6/7] React / Vite / TypeScript" -ForegroundColor Cyan
$webDir = FullPath 'apps/web'
[void](Save-File 'apps/web/package.json' @'
{
  "name": "quantdesk-web",
  "private": true,
  "version": "0.1.0",
  "type": "module",
  "scripts": {
    "dev": "vite --host 127.0.0.1",
    "build": "tsc --noEmit && vite build",
    "preview": "vite preview --host 127.0.0.1",
    "test": "vitest run"
  }
}
'@)
[void](Save-File 'apps/web/index.html' @'
<!doctype html>
<html lang="en">
  <head>
    <meta charset="UTF-8" />
    <meta name="viewport" content="width=device-width, initial-scale=1.0" />
    <title>QuantDesk - Development Scaffold</title>
  </head>
  <body>
    <div id="root"></div>
    <script type="module" src="/src/main.tsx"></script>
  </body>
</html>
'@)
[void](Save-File 'apps/web/tsconfig.json' @'
{
  "compilerOptions": {
    "target": "ES2022",
    "useDefineForClassFields": true,
    "lib": ["ES2022", "DOM", "DOM.Iterable"],
    "module": "ESNext",
    "moduleResolution": "Bundler",
    "jsx": "react-jsx",
    "noEmit": true,
    "isolatedModules": true,
    "resolveJsonModule": true,
    "skipLibCheck": true,
    "strict": true
  },
  "include": ["src"]
}
'@)
[void](Save-File 'apps/web/vite.config.ts' @'
import { defineConfig } from 'vite'
import react from '@vitejs/plugin-react'

export default defineConfig({
  plugins: [react()],
  server: {
    port: 5173,
    proxy: {
      '/api': 'http://127.0.0.1:5080',
      '/health': 'http://127.0.0.1:5080',
    },
  },
})
'@)
[void](Save-File 'apps/web/src/main.tsx' @'
import React from 'react'
import ReactDOM from 'react-dom/client'
import { App } from './app/App'
import './app/styles/globals.css'

ReactDOM.createRoot(document.getElementById('root')!).render(
  <React.StrictMode><App /></React.StrictMode>,
)
'@)
[void](Save-File 'apps/web/src/app/App.tsx' @'
import { useEffect, useState } from 'react'

type Module = { code: string; name: string; owner: string }

export function App() {
  const [modules, setModules] = useState<Module[]>([])
  const [status, setStatus] = useState('Connecting to API...')

  useEffect(() => {
    const controller = new AbortController()
    fetch('/api/meta/modules', { signal: controller.signal })
      .then(async (response) => {
        if (!response.ok) throw new Error(`HTTP ${response.status}`)
        return (await response.json()) as Module[]
      })
      .then((items) => { setModules(items); setStatus('Scaffold API connected') })
      .catch((error: unknown) => {
        if (!controller.signal.aborted) setStatus(`API not available: ${String(error)}`)
      })
    return () => controller.abort()
  }, [])

  return <main>
    <h1>QuantDesk</h1>
    <p>Engineering workspace - no trading functions have been implemented.</p>
    <p aria-live="polite">{status}</p>
    <h2>Planned functional modules ({modules.length})</h2>
    <ul>{modules.map((m) => <li key={m.code}>{m.code}: {m.name} ({m.owner})</li>)}</ul>
  </main>
}
'@)
# Vite's type declarations cover CSS side-effect imports under TypeScript 6.
# Without this, TypeScript reports TS2882 even when globals.css exists.
[void](Save-File 'apps/web/src/vite-env.d.ts' @'
/// <reference types="vite/client" />
'@)
[void](Save-File 'apps/web/src/app/styles/globals.css' @'
:root { font-family: system-ui, sans-serif; color: #172136; background: #f6f8fb; }
body { margin: 0; }
main { max-width: 880px; margin: 4rem auto; padding: 1.75rem; background: white; border-radius: 12px; }
h1 { margin-top: 0; }
li { padding: 0.2rem 0; }
'@)
[void](Save-File 'apps/web/src/app/config.ts' @'
export const productName = 'QuantDesk'
'@)
[void](Save-File 'apps/web/src/app/config.test.ts' @'
import { expect, test } from 'vitest'
import { productName } from './config'

test('bootstrapped product name', () => {
  expect(productName).toBe('QuantDesk')
})
'@)

# -----------------------------------------------------------------------------
# 7. Docker: dev-only infra + optional application profile
# -----------------------------------------------------------------------------
Write-Host "`n[7/7] Docker Compose and project operations" -ForegroundColor Cyan
[void](Save-File 'infra/postgres/init/01-create-app-and-keycloak.sh' @'
#!/usr/bin/env bash
set -euo pipefail
: "${QD_APP_DB_PASSWORD:?Missing QD_APP_DB_PASSWORD}"
: "${QD_KC_DB_PASSWORD:?Missing QD_KC_DB_PASSWORD}"

# Runs once on a brand-new PostgreSQL Docker volume; never modifies an existing DB.
psql -v ON_ERROR_STOP=1 --username "$POSTGRES_USER" --dbname "$POSTGRES_DB" \
  -v app_pw="$QD_APP_DB_PASSWORD" -v kc_pw="$QD_KC_DB_PASSWORD" <<'SQL'
SELECT format('CREATE ROLE %I LOGIN PASSWORD %L', 'qd_app', :'app_pw') \gexec
SELECT format('CREATE ROLE %I LOGIN PASSWORD %L', 'qd_keycloak', :'kc_pw') \gexec
SELECT format('CREATE DATABASE %I OWNER %I', 'quantdesk', 'qd_app') \gexec
SELECT format('CREATE DATABASE %I OWNER %I', 'keycloak', 'qd_keycloak') \gexec
SQL
'@)
[void](Save-File 'infra/keycloak/realm-quantdesk.json' @'
{
  "realm": "quantdesk",
  "enabled": true,
  "displayName": "QuantDesk Development",
  "roles": {
    "realm": [
      { "name": "PLAYER" },
      { "name": "ADMIN" }
    ]
  }
}
'@)
[void](Save-File 'infra/compose/.env.example' @'
# TEMPLATE ONLY: NEVER use these placeholder values as passwords.
QD_POSTGRES_ADMIN_PASSWORD=REPLACE_WITH_RANDOM_LOCAL_SECRET
QD_APP_DB_PASSWORD=REPLACE_WITH_RANDOM_LOCAL_SECRET
QD_KC_DB_PASSWORD=REPLACE_WITH_RANDOM_LOCAL_SECRET
QD_KC_ADMIN_USERNAME=admin
QD_KC_ADMIN_PASSWORD=REPLACE_WITH_RANDOM_LOCAL_SECRET
QD_PG_PORT=5433
QD_KC_PORT=8180
'@)
$localComposeEnv = FullPath 'infra/compose/.env'
if (-not (Test-Path -LiteralPath $localComposeEnv)) {
    $envText = @"
# Generated local development secrets; this file is Git-ignored. Do not share it.
QD_POSTGRES_ADMIN_PASSWORD=$(New-Secret)
QD_APP_DB_PASSWORD=$(New-Secret)
QD_KC_DB_PASSWORD=$(New-Secret)
QD_KC_ADMIN_USERNAME=admin
QD_KC_ADMIN_PASSWORD=$(New-Secret)
QD_PG_PORT=5433
QD_KC_PORT=8180
"@
    [void](Save-File 'infra/compose/.env' $envText)
    Write-Host 'Generated private Compose credentials (contents not printed).' -ForegroundColor Green
}
# Confirm that the existing scaffold .gitignore protects the generated secrets.
& git -C $script:Root check-ignore --quiet 'infra/compose/.env'
if ($LASTEXITCODE -ne 0) {
    throw 'SAFETY STOP: infra/compose/.env is not Git-ignored. Add a .gitignore rule BEFORE running git add.'
}
[void](Save-File 'infra/compose/compose.yaml' @'
name: quantdesk

services:
  postgres:
    image: postgres:18.7
    restart: unless-stopped
    environment:
      POSTGRES_USER: postgres
      POSTGRES_PASSWORD: ${QD_POSTGRES_ADMIN_PASSWORD:?Set local secret in .env}
      POSTGRES_DB: postgres
      QD_APP_DB_PASSWORD: ${QD_APP_DB_PASSWORD:?Set local secret in .env}
      QD_KC_DB_PASSWORD: ${QD_KC_DB_PASSWORD:?Set local secret in .env}
    ports:
      - "127.0.0.1:${QD_PG_PORT:-5433}:5432"
    volumes:
      - postgres-data:/var/lib/postgresql
      - ../postgres/init:/docker-entrypoint-initdb.d:ro
    healthcheck:
      test: ["CMD-SHELL", "pg_isready -U postgres -d postgres"]
      interval: 5s
      timeout: 5s
      retries: 15
    networks: [internal]

  keycloak:
    image: quay.io/keycloak/keycloak:26.8.2
    command: ["start-dev", "--import-realm"]
    restart: unless-stopped
    environment:
      KC_DB: postgres
      KC_DB_URL_HOST: postgres
      KC_DB_URL_DATABASE: keycloak
      KC_DB_USERNAME: qd_keycloak
      KC_DB_PASSWORD: ${QD_KC_DB_PASSWORD:?Set local secret in .env}
      KC_BOOTSTRAP_ADMIN_USERNAME: ${QD_KC_ADMIN_USERNAME:-admin}
      KC_BOOTSTRAP_ADMIN_PASSWORD: ${QD_KC_ADMIN_PASSWORD:?Set local secret in .env}
    ports:
      - "127.0.0.1:${QD_KC_PORT:-8180}:8080"
    volumes:
      - ../keycloak:/opt/keycloak/data/import:ro
    depends_on:
      postgres:
        condition: service_healthy
    networks: [internal]

  api:
    profiles: ["app"]
    build:
      context: ../..
      dockerfile: infra/docker/api.Dockerfile
    ports:
      - "127.0.0.1:5080:8080"
    environment:
      ASPNETCORE_URLS: http://+:8080
      ConnectionStrings__QuantDesk: "Host=postgres;Port=5432;Database=quantdesk;Username=qd_app;Password=${QD_APP_DB_PASSWORD:?}"
    depends_on:
      postgres:
        condition: service_healthy
    networks: [internal]

  web:
    profiles: ["app"]
    build:
      context: ../..
      dockerfile: infra/docker/web.Dockerfile
    ports:
      - "127.0.0.1:8088:80"
    depends_on: [api]
    networks: [internal]

networks:
  internal: {}

volumes:
  postgres-data: {}
'@)
[void](Save-File 'infra/docker/api.Dockerfile' @'
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /repo
COPY global.json ./
COPY apps/api/ ./apps/api/
RUN dotnet restore apps/api/QuantDesk.sln
RUN dotnet publish apps/api/src/QuantDesk.Api/QuantDesk.Api.csproj -c Release -o /publish --no-restore

FROM mcr.microsoft.com/dotnet/aspnet:10.0
WORKDIR /app
ENV ASPNETCORE_URLS=http://+:8080
EXPOSE 8080
COPY --from=build /publish ./
USER $APP_UID
ENTRYPOINT ["dotnet", "QuantDesk.Api.dll"]
'@)
[void](Save-File 'infra/docker/web.Dockerfile' @'
FROM node:26.13.1-bookworm-slim AS build
WORKDIR /repo/apps/web
COPY apps/web/package*.json ./
RUN npm ci
COPY apps/web/ ./
RUN npm run build

FROM nginx:stable-alpine3.24
COPY infra/docker/nginx.conf /etc/nginx/conf.d/default.conf
COPY --from=build /repo/apps/web/dist/ /usr/share/nginx/html/
EXPOSE 80
'@)
[void](Save-File 'infra/docker/nginx.conf' @'
server {
    listen 80;
    server_name _;
    root /usr/share/nginx/html;
    index index.html;
    location /api/ { proxy_pass http://api:8080; proxy_http_version 1.1; }
    location /health { proxy_pass http://api:8080; }
    location / { try_files $uri $uri/ /index.html; }
}
'@)
[void](Save-File 'infra/docker/engines.Dockerfile' @'
# Linux-only compilation / test verification; no engine production deployment yet.
FROM gcc:14-bookworm
RUN apt-get update && apt-get install -y --no-install-recommends cmake ninja-build \
    && rm -rf /var/lib/apt/lists/*
WORKDIR /repo
COPY engines/ ./engines/
RUN cmake -S engines -B /build -G Ninja -DCMAKE_BUILD_TYPE=Release \
    && cmake --build /build \
    && ctest --test-dir /build --output-on-failure
CMD ["/build/backtest/quantdesk_backtest"]
'@)
[void](Save-File 'infra/docker/strategy-runner.Dockerfile' @'
# MVP bootstrap binary; OCI sandbox supervision is NOT implemented by this image.
FROM rust:1.100.0-bookworm AS build
WORKDIR /repo
COPY rust-toolchain.toml ./
COPY services/strategy-runner/ ./services/strategy-runner/
WORKDIR /repo/services/strategy-runner
RUN cargo build --release --locked

FROM debian:bookworm-slim
COPY --from=build /repo/services/strategy-runner/target/release/quantdesk-strategy-runner /usr/local/bin/quantdesk-strategy-runner
USER 65532:65532
ENTRYPOINT ["/usr/local/bin/quantdesk-strategy-runner"]
'@)
[void](Save-File '.dockerignore' @'
.git
.vs
**/bin
**/obj
**/node_modules
**/dist
**/target
**/out
**/.venv
**/__pycache__
**/TestResults
**/*.user
**/.env
**/.env.*
!**/.env.example
'@)
[void](Save-File 'scripts/build/build-all.ps1' @'
#requires -Version 5.1
$ErrorActionPreference = 'Stop'
$root = (Resolve-Path (Join-Path $PSScriptRoot '../..')).Path
function Go([string]$cmd, [string[]]$parameters, [string]$directory) {
    Push-Location $directory
    try { & $cmd @parameters; if ($LASTEXITCODE -ne 0) { throw "$cmd failed" } }
    finally { Pop-Location }
}
Go 'dotnet' @('build','apps/api/QuantDesk.sln','-c','Debug') $root
Go 'cmake' @('--preset','windows-x64') (Join-Path $root 'engines')
Go 'cmake' @('--build','--preset','windows-x64-debug') (Join-Path $root 'engines')
Go 'cargo' @('build','--locked') (Join-Path $root 'services/strategy-runner')
Go 'npm.cmd' @('run','build') (Join-Path $root 'apps/web')
Write-Host 'QuantDesk bootstrap builds completed.' -ForegroundColor Green
'@)
[void](Save-File 'scripts/test/test-all.ps1' @'
#requires -Version 5.1
$ErrorActionPreference = 'Stop'
$root = (Resolve-Path (Join-Path $PSScriptRoot '../..')).Path
function Go([string]$cmd, [string[]]$parameters, [string]$directory) {
    Push-Location $directory
    try { & $cmd @parameters; if ($LASTEXITCODE -ne 0) { throw "$cmd failed" } }
    finally { Pop-Location }
}
Go 'dotnet' @('test','apps/api/QuantDesk.sln','-c','Debug') $root
Go 'ctest' @('--test-dir','out/build/windows-x64','-C','Debug','--output-on-failure') (Join-Path $root 'engines')
Go 'cargo' @('test','--locked') (Join-Path $root 'services/strategy-runner')
Go 'npm.cmd' @('test') (Join-Path $root 'apps/web')
if (Get-Command py -ErrorAction SilentlyContinue) {
    Go 'py' @('-3','-m','unittest','discover','-s','tests','-v') (Join-Path $root 'sdk/python')
} elseif (Get-Command python -ErrorAction SilentlyContinue) {
    Go 'python' @('-m','unittest','discover','-s','tests','-v') (Join-Path $root 'sdk/python')
} else { Write-Warning 'Python is not installed; skipped Python SDK smoke tests.' }
Write-Host 'QuantDesk bootstrap tests completed.' -ForegroundColor Green
'@)
[void](Save-File 'scripts/build/start-infrastructure.ps1' @'
#requires -Version 5.1
$ErrorActionPreference = 'Stop'
$root = (Resolve-Path (Join-Path $PSScriptRoot '../..')).Path
$envFile = Join-Path $root 'infra/compose/.env'
$compose = Join-Path $root 'infra/compose/compose.yaml'
& docker compose --env-file $envFile -f $compose up -d postgres keycloak
if ($LASTEXITCODE -ne 0) { throw 'Docker infrastructure startup failed.' }
Write-Host 'PostgreSQL: localhost:5433 ; Keycloak: http://localhost:8180' -ForegroundColor Green
'@)
[void](Save-File 'docs/operations/project-initialization.md' @'
# QuantDesk - project initialization (bootstrap only)

The single Git repository contains all 13 functional modules, not 13 repositories.
Run `Initialize-QuantDesk-Projects.ps1` from Windows PowerShell 5.1 or later,
with the existing Git checkout at `C:\QuantDesk-Trading-Simulation`.

## Verification and local run

From the repository root:

```powershell
.\scripts\build\build-all.ps1
.\scripts\test\test-all.ps1
.\scripts\build\start-infrastructure.ps1
cd apps\api
dotnet run --project src\QuantDesk.Api --urls http://localhost:5080
# In another terminal: cd C:\QuantDesk-Trading-Simulation\apps\web; npm run dev
```

- API smoke endpoint: http://localhost:5080/health
- API module-catalog smoke endpoint: http://localhost:5080/api/meta/modules
- Vite frontend: http://localhost:5173
- PostgreSQL: 127.0.0.1:5433 (separate qd_app and qd_keycloak database roles)
- Keycloak development console: http://localhost:8180
- For an entire app/web image build: `docker compose --env-file infra/compose/.env -f infra/compose/compose.yaml --profile app up --build -d`.
- For isolated Linux C++ tests: `docker build -f infra/docker/engines.Dockerfile -t quantdesk-engines-smoke .`.

## Bootstrap is NOT completed features

The scaffold API exposes only health and roadmap-module metadata. The module
folders do not yet have application services, persistence mappings or controllers.
React only displays a development shell. C++/Rust executables are startup smokes.
Keycloak realm/roles are imported, but no OIDC/BFF client or protected endpoints
exist yet. PostgreSQL has isolated development databases/users, but no EF Core
migration or business tables. Protobuf contracts, real-time SignalR, sandbox
hardening, market-data integrations and financial flows are future tasks.

## Secrets and safety

`infra/compose/.env` is generated with cryptographic random values and ignored by
Git. Never commit this file. The dev infrastructure binds DB and Keycloak ports
to 127.0.0.1, but `start-dev` is not suitable for public production exposure.
The bootstrap ASP.NET Core endpoints are unauthenticated; keep the server local.
Do not run `docker compose down -v` unless you intend to DESTROY local DB volumes.

## Coordination

Developer 1 owns M01/M02/M03/M04/M05/M08/M09/M11. Developer 2 owns P00,
M06/M07/M12/M13/M14. F00 and Q15 remain shared. Contracts must be frozen before
wiring financial cross-module behavior. Keep the branch and Git history intact.
'@)

# Refresh ONLY known, exact scaffold-status sentences; preserve all other README content.
function Update-ScaffoldStatus([string]$relative, [string]$oldText, [string]$newText) {
    $path = FullPath $relative
    if (-not (Test-Path -LiteralPath $path)) { return }
    $existing = [System.IO.File]::ReadAllText($path)
    if ($existing.Contains($oldText)) {
        [System.IO.File]::WriteAllText($path, $existing.Replace($oldText,$newText), $script:Encoding)
        [void]$script:Created.Add("$relative (updated exact scaffold-status sentence)")
    }
}
Update-ScaffoldStatus 'apps/api/README.md'   'No .sln/.csproj files are fabricated by this scaffold.'   'The buildable .NET 10 solution and baseline tests are initialized by scripts/setup/Initialize-QuantDesk-Projects.ps1.'
Update-ScaffoldStatus 'apps/web/README.md'   'Vite and package.json will be initialized in the frontend bootstrap PR.'   'The Vite/TypeScript app is initialized; feature pages and BFF authentication are not yet implemented.'
Update-ScaffoldStatus 'services/strategy-runner/README.md'   'Cargo.toml/source files will be created with `cargo init` during implementation.'   'The Cargo/Tokio bootstrap exists; OCI supervision and strategy protocol are not yet implemented.'
Update-ScaffoldStatus 'README.md'   'This commit is the *folder and configuration scaffold only*.'   'The buildable project skeleton is initialized; no trading or financial functionality is implemented.'

# Keep a reproducible copy of this initializer INSIDE the same repository.
if ($PSCommandPath) {
    $destination = FullPath 'scripts/setup/Initialize-QuantDesk-Projects.ps1'
    $source = [System.IO.Path]::GetFullPath($PSCommandPath)
    if (-not (Test-Path $destination) -and -not [string]::Equals($source,$destination,[StringComparison]::OrdinalIgnoreCase)) {
        Copy-Item -LiteralPath $source -Destination $destination
        [void]$script:Created.Add('scripts/setup/Initialize-QuantDesk-Projects.ps1')
    }
}

# -----------------------------------------------------------------------------
# Package installation, test builds and safe Docker validation
# -----------------------------------------------------------------------------
if (-not $SkipPackageInstall) {
    Write-Host "`nInstalling locked dependencies for the first build..." -ForegroundColor Cyan
    Run-Command 'dotnet' @('restore',$solution) $script:Root
    $cargoDir = FullPath 'services/strategy-runner'
    if (Test-Path (Join-Path $cargoDir 'Cargo.lock')) {
        Run-Command 'cargo' @('fetch','--locked') $cargoDir
    } else {
        Run-Command 'cargo' @('generate-lockfile') $cargoDir
        Run-Command 'cargo' @('fetch','--locked') $cargoDir
    }
    # Recover cleanly after an interrupted first npm install without destroying the feature folders.
    $webPkg = Get-Content -LiteralPath (FullPath 'apps/web/package.json') -Raw | ConvertFrom-Json
    $deps = @(); $devDeps = @()
    $dependencies = $null; $devDependencies = $null
    if ($null -ne $webPkg.PSObject.Properties['dependencies']) { $dependencies = $webPkg.dependencies }
    if ($null -ne $webPkg.PSObject.Properties['devDependencies']) { $devDependencies = $webPkg.devDependencies }
    if ($null -eq $dependencies -or $null -eq $dependencies.PSObject.Properties['react']) { $deps += 'react@19' }
    if ($null -eq $dependencies -or $null -eq $dependencies.PSObject.Properties['react-dom']) { $deps += 'react-dom@19' }
    foreach ($pkg in @('vite','typescript','@vitejs/plugin-react','@types/react','@types/react-dom','vitest')) {
        $found = $false
        if ($null -ne $devDependencies) { $found = $null -ne $devDependencies.PSObject.Properties[$pkg] }
        if (-not $found) {
            switch ($pkg) {
                'vite' { $devDeps += 'vite@8' }
                'typescript' { $devDeps += 'typescript@6' }
                '@types/react' { $devDeps += '@types/react@19' }
                '@types/react-dom' { $devDeps += '@types/react-dom@19' }
                default { $devDeps += $pkg }
            }
        }
    }
    if ($deps.Count -gt 0) { Run-Command 'npm.cmd' (@('install','--save-exact') + $deps) $webDir }
    if ($devDeps.Count -gt 0) { Run-Command 'npm.cmd' (@('install','--save-dev','--save-exact') + $devDeps) $webDir }
    if ($deps.Count -eq 0 -and $devDeps.Count -eq 0 -and -not (Test-Path (FullPath 'apps/web/node_modules'))) {
        if (Test-Path (FullPath 'apps/web/package-lock.json')) { Run-Command 'npm.cmd' @('ci') $webDir }
        else { Run-Command 'npm.cmd' @('install') $webDir }
    }
} else {
    [void]$script:Warnings.Add('Skipped package installs. Run dotnet restore, cargo generate-lockfile/fetch, and npm install before builds.')
}

# Validate Compose model without starting or exposing any services.
if (Get-Command docker -ErrorAction SilentlyContinue) {
    $compose = FullPath 'infra/compose/compose.yaml'
    $envFile = FullPath 'infra/compose/.env'
    Run-Command 'docker' @('compose','--env-file',$envFile,'-f',$compose,'config','--quiet') $script:Root
} else {
    [void]$script:Warnings.Add('Docker CLI not found: Compose file created but config validation was skipped.')
}

if (-not $SkipBuild -and -not $SkipPackageInstall) {
    Write-Host "`nBuilding and running real bootstrap tests..." -ForegroundColor Cyan
    Run-Command 'dotnet' @('build',$solution,'--no-restore','-c','Debug') $script:Root
    Run-Command 'dotnet' @('test',$solution,'--no-build','--no-restore','-c','Debug') $script:Root
    $enginesDir = FullPath 'engines'
    Run-Command 'cmake' @('--preset','windows-x64') $enginesDir
    Run-Command 'cmake' @('--build','--preset','windows-x64-debug') $enginesDir
    Run-Command 'ctest' @('--test-dir','out/build/windows-x64','-C','Debug','--output-on-failure') $enginesDir
    Run-Command 'cargo' @('test','--locked') (FullPath 'services/strategy-runner')
    Run-Command 'npm.cmd' @('run','build') $webDir
    Run-Command 'npm.cmd' @('test') $webDir
    if (Get-Command py -ErrorAction SilentlyContinue) {
        Run-Command 'py' @('-3','-m','unittest','discover','-s','tests','-v') (FullPath 'sdk/python')
    } elseif (Get-Command python -ErrorAction SilentlyContinue) {
        Run-Command 'python' @('-m','unittest','discover','-s','tests','-v') (FullPath 'sdk/python')
    } else { [void]$script:Warnings.Add('Python missing: SDK smoke test skipped.') }
} elseif ($SkipBuild) {
    [void]$script:Warnings.Add('Build verification skipped by -SkipBuild.')
}

if ($StartInfrastructure) {
    Need-Command 'docker'
    Run-Command 'docker' @('compose','--env-file',(FullPath 'infra/compose/.env'),
       '-f',(FullPath 'infra/compose/compose.yaml'),'up','-d','postgres','keycloak') $script:Root
}
Write-Host "`nQuantDesk initializer finished." -ForegroundColor Green
Write-Host "Created/updated: $($script:Created.Count); preserved existing: $($script:Kept.Count)" -ForegroundColor Green
foreach ($warning in $script:Warnings) { Write-Warning $warning }
Write-Host 'Review changes with git status; this script does NOT commit or push.' -ForegroundColor Yellow
Write-Host 'Documentation: docs/operations/project-initialization.md'
