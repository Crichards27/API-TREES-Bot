# API Trees Player

A Python script that plays [API Trees](https://github.com/I-Make-Stuff/API-Trees) from a separate computer. It claims a team, waits for the game to start, harvests fruit, plants trees, buys land, and removes poisoned branches.

## Requirements

- Python 3.10 or newer. No extra packages needed.
- The running game server's reachable address.
- The host's public `root.crt` certificate if it uses internal TLS.

## Run

Save `play_trees.py` on your computer and open a terminal in that folder. On Windows:

```powershell
py play_trees.py --server https://HOST --ca-cert root.crt bot --team apple
```

Replace `HOST` with the hosting computer's address. Choose an available team, or omit `--team apple` to pick the first available one. If the server has a trusted HTTPS certificate, omit `--ca-cert root.crt`.

On macOS/Linux, use `python3` instead of `py`. For testing on a trusted LAN, `--insecure` can replace `--ca-cert root.crt`; this disables certificate verification.

The bot checks again two seconds after each cycle and reinvests available resources with no default tree limit. It stops when the round ends. Press **Ctrl+C** to stop early; run the same command to resume your saved team in that round.

## Options

Add these after `bot`:

| Option | What it does |
| --- | --- |
| `--round-seconds 600` | Assumes 600 seconds remain from when it first sees an active game; stops investing in the final 45 seconds. |
| `--bank-only` | Harvests and prunes without spending. |
| `--max-trees 20` | Limits expansion to 20 trees. |
| `--reserve 100` | Keeps 100 money unspent. |

Use `py play_trees.py --help` for manual commands, or `py play_trees.py --guide` for more details. For a new round, run the `claim apple` command with the same server/TLS options before starting `bot` again.

## Notes

Use the host's address, not `localhost`. Different networks need a reachable VPN address or public game URL. Team tokens are saved in your home folder under `.api_trees`; keep them private.

The script uses the same API in the original and starting-plot-fixed versions. The server must be running successfully. The bot farms and defends automatically; attacks are manual. Winning is not guaranteed.
