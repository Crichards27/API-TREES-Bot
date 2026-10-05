#!/usr/bin/env python3
"""Remote API Trees player: Python 3.10+, standard library only.

QUICK START (run on YOUR playing computer)
  py play_trees.py --server https://HOSTNAME --ca-cert root.crt bot --team apple

Replace HOSTNAME with the hosting computer's reachable hostname or IP address.
Use `py` instead of `python` on Windows if needed. No pip install is required.
The script claims your team, saves its token locally, waits for the administrator
to start, then farms until you press Ctrl+C or the round ends. Resume with the
same server URL and `bot`. Only one bot/client should control a team at a time.
Use --help for all manual commands. Use `COMMAND --help` for its arguments.

CONNECT TO THE EXISTING GAME
Ask the host operator for its reachable game URL. For internal/self-signed TLS,
ask for the public root.crt certificate and use --ca-cert root.crt. For a trusted
public HTTPS certificate, omit --ca-cert. On a trusted LAN only, --insecure can
replace --ca-cert for testing (it disables certificate/hostname verification).
Do not use localhost: that points to YOUR computer. The repository normally
publishes HTTPS port 443. This script uses only the player's HTTP API.

DIFFERENT NETWORKS
If the computers are not on the same LAN, use a reachable VPN address/hostname
or ask the host operator for its public HTTPS URL. A private LAN IP does not
work across the internet by itself.
Changing a client URL cannot make a firewall-blocked host reachable.

AUTOMATIC STRATEGY
Every 2 seconds by default: prune poisoned branches on healthy trees, harvest
healthy fruit, plant empty plots, replace dead trees when affordable, and buy
up to four new plots per cycle (one per purchase). Default strategy reinvests
available resources without a tree limit or cash reserve, for growing fruit
production. --max-trees N limits expansion; 0 means unlimited. --reserve N
keeps N money unspent. Converts only fruit needed for seeds or land purchases.
Fruit and money have equal score value (10 per fruit), so other fruit is kept.
Use --max-trees 3 to stay on the original three plots. Use --farm-only to
harvest/prune without planting or purchases. No automatic attacks are made;
manual poison/molotov commands accept a target plot ID supplied by you.
Burning trees cannot be harvested; this script cannot guarantee fire recovery.
This is a practical farming strategy, not a guaranteed optimal winning bot.
If you know how many seconds remain when you start the bot, pass
--round-seconds N. It stops planting/replacing/buying during the final 45
seconds and continues harvesting/pruning to retain end-of-round points.
--bank-only harvests/prunes immediately without spending, for a nearly over
round. The administrator still controls when the game actually ends.
With no saved token, bot automatically claims --team (or the first available
team if --team is omitted). With a saved token, bot resumes that team.

TOKENS AND ROUNDS
Tokens are saved in ~/.api_trees/<server-hash>.json, with owner-only permissions
on POSIX. On Windows protect that folder with your user account's permissions.
Do not share those files. --state-file chooses another path; state is bound to
the server URL and game ID. API_TREES_TOKEN or --token-file may supply an existing
token. For an imported token with no saved game ID, the bot requires the round
to be active; the API's /me does not expose its game ID. Old imported tokens
cannot perform active-game actions. `claim` after a completed round replaces
the saved session; it refuses to overwrite a token for the same round.

ERRORS
TLS error: check hostname and --ca-cert; HTTP 404/non-JSON: check Caddy routing.
401: incorrect token. 409: team taken, round not active, or action conflict.
Connection failure: check the URL, port 443, firewall, LAN/VPN and host status.
The bot retries temporary READ failures up to five cycles. It never blindly
retries a write after a lost response, because that action might have succeeded.
Restart the bot to re-read server state; do not blindly repeat a failed claim,
purchase or attack. A lost claim response may require help from the host admin.
Growth requires the host's worker and Redis services to be running.

Built against I-Make-Stuff/API-Trees commit
b353df8aba3db86d911f8e42c50eb6117e77d992 (2026-10-05).
Source: https://github.com/I-Make-Stuff/API-Trees
"""

