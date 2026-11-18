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