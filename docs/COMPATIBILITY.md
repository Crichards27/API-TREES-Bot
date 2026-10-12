# Compatibility and validation

The C# client targets API Trees **0.2.0**, checked against the upstream `main`
commit **2acf9a0b3000a12e7e2e9b9f4ffa015b08a8c109**, dated 2026-10-06
(latest upstream HEAD inspected on 2026-10-11).

Source: https://github.com/I-Make-Stuff/API-Trees/tree/2acf9a0b3000a12e7e2e9b9f4ffa015b08a8c109

| Behavior | C# implementation |
| --- | --- |
| `GET /teams`, `POST /teams/select` | Claim only in lobby; retain team, token and round ID per server |
| `GET /me`, `/me/inventory`, `/me/plots`, `/trees` | Authenticated identity and typed orchard snapshots |
| Plot `row`, `column`, `connected_plots` | Draw the current four-column grid using server coordinates |
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
   All bot purchases use quantity 1; both GUIs/CLI reject multi-plot requests.
2. `get_current_game` excludes finished games, so `/teams` returns 404 after
   `/admin/end`. This is handled as a lifecycle transition, not a fatal bot error.
3. The end-game scorer includes dead trees. The live score estimate follows it;
   the opponent public tree count separately excludes dead trees.
4. `/team/{name}` is not scoped to the current game. A server retaining multiple
   rounds may return old plot IDs. Opponent snapshots are displayed with a warning;
   target IDs should be checked with the host. The bot never automatically attacks.
5. The public API does not expose a round countdown. Optional `round-seconds`
   means estimated seconds remaining when **this run** first observes activity.
   Reconnect/restart resets that estimate. The host still ends the actual round.

## Tests

The portable solution is built in Release with zero compiler warnings.
The complete suite passed **42 checks**, including the GUI controller's session
recovery and lifecycle checks, on 2026-10-11.
`tests/ApiTrees.Tests` is an executable regression suite, requiring no test-framework
packages. `tests/run_integration.py` starts the actual upstream FastAPI app and
exercises its unchanged gameplay endpoints through the C# HTTP client.

The integration harness uses an isolated temporary SQLite database instead of
PostgreSQL and stubs Redis scheduling. Branches/fruit are seeded deterministically.
It validates payloads, responses and game transitions, **not** PostgreSQL locking,
Redis timing, the full worker or real-world TLS/network behavior. No live game
server was supplied. Native Windows/macOS device execution needs the corresponding
OS; the included CI workflow publishes the Windows app when pushed to GitHub.

A cross-build here restored the MAUI/Windows dependencies and compiled the
shared C# code, then stopped at the Windows-only WinUI `XamlCompiler.exe` with
an execution-format error on Linux. That does not establish a native build pass.
The shared dashboard's initial server render and CSS are separately checked;
headless browser execution was blocked by this environment's socket restrictions,
so device/browser interaction and native secure storage remain platform checks.

The workflow pins the tested upstream revision for reproducible checks. To check a
future game update, clone/pull that revision and rerun the integration script;
update the pin and this document after it passes.
