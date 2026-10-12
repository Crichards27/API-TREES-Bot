"""Run the UNMODIFIED upstream FastAPI endpoints with isolated test infrastructure.

Usage: python tests/upstream_fixture.py /path/to/API-Trees
SQLite replaces PostgreSQL and event scheduling is stubbed. No Redis worker runs.
Test-only seed/reset endpoints bind to loopback and require a fixture-only token.
Never run this harness against a production database or expose its port.
"""
import os
from pathlib import Path
import sys
import tempfile

import uvicorn

upstream = Path(sys.argv[1]).resolve()
temporary = tempfile.TemporaryDirectory(prefix="api-trees-fixture-")
os.environ["DATABASE_URL"] = "sqlite:///" + str(Path(temporary.name) / "game.db")
os.environ["ADMIN_TOKEN"] = "integration-only"
sys.path.insert(0, str(upstream / "api"))

from app import main, worker
from app.auth import require_admin
from app.database import Base, SessionLocal, engine
from app.game_service import create_game
from app.models import Branch, BranchStatus, Team, TreeStatus
from fastapi import Depends
from sqlalchemy import select

main.schedule_event = lambda *args, **kwargs: None
main.clear_events = lambda: None
main.has_scheduled_event = lambda *args, **kwargs: False
worker.schedule_event = lambda *args, **kwargs: None
Base.metadata.create_all(engine)


@main.app.post("/_test/reset", dependencies=[Depends(require_admin)])
def reset():
    Base.metadata.drop_all(engine)
    Base.metadata.create_all(engine)
    with SessionLocal() as db:
        create_game(db)
    return {"reset": True}


@main.app.post("/_test/seed", dependencies=[Depends(require_admin)])
def seed():
    with SessionLocal() as db:
        teams = db.scalars(select(Team).where(Team.claimed.is_(True))).all()
        for team in teams:
            tree = next(plot.tree for plot in team.plots if plot.tree)
            db.add(Branch(tree_id=tree.id, fruit=10))
            if team.name == "apple":
                db.add(Branch(tree_id=tree.id, status=BranchStatus.POISONED, fruit=0))
            team.inventory.fruit = 15
        db.commit()
    return {"seeded": True}


@main.app.post("/_test/kill-tree", dependencies=[Depends(require_admin)])
def kill_tree():
    with SessionLocal() as db:
        team = db.scalar(select(Team).where(Team.name == "apple"))
        next(plot.tree for plot in team.plots if plot.tree).status = TreeStatus.DEAD
        db.commit()
    return {"killed": True}


@main.app.post("/_test/new-round", dependencies=[Depends(require_admin)])
def new_round():
    with SessionLocal() as db:
        game = create_game(db)
        return {"game_id": game.id}


uvicorn.run(main.app, host="127.0.0.1", port=5099, log_level="warning")
