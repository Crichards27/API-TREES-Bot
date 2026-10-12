# API Trees Bot — C# + Blazor MAUI

A C# player for [API Trees](https://github.com/I-Make-Stuff/API-Trees), with a
native **.NET MAUI Blazor Hybrid** GUI and a command-line client sharing the
same bot engine. Python is no longer required to play.

Compatibility checked against the latest upstream `main` inspected on
2026-10-11: **2acf9a0** (game version **0.2.0**, 2026-10-06).
See [compatibility and test limits](docs/COMPATIBILITY.md).

## What it does

- Claims an available team and saves its token privately per server.
- Waits in the lobby, automatically harvests fruit and prunes poisoned branches.
- Plants empty plots, replaces dead trees and expands with single-plot purchases.
- Supports tree limits, a money reserve, bank-only mode and an optional end-game estimate.
- Shows resources, score estimate, the four-column orchard grid, branches and activity.
- Displays opponent plot IDs from the latest public-team endpoint.
- Provides manual planting, pruning, tree removal, conversions, purchases and attacks.
- Stops when the round ends or changes; ambiguous write failures stop instead of replaying actions.

## Windows GUI: build and run

1. Install the **.NET 10 SDK** and the .NET MAUI development workload using a
   current Visual Studio version that supports .NET 10, or use the CLI below.
   Windows requires the **WebView2 Runtime** (normally installed on Windows 11).
2. Clone this repository, or extract the supplied source ZIP and open a terminal
   in its `API-TREES-Bot` folder.
3. Run:

```powershell
dotnet workload install maui-windows
dotnet build src/ApiTrees.Maui/ApiTrees.Maui.csproj -c Debug -f net10.0-windows10.0.19041.0 -p:ApiTreesTargetFramework=net10.0-windows10.0.19041.0 -t:Run -m:1
```

Alternatively, open `ApiTrees.slnx` in Visual Studio, select `ApiTrees.Maui` as
the startup project, select **Windows Machine**, and run it. Install the full
MAUI workload if Visual Studio restores all the configured mobile targets.

In the app:

1. Enter the host's reachable URL, such as `https://192.168.1.50`.
2. If the host uses Caddy internal TLS, browse to the host's public `root.crt`.
   Leave the certificate blank for publicly trusted HTTPS.
3. Click **Connect / resume**, choose an available team, then **Claim team**.
4. Set your strategy and click **Start bot**. It waits until the host starts the game.
5. Click **Stop bot** before using manual controls. Stopping preserves your session.

MAUI uses native `SecureStorage` for team sessions. It does not automatically
import a Python/CLI session, so use a different available team or the C# CLI to
resume an existing Python token. Never try to claim the same team twice.

Keep the application open while playing. On mobile, moving the app into the
background stops automation; restart it when you return. A new round needs a new
team claim in its lobby.

## Native release build

```powershell
dotnet publish src/ApiTrees.Maui/ApiTrees.Maui.csproj -c Release -f net10.0-windows10.0.19041.0 -p:ApiTreesTargetFramework=net10.0-windows10.0.19041.0 -p:RuntimeIdentifier=win-x64 -p:WindowsPackageType=None -m:1 -o artifacts/windows
```

The GitHub Actions workflow performs this build on Windows and uploads its
publish folder as `API-Trees-Bot-Windows-x64`. Copy the **entire folder**, not
only the executable. This build uses installed .NET/Windows App SDK runtime
dependencies; run from Visual Studio or install the required runtimes on the
destination machine. No prebuilt native executable is included in the source ZIP.

The MAUI project also includes Android, iOS and Mac Catalyst targets. Build those
with their platform toolchains, corresponding MAUI workloads and, for Apple,
Xcode/signing. The Windows-only property avoids restoring unrelated platforms:

```bash
# Example on macOS (requires the appropriate workload and Xcode):
dotnet build src/ApiTrees.Maui/ApiTrees.Maui.csproj -f net10.0-maccatalyst -p:ApiTreesTargetFramework=net10.0-maccatalyst
```

The CLI runs on Windows, macOS and Linux. The MAUI GUI has no native Linux target.

## C# command-line client

Install the .NET 10 SDK; MAUI workloads are unnecessary for the CLI.

```powershell
dotnet run --project src/ApiTrees.Cli -- --server https://HOST --ca-cert root.crt bot --team apple
```

