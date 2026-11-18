#requires -Version 5.1
<#
.SYNOPSIS
  Creates the full QuantDesk single-repository architecture on Windows.
.DESCRIPTION
  - Creates the directory structure for all 13 functional modules, engines,
    strategy runner, Python SDK, contracts, infrastructure, tests and docs.
  - Uses Onion-style layers within each .NET business module.
  - Adds safe starter configuration, governance docs and .gitkeep placeholders.
  - Creates ONE Git repository at the root if none exists; never creates nested repos.
  - NEVER overwrites an existing file, installs packages, commits or pushes.
  - This is a SOURCE TREE SCAFFOLD, not implementations of the 235 issues.
.EXAMPLE
  .\Initialize-QuantDesk-Monorepo.ps1
.EXAMPLE
  .\Initialize-QuantDesk-Monorepo.ps1 -RootPath 'C:\Work\QuantDesk-Trading-Simulation'
.EXAMPLE
  .\Initialize-QuantDesk-Monorepo.ps1 -SkipGitInit
#>
[CmdletBinding()]
param(
    [Parameter()]
    [string]$RootPath = 'C:\QuantDesk-Trading-Simulation',
    [Parameter()]
    [switch]$SkipGitInit
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$script:RootDir = [System.IO.Path]::GetFullPath($RootPath)
$script:CreatedFolders = New-Object 'System.Collections.Generic.List[string]'
$script:RegisteredFolders = New-Object 'System.Collections.Generic.List[string]'
$script:CreatedFiles = New-Object 'System.Collections.Generic.List[string]'
$script:SkippedFiles = New-Object 'System.Collections.Generic.List[string]'

function New-QdDirectory {
    param([Parameter(Mandatory = $true)][string]$RelativePath)
    [void]$script:RegisteredFolders.Add($RelativePath)
    $normalized = $RelativePath.Replace('/', [System.IO.Path]::DirectorySeparatorChar)
    $fullPath = Join-Path -Path $script:RootDir -ChildPath $normalized
    if (-not [System.IO.Directory]::Exists($fullPath)) {
        [void][System.IO.Directory]::CreateDirectory($fullPath)
        [void]$script:CreatedFolders.Add($RelativePath)
    }
}

function New-QdFile {
    param(
        [Parameter(Mandatory = $true)][string]$RelativePath,
        [Parameter(Mandatory = $true)][AllowEmptyString()][string]$Content
    )
    $normalized = $RelativePath.Replace('/', [System.IO.Path]::DirectorySeparatorChar)
    $fullPath = Join-Path -Path $script:RootDir -ChildPath $normalized
    $parent = Split-Path -Parent $fullPath
    if (-not [System.IO.Directory]::Exists($parent)) {
        [void][System.IO.Directory]::CreateDirectory($parent)
    }
    if ([System.IO.File]::Exists($fullPath)) {
        [void]$script:SkippedFiles.Add($RelativePath)
        return
    }
    $utf8NoBom = [System.Text.UTF8Encoding]::new($false)
    [System.IO.File]::WriteAllText($fullPath, $Content, $utf8NoBom)
    [void]$script:CreatedFiles.Add($RelativePath)
}

# Never turn C:\ itself into the project or overwrite an unrelated non-Git folder.
$driveRoot = [System.IO.Path]::GetPathRoot($script:RootDir)
if ($script:RootDir.TrimEnd('\', '/') -eq $driveRoot.TrimEnd('\', '/')) {
    throw 'RootPath must be a dedicated project folder, e.g. C:\QuantDesk-Trading-Simulation, not C:\ itself.'
}
if ([System.IO.Directory]::Exists($script:RootDir)) {
    $existingEntries = @(Get-ChildItem -LiteralPath $script:RootDir -Force -ErrorAction Stop | Select-Object -First 1)
    $hasGit = Test-Path -LiteralPath (Join-Path $script:RootDir '.git')
    $hasScaffold = Test-Path -LiteralPath (Join-Path $script:RootDir '.quantdesk-scaffold')
    if ($existingEntries.Count -gt 0 -and -not $hasGit -and -not $hasScaffold) {
        throw "The destination is nonempty but is not a Git checkout or a QuantDesk scaffold: $($script:RootDir). Use an empty folder or clone the existing GitHub repository first."
    }
}
[void][System.IO.Directory]::CreateDirectory($script:RootDir)
Write-Host "QuantDesk root: $($script:RootDir)" -ForegroundColor Cyan

# --- Team/module ownership comes from the 15 Nov 2026 synchronized roadmap. ---
$modules = @(
    @{ Id='M01'; Name='MarketData';           Feature='market-data';     Owner='D1'; Description='Provider adapters, instruments, normalized observations and immutable market snapshots' },
    @{ Id='M02'; Name='Indicators';           Feature='indicators';      Owner='D1'; Description='Technical indicators and numerical analytics integration' },
    @{ Id='M03'; Name='StrategyRegistry';     Feature='strategies';      Owner='D1'; Description='Strategy manifests, ownership, versioned OCI image metadata and approval' },
    @{ Id='M04'; Name='Backtesting';          Feature='backtesting';     Owner='D1'; Description='Historical jobs, deterministic replay, reports and engine orchestration' },
    @{ Id='M05'; Name='PaperTrading';         Feature='paper-trading';   Owner='D1'; Description='Strategy runner integration and paper-trading deployments' },
    @{ Id='M06'; Name='Matching';             Feature='trading';         Owner='D2'; Description='Orders, C++ matching-engine adapter, command and execution lifecycles' },
    @{ Id='M07'; Name='Portfolio';            Feature='portfolio';       Owner='D2'; Description='Accounts, cash/stock reservations, immutable journal and settlement' },
    @{ Id='M08'; Name='Risk';                 Feature='risk';            Owner='D1'; Description='Pre-trade policy, exposures, risk metrics and audit' },
    @{ Id='M09'; Name='Analytics';            Feature='analytics';       Owner='D1'; Description='Performance metrics, P&L history and comparison' },
    @{ Id='M11'; Name='Options';              Feature='options';         Owner='D1'; Description='Long equity-option metadata, valuation, Greeks and lifecycle rules' },
    @{ Id='M12'; Name='MarketSimulation';     Feature='simulation';      Owner='D2'; Description='Simulated market clock, agents, sessions and shocks' },
    @{ Id='M13'; Name='Arena';                Feature='arena';           Owner='D2'; Description='1v1 challenges, seeded replay, isolated accounts and scoring' },
    @{ Id='M14'; Name='Administration';       Feature='administration';  Owner='D2'; Description='Administrative permissions, education and audit UI' }
)

# --- Repository-level/shared folders ---
$baseDirectories = @(
    '.github/ISSUE_TEMPLATE', '.github/workflows',
    '.vscode',
    'apps/api/src/QuantDesk.Api/Configuration',
    'apps/api/src/QuantDesk.Api/Authentication',
    'apps/api/src/QuantDesk.Api/Middleware',
    'apps/api/src/QuantDesk.Api/Realtime',
    'apps/api/src/QuantDesk.Api/Health',
    'apps/api/src/QuantDesk.Api/Observability',
    'apps/api/src/QuantDesk.Shared/Abstractions',
    'apps/api/src/QuantDesk.Shared/Primitives',
    'apps/api/src/QuantDesk.Shared/Errors',
    'apps/api/src/QuantDesk.Shared/Time',
    'apps/api/src/QuantDesk.Shared/Results',
    'apps/api/src/QuantDesk.Modules/Infrastructure/Persistence/Migrations',
    'apps/api/src/QuantDesk.Modules/Infrastructure/Outbox',
    'apps/api/tests/QuantDesk.UnitTests',
    'apps/api/tests/QuantDesk.IntegrationTests',
    'apps/api/tests/QuantDesk.ArchitectureTests',
    'apps/web/public/assets',
    'apps/web/src/app/router',
    'apps/web/src/app/providers',
    'apps/web/src/app/layouts',
    'apps/web/src/app/styles',
    'apps/web/src/shared/ui',
    'apps/web/src/shared/api',
    'apps/web/src/shared/auth',
    'apps/web/src/shared/hooks',
    'apps/web/src/shared/types',
    'apps/web/src/shared/utils',
    'apps/web/src/shared/constants',
    'apps/web/src/generated/api',
    'apps/web/tests/smoke',
    'engines/cmake',
    'engines/quant-core/benchmarks',
    'engines/backtest/benchmarks',
    'engines/exchange/benchmarks',
    'services/strategy-runner/src/domain',
    'services/strategy-runner/src/supervisor',
    'services/strategy-runner/src/runtime',
    'services/strategy-runner/src/protocol',
    'services/strategy-runner/src/market',
    'services/strategy-runner/src/execution',
    'services/strategy-runner/src/transport',
    'services/strategy-runner/src/telemetry',
    'services/strategy-runner/tests',
    'services/strategy-runner/examples/protocol-messages',
    'sdk/python/quantdesk_sdk',
    'sdk/python/examples/sma-crossover',
    'sdk/python/examples/momentum',
    'sdk/python/tests',
    'contracts/proto/quantdesk/v1',
    'contracts/json-schema/strategy/v1',
    'contracts/json-schema/api/v1',
    'contracts/fixtures/market',
    'contracts/fixtures/orders',
    'contracts/fixtures/executions',
    'contracts/fixtures/portfolio',
    'contracts/fixtures/options',
    'contracts/fixtures/strategies',
    'contracts/fixtures/arena',
    'contracts/conventions',
    'infra/compose',
    'infra/docker',
    'infra/keycloak',
    'infra/postgres/init',
    'tests/contracts',
    'tests/integration/manual-trading',
    'tests/integration/accounting',
    'tests/integration/backtesting',
    'tests/integration/paper-trading',
    'tests/integration/options',
    'tests/integration/arena',
    'tests/e2e/authentication',
    'tests/e2e/manual-trade',
    'tests/e2e/strategy-backtest',
    'tests/e2e/option-expiry',
    'tests/e2e/arena-match',
    'tests/performance/matching',
    'tests/performance/backtesting',
    'tests/performance/strategy-runner',
    'tests/fixtures/market',
    'tests/fixtures/numerical-reference',
    'tests/fixtures/accounting',
    'tests/fixtures/option-scenarios',
    'tests/fixtures/arena',
    'docs/requirements',
    'docs/architecture',
    'docs/uml/use-case',
    'docs/uml/class',
    'docs/uml/sequence/trading',
    'docs/uml/sequence/backtesting',
    'docs/uml/sequence/options',
    'docs/uml/sequence/arena',
    'docs/uml/activity',
    'docs/uml/state-machine',
    'docs/uml/component',
    'docs/uml/deployment',
    'docs/uml/erd',
    'docs/adr',
    'docs/contracts',
    'docs/quantitative',
    'docs/testing',
    'docs/operations',
    'scripts/setup',
    'scripts/build',
    'scripts/test',
    'scripts/generate'
)
foreach ($directory in $baseDirectories) { New-QdDirectory $directory }

# --- Onion / Clean style within every ASP.NET Core functional module ---
$onionSubfolders = @(
    'Domain/Entities',
    'Domain/ValueObjects',
    'Domain/Events',
    'Domain/Rules',
    'Application/Abstractions',
    'Application/UseCases',
    'Application/DTOs',
    'Application/Contracts',
    'Infrastructure/Persistence',
    'Infrastructure/Adapters',
    'Infrastructure/Services',
    'Presentation/Endpoints',
    'Presentation/Contracts'
)
$reactSubfolders = @('api', 'components', 'pages', 'hooks', 'types', 'tests')
foreach ($module in $modules) {
    $apiRoot = "apps/api/src/QuantDesk.Modules/$($module.Name)"
    foreach ($subfolder in $onionSubfolders) { New-QdDirectory "$apiRoot/$subfolder" }
    $featureRoot = "apps/web/src/features/$($module.Feature)"
    foreach ($subfolder in $reactSubfolders) { New-QdDirectory "$featureRoot/$subfolder" }

    $moduleIntro = @"
# $($module.Id) - $($module.Name)

**Owner:** $($module.Owner) (synchronized roadmap, 15 November 2026)

**Scope:** $($module.Description).

This module uses Onion architecture: Domain <- Application <- external adapters.
Presentation and Infrastructure depend on Application; Domain depends on none.
Do not access another module's database tables or internal classes directly.
Start with explicit application contracts and acceptance tests for the assigned QD issues.
"@
    New-QdFile "$apiRoot/README.md" $moduleIntro
    New-QdFile "$featureRoot/README.md" "# $($module.Name) UI`r`n`r`nOwner: $($module.Owner). Keep feature-specific React pages, components, hooks, API clients and tests here.`r`n"
}

# --- C++ engine folder conventions (one CMake root, no nested Git repos) ---
$engineDefinitions = @(
    @{ Name='quant-core'; Include='quant';    Owner='D1' },
    @{ Name='backtest';   Include='backtest'; Owner='D1' },
    @{ Name='exchange';   Include='exchange'; Owner='D2' }
)
foreach ($engine in $engineDefinitions) {
    $base = "engines/$($engine.Name)"
    foreach ($sub in @('include/quantdesk/' + $engine.Include, 'src', 'tests/unit', 'tests/integration')) {
        New-QdDirectory "$base/$sub"
    }
    if ($engine.Name -eq 'quant-core') {
        foreach ($sub in @('include/quantdesk/quant/indicators', 'include/quantdesk/quant/statistics',
                           'include/quantdesk/quant/options', 'include/quantdesk/quant/risk',
                           'include/quantdesk/quant/math', 'src/indicators', 'src/statistics',
                           'src/options', 'src/risk', 'src/math', 'tests/numerical-reference')) {
            New-QdDirectory "$base/$sub"
        }
    }
    if ($engine.Name -eq 'backtest') {
        foreach ($sub in @('src/engine', 'src/replay', 'src/execution', 'src/portfolio',
                           'src/adapters', 'src/reporting', 'tests/deterministic')) {
            New-QdDirectory "$base/$sub"
        }
    }
    if ($engine.Name -eq 'exchange') {
        foreach ($sub in @('src/orderbook', 'src/matching', 'src/commands', 'src/events',
                           'src/persistence', 'src/transport', 'tests/property', 'tests/recovery')) {
            New-QdDirectory "$base/$sub"
        }
    }
}

# --- Version and editor configuration ---
New-QdFile '.gitignore' @'
# Environment and private credentials
.env
.env.*
!.env.example
*.pfx
*.pem
*.key
*.secrets.json
secrets.json

# IDE and OS
.vs/
.idea/
*.user
*.suo
*.rsuser
.DS_Store
Thumbs.db

# .NET
**/bin/
**/obj/
**/TestResults/

# CMake / C++
**/out/
**/cmake-build-*/
CMakeUserPresets.json
**/CMakeUserPresets.json
*.obj
*.pdb
*.ilk
*.exp
*.lib
*.dll
*.exe

# Rust
**/target/

# Node / React
**/node_modules/
**/dist/
**/.vite/
**/coverage/

# Python
**/__pycache__/
**/.pytest_cache/
**/.venv/
*.py[cod]
*.egg-info/

# Infrastructure runtime data
infra/**/data/
infra/**/volumes/
infra/compose/compose.override.yaml
'@

New-QdFile '.editorconfig' @'
root = true

[*]
charset = utf-8
end_of_line = lf
insert_final_newline = true
indent_style = space
indent_size = 4
trim_trailing_whitespace = true

[*.{json,jsonc,ts,tsx,js,jsx,css,scss,yml,yaml}]
indent_size = 2

[*.md]
trim_trailing_whitespace = false

[*.{cpp,hpp,h,c}]
indent_size = 4
'@

New-QdFile '.gitattributes' @'
* text=auto
*.ps1 text eol=crlf
*.bat text eol=crlf
*.sh text eol=lf
*.proto text eol=lf
'@

New-QdFile 'apps/api/Directory.Build.props' @'
<Project>
  <PropertyGroup>
    <TargetFramework>net10.0</TargetFramework>
    <Nullable>enable</Nullable>
    <ImplicitUsings>enable</ImplicitUsings>
    <LangVersion>latest</LangVersion>
  </PropertyGroup>
</Project>
'@
New-QdFile 'apps/api/Directory.Packages.props' @'
<Project>
  <PropertyGroup>
    <ManagePackageVersionsCentrally>true</ManagePackageVersionsCentrally>
  </PropertyGroup>
  <ItemGroup>
    <!-- Explicit package versions will be added with the first real .NET projects. -->
  </ItemGroup>
</Project>
'@

New-QdFile 'global.json' @'
{
  "sdk": {
    "version": "10.0.301",
    "rollForward": "latestFeature"
  }
}
'@
New-QdFile '.node-version' "26.13.1`n"
New-QdFile 'rust-toolchain.toml' @'
[toolchain]
channel = "1.100.0"
profile = "minimal"
'@
New-QdFile '.env.example' @'
# Placeholder only: copy to .env locally when Compose is configured.
# Never commit real passwords, provider API keys, or secrets.
# Variable names will be defined by Developer 2 in infra/compose.
'@

New-QdFile '.vscode/extensions.json' @'
{
  "recommendations": [
    "ms-dotnettools.csharp",
    "ms-vscode.cpptools",
    "ms-vscode.cmake-tools",
    "rust-lang.rust-analyzer",
    "dbaeumer.vscode-eslint",
    "esbenp.prettier-vscode",
    "ms-azuretools.vscode-docker"
  ]
}
'@
New-QdFile '.vscode/settings.json' @'
{
  "files.exclude": {
    "**/bin": true,
    "**/obj": true,
    "**/node_modules": true,
    "**/target": true
  }
}
'@
New-QdFile '.vscode/launch.json' @'
{
  "version": "0.2.0",
  "configurations": []
}
'@
New-QdFile '.vscode/tasks.json' @'
{
  "version": "2.0.0",
  "tasks": []
}
'@

# --- CMake configuration: valid initial configure, targets added in implementation PRs ---
New-QdFile 'engines/CMakeLists.txt' @'
cmake_minimum_required(VERSION 3.25)
project(QuantDeskEngines LANGUAGES CXX)
set(CMAKE_CXX_STANDARD 20)
set(CMAKE_CXX_STANDARD_REQUIRED ON)
set(CMAKE_CXX_EXTENSIONS OFF)
include(CTest)
add_subdirectory(quant-core)
add_subdirectory(backtest)
add_subdirectory(exchange)
'@
New-QdFile 'engines/CMakePresets.json' @'
{
  "version": 6,
  "configurePresets": [
    {
      "name": "windows-x64",
      "displayName": "Windows x64 - Visual Studio 2026",
      "generator": "Visual Studio 18 2026",
      "architecture": "x64",
      "binaryDir": "${sourceDir}/out/build/${presetName}",
      "cacheVariables": {
        "CMAKE_CXX_STANDARD": "20",
        "CMAKE_CXX_STANDARD_REQUIRED": "ON"
      }
    }
  ],
  "buildPresets": [
    {
      "name": "windows-x64-debug",
      "configurePreset": "windows-x64",
      "configuration": "Debug"
    },
    {
      "name": "windows-x64-release",
      "configurePreset": "windows-x64",
      "configuration": "Release"
    }
  ]
}
'@
foreach ($engine in $engineDefinitions) {
    $info = "# $($engine.Name) [$($engine.Owner)]`n# C++20 target will be defined in its implementation PR.`n# This empty CMake file intentionally allows root configure to succeed.`n"
    New-QdFile "engines/$($engine.Name)/CMakeLists.txt" $info
}

# --- Collaboration and architecture documentation ---
New-QdFile 'README.md' @'
# QuantDesk — Trading Simulation

A virtual-funds equities and listed equity-options trading simulator with:
manual trading, financial accounting, historical backtesting, approved OCI
strategies, paper trading, simulated market sessions, risk analytics and 1v1 Arena.

## Architecture

- One GitHub monorepo; no separate repositories per module.
- ASP.NET Core 10 modular monolith with Onion layers per business module.
- One React/TypeScript frontend with feature-owned screens.
- C++20 quant-core library and standalone historical/matching engines.
- Rust Tokio strategy runner and Python strategy SDK.
- PostgreSQL + Keycloak managed by Docker Compose.
- REST/SignalR to the web; versioned protobuf/gRPC and framed JSON at process boundaries.

## Ownership

Developer 1: M01/M02/M03/M04/M05/M08/M09/M11.
Developer 2: P00/M06/M07/M12/M13/M14.
Shared: F00 planning/UML and Q15 integration/testing.

See `docs/architecture/module-ownership.md` for the full mapping.

## Development baseline

Native Windows + Docker Desktop, VS 2026/MSVC x64, CMake, .NET 10,
Rust 1.100.0, Node.js 26.13.1 and Git. Toolchain versions are declared
in `global.json`, `.node-version`, and `rust-toolchain.toml`.

## Important

This commit is the *folder and configuration scaffold only*.
It does not yet contain functional engines, API endpoints, React pages,
Cargo crates, a runnable Compose stack, or completed QD tasks.
Build and application projects should be initialized next, in reviewed PRs.

## Repository hygiene

Do not commit `.env`, local secrets, build output, runtime volumes, or API keys.
There must be only one `.git` directory, at this repository root.
'@

New-QdFile 'CONTRIBUTING.md' @'
# Contributing to QuantDesk

- One monorepo and one integration branch (`main`).
- Branches: `feat/qd-###-short-name`, `fix/qd-###-short-name`, `docs/qd-###-short-name`.
- One developer owns each functional module end to end (.NET, React, engine, tests).
- Cross-module contracts require both developers' approval.
- Pull requests must link the QD issue and describe tests, contract impact, and migrations.
- Keep `main` runnable after the initial executable scaffold is introduced.
- Avoid a full CI/CD pipeline; local tests and optional minimal PR checks are enough.
- Weekly integration and demonstrations; keep PRs small.
- No real-money trading, broker credentials, or unsafe unrestricted strategy images.
'@
New-QdFile '.github/pull_request_template.md' @'
## Linked issue
QD-###

## What changed?

## Tests run

## Contract changes / migrations

## UI screenshots (if applicable)

## Risks and rollback notes
'@
New-QdFile '.github/CODEOWNERS.example' @'
# TEMPLATE ONLY. Rename to CODEOWNERS once both GitHub usernames are confirmed.
# /engines/quant-core/      @developer1-handle
# /engines/backtest/        @developer1-handle
# /services/strategy-runner/ @developer1-handle
# /engines/exchange/        @developer2-handle
# /infra/                   @developer2-handle
# /contracts/               @developer1-handle @developer2-handle
'@
New-QdFile 'docs/architecture/module-ownership.md' @'
# Module ownership — authoritative 15 Nov 2026 roadmap

| Module | Owner | Main implementation surface |
|---|---|---|
| F00 Planning, UML & GitHub | Both | docs, GitHub, contracts |
| P00 Docker Platform & Keycloak | Developer 2 | infra, API host, web shell |
| M01 Market Data | Developer 1 | .NET + React |
| M02 Quantitative Indicators | Developer 1 | quant-core + .NET + React |
| M03 Strategy Registry | Developer 1 | .NET + React |
| M04 Backtesting | Developer 1 | C++ backtest + .NET + React |
| M05 Strategy Runner & Paper Trading | Developer 1 | Rust + Python SDK + .NET + React |
| M06 Matching Engine | Developer 2 | C++ exchange + .NET + React |
| M07 Portfolio & Accounting | Developer 2 | .NET + React |
| M08 Risk Management | Developer 1 | .NET / quant-core + React |
| M09 Performance Analytics | Developer 1 | .NET / quant-core + React |
| M11 Equity Options | Developer 1 | .NET / quant-core + React |
| M12 Market Simulation | Developer 2 | .NET + React |
| M13 Arena | Developer 2 | .NET + React |
| M14 Administration & Education | Developer 2 | .NET + React |
| Q15 Integration & Testing | Both | tests, docs, release/demo checks |

Original QD-001...QD-235 issues are retained. The older corrected backlog PDF
has a superseded ownership split; the synchronized roadmap takes precedence.
'@
New-QdFile 'docs/architecture/module-boundaries.md' @'
# Backend and engine boundaries

- **Domain**: business rules, entities, value objects, invariants; no EF Core, HTTP, gRPC.
- **Application**: use cases and outbound interfaces; depends on Domain.
- **Infrastructure**: EF Core, market providers, gRPC/native adapters; implements Application ports.
- **Presentation**: REST endpoints and DTO validation; calls Application only.
- **Modules**: communicate through explicit application interfaces or documented events,
  not by reaching into one another's persistence implementation.
- **Financial authority**: Developer 2 Portfolio owns cash/positions/reservations/settlement.
  C++ matching owns price-time matching, but never bypasses authoritative reservations.
- **Risk**: Developer 1 owns risk policies; clarify risk failure and timeout behavior before trading.
- **C++**: quant-core is a library; backtest and exchange are separate engine processes.
- **Rust**: trusted runner controls approved sandboxed strategy containers; no direct DB access.
- **Market data**: use immutable, provenance-labelled snapshots and UTC event ordering.
- **Replay**: identical seed + input snapshot + model version must reproduce output.
'@
New-QdFile 'docs/requirements/README.md' @'
# Requirements and traceability

Store the approved cahier des charges, scope/non-goals and QD-001...QD-235
requirement-to-test traceability here. Do not change task ownership based on the older backlog PDF.
'@
New-QdFile 'contracts/README.md' @'
# Shared contracts (freeze before cross-module implementation)

Core contracts: MarketEvent, OrderRequest, ExecutionEvent, PortfolioSnapshot,
OptionContract and ArenaRuleset. Schemas belong in proto/ or json-schema/;
producer/consumer examples and failure vectors belong in fixtures/.

Agree IDs, units, currencies, UTC timestamps, money precision, event sequencing,
versioning, idempotency and errors. Do not invent fields in consumer implementations.
'@
New-QdFile 'infra/README.md' @'
# Infrastructure — Developer 2 ownership

Docker Compose will provision PostgreSQL (separate application/Keycloak databases),
Keycloak, API, web, and engine services as needed. Use explicit image versions.
Avoid committing credentials or database volumes. A real Compose configuration will
be added in an implementation PR; no invalid placeholder Compose YAML is generated.
'@
New-QdFile 'apps/api/README.md' @'
# ASP.NET Core backend

Initial structure only. Initialize these three production projects next:
- `src/QuantDesk.Api` (ASP.NET Core host and BFF)
- `src/QuantDesk.Shared` (minimal stable primitives)
- `src/QuantDesk.Modules` (business modules with Onion layers)

Add tests in `tests/`. Use .NET 10 selected by repository root `global.json`.
No .sln/.csproj files are fabricated by this scaffold.
'@
New-QdFile 'apps/web/README.md' @'
# React frontend

One Vite + React + TypeScript SPA. Each module owner implements their own feature
under `src/features/`; common routing, auth and components stay under `src/app` and
`src/shared`. Vite and package.json will be initialized in the frontend bootstrap PR.
'@
New-QdFile 'services/strategy-runner/README.md' @'
# Rust strategy runner — Developer 1

Supervised lifecycle, framed JSON strategy protocol, market-event dispatch,
order intents, fill notices, execution constraints, gRPC orchestration and telemetry.
Cargo.toml/source files will be created with `cargo init` during implementation.
'@
New-QdFile 'sdk/python/README.md' @'
# Python Strategy SDK — Developer 1

A lightweight language-neutral client for the strategy stdin/stdout protocol.
Approved SMA crossover reference image first; no unrestricted user images until
sandbox threat-model and isolation acceptance tests have passed.
'@
New-QdFile 'docs/adr/README.md' @'
# Architecture Decision Records

Record trade-offs and approvals for module boundaries, engine contracts, financial
invariants, authentication, persistence, supported option conventions and containers.
'@
New-QdFile 'docs/operations/windows-setup.md' @'
# Windows build environment

Use Visual Studio 2026 with Desktop C++ workload + MSVC x64 + CMake,
.NET 10 SDK, Rust/MSVC, Node.js 26, Git and Docker Desktop.

From a VS x64 Developer Command Prompt, verify `cl` says `for x64`.
Inside `engines/`, CMake can be configured using:

```powershell
cmake --preset windows-x64
cmake --build --preset windows-x64-debug
```

CMake currently has skeleton targets only; backend, Rust and React projects are
scaffolded next. Do not treat successful CMake configuration as functional tests.
'@

# Track every registered empty directory. Git does not track directories themselves.
# Only our registered project directories are inspected (never the internal .git tree).
foreach ($directory in ($script:RegisteredFolders | Sort-Object -Unique)) {
    $fullPath = Join-Path $script:RootDir ($directory.Replace('/', [System.IO.Path]::DirectorySeparatorChar))
    if ([System.IO.Directory]::Exists($fullPath)) {
        $items = @(Get-ChildItem -LiteralPath $fullPath -Force | Select-Object -First 1)
        if ($items.Count -eq 0) { New-QdFile "$directory/.gitkeep" '' }
    }
}

# Human-readable manifest: all registered folders are under this same repository.
$manifest = @(
    '# QuantDesk folder manifest',
    '',
    'Generated by Initialize-QuantDesk-Monorepo.ps1. One Git repository at the root.',
    '',
    '```text'
)
foreach ($directory in ($script:RegisteredFolders | Sort-Object -Unique)) {
    $manifest += $directory
}
$manifest += '```'
New-QdFile 'docs/architecture/folder-manifest.md' ($manifest -join "`n")

# Keep the scaffold script inside the ONE repository for reproducibility.
if (-not [string]::IsNullOrWhiteSpace($PSCommandPath)) {
    $copyDestination = Join-Path $script:RootDir 'scripts\setup\Initialize-QuantDesk-Monorepo.ps1'
    if (-not [System.IO.File]::Exists($copyDestination)) {
        $source = [System.IO.Path]::GetFullPath($PSCommandPath)
        if (-not [string]::Equals($source, $copyDestination, [System.StringComparison]::OrdinalIgnoreCase)) {
            Copy-Item -LiteralPath $source -Destination $copyDestination -ErrorAction Stop
            [void]$script:CreatedFiles.Add('scripts/setup/Initialize-QuantDesk-Monorepo.ps1')
        }
    }
}
New-QdFile '.quantdesk-scaffold' "QuantDesk monorepo scaffold v1.0`n"

# Only ONE Git repository at the project root. No automatic commit or push.
if (-not $SkipGitInit -and -not (Test-Path -LiteralPath (Join-Path $script:RootDir '.git'))) {
    if (Get-Command git -ErrorAction SilentlyContinue) {
        Push-Location $script:RootDir
        try {
            & git init -b main
            if ($LASTEXITCODE -ne 0) { throw 'git init failed; all generated files remain available.' }
        }
        finally { Pop-Location }
    }
    else {
        Write-Warning 'Git not found on PATH. Files were created, but you must initialize Git manually.'
    }
}

Write-Host ''
Write-Host 'QuantDesk scaffold completed.' -ForegroundColor Green
Write-Host "Directories created: $($script:CreatedFolders.Count)"
Write-Host "Files created:       $($script:CreatedFiles.Count)"
Write-Host "Files kept intact:   $($script:SkippedFiles.Count)"
Write-Host "Project directory:   $($script:RootDir)"
Write-Host 'All modules are in ONE root repository; no modules were initialized as separate Git repositories.' -ForegroundColor Cyan
Write-Host 'Next: review the structure, initialize .NET/Cargo/Vite actual project files, and agree shared DTOs before functional coding.'
