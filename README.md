<div align="right">

**English** · [Українська](README.uk-UA.md) · [Español](README.es-ES.md) · [Русский](README.ru-RU.md)

</div>

# Cities: Skylines 2 — macOS / Wine Patcher

Gets **Cities: Skylines 2** running under CrossOver on macOS: fixes the crashes and makes Paradox Mods work.

Tested: **CrossOver 26 · Game v1.5.8f1–v1.6.4f1 · Apple Silicon (M3 Pro → M5 Max)**

> [!IMPORTANT]
> **Black 3D world on v1.6.4f1?** The UI works, but terrain, roads and buildings are all black.
> In the bottle settings switch **Graphics** from D3DMetal to **DXMT** and keep **MSync**.
> Details in [Black world after updating to 1.6.4f1](#black-world-after-updating-to-164f1).

---

## How to use

Open Terminal, paste this and press Enter:

```bash
git clone https://github.com/alien-agent/cs2-macos-patcher && cd cs2-macos-patcher && ./patch.py
```

`patch.py` asks before it does anything. A run goes like this:

1. It finds the game in your CrossOver bottles, including a custom bottle folder set in CrossOver's settings, and tells you whether it's already patched.
2. It shows what it's about to change. Nothing is written yet; you confirm first.
3. It applies the fixes (launch, assets, pause menu, network snapping, Paradox Mods) and keeps the original DLLs next to them as `*.bak`.
4. If dotnet is missing, it installs it through Homebrew.

> You only need [Homebrew](https://brew.sh); the patcher takes care of dotnet. If you already have
> dotnet but the SDK is older than 9 (say, a leftover .NET 6), it installs a current one.

### After a game update

Run `./patch.py` again and choose Patch. DLLs the update replaced get every fix applied and a fresh
`.bak`. DLLs it didn't touch are rebuilt from their backups, as described below. Running it again is
always safe.

### After updating the patcher

Run `./patch.py` and choose **Re-Patch**. It restores every DLL it patched earlier from its `.bak`
and applies all fixes again in their current form. Without this, a fix whose code changed between
releases would be reported as "already patched" and you'd keep the old version. The preview lists
exactly what gets rebuilt.

### Upgrade note: mods that reappear after deletion

Older releases of this patcher, and upstream patchers carrying FIX 6, can leave deleted mods,
superseded versions and `.downloading` folders on disk. Update the patcher and choose **Re-Patch**;
the rebuilt PDX.SDK.dll no longer carries FIX 6.

If you patched before the patcher started keeping a manifest (there's no `.cs2patch.json` next to
the DLLs), it can't rebuild automatically. Re-Patch still fixes the deletion bug in place and keeps
your backups. To refresh the other fixes too, choose **Restore original files**, then **Patch**.

One known gap: if another process holds a mod file open, the SDK may silently leave that file and
its folder behind. Close whatever holds the file and delete again. The patcher doesn't report this
case.

### Can't find the game automatically?

Pass the Managed folder directly:

```bash
./patch.py "/path/to/Cities2_Data/Managed"
```

It usually sits inside the CrossOver bottle:

```
~/Library/Application Support/CrossOver/Bottles/<bottle-name>/drive_c/
  Program Files (x86)/.../Cities2_Data/Managed
```

### Black world after updating to 1.6.4f1

After the 1.6.4f1 update, some players saw the game load with a working UI but a completely black
map: terrain, roads and buildings. A fresh bottle, a clean user folder, launch flags and lower
graphics settings didn't help ([#18](https://github.com/alien-agent/cs2-macos-patcher/issues/18)).

What helped was changing the graphics backend in the bottle:

- **Graphics:** DXMT instead of D3DMetal
- **Synchronization:** MSync

The patch itself stays the same, you don't need to re-run it. If the world is fine on D3DMetal for
you, there's no reason to switch.

### Launcher error "exit code null" after updating CrossOver (26.2+)

Wine in CrossOver 26.2 looks up executables differently, and the Paradox launcher can no longer
start `Cities2.exe`. Every launch fails immediately with *"The game appears to have crashed or
terminated unexpectedly (exit code null)"*, even when the game is patched correctly. The launcher
log shows `spawn Cities2.exe ENOENT`.

The patcher handles this: it adds the game folder to the bottle's user `PATH`
(`HKCU\Environment` inside the bottle). After patching, **fully quit Steam in the bottle and start
it again** so the launcher picks up the new environment.

### Restoring original DLLs

Run `./patch.py` and choose **Restore original files**. It copies every `*.bak` back over its DLL.

The backups are plain copies, so you can also do it by hand:

```bash
cd "<path-to>/Cities2_Data/Managed"
cp Backtrace.Unity.dll.bak Backtrace.Unity.dll
cp Colossal.IO.dll.bak Colossal.IO.dll
cp Colossal.IO.AssetDatabase.dll.bak Colossal.IO.AssetDatabase.dll
cp Game.dll.bak Game.dll
cp PDX.SDK.dll.bak PDX.SDK.dll
```

---

## CrossOver settings

What I use on CrossOver 26:

| Setting                       | Value                                     | Notes |
|-------------------------------|-------------------------------------------|-------|
| **Graphics**                  | **D3DMetal**, or **DXMT** on v1.6.4f1     | D3DMetal (from Apple's Game Porting Toolkit) handles CS2's DirectX 12 best; DXVK and wined3d are slower or broken there. On v1.6.4f1 D3DMetal can render a black world, and switching to DXMT fixes it, see [above](#black-world-after-updating-to-164f1). |
| **Synchronization**           | **MSync**                                 | Mach-semaphore sync. Works better than ESync for CS2. |
| **DLSS (powered by MetalFX)** | **On, but turn it off if CS2 crashes**    | New in CrossOver 26. DLSS must also be enabled in the game. Gives a big FPS boost on Apple Silicon. ⚠️ On some setups (high resolution, heavy or asset-modded cities) it causes D3DMetal crashes a few minutes into play: the game drops to desktop without an error, and Steam writes an `assert_cities2.exe_*.dmp`. If that happens, turn this **Off**. A long session without new dumps means that was it. |
| **High Resolution Mode**      | **On, with in-game resolution capped**    | Turns off pixel doubling, so the UI is sharp on Retina. ⚠️ It also lets CS2 render at the display's **native** resolution, which on a 5K/6K screen means a ~6K render: performance drops and the MetalFX crash gets more likely. Keep it **On** only if you cap the in-game **Resolution** to 1080p/1440p (see [below](#in-game-graphics-settings)); otherwise turn it **Off** on high-res displays. |
| **Windows version**           | **Windows 10 or 11**                      | XP and 7 break .NET runtime features the game needs. |
| **AVX**                       | **Enabled**                               | CrossOver 25+ exposes AVX to the game via `ROSETTA_ADVERTISE_AVX=1`. Faster under Rosetta. |

> **macOS Tahoe (26)** has the best Metal 4 support and gets the full DLSS/MetalFX benefit. On
> Sequoia (15.x) some Metal 4 features are missing.

---

## In-game graphics settings

These matter most for performance inside CS2:

| Setting                    | Value                                                                   | Notes                                              |
|----------------------------|-------------------------------------------------------------------------|----------------------------------------------------|
| **Display Mode**           | **Fullscreen Windowed**                                                 | Faster than Exclusive Fullscreen                   |
| **Resolution**             | **1080p or 1440p**                                                      | Native Retina resolution kills performance         |
| **VSync**                  | **Disabled**                                                            |                                                    |
| **Performance preference** | **Frame rate**                                                          |                                                    |
| **Dynamic resolution**     | **DLSS Balanced** (if MetalFX is on), otherwise **FSR Quality**         |                                                    |
| **Depth of Field**         | **Disabled**                                                            | One of the heaviest effects in CS2                 |
| **Motion Blur**            | **Disabled**                                                            | Free performance                                   |

---

## Technical details

Every Wine bug the patcher works around, and how each fix works at the IL level:
[docs/technical.md](docs/technical.md).

---

## Credits

This patcher is built on:

- **[alexqzd/cs2-crossover-patcher](https://github.com/alexqzd/cs2-crossover-patcher)**: the
  original CrossOver patcher and the base fixes for `Colossal.IO.dll`,
  `Colossal.IO.AssetDatabase.dll` and Paradox Mods.
- **[icetear/cs2-net-snap-fix](https://github.com/icetear/cs2-net-snap-fix)**: found the root cause
  of the elevated-network snapping bug (Rosetta miscompiling Unity's Burst SIMD code). The snap fix
  here is based on it.

Thanks to both for figuring these out first. And thanks to the players in
[#18](https://github.com/alien-agent/cs2-macos-patcher/issues/18) who found the DXMT workaround.
