# Cities: Skylines 2 — macOS / Wine Patcher

Fixes crashes and enables Paradox Mods for **Cities: Skylines 2** running under CrossOver on macOS.

Tested: **CrossOver 26 · Game v1.5.8f1–v1.6.0f1 · Apple Silicon (M3 Pro → M5 Max)**

> **Elevated networks snapping to the ground?** That's a *separate* Apple Silicon bug (Rosetta
> miscompiles Unity's Burst SIMD code and drops the height value), so raised roads, bridges and pipes
> wrongly snap down onto whatever is below them. Fix it with
> **[icetear/cs2-net-snap-fix](https://github.com/icetear/cs2-net-snap-fix)** — re-apply after each game update.

---

## How to use

Open Terminal, paste this, press Enter:

```bash
git clone https://github.com/alien-agent/cs2-macos-patcher && cd cs2-macos-patcher && ./patch.py
```

`patch.py` is a single **guided, interactive** tool. It walks you through:

1. **Finds your game** automatically across all CrossOver bottles, and shows whether it's already patched
2. Lets you pick **Lightweight** or **Full**
3. **Previews the change first** (a dry-run that writes nothing), then asks you to confirm
4. **Applies** the patches and backs up the originals to `*.bak`
5. Installs dotnet via Homebrew automatically if needed

> **No dotnet?** No problem — the patcher installs it for you. You only
> need [Homebrew](https://brew.sh).
>
> If you already have **.NET 10** but the patcher complains the `9.0.0` runtime is missing, install
> the matching SDK: `brew install --cask dotnet-sdk@9`.

### After a game update

Re-run `./patch.py` and pick your patch mode again. The preview and the patcher both detect
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
cp Game.dll.bak Game.dll                # fixes the in-game pause menu (Esc / gear)
cp PDX.SDK.dll.bak PDX.SDK.dll          # Full patch only
```

---

## CrossOver settings for best performance

My personal recommendation for best graphic/performance on Crossover 26:

| Setting                       | Value                | Notes                                                                                                                                                                                               |
|-------------------------------|----------------------|-----------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------|
| **Graphics**                  | **D3DMetal**         | CS2 uses DirectX 12. D3DMetal (from Apple Game Porting Toolkit) is the only translator that supports DX12 properly. DXVK and wined3d are slower or broken for DX12. DXMT is DX11-only — do not use. |
| **Synchronization**           | **MSync**            | Mach semaphore-based sync. Confirmed better than ESync for CS2.                                                                                                                                     |
| **DLSS (powered by MetalFX)** | **Enabled**          | New in CrossOver 26. Requires DLSS to also be enabled inside the game. Significant FPS gain on Apple Silicon.                                                                                       |
| **High Resolution Mode**      | **On**               | Disables pixel doubling — correct behaviour on Retina displays.                                                                                                                                     |
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
  truth under Wine) stops the throw and the menu works. Part of Lightweight.
- **Single guided command** — `./patch.py` handles everything: a dry-run **preview before
  applying**, in-menu **restore**, and automatic dotnet installation.
- **Auto-detection** of game across all CrossOver bottles.
- **Lightweight / Full split** — game-launch fixes require no extra dependencies; Paradox Mods patch
  installs dotnet automatically if needed.
