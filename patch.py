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
import filecmp
import termios
import tty
import select
import hashlib
import json
import re

SCRIPT_DIR = os.path.dirname(os.path.abspath(__file__))
PATCHER_PROJECT = os.path.join(SCRIPT_DIR, "cs2patcher")

# The DLLs the patcher targets. The C# patcher (FixRegistry) is the real authority;
# this mirror lets patch.py report status without invoking dotnet. There is one Patch
# action that applies everything — every fix repairs Wine breakage without touching
# gameplay, security or achievements, so a partial patch has no use.
DLLS = ["Colossal.IO.dll", "Colossal.IO.AssetDatabase.dll", "Game.dll", "PDX.SDK.dll"]

# Records the sha256 of each DLL as we patched it. A later game update replaces the
# DLL out from under its (now stale) .bak; without this record a plain byte-compare
# would mistake the fresh original for a patch and let Restore downgrade the install.
MANIFEST = ".cs2patch.json"

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

def run_patcher(dotnet, managed_dir, apply):
    """Run the C# patcher, print results, return True if all ok."""
    cmd = [
        dotnet, "run",
        "--project", PATCHER_PROJECT,
        "--",
        managed_dir,
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
# Launcher spawn fix (CrossOver 26.2+)
# ──────────────────────────────────────────────────────────────────────────────

CROSSOVER_WINE = "/Applications/CrossOver.app/Contents/SharedSupport/CrossOver/bin/wine"


def _wine_reg(bottle, *args):
    """Run `wine --bottle <bottle> reg <args>` and return the CompletedProcess.

    `bottle` is the bottle's absolute path — CXBottle's find_bottle() takes a
    path as-is for private bottles and only searches CX_BOTTLE_PATH when given a
    bare name, and that search does not know about every location we scan (see
    _bottle_roots). errors='replace' is not optional: wine's reg prints
    localized messages in the bottle's OEM codepage (CP866 for a Russian
    locale, say), which is not valid UTF-8 — strict decoding raises
    UnicodeDecodeError and takes the whole run down."""
    return subprocess.run(
        [CROSSOVER_WINE, "--bottle", bottle, "--wait-children", "reg", *args],
        capture_output=True, text=True, errors="replace", timeout=180)


def _bottle_and_game_dir(managed_dir):
    """For a CrossOver install, return (bottle_dir, windows_game_dir): the bottle
    is the directory holding drive_c, and windows_game_dir is the game root
    ('C:\\...\\Cities Skylines II') as seen inside it. The bottle is recognised
    by its own files, never by the name of the folder it sits in — bottles live
    wherever the user's BottleDir points (see _bottle_roots), not only under a
    directory called 'Bottles'. Returns (None, None) for non-CrossOver paths."""
    game_root = os.path.dirname(os.path.dirname(os.path.abspath(managed_dir)))
    parts = game_root.split(os.sep)
    try:
        i = parts.index("drive_c")
    except ValueError:
        return None, None
    if i == 0:
        return None, None
    bottle_dir = os.sep.join(parts[:i]) or os.sep
    if not any(os.path.exists(os.path.join(bottle_dir, marker))
               for marker in ("cxbottle.conf", "system.reg")):
        return None, None
    win_path = "C:\\" + "\\".join(parts[i + 1:])
    return bottle_dir, win_path


def _reg_query_user_path(bottle):
    """Return the bottle's HKCU\\Environment PATH value, '' when it is unset, or
    None when the key could not be read at all.

    Queries the whole key instead of `/v PATH`: for a missing value wine's reg
    exits non-zero with a localized "not found" message, which is
    indistinguishable from a real failure — and treating a failed read as "unset"
    would overwrite the very PATH we failed to read. The key itself always exists
    in a bottle (it holds TEMP/TMP), so rc != 0 means we could not talk to the
    bottle, while a readable key with no PATH row means genuinely unset."""
    result = _wine_reg(bottle, "query", "HKCU\\Environment")
    if result.returncode != 0:
        return None
    for line in result.stdout.splitlines():
        cols = line.split(None, 2)
        if len(cols) >= 2 and cols[0].upper() == "PATH" and cols[1].startswith("REG_"):
            return cols[2].strip() if len(cols) == 3 else ""   # PATH set but empty
    return ""


def ensure_launcher_path_fix(managed_dir):
    """Add the game directory to the bottle's user PATH.

    CrossOver 26.2's Wine dropped the working directory from the executable
    search that CreateProcess-by-name relies on (NeedCurrentDirectoryForExePath
    now says no). The Paradox launcher spawns the game as a bare 'Cities2.exe'
    with cwd set to the game directory, so on 26.2 every launch dies with
    'spawn Cities2.exe ENOENT' ("exit code null" in the launcher UI). Putting
    the game directory on PATH restores the lookup through the fallback the
    launcher's Node runtime still performs. Idempotent; appends, never
    replaces, an existing user PATH. Failures only warn — the DLL patch is
    already applied and remains useful without this.

    Why the bottle keeps working either way: wine APPENDS HKCU\\Environment's
    PATH to the machine PATH from HKLM instead of replacing it (measured in a
    scratch bottle: C:\\windows\\system32;... stays, the game dir lands at the
    end), so nothing here can strip the system directories from PATH."""
    bottle, game_dir = _bottle_and_game_dir(managed_dir)
    if not bottle or not game_dir:
        print(yellow("  SKIP  launcher PATH — not a CrossOver bottle, cannot apply"))
        return
    if not os.access(CROSSOVER_WINE, os.X_OK):
        print(yellow("  SKIP  launcher PATH — CrossOver not found at /Applications"))
        return
    try:
        current = _reg_query_user_path(bottle)
        if current is None:                   # never write a PATH we failed to read
            print(yellow("  WARN  launcher PATH — cannot read the bottle's user PATH, "
                         "leaving it alone"))
            return
        if game_dir.lower() in [p.strip().lower() for p in current.split(";") if p.strip()]:
            print(f"  {green('OK')}    launcher PATH — game dir already present")
            return
        new_value = f"{current};{game_dir}" if current else game_dir
        result = _wine_reg(bottle, "add", "HKCU\\Environment", "/v", "PATH",
                           "/t", "REG_EXPAND_SZ", "/d", new_value, "/f")
        if result.returncode == 0:
            print(f"  {green('OK')}    launcher PATH — game dir added (restart Steam in the bottle)")
        else:
            print(yellow("  WARN  launcher PATH — reg add failed: "
                         + (result.stderr.strip() or result.stdout.strip())))
    except (OSError, ValueError, subprocess.TimeoutExpired) as e:
        print(yellow(f"  WARN  launcher PATH — {e}"))


# ──────────────────────────────────────────────────────────────────────────────
# UI helpers
# ──────────────────────────────────────────────────────────────────────────────

def shorten(path):
    home = os.path.expanduser("~")
    return "~" + path[len(home):] if path.startswith(home) else path


def _sha(path):
    h = hashlib.sha256()
    with open(path, "rb") as f:
        for chunk in iter(lambda: f.read(1 << 20), b""):
            h.update(chunk)
    return h.hexdigest()


def load_manifest(managed_dir):
    try:
        with open(os.path.join(managed_dir, MANIFEST)) as f:
            data = json.load(f)
        return data if isinstance(data, dict) else {}
    except (OSError, ValueError):
        return {}


def save_manifest(managed_dir, data):
    path = os.path.join(managed_dir, MANIFEST)
    if data:
        with open(path, "w") as f:
            json.dump(data, f)
    elif os.path.isfile(path):
        os.remove(path)


def dll_state(managed_dir, dll):
    """Return 'missing', 'patched', or 'original'.

    Prefer the manifest (sha of the exact bytes we wrote): if it lists this DLL and
    the sha still matches, it's our patch; if it no longer matches, the game updated
    the file, so it's an unpatched original again. Fall back to a byte-compare against
    the .bak for installs patched before the manifest existed."""
    path = os.path.join(managed_dir, dll)
    if not os.path.isfile(path):
        return "missing"
    manifest = load_manifest(managed_dir)
    if dll in manifest:
        return "patched" if _sha(path) == manifest[dll] else "original"
    bak = path + ".bak"
    if os.path.isfile(bak) and not filecmp.cmp(path, bak, shallow=False):
        return "patched"
    return "original"


def record_patched(managed_dir):
    """After an apply, record the sha of each DLL actually patched (differs from its
    .bak); drop entries for targets left unpatched. Lets status/restore tell our patch
    apart from a later game update."""
    manifest = load_manifest(managed_dir)
    for dll in DLLS:
        path = os.path.join(managed_dir, dll)
        bak = path + ".bak"
        if (os.path.isfile(path) and os.path.isfile(bak)
                and not filecmp.cmp(path, bak, shallow=False)):
            manifest[dll] = _sha(path)
        else:
            manifest.pop(dll, None)
    save_manifest(managed_dir, manifest)


def restore_dlls(managed_dir):
    """Copy each *.bak back over its DLL. Returns count restored. Skips a DLL whose
    current bytes no longer match the patch we recorded — that means the game updated
    it, and restoring the stale backup would downgrade the install."""
    manifest = load_manifest(managed_dir)
    restored = 0
    for dll in DLLS:
        path = os.path.join(managed_dir, dll)
        bak = path + ".bak"
        if not os.path.isfile(bak):
            continue
        if dll in manifest and (not os.path.isfile(path) or _sha(path) != manifest[dll]):
            print(yellow(f"  skipped   {dll} — changed since patch (game update?), not restoring"))
            continue
        shutil.copy2(bak, path)
        print(green(f"  restored  {dll}"))
        manifest.pop(dll, None)
        restored += 1
    save_manifest(managed_dir, manifest)
    if restored == 0:
        print(yellow("  No backups found — nothing to restore."))
    return restored


# ──────────────────────────────────────────────────────────────────────────────
# Paradox Launcher render fix (SwiftShader) — Steam launch options
# ──────────────────────────────────────────────────────────────────────────────

# Paradox Launcher v2.2026.8+ (self-updates silently mid-session) cannot create ANY
# GPU context under Wine: D3D11 fails, software D3D (WARP) fails, every EGL path
# fails, so its window never paints and it gives up after ~10 s — the game "won't
# launch" even though nothing else changed. Chromium ships a pure-CPU renderer
# (SwiftShader, vk_swiftshader.dll) that works fine under Wine, but never falls back
# to it on its own; it must be requested on the command line. Steam's per-app Launch
# Options are forwarded verbatim through the whole chain (dowser.exe → bootstrapper
# → Paradox Launcher; the launcher passes the extras on to Cities2.exe, which
# ignores unknown args), so writing them there fixes the launcher for good — no
# Paradox files touched, survives launcher self-updates.
LAUNCHER_RENDER_FLAGS = "--use-angle=swiftshader --enable-unsafe-swiftshader"
CS2_APP_ID = "949230"


def _vdf_find_block(text, name, start=0):
    """Return (open_idx, close_idx) of the { } block following "name" after start,
    or None. Key match is case-insensitive (VDF keys are; real files mix "apps"/
    "Apps"). Walks the braces quote-aware so braces inside quoted values can't
    unbalance the scan."""
    m = re.compile(r'"%s"\s*\{' % re.escape(name), re.IGNORECASE).search(text, start)
    if not m:
        return None
    i = m.end() - 1                     # index of the opening '{'
    depth, in_quote, escaped = 0, False, False
    for j in range(i, len(text)):
        ch = text[j]
        if escaped:
            escaped = False
        elif ch == "\\" and in_quote:
            escaped = True
        elif ch == '"':
            in_quote = not in_quote
        elif not in_quote:
            if ch == "{":
                depth += 1
            elif ch == "}":
                depth -= 1
                if depth == 0:
                    return (i, j)
    return None


def _steam_running():
    out = subprocess.run(["pgrep", "-fl", "steam.exe"],
                         capture_output=True, text=True).stdout
    return any("steam.exe" in line for line in out.splitlines())


def ensure_launcher_render_fix(managed_dir):
    """Add the SwiftShader flags to CS2's Steam launch options in every Steam user's
    localconfig.vdf inside the bottle. Idempotent; preserves options the user already
    set; backs the file up once; warn-only on failure. Steam rewrites localconfig.vdf
    from memory on exit, so the edit only sticks while Steam is closed — if Steam is
    running we skip and say so rather than write an edit that would be lost."""
    print("Ensuring Paradox Launcher render fix (SwiftShader launch options)...")
    try:
        if _steam_running():
            print(yellow("  ⚠ Steam is running — close Steam and re-run ./patch.py to apply"))
            print(yellow(f"    (or set Launch Options yourself: %command% {LAUNCHER_RENDER_FLAGS})"))
            return False

        # managed_dir = <steam>/steamapps/common/Cities Skylines II/Cities2_Data/Managed
        steam_root = os.path.normpath(os.path.join(managed_dir, *[".."] * 5))
        vdfs = glob.glob(os.path.join(steam_root, "userdata", "*", "config", "localconfig.vdf"))
        if not vdfs:
            print(yellow("  ⚠ No Steam userdata found next to this install (game in a"))
            print(yellow("    secondary Steam library?) — set Launch Options manually in"))
            print(yellow(f"    Steam → CS2 → Properties: %command% {LAUNCHER_RENDER_FLAGS}"))
            return False

        changed_any = False
        for vdf in vdfs:
            account = os.path.basename(os.path.dirname(os.path.dirname(vdf)))
            with open(vdf, encoding="utf-8", errors="surrogateescape") as f:
                text = f.read()

            # The app's config block lives at Software/Valve/Steam/apps/949230; the
            # bare string '"949230"  "<hex>"' elsewhere is an app ticket, so anchor
            # on an "apps" OBJECT and search only inside it. Other sections can carry
            # a same-named key, so scan every "apps" block until one holds the app.
            apps = _vdf_find_block(text, "apps")
            if not apps:
                print(yellow(f"  ⚠ account {account}: no apps section, skipped"))
                continue
            app_span = None
            while apps:
                app = _vdf_find_block(text[apps[0]:apps[1]], CS2_APP_ID)
                if app:
                    app_span = (apps[0] + app[0], apps[0] + app[1])
                    break
                apps = _vdf_find_block(text, "apps", apps[1] + 1)
            if not app_span:
                print(yellow(f"  ⚠ account {account}: CS2 ({CS2_APP_ID}) not configured, skipped"))
                continue
            a_open, a_close = app_span

            block = text[a_open:a_close]
            # Value match must skip escaped characters (\" inside the value) — same
            # rule the brace walker above follows — or a quote inside the user's
            # options would truncate the match and corrupt the rewrite.
            m = re.search(r'("LaunchOptions"\s*")((?:\\.|[^"\\])*)(")', block, re.IGNORECASE)
            if m and LAUNCHER_RENDER_FLAGS in m.group(2):
                print(green(f"  ✓ account {account}: launch options already set"))
                continue

            if m:  # append to whatever the user already has (Steam keeps the key even when blank)
                existing = m.group(2).strip()
                new_value = (existing + " " + LAUNCHER_RENDER_FLAGS) if existing \
                    else f"%command% {LAUNCHER_RENDER_FLAGS}"
                new_block = block[:m.start(2)] + new_value + block[m.end(2):]
            else:  # insert a fresh key right after the block's opening brace
                indent_m = re.search(r'\n([ \t]*)"', block)
                indent = indent_m.group(1) if indent_m else "\t"
                new_block = (block[:1]
                             + f'\n{indent}"LaunchOptions"\t\t"%command% {LAUNCHER_RENDER_FLAGS}"'
                             + block[1:])

            bak = vdf + ".cs2patch.bak"
            if not os.path.exists(bak):
                shutil.copy2(vdf, bak)
            with open(vdf, "w", encoding="utf-8", errors="surrogateescape") as f:
                f.write(text[:a_open] + new_block + text[a_close:])
            print(green(f"  ✓ account {account}: launch options written"))
            changed_any = True

        if changed_any:
            print(green("  ✓ Paradox Launcher will use software rendering (SwiftShader)"))
        return True
    except Exception as e:                                  # never fail the patch flow
        print(yellow(f"  ⚠ Launcher render fix failed: {e}"))
        print(yellow(f"    Set it manually in Steam → CS2 → Properties → Launch Options:"))
        print(yellow(f"    %command% {LAUNCHER_RENDER_FLAGS}"))
        return False


def menu(title, options):
    """Arrow-key menu: ↑/↓ or j/k to move, Enter to select, q to pick the last
    option. Returns the selected index. Callers must make the last option the
    cancel/quit choice — q and EOF both resolve to it. Falls back to a numbered
    prompt when stdin is not a TTY (pipes / CI) so the tool stays scriptable."""
    print(title)

    if not sys.stdin.isatty():
        for i, opt in enumerate(options, 1):
            print(f"  {i}) {opt}")
        raw = sys.stdin.readline().strip().lower()
        try:
            n = int(raw)
            if 1 <= n <= len(options):
                return n - 1
        except ValueError:
            pass
        return len(options) - 1     # q / EOF / blank / out-of-range → cancel (last)

    sel = 0
    # Keep every option on exactly one row; a wrapped label would desync the
    # cursor-up count below and smear the menu during redraws.
    width = shutil.get_terminal_size((80, 24)).columns

    def render(first):
        if not first:
            sys.stdout.write(f"\033[{len(options)}A")
        for i, opt in enumerate(options):
            text = opt[:max(1, width - 6)]
            if i == sel:
                sys.stdout.write(f"\r  \033[7m ❯ {text} \033[0m\033[K\n")
            else:
                sys.stdout.write(f"\r    {text}\033[K\n")
        sys.stdout.flush()

    fd = sys.stdin.fileno()
    old = termios.tcgetattr(fd)
    try:
        tty.setraw(fd)
        render(True)
        while True:
            # Read raw bytes straight from the fd — sys.stdin's buffered text
            # layer does not reliably deliver escape sequences in raw mode.
            ch = os.read(fd, 1)
            if ch == b"\x1b":
                # An arrow key follows ESC with more bytes immediately; a lone ESC
                # (or Alt+key) does not. Poll for the rest instead of a blocking
                # os.read(fd, 2), which would hang forever on a bare ESC.
                seq = b""
                while len(seq) < 2 and select.select([fd], [], [], 0.02)[0]:
                    seq += os.read(fd, 1)
                if seq in (b"[A", b"OA"):
                    sel = (sel - 1) % len(options)
                elif seq in (b"[B", b"OB"):
                    sel = (sel + 1) % len(options)
                elif seq == b"":            # bare ESC = cancel
                    sel = len(options) - 1
                    break
            elif ch in (b"k", b"K"):
                sel = (sel - 1) % len(options)
            elif ch in (b"j", b"J"):
                sel = (sel + 1) % len(options)
            elif ch in (b"\r", b"\n"):
                break
            elif ch in (b"q", b"Q"):
                sel = len(options) - 1
                break
            elif ch == b"\x03":  # Ctrl-C
                raise KeyboardInterrupt
            render(False)
    finally:
        termios.tcsetattr(fd, termios.TCSADRAIN, old)
    return sel


# ──────────────────────────────────────────────────────────────────────────────
# Main
# ──────────────────────────────────────────────────────────────────────────────

def main():
    print(cyan(bold("=================================================")))
    print(cyan(bold("  Cities: Skylines 2 — macOS / Wine Patcher")))
    print(cyan(bold("  Tested: CrossOver 26 · Game v1.5.8f1–v1.6.0f1")))
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
            options = [shorten(p) for p in found] + ["Enter path manually", "Quit"]
            idx = menu("Multiple installations found:", options)
            if idx == len(found) + 1:        # Quit
                return
            if idx == len(found):            # Enter path manually
                managed_dir = os.path.expanduser(input("Path: ").strip())
            else:
                managed_dir = found[idx]

    if not _is_valid_managed(managed_dir):
        print(red(f"\n  '{managed_dir}' does not look like a valid Managed directory."))
        sys.exit(1)

    # ── Current patch status ─────────────────────────────────────────────────
    state = {d: dll_state(managed_dir, d) for d in DLLS}
    all_patched = all(state[d] == "patched" for d in DLLS)
    any_patched = any(v == "patched" for v in state.values())
    missing = [d for d in DLLS if state[d] == "missing"]

    if all_patched:
        print("  " + green(bold("● Already patched")))
    elif any_patched:
        print("  " + yellow(bold("● Partially patched — re-patch to complete")))
    else:
        print("  " + cyan("○ Not patched yet"))
    if missing:
        print("  " + yellow("⚠ Missing DLL(s): " + ", ".join(missing)))
    print()

    # ── Step 2: choose action ────────────────────────────────────────────────
    # "Re-Patch" appears when everything is already applied; the patcher is
    # idempotent, so re-patching is the safe thing to do after a game update.
    patch_verb = "Re-Patch" if all_patched else "Patch"
    actions = [
        ("patch", f"{patch_verb} — apply all fixes (launch, assets, pause menu, snapping, Paradox Mods)"),
    ]
    if any_patched:                       # Restore only when there's something to undo
        actions.append(("restore", "Restore original files"))
    actions.append(("quit", "Quit"))

    choice = menu("What would you like to do?   (↑/↓ to move, Enter to select)",
                  [label for _, label in actions])
    action = actions[choice][0]

    if action == "quit":
        print("\nCancelled.")
        return
    if action == "restore":
        print()
        restore_dlls(managed_dir)
        print()
        return

    # ── Step 3: ensure dotnet ────────────────────────────────────────────────
    # dotnet is always needed to run the C# patcher (it's not a compiled binary)
    print("\nChecking for dotnet...")
    dotnet = ensure_dotnet()
    print(f"  {green('✓')} dotnet: {dotnet}\n")

    # ── Step 4: preview (dry-run, writes nothing) ────────────────────────────
    print("Step 1 of 2 — Preview. Nothing is written yet.")
    print("─" * 60)
    preview_ok = run_patcher(dotnet, managed_dir, apply=False)
    print("─" * 60)
    if not preview_ok:
        print(yellow("  Preview reported warnings — review the output above before applying."))
    print()

    if menu("Apply these changes now?", ["Yes, apply", "No, cancel"]) != 0:
        print("\nNot applied. Nothing was changed.")
        return

    # ── Step 5: apply ────────────────────────────────────────────────────────
    print("\nStep 2 of 2 — Applying. Originals are backed up to *.bak.")
    print("─" * 60)
    ok = run_patcher(dotnet, managed_dir, apply=True)
    print("─" * 60 + "\n")

    # ── Step 6: verify outcome & summarise ───────────────────────────────────
    # Always snapshot what actually patched (record_patched only records DLLs whose bytes
    # differ from their .bak, and drops the rest). Gating this on `ok` meant one DLL's WARN
    # discarded the manifest entries for the DLLs that DID patch, dropping their game-update
    # downgrade protection — so record unconditionally after an apply.
    record_patched(managed_dir)               # snapshot what we patched (see MANIFEST)
    print()
    ensure_launcher_render_fix(managed_dir)   # Paradox Launcher 2026.8+ window fix
    print()
    ensure_launcher_path_fix(managed_dir)     # CrossOver 26.2+ launcher spawn fix
    print()
    if ok:
        # Trust the result, not the patcher's report: a game update can change method
        # bodies so patterns silently stop matching, leaving a DLL unpatched while the
        # patcher still reports SKIP / "already patched". Verify the bytes changed.
        unpatched = [d for d in DLLS if dll_state(managed_dir, d) != "patched"]
        if unpatched:
            print(yellow("⚠ Not fully patched. These DLLs did not change — this game"))
            print(yellow("  version may be unsupported (patch patterns didn't match):"))
            print(yellow("    " + ", ".join(unpatched)))
            print(yellow("  The game will likely still misbehave; please report the game version.\n"))
        else:
            print(green("All done!") + "\n")
            print("  Paradox Mods: launch the game and use the in-game mod browser.\n")
    else:
        print(yellow("Completed with warnings — check the output above.\n"))

    print("To undo later: run ./patch.py again and choose Restore.\n")


if __name__ == "__main__":
    try:
        main()
    except KeyboardInterrupt:
        print("\nCancelled.")
