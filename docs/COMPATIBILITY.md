# Compatibility and validation

The integration workflow pins upstream API Trees to
**2acf9a0b3000a12e7e2e9b9f4ffa015b08a8c109** for reproducible checks.
This is not a claim that the revision is the latest upstream release or that all
server forks and future versions are compatible.

Source: https://github.com/I-Make-Stuff/API-Trees/tree/2acf9a0b3000a12e7e2e9b9f4ffa015b08a8c109

The player interface is .NET MAUI Blazor Hybrid. There is no Python or C#
command-line player. The optional loopback web host is a dashboard development
preview, not the persistent native player.

| Behavior | C# implementation |
| --- | --- |
| `GET /teams`, `POST /teams/select` | Claim only in lobby; retain team, token and round ID per server |
| `GET /me`, `/me/inventory`, `/me/plots`, `/trees` | Authenticated identity and typed orchard snapshots |
| Plot `row`, `column`, `connected_plots` | Inspect plots and tree IDs in tables with server coordinates |
| `GET /team/{name}` with `plot_ids` | Show opponent land IDs for manual targeting |
| `DELETE /branches/{id}` | Prune poisoned branches before harvesting |
| `PATCH /branches/{id}` with `amount` | Harvest healthy branches on healthy trees, fullest first |
| Burning/dead branches and trees | Do not attempt to harvest them |
| `PUT /plots/{id}/tree` | Spend three seeds per tree |
| `PATCH /inventory/fruit` | One fruit converts to five seeds or ten money |
| `GET /market`, `POST /market/buy` | Read plot price; buy one plot per request and plant it |
| `POST /attacks/poison`, `/attacks/molotov` | Manual controls; automation does not attack |
| `GET /results` | Show only results matching the saved round |
| `/teams` becomes 404 on game end | Fetch matching results and stop cleanly |
| A different current game ID | Stop the old bot session; require a fresh lobby claim |
| Final score | Money + 10 × inventory fruit + 100 × every extant tree |

## Upstream quirks handled by the client

1. `buy_market_item` deducts `quantity × price` but creates only **one plot**.
   All bot purchases use quantity 1; the dashboard rejects multi-plot requests.
2. `get_current_game` excludes finished games, so `/teams` returns 404 after
   `/admin/end`. This is handled as a lifecycle transition, not a fatal bot error.
3. The end-game scorer includes dead trees. The live score estimate follows it;
   the opponent public tree count separately excludes dead trees.
4. `/team/{name}` is not scoped to the current game. A server retaining multiple
   rounds may return old plot IDs. Opponent snapshots are displayed with a warning;
   target IDs should be checked with the host. The bot never automatically attacks.
5. The public API does not expose a round countdown. The dashboard's optional
   estimated seconds remaining starts when **this run** first observes activity.
   Reconnect/restart resets that estimate. The host still ends the actual round.
6. Team selection and `/me` do not return an authoritative round ID. A changed
   or unreadable round after selection is saved as an unconfirmed claim; the app
   retains its token and requires explicit host-verified round association before
   play. Authentication alone is not used to retag an old token as current.

## Test layers and limits

- `ApiTrees.Portable.slnx` builds the core, shared Blazor dashboard, development
  web host and executable regression suite without MAUI workloads.
- `tests/ApiTrees.Tests` exercises C# behavior without test-framework packages.
  Run the README commands and inspect their output for the current result; this
  document does not assert an unrerun historical check count or build pass.
- `tests/run_integration.py` launches `tests/upstream_fixture.py` and runs the C#
  suite against the pinned upstream FastAPI application's gameplay endpoints.
  These Python files are **test-only server infrastructure**, retained because
  the upstream game server is Python. They are not a player or a runtime
  dependency of the MAUI app.
- The upstream fixture uses isolated temporary SQLite storage, deterministic
  seed data and stubbed Redis scheduling. It does not validate PostgreSQL locking,
  Redis timing, the full worker or a live game deployment. It binds only to
  loopback; never expose its test administration endpoints to the LAN.
- That fixture uses loopback HTTP, so its pass does not establish certificate
  validation or real LAN connectivity. TLS/network checks are separate from
  upstream gameplay checks. A local TLS test is not a native-device LAN test.
- The C# suite also starts real loopback HTTPS servers with generated test
  certificates. It checks PEM/DER self-signed trust, private CA trust, default
  rejection, hostname and server-authentication usage validation, and explicit
  per-connection bypass. It also exercises dashboard/controller round updates,
  reconnect preservation, and refresh gates after uncertain writes.
- Native build, interactive dashboard behavior, secure storage, certificate
  picking and mobile lifecycle require the corresponding platform/device checks.
  The workflow is configured to publish the Windows app; inspect the actual run
  before claiming a successful native build or download.

For self-signed LAN HTTPS, prefer the host-provided public CA/self-signed
certificate (PEM or DER), with hostname and validity checks enabled. Explicit
verification bypass is only for a trusted LAN, disables server authentication,
and must never be combined with a supplied certificate. See the
[connection guide](../README.md#lan-and-self-signed-https).

To evaluate another upstream revision, check out that exact revision and rerun
the integration harness. Update the workflow pin and documentation only after
checking the results; server forks can require additional coverage.