Replace `HOST` with the game's address. Omit `--ca-cert` for trusted HTTPS.
Use `dotnet run --project src/ApiTrees.Cli -- --help` for all commands.

```powershell
# Resume, cap expansion and retain a money reserve:
dotnet run --project src/ApiTrees.Cli -- --server https://HOST bot --max-trees 20 --reserve 100

# Harvest/defend without spending:
dotnet run --project src/ApiTrees.Cli -- --server https://HOST bot --bank-only

# Public opponent plot IDs:
dotnet run --project src/ApiTrees.Cli -- --server https://HOST team orange

# Claim for a NEW round (after the host opens a lobby):
dotnet run --project src/ApiTrees.Cli -- --server https://HOST claim apple
```

| Option | Meaning |
| --- | --- |
| `--interval 2` | Seconds between bot cycles |
| `--max-trees 0` | Tree limit; 0 means unlimited |
| `--reserve 0` | Money to keep unspent during expansion |
| `--expansion-batch 4` | Maximum new plots purchased per cycle |
| `--round-seconds 600` | Estimated seconds remaining at this run's first active observation; stops investing in its final 45 seconds |
| `--bank-only` | Harvest and defend without investing (`--farm-only` alias) |
| `--timeout 10` | HTTP timeout in seconds |
| `--session-dir PATH` | Override the default `~/.api_trees` session directory |
| `--insecure` | Skip certificate verification for trusted-LAN testing; mutually exclusive with `--ca-cert` |

`API_TREES_SERVER` can replace `--server`. Press **Ctrl+C** to stop. CLI tokens
are stored in the same per-server JSON format as the Python bot, so the C# CLI
can resume existing sessions. Protect that directory: its files contain tokens.

The original Python client is retained under `legacy/` for reference. It is not
the current implementation or part of the compatibility checks.

## Connection notes

Use the game host's address, not `localhost`, when playing from another computer.
Different networks require a reachable VPN address or public server URL.
Prefer HTTPS. HTTP carries bearer tokens without encryption. The native GUI
uses `HttpClient`, so browser CORS does not block game requests.

Redirects are disabled to avoid forwarding team tokens to another host. A private
CA does not disable hostname verification. Check the host's server URL/certificate
if TLS validation fails. Read or explicit busy-server errors can refresh state
with backoff; an uncertain write stops the bot for inspection.

## Build and test without MAUI

```bash
dotnet build ApiTrees.Portable.slnx -c Release -m:1
dotnet run --project tests/ApiTrees.Tests -c Release --no-build
```

To exercise the actual upstream game API with deterministic test infrastructure:

```bash
git clone https://github.com/I-Make-Stuff/API-Trees.git ../API-Trees
git -C ../API-Trees checkout 2acf9a0b3000a12e7e2e9b9f4ffa015b08a8c109
python -m pip install -r ../API-Trees/api/requirements.txt
python tests/run_integration.py ../API-Trees
```

Python is needed only for these upstream integration tests. The fixture binds to
loopback, uses a temporary SQLite database and stubs Redis scheduling. It does
not contact or modify a live game server. See the compatibility document for
what these tests do and do not establish.

## Optional local UI preview

For development on a machine without MAUI, the same Razor dashboard can run in
a local Blazor Server host:

```bash
dotnet run --project src/ApiTrees.Web
```

Open `http://127.0.0.1:5080`. The preview stores team tokens **only in memory**;
closing/reloading the page loses them. Use the MAUI GUI or CLI for actual play
with persistent sessions. Certificate selection in this host accepts a typed
local path; the native Browse picker is available in MAUI.

## Project structure

| Project | Purpose |
| --- | --- |
| `src/ApiTrees.Core` | Typed API client, sessions and automation |
| `src/ApiTrees.Cli` | Portable C# command-line player |
| `src/ApiTrees.Ui` | Shared Blazor dashboard and controller |
| `src/ApiTrees.Maui` | Native MAUI host, secure storage and file picker |
| `src/ApiTrees.Web` | Optional loopback developer preview |
| `tests/ApiTrees.Tests` | Executable regression and API integration suite |

Automatic farming and defense are provided; attacks stay manual. Winning is not
guaranteed, and compatibility with future server changes requires another check.