import argparse
import hashlib
from http.client import HTTPException as HTTPConnectionError
import json
import math
import os
from pathlib import Path
import socket
import ssl
import sys
import tempfile
import time
from urllib.error import HTTPError, URLError
from urllib.parse import urlsplit
from urllib.request import build_opener, HTTPRedirectHandler, HTTPSHandler, Request


class GameError(Exception):
    pass


class APIError(GameError):
    def __init__(self, code, detail):
        self.code = code
        super().__init__(f"HTTP {code}: {detail}")


class ConnectionFailure(GameError):
    def __init__(self, message, uncertain=False):
        self.uncertain = uncertain
        super().__init__(message)


class NoRedirect(HTTPRedirectHandler):
    def redirect_request(self, req, fp, code, msg, headers, newurl):
        return None  # Never forward a team bearer token to a redirected host.


class Client:
    def __init__(self, server, token=None, ca_cert=None, insecure=False, timeout=10):
        parsed = urlsplit(server)
        if parsed.scheme not in ("http", "https") or not parsed.netloc:
            raise GameError("--server must be a full URL, e.g. https://192.168.1.50")
        if parsed.username or parsed.password or parsed.query or parsed.fragment:
            raise GameError("Server URL cannot include credentials, a query, or a fragment.")
        self.server = server.rstrip("/")
        self.token = token
        self.timeout = timeout
        ctx = ssl.create_default_context(cafile=ca_cert)
        if insecure:
            ctx.check_hostname = False
            ctx.verify_mode = ssl.CERT_NONE
        self.opener = build_opener(NoRedirect(), HTTPSHandler(context=ctx))

    def call(self, method, path, body=None, auth=True):
        headers = {"Accept": "application/json", "User-Agent": "API-Trees-remote-player/1.0"}
        if auth:
            if not self.token:
                raise GameError("Claim a team first, or provide API_TREES_TOKEN / --token-file.")
            headers["Authorization"] = "Bearer " + self.token
        data = None if body is None else json.dumps(body).encode()
        if data is not None:
            headers["Content-Type"] = "application/json"
        request = Request(self.server + path, data=data, headers=headers, method=method)
        try:
            with self.opener.open(request, timeout=self.timeout) as response:
                raw = response.read()
        except HTTPError as exc:
            raw_error = exc.read().decode("utf-8", errors="replace")
            try:
                detail = json.loads(raw_error).get("detail", raw_error[:400])
            except (ValueError, AttributeError):
                detail = raw_error[:400]
            if method != "GET" and exc.code >= 500:
                raise ConnectionFailure(f"HTTP {exc.code}: {detail}. Action may have succeeded; inspect state before repeating.", True) from None
            raise APIError(exc.code, detail) from None
        except (URLError, OSError, socket.timeout, HTTPConnectionError) as exc:
            reason = str(getattr(exc, "reason", exc))
            if "CERTIFICATE_VERIFY_FAILED" in reason:
                reason += ". Check the server hostname and --ca-cert root.crt."
            write = method != "GET"
            suffix = " Action may have succeeded; inspect server state before repeating." if write else ""
            raise ConnectionFailure("Connection failed: " + reason + suffix, uncertain=write) from None
        try:
            return json.loads(raw)
        except (ValueError, UnicodeError):
            if method != "GET":
                raise ConnectionFailure("Non-JSON write response. Action may have succeeded; inspect state.", True)
            raise GameError("Non-JSON response. Check server URL and Caddy routing.") from None


def show(value):
    print(json.dumps(value, indent=2))


