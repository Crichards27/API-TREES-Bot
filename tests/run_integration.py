"""Start an isolated upstream fixture, run the C# suite, then shut it down."""
import argparse
from pathlib import Path
import subprocess
import sys
import time
from urllib.request import urlopen

parser = argparse.ArgumentParser()
parser.add_argument("upstream", type=Path)
parser.add_argument("--dotnet", default="dotnet")
args = parser.parse_args()
root = Path(__file__).resolve().parent.parent
fixture = subprocess.Popen([sys.executable, str(root / "tests/upstream_fixture.py"), str(args.upstream.resolve())])
try:
    deadline = time.monotonic() + 20
    while time.monotonic() < deadline:
        if fixture.poll() is not None:
            raise RuntimeError("Upstream fixture exited during startup.")
        try:
            with urlopen("http://127.0.0.1:5099/health", timeout=1) as response:
                if response.status == 200:
                    break
        except OSError:
            time.sleep(0.1)
    else:
        raise RuntimeError("Upstream fixture did not become healthy.")
    result = subprocess.run([args.dotnet, "run", "--project", "tests/ApiTrees.Tests", "-c", "Release", "--no-build", "--", "--integration", "http://127.0.0.1:5099"], cwd=root)
finally:
    fixture.terminate()
    try:
        fixture.wait(timeout=5)
    except subprocess.TimeoutExpired:
        fixture.kill()
        fixture.wait()
sys.exit(result.returncode)
