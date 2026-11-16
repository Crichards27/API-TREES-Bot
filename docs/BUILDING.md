# Building and testing

These instructions are for developers. To play, download the Windows release
as described in the [README](../README.md).
Run all commands from the repository root.

## Windows development

Install the .NET 10 SDK and the Windows MAUI workload. The app also needs the
WebView2 Runtime. Visual Studio is optional; if used, install a version with
.NET 10 and MAUI support.

```powershell
dotnet workload install maui-windows
dotnet build src/ApiTrees.Maui/ApiTrees.Maui.csproj -c Debug -f net10.0-windows10.0.19041.0 -p:ApiTreesTargetFramework=net10.0-windows10.0.19041.0 -t:Run -m:1
```

Alternatively, open `ApiTrees.slnx` in Visual Studio, select `ApiTrees.Maui` as
the startup project and run on **Windows Machine**. Install the full MAUI
workload if Visual Studio restores all configured mobile targets.
The `ApiTreesTargetFramework` property in the command limits restore to Windows.

If the SDK host pack is missing `apphost.exe` or `singlefilehost.exe` (for example, build error `MSB3030`),
repair or reinstall the affected local .NET SDK/Visual Studio components and
retry the build. This is a missing SDK file, not a reason to disable the app host
or substitute a machine-specific `AppHostSourcePath`.

## Publish the standalone Windows EXE

```powershell
dotnet publish src/ApiTrees.Maui/ApiTrees.Maui.csproj -c Release -f net10.0-windows10.0.19041.0 -p:ApiTreesTargetFramework=net10.0-windows10.0.19041.0 -p:PublishProfile=WindowsSingleFile -m:1 -o artifacts/windows
```

Distribute `artifacts/windows/ApiTrees.Maui.exe`. The profile bundles .NET,
the Windows App SDK and Blazor assets; the other publish files are not needed to
run this EXE. Keep its original filename: this WinUI build does not start correctly when renamed.
WebView2 Runtime is still required. Source archives are not compiled player downloads.

The EXE extracts its dependencies at first launch. If the system drive is short
on space, use a different publish output path and, when launching from PowerShell,
choose a native Windows path for the extraction cache:

```powershell
$env:DOTNET_BUNDLE_EXTRACT_BASE_DIR = 'D:\API-Trees-Bot-cache'
& 'D:\path\to\ApiTrees.Maui.exe'
```

CI publishes the EXE plus a SHA-256 checksum. Pushing a `v*` tag creates a GitHub
Release only after the portable/API checks and Windows publish succeed. Existing
release assets are not overwritten by this job.

## Portable build and tests

The portable solution contains the core, shared dashboard, loopback web preview
and executable tests. It needs the .NET 10 SDK but no MAUI workload.

```bash
dotnet build ApiTrees.Portable.slnx -c Release -m:1
dotnet run --project tests/ApiTrees.Tests -c Release --no-build
```

Inspect the command output for results. These checks do not establish that a
native app launches or connects to a real LAN game; see
[compatibility and validation limits](COMPATIBILITY.md).

## Optional upstream integration tests

Only these tests need Python, because the upstream game server is Python.
`tests/run_integration.py` and `tests/upstream_fixture.py` are test-only server
infrastructure, not players or application dependencies. After the portable build:

```bash
git clone https://github.com/I-Make-Stuff/API-Trees.git ../API-Trees
git -C ../API-Trees checkout 2acf9a0b3000a12e7e2e9b9f4ffa015b08a8c109
python -m pip install -r ../API-Trees/api/requirements.txt
python tests/run_integration.py ../API-Trees
```

The pinned revision makes the check reproducible, not current with every server.
Use a Python interpreter with its matching pip installation. The fixture binds
only to loopback, uses temporary SQLite storage and stubs Redis scheduling.
It does not modify a live game server or verify real LAN/certificate behavior.

## Local UI preview

Run the same Razor dashboard without MAUI:

```bash
dotnet run --project src/ApiTrees.Web
```

Open `http://127.0.0.1:5080`. Tokens remain only in memory for the circuit's
lifetime; use the native app for persistent sessions. Certificate selection in
the preview takes a typed path on the machine running it; MAUI has the native
**Browse certificate** picker. Do not expose this preview as a shared player service.

## Other native targets

The MAUI project includes Android, iOS and Mac Catalyst targets. They require
platform toolchains and corresponding workloads; Apple targets also need
Xcode/signing. For example, on a suitably configured Mac:

```bash
dotnet build src/ApiTrees.Maui/ApiTrees.Maui.csproj -f net10.0-maccatalyst -p:ApiTreesTargetFramework=net10.0-maccatalyst
```

There is no native Linux MAUI target. A Windows build does not validate the
other platforms.

## Project layout

| Path | Purpose |
| --- | --- |
| `src/ApiTrees.Core` | API client, sessions and automation |
| `src/ApiTrees.Ui` | Shared Blazor dashboard and controller |
| `src/ApiTrees.Maui` | Native host, secure storage and file picker |
| `src/ApiTrees.Web` | Loopback developer preview |
| `tests/ApiTrees.Tests` | Executable regression and integration suite |
| `tests/*.py` | Test-only upstream server harness |