def load_state(path, server):
    if not path.exists():
        return {}
    try:
        state = json.loads(path.read_text(encoding="utf-8"))
    except (ValueError, OSError) as exc:
        raise GameError(f"Cannot read session file {path}: {exc}") from None
    if not isinstance(state, dict) or state.get("server") != server:
        raise GameError("Session file belongs to another server or is invalid. Use another --state-file.")
    return state


def save_state(path, state):
    path.parent.mkdir(parents=True, exist_ok=True)
    fd, temp = tempfile.mkstemp(prefix="session-", dir=path.parent)
    try:
        with os.fdopen(fd, "w", encoding="utf-8") as f:
            json.dump(state, f, indent=2)
            f.flush()
            os.fsync(f.fileno())
        os.replace(temp, path)
    finally:
        if os.path.exists(temp):
            os.unlink(temp)


def action(client, method, path, body=None):
    """Allow observed game-state races; never hide auth/server/transport errors."""
    try:
        return client.call(method, path, body)
    except APIError as exc:
        if exc.code == 409 and "only allowed while the game is active" in str(exc):
            raise GameError("This token's round is not active. Check teams/results; claim again for a new round.") from None
        if exc.code in (400, 404, 409):
            print(f"Action skipped; state changed: {exc}", file=sys.stderr)
            return None
        raise


def harvest_and_defend(client):
    trees = client.call("GET", "/trees")["trees"]
    harvested = 0
    healthy = [tree for tree in trees if tree["status"] == "healthy"]
    # Defend the entire orchard before spending time on harvest requests.
    for tree in healthy:
        for branch in tree["branches"]:
            if branch["status"] == "poisoned":
                action(client, "DELETE", f"/branches/{branch['id']}")
    branches = [branch for tree in healthy for branch in tree["branches"]
                if branch["status"] == "healthy" and branch["fruit"] > 0]
    # Near-capacity branches first, so they can resume producing sooner.
    for branch in sorted(branches, key=lambda b: b["fruit"], reverse=True):
        result = action(client, "PATCH", f"/branches/{branch['id']}", {"amount": branch["fruit"]})
        if result:
            harvested += result["harvested"]
    return harvested


def grow(client, max_trees, reserve, expansion_batch=4):
    inv = client.call("GET", "/me/inventory")
    plots = client.call("GET", "/me/plots")["plots"]
    trees = client.call("GET", "/trees")["trees"]
    # Replant a dead tree only when replacement resources exist.
    for tree in trees:
        if tree["status"] == "dead" and inv["seeds"] + 5 * inv["fruit"] >= 3:
            if inv["seeds"] < 3:
                if not action(client, "PATCH", "/inventory/fruit", {"amount": 1, "convert_to": "seeds"}):
                    return
            if action(client, "DELETE", f"/trees/{tree['id']}"):
                action(client, "PUT", f"/plots/{tree['plot_id']}/tree")
            inv = client.call("GET", "/me/inventory")
    plots = client.call("GET", "/me/plots")["plots"]
    occupied = sum(p["tree_id"] is not None for p in plots)
    empty = [p for p in plots if p["tree_id"] is None]
    if max_trees:
        empty = empty[:max(0, max_trees - occupied)]
    for plot in empty:
        if inv["seeds"] < 3:
            need = math.ceil((3 - inv["seeds"]) / 5)
            if inv["fruit"] < need:
                break
            if not action(client, "PATCH", "/inventory/fruit", {"amount": need, "convert_to": "seeds"}):
                return
        action(client, "PUT", f"/plots/{plot['id']}/tree")
        inv = client.call("GET", "/me/inventory")
    # Single-plot purchases: upstream currently charges for quantity N but
    # creates only ONE plot. Do not batch-buy plots.
    if (max_trees and len(plots) >= max_trees) or any(p["tree_id"] is None for p in client.call("GET", "/me/plots")["plots"]):
        return
    allowance = expansion_batch if not max_trees else min(expansion_batch, max(0, max_trees - len(plots)))
    for _ in range(allowance):
        inv = client.call("GET", "/me/inventory")
        seed_fruit = max(0, math.ceil((3 - inv["seeds"]) / 5))
        money_fruit = max(0, math.ceil((100 + reserve - inv["money"]) / 10))
        if inv["fruit"] < seed_fruit + money_fruit:
            return
        if seed_fruit and not action(client, "PATCH", "/inventory/fruit", {"amount": seed_fruit, "convert_to": "seeds"}):
            return
        if money_fruit and not action(client, "PATCH", "/inventory/fruit", {"amount": money_fruit, "convert_to": "money"}):
            return
        purchase = action(client, "POST", "/market/buy", {"item": "plot", "quantity": 1})
        if not purchase:
            return
        for plot_id in purchase["plot_ids"]:
            if not action(client, "PUT", f"/plots/{plot_id}/tree"):
                return


