"""Install the pinned local OCR and voice dependencies; never creates API keys."""
import argparse
import json
from pathlib import Path
import subprocess
import sys
import venv

ROOT = Path(__file__).resolve().parents[1]


def run(*args):
    subprocess.run([str(a) for a in args], cwd=ROOT, check=True)


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--backend", action="store_true", help="Install OCR and speech dependencies; designers do not need these")
    args = parser.parse_args()
    if sys.version_info[:2] != (3, 12):
        raise SystemExit("Use Python 3.12: python3.12 Tools/setup-team.py [--backend] (Windows: py -3.12 ...)")
    if not args.backend:
        print("Designers can work directly in Assets/DesignerWorkspace. Use --backend only to install OCR and speech dependencies.")
        return
    pin = json.loads((ROOT/"Config/recognition-source.json").read_text())
    source = ROOT/pin["directory"]
    source.parent.mkdir(exist_ok=True)
    if not source.exists():
        run("git", "clone", pin["repository"], source)
    if not (source/".git").exists():
        raise SystemExit("External/LEGO_OCR exists but is not a Git checkout; preserve your files and choose another location manually.")
    remote = subprocess.check_output(["git", "-C", str(source), "remote", "get-url", "origin"], text=True).strip()
    if remote != pin["repository"]:
        raise SystemExit("Recognition checkout has a different remote. No files changed.")
    dirty = subprocess.check_output(["git", "-C", str(source), "status", "--porcelain"], text=True)
    if dirty:
        raise SystemExit("Recognition checkout has local changes. Commit or preserve them before updating the pin.")
    run("git", "-C", source, "fetch", "origin", pin["commit"])
    run("git", "-C", source, "checkout", "--detach", pin["commit"])
    for folder, requirement in ((".ocr-venv", source/pin["requirements"]), (".venv", ROOT/"Python/requirements.txt")):
        env = ROOT/"Python"/folder
        venv.EnvBuilder(with_pip=True).create(env)
        python = env/("Scripts/python.exe" if sys.platform == "win32" else "bin/python")
        run(python, "-m", "pip", "install", "-r", requirement)
    print("Backend dependencies ready. Configure your local .env files; see README.md.")


if __name__ == "__main__":
    main()
