# API Trees Bot — .NET MAUI Blazor Hybrid

A C# bot interface for [API Trees](https://github.com/I-Make-Stuff/API-Trees), with a
native **.NET MAUI Blazor Hybrid** dashboard. Python is not required to build or
play with the app; there is no command-line player.

The upstream integration target is pinned to
**2acf9a0b3000a12e7e2e9b9f4ffa015b08a8c109**. This is not a claim of compatibility
with the latest upstream revision or every modified server.
See [compatibility and test limits](docs/COMPATIBILITY.md).

## What it does

- Claims an available team and saves its token privately per server.
- Waits in the lobby, automatically harvests fruit and prunes poisoned branches.
- Plants empty plots, replaces dead trees and expands with single-plot purchases.
- Supports tree limits, a money reserve, bank-only mode and an optional end-game estimate.
- Shows resources, score estimate, plot/tree tables, branch IDs and activity logs.
- Displays public opponent plot IDs for manual targeting.
- Provides manual planting, pruning, tree removal, conversions, purchases and attacks.
- Stops when the round ends or changes; ambiguous write failures stop instead of replaying actions.

## Windows GUI: build and run

1. Install the **.NET 10 SDK** and the .NET MAUI development workload using a
   Visual Studio version that supports .NET 10, or use the build commands below.
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

If a clean build fails with `MSB3030` because the SDK host pack's `apphost.exe`
is missing, repair the .NET SDK/Visual Studio installation. This is an SDK-file
problem; the native app still needs its executable host.

In the app:

1. Enter the host's reachable URL, such as `https://192.168.1.50`.
2. Configure certificates using the LAN/self-signed HTTPS guide below.
3. Click **Connect / resume**. If no current saved team resumes, choose an available
   team in the lobby, then **Claim team**.
4. Set your strategy and click **Start bot**. It waits until the host starts the game.
5. Click **Stop bot** before using manual controls. Stopping preserves your session.

MAUI uses native `SecureStorage` for team sessions. **Connect / resume** restores
the saved team for the current round. Never try to claim the same team twice.

Keep the application open while playing. On mobile, moving the app into the
background stops automation; restart it when you return. A new round needs a new
team claim in its lobby.

If the lobby changes while a claim is in flight, the app retains the issued token
but marks its round **unconfirmed** and blocks further claims/actions. Ask the
host which exact round received the claim, enter that ID and explicitly confirm
the host's verification, then refresh. The server's `/me` response authenticates
old tokens too; it cannot prove round membership. **Retry Save session** remains
available if saving fails, even after the round changes. Do not claim again.

## Native release build

```powershell
dotnet publish src/ApiTrees.Maui/ApiTrees.Maui.csproj -c Release -f net10.0-windows10.0.19041.0 -p:ApiTreesTargetFramework=net10.0-windows10.0.19041.0 -p:RuntimeIdentifier=win-x64 -p:WindowsPackageType=None -m:1 -o artifacts/windows
```

The GitHub Actions workflow is configured to publish the Windows app and upload
`API-Trees-Bot-Windows-x64`; check its run before treating an artifact as built.
Copy the **entire folder**, not only the executable. This build uses installed
.NET/Windows App SDK runtime dependencies; run from Visual Studio or install the required runtimes on the
destination machine. No prebuilt native executable is included in the source ZIP.

The MAUI project also includes Android, iOS and Mac Catalyst targets. Build those
with their platform toolchains, corresponding MAUI workloads and, for Apple,
Xcode/signing. The Windows-only property avoids restoring unrelated platforms:

```bash
# Example on macOS (requires the appropriate workload and Xcode):
dotnet build src/ApiTrees.Maui/ApiTrees.Maui.csproj -f net10.0-maccatalyst -p:ApiTreesTargetFramework=net10.0-maccatalyst
```

The MAUI GUI has no native Linux target.

## LAN and self-signed HTTPS

Use the game host's address, not `localhost`, when playing from another computer.
Different networks require a reachable VPN address or public server URL.
The server must also be reachable through the LAN/firewall.

- **Preferred for private/self-signed TLS:** obtain the host's public CA or
  self-signed server certificate through a trusted channel. Use **Browse certificate**
  to select its PEM or DER certificate file (for example, Caddy's public `root.crt`).
  Keep **Skip TLS verification** unchecked. Never obtain or share a private key.
- The URL's hostname or IP address must match the server certificate. Providing a
  certificate does not bypass hostname or expiry checks. Ask the host to correct
  an invalid certificate or URL instead of bypassing validation.
- For publicly trusted HTTPS, leave the certificate field blank and verification enabled.
- **Explicit trusted-LAN fallback only:** select **Skip TLS verification (trusted LAN only)**.
  This disables certificate selection and server authentication, permitting
  interception even though the connection is encrypted. Never use it on untrusted
  networks. Certificate trust and bypass are separate modes; the API client rejects
  callers that combine them. Prefer fixing certificate trust instead.

Prefer HTTPS. HTTP carries bearer tokens without encryption. The native GUI
uses `HttpClient`, so browser CORS does not block game requests.

Redirects are disabled to avoid forwarding team tokens to another host. A private
CA does not disable hostname verification. Check the host's server URL/certificate
if TLS validation fails. Read or explicit busy-server errors can refresh state
with backoff; an uncertain write stops the bot for inspection.
Manual actions and restarting automation stay blocked until **Refresh orchard &
opponents** completes successfully. Cancelling an in-flight write also needs this
check: cancellation does not prove the server rolled the action back.

## Build and test without MAUI

```bash
dotnet build ApiTrees.Portable.slnx -c Release -m:1
dotnet run --project tests/ApiTrees.Tests -c Release --no-build
```

The portable solution contains the core, shared dashboard, local web preview and
tests, without requiring MAUI workloads.

### Optional upstream game integration tests

Only these tests require Python, because the upstream game server is Python.
`tests/run_integration.py` and `tests/upstream_fixture.py` are test-only server
infrastructure, not players or application dependencies. After the portable build:

```bash
git clone https://github.com/I-Make-Stuff/API-Trees.git ../API-Trees
git -C ../API-Trees checkout 2acf9a0b3000a12e7e2e9b9f4ffa015b08a8c109
python -m pip install -r ../API-Trees/api/requirements.txt
python tests/run_integration.py ../API-Trees
```

The fixture binds to loopback, uses a temporary SQLite database and stubs Redis
scheduling. It does not contact or modify a live game server. See the compatibility
document for test limits; a fixture pass is not a real LAN/device test.

## Optional local UI preview

For development on a machine without MAUI, the same Razor dashboard can run in
a local Blazor Server host:

```bash
dotnet run --project src/ApiTrees.Web
```

Open `http://127.0.0.1:5080`. The preview stores team tokens **only in memory**
for the circuit's lifetime; use the MAUI app for persistent sessions. Certificate
selection accepts a typed path on the machine running the preview; the native
Browse picker is available in MAUI. Do not expose this developer preview as a
shared player service.

## Project structure

| Project | Purpose |
| --- | --- |
| `src/ApiTrees.Core` | Typed API client, sessions and automation |
| `src/ApiTrees.Ui` | Shared Blazor dashboard and controller |
| `src/ApiTrees.Maui` | Native MAUI host, secure storage and file picker |
| `src/ApiTrees.Web` | Optional loopback developer preview |
| `tests/ApiTrees.Tests` | Executable regression and API integration suite |
| `tests/*.py` | Test-only upstream Python server harness |

Automatic farming and defense are provided; attacks stay manual. Winning is not
guaranteed, and compatibility with future server changes requires another check.
