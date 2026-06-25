#!/usr/bin/env python3
"""
Cities: Skylines 2 — macOS / Wine Patcher
Tested: CrossOver 26 · Game v1.5.8f1
"""

import os
import sys
import subprocess
import shutil
import glob

SCRIPT_DIR = os.path.dirname(os.path.abspath(__file__))
PATCHER_PROJECT = os.path.join(SCRIPT_DIR, "cs2patcher")

# ──────────────────────────────────────────────────────────────────────────────
# Colours
# ──────────────────────────────────────────────────────────────────────────────

def c(text, code): return f"\033[{code}m{text}\033[0m" if sys.stdout.isatty() else text
def green(t):  return c(t, "32")
def yellow(t): return c(t, "33")
def cyan(t):   return c(t, "36")
def red(t):    return c(t, "31")
def bold(t):   return c(t, "1")


# ──────────────────────────────────────────────────────────────────────────────
# Game locator — CrossOver only (Steam native not supported)
# ──────────────────────────────────────────────────────────────────────────────

def find_game_installations():
    bottles_root = os.path.expanduser(
        "~/Library/Application Support/CrossOver/Bottles")
    results = []
    if not os.path.isdir(bottles_root):
        return results
    for bottle in os.listdir(bottles_root):
        drive_c = os.path.join(bottles_root, bottle, "drive_c")
        if not os.path.isdir(drive_c):
            continue
        for managed in _search_managed(drive_c, depth=6):
            results.append(managed)
    return results


def _search_managed(root, depth):
    if depth == 0:
        return
    try:
        entries = os.listdir(root)
    except PermissionError:
        return
    for entry in entries:
        full = os.path.join(root, entry)
        if not os.path.isdir(full):
            continue
        if entry == "Cities2_Data":
            candidate = os.path.join(full, "Managed")
            if _is_valid_managed(candidate):
                yield candidate
                continue
        yield from _search_managed(full, depth - 1)


def _is_valid_managed(path):
    return os.path.isdir(path) and os.path.isfile(
        os.path.join(path, "Colossal.IO.dll"))


# ──────────────────────────────────────────────────────────────────────────────
# dotnet detection & auto-install
# ──────────────────────────────────────────────────────────────────────────────

def find_dotnet():
    """Return path to dotnet CLI or None."""
    # Check PATH first
    p = shutil.which("dotnet")
    if p:
        return p
    # Common install locations
    for candidate in [
        "/usr/local/share/dotnet/dotnet",
        os.path.expanduser("~/.dotnet/dotnet"),
        "/opt/homebrew/opt/dotnet/bin/dotnet",
        "/opt/homebrew/share/dotnet/dotnet",
    ]:
        if os.path.isfile(candidate) and os.access(candidate, os.X_OK):
            return candidate
    return None


def ensure_dotnet():
    """Return dotnet path, installing via Homebrew if needed. Exits on failure."""
    dotnet = find_dotnet()
    if dotnet:
        return dotnet

    print(yellow("  dotnet CLI not found — needed for IL patching."))

    # Check Homebrew
    brew = shutil.which("brew")
    if not brew:
        print(red(
            "  Homebrew not found either.\n"
            "  Install Homebrew first: https://brew.sh\n"
            "  Then re-run this script, or install dotnet manually:\n"
            "  https://dotnet.microsoft.com/download"
        ))
        sys.exit(1)

    # Pin to .NET 9: the patcher targets net9.0, and the unversioned `dotnet-sdk`
    # cask now installs .NET 10, whose runtime won't run a net9.0 app by default.
    print(cyan("  Installing dotnet-sdk@9 via Homebrew (this may take a minute)..."))
    result = subprocess.run(
        [brew, "install", "--cask", "dotnet-sdk@9"],
        capture_output=False
    )
    if result.returncode != 0:
        print(red("  Homebrew install failed. Try manually: brew install --cask dotnet-sdk@9"))
        sys.exit(1)

    # Try again after install
    dotnet = find_dotnet()
    if not dotnet:
        # Homebrew installs to a versioned path; try to find it
        for p in glob.glob("/usr/local/share/dotnet/dotnet") + \
                 glob.glob("/opt/homebrew/opt/dotnet*/bin/dotnet") + \
                 glob.glob("/usr/local/share/dotnet-sdk/*/dotnet"):
            if os.path.isfile(p) and os.access(p, os.X_OK):
                dotnet = p
                break

    if not dotnet:
        print(red("  dotnet installed but not found. Open a new terminal and re-run the script."))
        sys.exit(1)

    print(green(f"  dotnet installed: {dotnet}\n"))
    return dotnet


# ──────────────────────────────────────────────────────────────────────────────
# C# patcher runner
# ──────────────────────────────────────────────────────────────────────────────

def run_patcher(dotnet, managed_dir, mode, apply):
    """Run the C# patcher, print results, return True if all ok."""
    cmd = [
        dotnet, "run",
        "--project", PATCHER_PROJECT,
        "--",
        managed_dir,
        mode,
    ]
    if apply:
        cmd.append("--apply")

    result = subprocess.run(cmd, capture_output=True, text=True)

    ok = True
    for line in result.stdout.strip().splitlines():
        parts = line.split(":", 2)
        status = parts[0] if parts else ""
        dll    = parts[1] if len(parts) > 1 else ""
        detail = parts[2] if len(parts) > 2 else ""

        if status == "OK":
            print(f"  {green('OK')}    {dll} — {detail}")
        elif status == "SKIP":
            print(f"  {yellow('SKIP')}  {dll} — {detail}")
        elif status == "DRY":
            print(f"  {cyan('DRY')}   {dll} — {detail}")
        elif status == "WARN":
            print(f"  {yellow('WARN')}  {detail}")
            ok = False
        else:
            print(f"  {line}")

    if result.stderr.strip():
        print(red(f"\n  Error output:\n{result.stderr.strip()}"))
        ok = False

    if result.returncode != 0:
        ok = False

    return ok


