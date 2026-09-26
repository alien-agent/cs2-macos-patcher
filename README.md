<div align="right">

**English** · [Українська](README.uk-UA.md) · [Español](README.es-ES.md) · [Русский](README.ru-RU.md)

</div>

# Cities: Skylines 2 — macOS / Wine Patcher

Fixes crashes and enables Paradox Mods for **Cities: Skylines 2** running under CrossOver on macOS.

Tested: **CrossOver 26 · Game v1.5.8f1–v1.6.0f1 · Apple Silicon (M3 Pro → M5 Max)**

---

## How to use

Open Terminal, paste this, press Enter:

```bash
git clone https://github.com/alien-agent/cs2-macos-patcher && cd cs2-macos-patcher && ./patch.py
```

`patch.py` is a single **guided, interactive** tool. It walks you through:

1. **Finds your game** automatically across all CrossOver bottles — including a custom bottle folder set in CrossOver's settings — and shows whether it's already patched
2. **Previews the change first** (a dry-run that writes nothing), then asks you to confirm
3. **Applies all fixes** — launch, assets, pause menu, network snapping, Paradox Mods — and backs up the originals to `*.bak`
4. Installs dotnet via Homebrew automatically if needed

> **No dotnet?** No problem — the patcher installs it for you. You only
> need [Homebrew](https://brew.sh).
>
> If dotnet is already installed, the patcher checks that it can build the fixes (SDK 9 or
> newer) and installs a current SDK via Homebrew if yours is too old (e.g. a leftover .NET 6).

### After a game update

Re-run `./patch.py` and Patch again. A DLL the update replaced is a fresh original: every
fix is applied to it and its `.bak` is refreshed. DLLs the update left alone are rebuilt
from their backups as described next — it's always safe to re-run.

### After updating the patcher

Re-run `./patch.py` and choose **Re-Patch**. It restores every DLL it patched earlier from
its `.bak` original and applies all fixes again in their current form, so a fix whose
code changed between releases is picked up even though its older form was already in
place (a plain re-apply on top would report it as already patched). The preview lists
exactly what the rebuild applies.

### Upgrade note: mods that reappear after deletion

Older releases of this patcher, and upstream patchers carrying FIX 6, can leave deleted
mods, superseded versions, and `.downloading` folders on disk. Update the patcher and
choose **Re-Patch** as above; the rebuilt PDX.SDK.dll no longer carries FIX 6.

Installs patched before the patcher kept a manifest (no `.cs2patch.json` next to the DLLs)
are not rebuilt automatically. There Re-Patch still repairs this particular deletion bug in
place, preserving the existing backups; choose **Restore original files** and then
**Patch** to refresh the other fixes as well.

Deletion still has a known limitation: if another process holds a mod file open, the SDK
can silently leave that file and its folder behind after deleting the other files. Close
the process holding the file and retry deletion; this repair does not add reliable error
reporting for that case.

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

### Launcher error "exit code null" after updating CrossOver (26.2+)

CrossOver 26.2's Wine changed executable lookup so that the Paradox launcher can no longer start
`Cities2.exe` — every launch fails instantly with *"The game appears to have crashed or terminated
unexpectedly (exit code null)"*, even on a correctly patched install (in the launcher logs:
`spawn Cities2.exe ENOENT`).

The patcher fixes this automatically during apply by adding the game directory to the bottle's
user `PATH` (`HKCU\Environment` inside the bottle). After patching, **fully quit and restart Steam
in the bottle** once so the launcher picks up the new environment.

### Restoring original DLLs

Run `./patch.py` and choose **Restore original files** — it copies every `*.bak` back over its DLL.

Prefer to do it by hand? The backups are plain copies:

```bash
cd "<path-to>/Cities2_Data/Managed"
cp Backtrace.Unity.dll.bak Backtrace.Unity.dll
cp Colossal.IO.dll.bak Colossal.IO.dll
cp Colossal.IO.AssetDatabase.dll.bak Colossal.IO.AssetDatabase.dll
cp Game.dll.bak Game.dll
cp PDX.SDK.dll.bak PDX.SDK.dll
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

## Credits

This patcher stands on the work of:

- **[alexqzd/cs2-crossover-patcher](https://github.com/alexqzd/cs2-crossover-patcher)** — the
  original CrossOver patcher and the foundation fixes for `Colossal.IO.dll`,
  `Colossal.IO.AssetDatabase.dll` and Paradox Mods.
- **[icetear/cs2-net-snap-fix](https://github.com/icetear/cs2-net-snap-fix)** — root-cause
  discovery of the elevated-network snapping bug (Rosetta miscompiling Unity's Burst SIMD code),
  which this patcher's own snap fix is built on.

Thanks to both for figuring these out first.
