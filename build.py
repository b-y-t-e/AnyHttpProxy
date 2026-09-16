#!/usr/bin/env python3
"""
Buduje samodzielne pliki .exe do katalogu publish/<rid>.

    python build.py                     # win-x64, obie aplikacje
    python build.py --rid linux-x64     # wydanie dla Linuksa
    python build.py --only host         # tylko wybrana aplikacja
    python build.py --clean             # najpierw czysci katalog wyjsciowy

Kazda aplikacja jest publikowana jako pojedynczy plik zawierajacy runtime .NET
i biblioteki natywne, wiec na maszynie docelowej nie trzeba niczego instalowac.
"""

from __future__ import annotations

import argparse
import shutil
import subprocess
import sys
import time
from pathlib import Path

ROOT = Path(__file__).resolve().parent

# klucz -> (projekt, nazwa pliku wynikowego bez rozszerzenia, opis)
APPS: dict[str, tuple[str, str, str]] = {
    "host": (
        "src/AnyHttpProxy.Host",
        "ahp-host",
        "komputer z uslugami: skanuje porty HTTP i udostepnia je",
    ),
    "gateway": (
        "src/AnyHttpProxy.Gateway",
        "ahp-gateway",
        "komputer korzystajacy: wystawia porty hostow lokalnie",
    ),
}


def publish(app: str, rid: str, out_dir: Path) -> Path:
    project, binary, _ = APPS[app]

    command = [
        "dotnet", "publish", str(ROOT / project),
        "-c", "Release",
        "-r", rid,
        "--self-contained",
        "-p:PublishSingleFile=true",
        "-o", str(out_dir),
        "--nologo",
    ]

    print(f"  {app:<8} {project}")
    result = subprocess.run(command, cwd=ROOT, capture_output=True, text=True, encoding="utf-8", errors="replace")

    if result.returncode != 0:
        # Kompilator sypie duzo tekstu - do konsoli trafiaja same bledy.
        errors = [line for line in (result.stdout or "").splitlines() if ": error" in line]
        print("\n".join(errors[:10] or [result.stdout, result.stderr]), file=sys.stderr)
        raise SystemExit(f"Publikacja {app} nie powiodla sie (kod {result.returncode}).")

    suffix = ".exe" if rid.startswith("win") else ""
    return out_dir / f"{binary}{suffix}"


def main() -> int:
    parser = argparse.ArgumentParser(description="Buduje samodzielne pliki wykonywalne AnyHttpProxy.")
    parser.add_argument("--rid", default="win-x64", help="runtime docelowy, np. win-x64, linux-x64 (domyslnie win-x64)")
    parser.add_argument("--only", choices=sorted(APPS), action="append",
                        help="zbuduj tylko wybrana aplikacje (mozna podac kilka razy)")
    parser.add_argument("--clean", action="store_true", help="wyczysc katalog wyjsciowy przed budowaniem")
    parser.add_argument("--out", help="katalog wyjsciowy (domyslnie publish/<rid>)")
    args = parser.parse_args()

    if shutil.which("dotnet") is None:
        raise SystemExit("Nie znaleziono 'dotnet' w PATH.")

    out_dir = Path(args.out).resolve() if args.out else ROOT / "publish" / args.rid
    apps = args.only or list(APPS)

    if args.clean and out_dir.exists():
        print(f"czyszczenie {out_dir}")
        shutil.rmtree(out_dir)

    out_dir.mkdir(parents=True, exist_ok=True)

    print(f"publikacja {args.rid} -> {out_dir}")
    started = time.monotonic()
    produced: list[Path] = []

    for app in apps:
        produced.append(publish(app, args.rid, out_dir))

    print(f"\ngotowe w {time.monotonic() - started:.0f} s:")
    for path in produced:
        if path.exists():
            print(f"  {path.name:<24} {path.stat().st_size / 1024 / 1024:6.1f} MB   {APPS[key_of(path)][2]}")
        else:
            # Nazwa pliku wynikowego bierze sie z csproj - jesli sie rozjedzie, lepiej to zobaczyc.
            print(f"  BRAK {path.name} - sprawdz AssemblyName w projekcie", file=sys.stderr)

    return 0


def key_of(path: Path) -> str:
    stem = path.stem
    return next(app for app, (_, binary, _) in APPS.items() if binary == stem)


if __name__ == "__main__":
    raise SystemExit(main())
