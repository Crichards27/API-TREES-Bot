# API Trees Bot

A Windows app for [API Trees](https://github.com/I-Make-Stuff/API-Trees).
It farms and defends your orchard automatically; attacks stay manual.

## Download and run

1. Download [ApiTrees.Maui.exe](https://github.com/Crichards27/API-TREES-Bot/releases/latest/download/ApiTrees.Maui.exe) from the latest Windows x64 release.
2. Keep the filename **ApiTrees.Maui.exe** and double-click it. No manual extraction or app installer is needed.
3. Keep the app open while the bot is playing.

The EXE includes .NET and the Windows App SDK. No SDK, Python or Visual Studio is needed.
Microsoft Edge WebView2 Runtime is required if it is not already installed.
The app extracts its bundled files to a cache on first launch, so leave free disk space.

## Connect and play

1. Enter the game host's URL, such as `https://192.168.1.50`.
   Use the host's LAN address, not `localhost`, when playing from another computer.
2. Set up HTTPS trust as described below, then click **Connect / resume**.
3. If no saved team resumes, choose an available team in the lobby and click **Claim team**.
4. Set your strategy and click **Start bot**. It waits for the host to start the game.
5. Click **Stop bot** before using manual controls. Stopping keeps your saved session.

The app saves team tokens privately on this Windows account, separately for each server.
Use **Connect / resume** to restore your team; do not claim the same team again.
A new round needs a new claim in its lobby.

## LAN and self-signed HTTPS

For a self-signed game server, get the host's public CA or server certificate
through a trusted channel. Click **Browse certificate** and select its PEM or DER
file, such as Caddy's public `root.crt`. Never request or share a private key.
Keep **Skip TLS verification** unchecked. The certificate must match the URL's
hostname or IP address and be valid; ask the host to fix mismatches or expiry.

For publicly trusted HTTPS, leave the certificate field blank.
**Skip TLS verification (trusted LAN only)** is a fallback for a trusted LAN:
it disables server authentication and allows interception. Never use it on
untrusted networks. Prefer certificate trust; do not combine both modes.
HTTP also exposes team tokens without encryption. The host's firewall must allow the connection.

## If an action fails

An uncertain write stops the bot rather than repeating an action that may have
already happened. Complete **Refresh orchard & opponents** before restarting or
using manual controls. Cancelling a write does not prove the server undid it.

If a claim's round is unconfirmed, ask the host which exact round received it,
enter that round ID and explicitly confirm the host's verification, then refresh.
Use **Retry Save session** if saving fails. Do not claim again to recover a token.

## Development and limits

- [Build, test and developer preview](docs/BUILDING.md)
- [Server compatibility and validation limits](docs/COMPATIBILITY.md)