def run_bot(client, state, args):
    game_id = state.get("game_id")
    failures = 0
    last_status = None
    active_since = None
    was_banking = False
    print("Bot running. Ctrl+C stops it; your token is retained.")
    while True:
        try:
            current = client.call("GET", "/teams", auth=False)
            if game_id is None:
                if current["status"] != "active":
                    raise GameError("Imported token has no saved round ID. Start the round first, or claim using this script.")
                game_id = current["game_id"]
            if current["game_id"] != game_id or current["status"] == "finished":
                print(f"Round {game_id} ended. Final results:")
                show(client.call("GET", "/results", auth=False))
                return
            if current["status"] != last_status:
                print(f"Round {game_id}: {current['status']}" + ("; waiting for administrator." if current["status"] == "lobby" else ""))
                last_status = current["status"]
            if current["status"] == "active":
                if active_since is None:
                    active_since = time.monotonic()
                remaining = None if args.round_seconds is None else args.round_seconds - (time.monotonic() - active_since)
                banking = args.farm_only or (remaining is not None and remaining <= 45)
                if banking and not was_banking:
                    print("Banking points: continuing harvest and defense; investments stopped.")
                was_banking = banking
                harvested = harvest_and_defend(client)
                if not banking:
                    grow(client, args.max_trees, args.reserve, args.expansion_batch)
                inv = client.call("GET", "/me/inventory")
                trees = client.call("GET", "/trees")["trees"]
                # Upstream end_game counts all extant trees, including dead ones.
                score = inv["money"] + 10 * inv["fruit"] + 100 * len(trees)
                burning = sum(t["status"] == "burning" for t in trees)
                print(f"Harvest +{harvested} | money {inv['money']} | fruit {inv['fruit']} | seeds {inv['seeds']} | trees {len(trees)} | estimated score {score}" + (f" | BURNING {burning}" if burning else ""), flush=True)
            failures = 0
        except ConnectionFailure as exc:
            if exc.uncertain:
                raise
            failures += 1
            if failures >= 5:
                raise
            print(f"{exc} Retrying reads ({failures}/5).", file=sys.stderr)
        except APIError as exc:
            if exc.code < 500 and exc.code != 429:
                raise
            # HTTP error is explicit, unlike an ambiguous lost write response.
            failures += 1
            if failures >= 5:
                raise
            print(f"{exc}; refreshing state next cycle ({failures}/5).", file=sys.stderr)
        time.sleep(args.interval * (min(4, 2 ** failures) if failures else 1))


def positive(value):
    number = int(value)
    if number <= 0:
        raise argparse.ArgumentTypeError("must be a positive integer")
    return number


def nonnegative(value):
    number = int(value)
    if number < 0:
        raise argparse.ArgumentTypeError("must be at least zero")
    return number