# ──────────────────────────────────────────────────────────────────────────────
# UI helpers
# ──────────────────────────────────────────────────────────────────────────────

def shorten(path):
    home = os.path.expanduser("~")
    return "~" + path[len(home):] if path.startswith(home) else path


def print_dll_status(managed_dir):
    dlls = ["Colossal.IO.dll", "Colossal.IO.AssetDatabase.dll", "PDX.SDK.dll"]
    print("Game files:")
    for dll in dlls:
        exists  = os.path.isfile(os.path.join(managed_dir, dll))
        patched = os.path.isfile(os.path.join(managed_dir, dll + ".bak"))
        mark  = green("✓") if exists else red("✗")
        extra = yellow("  (already patched)") if patched else ""
        print(f"  {mark} {dll:<44}{extra}")


def ask(prompt, valid):
    while True:
        val = input(prompt).strip().upper()
        if val in valid:
            return val
        print(f"  Please enter one of: {', '.join(sorted(valid))}")


# ──────────────────────────────────────────────────────────────────────────────
# Main
# ──────────────────────────────────────────────────────────────────────────────

def main():
    print(cyan(bold("=================================================")))
    print(cyan(bold("  Cities: Skylines 2 — macOS / Wine Patcher")))
    print(cyan(bold("  Tested: CrossOver 26 · Game v1.5.8f1")))
    print(cyan(bold("=================================================")))
    print()

    # ── Step 1: locate game ──────────────────────────────────────────────────
    # CLI override: python3 patch.py <managed-dir>
    cli_path = sys.argv[1] if len(sys.argv) > 1 else None
    if cli_path:
        cli_path = os.path.expanduser(cli_path)

    if cli_path and os.path.isdir(cli_path):
        print(f"Using path from argument:\n  {shorten(cli_path)}\n")
        managed_dir = cli_path
    else:
        print("Scanning for game installations...")
        found = find_game_installations()
        if not found:
            print("  No installation found automatically.\n")
            managed_dir = input("Enter path to Cities2_Data/Managed: ").strip()
            if managed_dir.startswith("~"):
                managed_dir = os.path.expanduser(managed_dir)
        elif len(found) == 1:
            print(f"  Found: {shorten(found[0])}\n")
            managed_dir = found[0]
        else:
            print()
            for i, p in enumerate(found, 1):
                print(f"  [{i}] {shorten(p)}")
            print("  [M] Enter path manually")
            print("  [Q] Quit\n")
            valid = {str(i) for i in range(1, len(found) + 1)} | {"M", "Q"}
            sel = ask("Select: ", valid)
            if sel == "Q":
                return
            if sel == "M":
                managed_dir = os.path.expanduser(input("Path: ").strip())
            else:
                managed_dir = found[int(sel) - 1]

    if not _is_valid_managed(managed_dir):
        print(red(f"\n  '{managed_dir}' does not look like a valid Managed directory."))
        sys.exit(1)

    print()
    print_dll_status(managed_dir)
    print()

    # ── Step 2: choose mode ──────────────────────────────────────────────────
    print("Choose patch mode:\n")
    print("  [1] Lightweight  — fixes game launch and asset loading")
    print("                     (Colossal.IO.dll + Colossal.IO.AssetDatabase.dll)\n")
    print("  [2] Full patch   — lightweight + Paradox Mods fix")
    print("                     (+ PDX.SDK.dll)  requires dotnet (auto-installed if needed)\n")
    print("  [Q] Quit\n")
    mode_sel = ask("Mode: ", {"1", "2", "Q"})
    if mode_sel == "Q":
        return

    full_patch = (mode_sel == "2")
    mode = "full" if full_patch else "lightweight"
    print()

    # ── Step 3: ensure dotnet ────────────────────────────────────────────────
    # dotnet is always needed to run the C# patcher (it's not a compiled binary)
    print("Checking for dotnet...")
    dotnet = ensure_dotnet()
    print(f"  {green('✓')} dotnet: {dotnet}\n")

    # ── Step 4: patch ────────────────────────────────────────────────────────
    print("Patching...\n")
    ok = run_patcher(dotnet, managed_dir, mode, apply=True)
    print()

    # ── Step 5: summary ──────────────────────────────────────────────────────
    if ok:
        print(green("All done!") + "\n")
        if full_patch:
            print("  Paradox Mods: launch the game and use the in-game mod browser.\n")
        else:
            print("  Game should now launch. Run again with Full patch to enable Paradox Mods.\n")
    else:
        print(yellow("Completed with warnings — check the output above.\n"))

    print("To restore original DLLs:")
    print(f'  cd "{managed_dir}"')
    for dll in ["Colossal.IO.dll", "Colossal.IO.AssetDatabase.dll"] + \
               (["PDX.SDK.dll"] if full_patch else []):
        bak = os.path.join(managed_dir, dll + ".bak")
        if os.path.isfile(bak):
            print(f'  cp "{dll}.bak" "{dll}"')

    print()


if __name__ == "__main__":
    try:
        main()
    except KeyboardInterrupt:
        print("\nCancelled.")
