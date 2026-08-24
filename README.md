# Cities: Skylines 2 — macOS / Wine Patcher

Fixes crashes and enables Paradox Mods for **Cities: Skylines 2** running under CrossOver on macOS.

Tested: **CrossOver 26 · Game v1.5.8f1–v1.6.0f1 · Apple Silicon (M3 Pro → M5 Max)**

> **Elevated networks snapping to the ground?** An Apple Silicon bug (Rosetta miscompiles Unity's
> Burst SIMD code and drops the height value) makes raised roads, bridges and pipes wrongly snap
> down onto whatever is below them. **This patcher fixes it** — without the FPS regression
> of the standalone [icetear/cs2-net-snap-fix](https://github.com/icetear/cs2-net-snap-fix) patch
> (see [docs/technical.md](docs/technical.md)).

---

## How to use

Open Terminal, paste this, press Enter:

```bash
git clone https://github.com/alien-agent/cs2-macos-patcher && cd cs2-macos-patcher && ./patch.py
```

`patch.py` is a single **guided, interactive** tool. It walks you through:

1. **Finds your game** automatically across all CrossOver bottles, and shows whether it's already patched
2. **Previews the change first** (a dry-run that writes nothing), then asks you to confirm
3. **Applies all fixes** — launch, assets, pause menu, network snapping, Paradox Mods — and backs up the originals to `*.bak`
4. Installs dotnet via Homebrew automatically if needed

> **No dotnet?** No problem — the patcher installs it for you. You only
> need [Homebrew](https://brew.sh).
>
> If you already have **.NET 10** but the patcher complains the `9.0.0` runtime is missing, install
> the matching SDK: `brew install --cask dotnet-sdk@9`.

### After a game update

Re-run `./patch.py` and Patch again. The preview and the patcher both detect
already-patched files and skip them, then apply any new fixes to updated DLLs — it's always safe to
re-run.

### Can't find the game automatically?

Pass the Managed folder directly:

```bash
./patch.py "/path/to/Cities2_Data/Managed"
```

The Managed folder is typically inside your CrossOver bottle:

```
~/Library/Application Support/CrossOver/Bottles/<bottle-name>/drive_c/
  Program Files (x86)/.../Cities2_Data/Managed
```

### Restoring original DLLs

Run `./patch.py` and choose **Restore original files** — it copies every `*.bak` back over its DLL.

Prefer to do it by hand? The backups are plain copies:

```bash
cd "<path-to>/Cities2_Data/Managed"
cp Colossal.IO.dll.bak Colossal.IO.dll
cp Colossal.IO.AssetDatabase.dll.bak Colossal.IO.AssetDatabase.dll
cp Game.dll.bak Game.dll
cp PDX.SDK.dll.bak PDX.SDK.dll
cp Backtrace.Unity.dll.bak Backtrace.Unity.dll
```

---

## CrossOver settings for best performance

My personal recommendation for best graphic/performance on Crossover 26:

| Setting                       | Value                | Notes                                                                                                                                                                                               |
|-------------------------------|----------------------|-----------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------|
| **Graphics**                  | **D3DMetal**         | CS2 uses DirectX 12. D3DMetal (from Apple Game Porting Toolkit) is the only translator that supports DX12 properly. DXVK and wined3d are slower or broken for DX12. DXMT is DX11-only — do not use. |
| **Synchronization**           | **MSync**            | Mach semaphore-based sync. Confirmed better than ESync for CS2.                                                                                                                                     |
| **DLSS (powered by MetalFX)** | **Enabled — but disable if CS2 crashes**          | New in CrossOver 26; needs DLSS enabled in-game too; big FPS gain on Apple Silicon. ⚠️ **Can cause native D3DMetal crashes a few minutes into play** on some setups (high resolution + heavy/asset-modded cities) — the game closes to desktop with no error dialog and Steam writes an `assert_cities2.exe_*.dmp`. If that happens, turn this **Off** (it is the MetalFX control); a long clean session with no new dumps confirms the fix.                                                                                       |
| **High Resolution Mode**      | **On (with a capped in-game resolution)** | Disables pixel doubling so the UI stays crisp on Retina displays. ⚠️ It also lets CS2 render at the display's **native** resolution — on 5K/6K screens that is a ~6K render that tanks performance and aggravates the MetalFX crash above. Keep it **On only if** you cap the in-game **Resolution** to 1080p/1440p (see the [in-game settings](#in-game-graphics-settings) below); otherwise set it **Off** on high-res displays.                                                                                                                                     |
| **Windows version**           | **Windows 10 or 11** | Do not use XP or 7 — they break .NET runtime features the game relies on.                                                                                                                           |
| **AVX**                       | **Enabled**          | CrossOver 25+ exposes AVX to the game via `ROSETTA_ADVERTISE_AVX=1`. Improves performance on Apple Silicon under Rosetta.                                                                           |

> **macOS Tahoe (26)** gives the best Metal 4 support and full DLSS/MetalFX benefits. Under macOS
> Sequoia (15.x) some Metal 4 features are unavailable.

---

## In-game graphics settings

These settings make the biggest difference for performance inside CS2 itself.

**Basic settings:**

| Setting                    | Value                                                                   | Notes                                                      |
|----------------------------|-------------------------------------------------------------------------|------------------------------------------------------------|
| **Display Mode**           | **Fullscreen Windowed**                                                 | Faster than Exclusive Fullscreen                           |
| **Resolution**             | **1080p or 1440p**                                                      | Do not use native Retina resolution — it tanks performance |
| **VSync**                  | **Disabled**                                                            |                                                            |
| **Performance preference** | **Frame rate**                                                          |                                                            |
| **Dynamic resolution**     | **DLSS Balanced** (if MetalFX enabled above), otherwise **FSR Quality** |                                                            |
| **Depth of Field**         | **Disabled**                                                            | One of the heaviest effects in CS2                         |
| **Motion Blur**            | **Disabled**                                                            | Nice perfomance boost for free                             |

---

## Technical details

For a full explanation of every Wine bug this patcher works around and how each fix works at the IL
level, see [docs/technical.md](docs/technical.md).

---

## Credits and prior work

This patcher builds
on [alexqzd/cs2-crossover-patcher](https://github.com/alexqzd/cs2-crossover-patcher), which provided
the foundation fixes for `Colossal.IO.dll`, `Colossal.IO.AssetDatabase.dll`, and the initial Paradox
Mods patches.

**What this patcher adds compared to alexqzd:**

- **Paradox Mods support for v1.5.8f1+.** alexqzd's patcher stopped working after the v1.5.6+
  updates. Two root-cause bugs were identified and fixed properly:
    1. `FileIO.GetLockToken` — a Win32 waitable timer for a 10-second lock timeout fires in
       milliseconds under Wine, cancelling every download before it starts.
    2. `FileIO.<CreateFileStream>.MoveNext` — Wine's `File.Exists` returns `true` for non-existent
       files, causing the code to acquire a reader lock, fail to open the file, and exit the
       exception handler without releasing the lock. All subsequent write attempts for the same path
       hang forever.
- **In-game pause menu fix (`Game.dll`).** The modding toolchain's Rider-IDE probe throws on
  Wine's lying `File.Exists`/`Directory.Exists` during load; that thrown exception leaves the
  **Esc / gear pause menu unable to open**. Forcing the two existence checks to `false` (the
  truth under Wine) stops the throw and the menu works.
- **Elevated-network snap fix (`Game.dll`).** On Apple Silicon, Rosetta breaks the Burst SIMD
  height check, so bridges/power lines/pipes snap down onto structures below. The fix runs the
  net tool's snap jobs on the (correct) managed path **only while the tool is active** — no
  global Burst toggle, zero cost when the tool is closed. Same root-cause insight as
  [icetear/cs2-net-snap-fix](https://github.com/icetear/cs2-net-snap-fix), without its
  reported performance regression.
- **Spurious `IOException: …Success` dialog fix (`Colossal.IO.dll`).** Wine reports a failed
  file open with error code `0` ("Success") instead of "file not found", so reading an absent
  settings file (e.g. `Benchmark.coc`) pops an in-game error overlay instead of being handled
  silently. The fix remaps Wine's error-code-0 to file-not-found so the game's existing
  handler swallows it.
- **`IOException: Sharing violation` dialog fix (`Backtrace.Unity.dll`).** Colossal's crash
  reporter attaches the game's own log files to every report and reads them with a share
  mode that refuses to coexist with a writer — but the game's logger is holding exactly
  that file open, and the report was triggered by the very line being written. Windows
  wins that race; Wine loses it often enough to interrupt play with an error overlay. The
  fix skips an attachment that can't be read instead of throwing, so the report still
  uploads. Its trigger is fixed too: `PDX.SDK.dll` no longer reports phantom
  `IOERR_101 … Success` failures when deleting cache files that were never there.
- **Paradox Launcher 2026.8+ fix.** The launcher self-updates silently and the new version's
  Chromium can't create any GPU context under Wine — the launcher window never opens and the
  game "won't start". `patch.py` automatically adds SwiftShader (software-rendering) flags to
  CS2's Steam launch options, fixing the launcher without touching Paradox files. (Run
  `./patch.py` with Steam closed for this step to apply.)
- **Single guided command** — `./patch.py` handles everything: a dry-run **preview before
  applying**, in-menu **restore**, and automatic dotnet installation.
- **Auto-detection** of game across all CrossOver bottles.
- **Every fix documented in its source file** — each patch lives in
  [`cs2patcher/Fixes/`](cs2patcher/Fixes/) in a file named for the problem it fixes, with the
  full root-cause writeup in its header; [docs/technical.md](docs/technical.md) is the index.