def parser():
    p = argparse.ArgumentParser(description="Play API Trees from another computer. No extra packages required.", epilog="Global options go BEFORE the command. Use --guide for connection/setup instructions.")
    p.add_argument("--guide", action="store_true", help="print complete setup guide")
    p.add_argument("--server", default=os.environ.get("API_TREES_SERVER"), help="host URL, e.g. https://192.168.1.50; or API_TREES_SERVER")
    tls = p.add_mutually_exclusive_group()
    tls.add_argument("--ca-cert", help="path to hosting Caddy's public root.crt")
    tls.add_argument("--insecure", action="store_true", help="disable TLS verification (trusted-network testing only)")
    p.add_argument("--timeout", type=positive, default=10, help="request timeout seconds")
    p.add_argument("--state-file", type=Path, help="custom session file path")
    p.add_argument("--token-file", type=Path, help="file containing an existing bearer token (plain text)")
    sub = p.add_subparsers(dest="command")
    for name in ("health", "teams", "status", "plots", "trees", "market", "results"):
        sub.add_parser(name)
    claim = sub.add_parser("claim", help="claim an available team and save its token")
    claim.add_argument("team", choices=("apple", "orange", "cherry", "plum", "pear", "chestnut", "walnut", "peach"))
    bot = sub.add_parser("bot", help="automatically farm; wait for start and stop at round end")
    bot.add_argument("--team", choices=("apple", "orange", "cherry", "plum", "pear", "chestnut", "walnut", "peach"), help="automatically claim this team if no saved token; otherwise pick first available")
    bot.add_argument("--interval", type=positive, default=2)
    bot.add_argument("--max-trees", type=nonnegative, default=0, help="0 means unlimited expansion (default)")
    bot.add_argument("--reserve", type=nonnegative, default=0, help="money to keep unspent; default reinvests all available resources")
    bot.add_argument("--expansion-batch", type=positive, default=4, help="maximum new plots per cycle")
    bot.add_argument("--round-seconds", type=positive, help="expected seconds remaining from first observed active state; bank in last 45")
    bot.add_argument("--farm-only", "--bank-only", dest="farm_only", action="store_true")
    for name, key in (("plant", "plot_id"), ("prune", "branch_id"), ("delete-tree", "tree_id"), ("poison", "plot_id"), ("molotov", "plot_id")):
        sub.add_parser(name).add_argument(key, type=positive)
    harvest = sub.add_parser("harvest", help="harvest one branch, or all healthy branches with no arguments")
    harvest.add_argument("branch_id", nargs="?", type=positive)
    harvest.add_argument("amount", nargs="?", type=positive)
    buy = sub.add_parser("buy")
    buy.add_argument("item", choices=("plot", "poison", "molotov"))
    buy.add_argument("quantity", nargs="?", type=positive, default=1)
    convert = sub.add_parser("convert")
    convert.add_argument("convert_to", choices=("money", "seeds"))
    convert.add_argument("amount", type=positive)
    return p


def main(argv=None):
    p = parser()
    args = p.parse_args(argv)
    if args.guide:
        print(__doc__)
        return 0
    if not args.command or not args.server:
        p.error("Provide --server https://HOST and a command, or use --guide.")
    if args.command == "harvest" and args.branch_id is None and args.amount is not None:
        p.error("Provide branch_id before amount.")
    client = Client(args.server, ca_cert=args.ca_cert, insecure=args.insecure, timeout=args.timeout)
    if args.insecure:
        print("TLS verification disabled for this connection.", file=sys.stderr)
    if client.server.startswith("http://"):
        print("HTTP connection: tokens travel without encryption. Prefer HTTPS.", file=sys.stderr)
    path = args.state_file or Path.home() / ".api_trees" / (hashlib.sha256(client.server.encode()).hexdigest()[:20] + ".json")
    public = {"health": "/health", "teams": "/teams", "market": "/market", "results": "/results"}
    if args.command in public:
        show(client.call("GET", public[args.command], auth=False))
        return 0
    state = load_state(path, client.server)
    supplied = args.token_file.read_text(encoding="utf-8").strip() if args.token_file else os.environ.get("API_TREES_TOKEN", "").strip()
    client.token = supplied or state.get("token")
    if supplied and supplied != state.get("token"):
        state = {}  # Never attach an unrelated token to the saved round ID.
    if args.command == "claim" or (args.command == "bot" and not client.token):
        current = client.call("GET", "/teams", auth=False)
        if state.get("token") and state.get("game_id") == current["game_id"]:
            raise GameError("Already have a saved team for this round. Use status or bot.")
        # Check the destination before the irreversible claim to avoid losing
        # its only returned token to a simple file-permission error.
        path.parent.mkdir(parents=True, exist_ok=True)
        fd, probe = tempfile.mkstemp(dir=path.parent)
        os.close(fd)
        os.unlink(probe)
        available = [team["name"] for team in current["teams"] if team["available"]]
        if not args.team and not available:
            raise GameError("No teams available in this round.")
        chosen = args.team or available[0]
        result = client.call("POST", "/teams/select", {"team": chosen}, auth=False)
        state = {"server": client.server, "game_id": current["game_id"], "team": result["team"], "token": result["token"]}
        try:
            save_state(path, state)
        except OSError:
            # Last-resort recovery only: the claim response cannot be replayed.
            print("TOKEN SAVE FAILED. Privately save this token before closing:", file=sys.stderr)
            print(result["token"], file=sys.stderr)
            raise
        client.token = result["token"]
        print(f"Claimed {result['team']} in round {current['game_id']}. Token saved to {path}.")
        if args.command == "claim":
            return 0
    me = client.call("GET", "/me")
    if args.command == "status":
        show({"me": me, "current_round": client.call("GET", "/teams", auth=False), "saved_game_id": state.get("game_id"), "inventory": client.call("GET", "/me/inventory")})
    elif args.command in ("plots", "trees"):
        show(client.call("GET", "/me/plots" if args.command == "plots" else "/trees"))
    elif args.command == "bot":
        run_bot(client, state, args)
    elif args.command == "plant":
        show(client.call("PUT", f"/plots/{args.plot_id}/tree"))
    elif args.command == "harvest":
        if args.branch_id is None:
            # Manual harvest does not prune. Bot defense is separate.
            total = 0
            for tree in client.call("GET", "/trees")["trees"]:
                if tree["status"] == "healthy":
                    for branch in tree["branches"]:
                        if branch["status"] == "healthy" and branch["fruit"]:
                            result = action(client, "PATCH", f"/branches/{branch['id']}", {"amount": branch["fruit"]})
                            total += result["harvested"] if result else 0
            show({"harvested": total})
        else:
            amount = args.amount
            if amount is None:
                amount = client.call("GET", f"/branches/{args.branch_id}")["fruit"]
            show(client.call("PATCH", f"/branches/{args.branch_id}", {"amount": amount}) if amount else {"harvested": 0})
    elif args.command == "buy":
        if args.item == "plot" and args.quantity != 1:
            raise GameError("Buy one plot at a time: this upstream revision overcharges multi-plot purchases.")
        show(client.call("POST", "/market/buy", {"item": args.item, "quantity": args.quantity}))
    elif args.command == "convert":
        show(client.call("PATCH", "/inventory/fruit", {"amount": args.amount, "convert_to": args.convert_to}))
    elif args.command == "prune":
        show(client.call("DELETE", f"/branches/{args.branch_id}"))
    elif args.command == "delete-tree":
        show(client.call("DELETE", f"/trees/{args.tree_id}"))
    elif args.command in ("poison", "molotov"):
        show(client.call("POST", f"/attacks/{args.command}", {"plot_id": args.plot_id}))
    return 0


if __name__ == "__main__":
    try:
        raise SystemExit(main())
    except KeyboardInterrupt:
        print("\nStopped. Resume with the same server URL and bot.")
        raise SystemExit(0)
    except (GameError, OSError) as exc:
        print(f"Error: {exc}", file=sys.stderr)
        raise SystemExit(1)
