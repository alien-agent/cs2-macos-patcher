# AGENTS.md — CS2 macOS Patcher (development guide)

## Quick start

```bash
# Re-run the full patcher
echo "2" | python3 patch.py

# Full mode (Paradox Mods) without interaction
cd cs2patcher && dotnet run --project cs2patcher -- <managed-dir> full --apply
```

## File layout

```
patch.py                          # Main launcher (Python)
cs2-css-cohtml-fix.py             # CSS post-processor for Cohtml
cs2patcher/
  cs2patcher.csproj               # .NET 9 project
  Program.cs                      # Patch pipeline (which DLLs get patched, in what order)
  PatchSummary.cs                 # Result record
    Patchers/
    InjectDlcCachePatcher.cs          # Fix 18: DLC cache injection (Colossal.PSI.Common)
    PlatformManagerIsDlcOwnedPatcher.cs  # Fix 19: auto-own all DLCs (Colossal.PSI.Common)
    SteamworksDlcMapperMapPatcher.cs       # Fix 20: Map set_Item (Colossal.PSI.Steamworks)
    ColossalIoPatcher.cs                   # Fix 21: LongDirectory IOException (Colossal.IO)
    LongFileOpenWineFallbackPatcher.cs     # Fix 22: LongFile \\?\ Wine fix (Colossal.IO)
    AssetDatabasePatcher.cs                # Fix 23: .priority File.Exists NOP (Colossal.IO.AssetDatabase)
    RiderPathLocatorPatcher.cs             # Fix 24: Rider settings.json (Game)
    PdxSdkPatcher.cs                       # Full-mode PdxSdk fixes
    TimestampedBackup.cs                   # Shared backup helper (timestamped .bak files)
docs/
  technical.md                    # Detailed root-cause analysis
```

## Build

```bash
cd cs2patcher && dotnet build
```

Requires .NET 9 SDK. The project uses `<RollForward>Major</RollForward>` so it works with .NET 10+.

## Game file locations

```
# Game root
~/Library/Application Support/CrossOver/Bottles/Cities Skylines II/drive_c/
  Program Files (x86)/Steam/steamapps/common/Cities Skylines II/

# DLLs we patch
Cities2_Data/Managed/Colossal.IO.dll
Cities2_Data/Managed/Colossal.IO.AssetDatabase.dll
Cities2_Data/Managed/Colossal.PSI.Common.dll
Cities2_Data/Managed/Game.dll
Cities2_Data/Managed/PDX.SDK.dll

# Content directory (DLC .ntl files)
Cities2_Data/Content/<DLC>/<DLC>.ntl

# CSS for game UI
Cities2_Data/Content/Game/UI/index.css
```

## Game logs

```
# CrossOver bottle
~/Library/Application Support/CrossOver/Bottles/Cities Skylines II/drive_c
  users/crossover/AppData/LocalLow/Colossal Order/Cities Skylines II/

# Main log
Player.log

# Subsystem logs
Logs/FileSystem.log    # Database registrations, content integrity errors
Logs/SceneFlow.log     # GameManager lifecycle, version info
Logs/UI.log            # Cohtml UI loading, CSS warnings
Logs/Steamworks.log    # Steam platform errors
Logs/Modding.log       # Mod loading
Logs/PdxSdk.log        # Paradox Mods SDK operations
```

## How to restore original DLLs

```bash
cd ".../Cities2_Data/Managed"

# Restore from timestamped backup (recommended)
cp "Colossal.IO.dll.bak.20260626-122500" "Colossal.IO.dll"

# Restore from plain backup (most recent pre-patch state)
cp "Colossal.IO.dll.bak" "Colossal.IO.dll"

# List all backups
ls -la *.bak*

# Delete stale backups (caution: no way back)
rm -f *.bak *.bak.*
```

Alternatively, use Steam to verify game files:
- Right-click CS2 → Properties → Installed Files → Verify integrity

## How to test

```bash
# 1. Delete stale .bak files (they cause false "already patched" skips)
cd ".../Cities2_Data/Managed" && rm -f *.bak

# 2. Run the patcher
cd /path/to/cs2-macos-patcher && echo "2" | python3 patch.py

# 3. Enable Wine virtual desktop (REQUIRED for display)
# Edit ~/Library/Application Support/CrossOver/Bottles/Cities Skylines II/user.reg
# Add this section if not present:
#   [Software\\Wine\\Explorer]
#   "Desktop"="1920x1080"
# Without this, CrossOver reports screen as 1×1 and UI renders broken.

# 4. Launch the game via CrossOver

# 5. Check the Player.log for FATAL errors
tail -f ".../AppData/LocalLow/Colossal Order/Cities Skylines II/Player.log" | grep FATAL

# 6. Check FileSystem.log for DLC registrations
tail -f ".../Logs/FileSystem.log" | grep Registered
```

Expected log output on a working install:
```
[INFO]  Registered 'SteamCloud' database
[INFO]  Registered 'User' database
[INFO]  Registered 'BridgesAndPorts' database
[INFO]  Registered 'CityStations' database
[INFO]  Registered 'DeluxeRelaxRadio' database
[INFO]  Registered 'Game' database
[INFO]  Registered 'LeisureVenues' database
[INFO]  Registered 'ModernArchitecture' database
[INFO]  Registered 'Skyscrapers' database
[INFO]  Registered 'UrbanPromenades' database
[INFO]  Boot completed
[INFO]  Loading mode MainMenu with purpose Cleanup
[INFO]  MainMenu reached
```

## CrossOver settings

Located in `cxbottle.conf`:
```ini
[EnvironmentVariables]
WINEMSYNC = "1"           # MSync synchronization
CX_GRAPHICS_BACKEND = "d3dmetal"  # DirectX 12 through Metal
D3DM_ENABLE_METALFX = "1"  # DLSS / MetalFX
```

## Known issues & fixes

### Fix 18 — DlcAttribute NRE on Wine (v1.6.0f1)
DlcHelper.GetDlcAttributes decrypts `.ntl` files with wrong xxHash3 key on Wine (directory sort order differs) → null Variant → NRE. Inject hardcoded cache with correct DlcIds using single-arg `DlcAttribute(int)` ctor. Must import ctor from type definition — the 1-arg ctor isn't called in original body.

### Fix 19 — No DLC databases register
PlatformManager.IsDlcOwned only auto-owns `DlcId.BaseGame`. Steam backend on Wine always returns false. Replace entire body with minimal version (Invalid → false, everything else → true). Must clear body + exception handlers — patch-in-place with `nop` leaves unreachable try/catch that the CLR verifier rejects.

### Fix 20 — SteamworksDlcMapper.Map "Key already added"
Game.Dlc.SteamworksDlcsMapping..ctor calls `Map` with DlcIds 0, 1, 5. Duplicate key 0 on Wine. Replace `callvirt Dictionary::Add` with `set_Item`. New `MethodReference` must be CLONED from existing `Add` ref — brand-new refs fail at runtime.

### Fix 21 — LongDirectory IOException
LongDirectory.EnumerateFileSystemIterator.MoveNext throws when Wine's `FindNextFile` returns false with `ERROR_NO_MORE_FILES`. NOP the `GetLastWin32Error → GetExceptionFromWin32Error → throw` block.

### Fix 22 — LongFile \\?\ IOException: Success
Wine returns `ERROR_SUCCESS` for `\\?\` prefixed paths. Wrap `LongFile.Open` in try/catch + retry without prefix. `GetFileHandle` fallback strips prefix.

### Fix 23 — .priority FileNotFoundException
Wine's `File.Exists` returns true for non-existent files. NOP the `.priority` File.Exists check + change brfalse to br in `PopulateFromDirectory`.

### Fix 24 — RiderPathLocator .settings.json
Same Wine `File.Exists` lie on JetBrains Toolbox `.settings.json`. NOP `File.Exists` + brfalse → br in `GetToolboxRiderRootPath`.

### Wine virtual desktop (1×1 screen)
D3DMetal reports display as 1×1. Add `[Software\\Wine\\Explorer] "Desktop"="1920x1080"` to `user.reg`.

### D3D11 video decoder (0x80004002)
D3DMetal doesn't expose multithread video decoder. Software fallback works, only affects intro movies. No patch.

### CSS warnings (Cohtml 1.64)
`border-width: var(...)` shorthand silently dropped. `cs2-css-cohtml-fix.py` expands to 4 long-form. `gap:`, `word-wrap:` also handled.

### Paradox Mods (full mode)
`PdxSdkPatcher.cs` — Win32 timer fires in ms instead of 10s, lock leak on failed File.Exists.

## Typical patch workflow

1. **User reports issue** → check Player.log for FATAL stack trace
2. **Find the offending method** → `ilspycmd -il <dll>` to inspect IL
3. **Write a Patcher** → new file in cs2patcher/Patchers/
4. **Wire it in Program.cs** → `Print(MynewPatcher.Patch(...))`
5. **Build + test** → `dotnet build && echo "2" | python3 patch.py`
6. **Launch game** → check Player.log, FileSystem.log for improvements
7. **Iterate** → if still broken, check .bak file idempotency, restore original, retry

## Mono.Cecil tips

- **`IsConstructor` doesn't exist on MethodReference** → use `mr.Name == ".ctor"` for ctors
- **Generic method import fails for closed types in same assembly** → capture MethodReference from original body, don't clear or re-import
- **`InsertAfter(anchor, X)` inserts at anchor+N** → subsequent inserts go BEFORE earlier ones; advance anchor to keep order
- **`ldsfld` pushes value, `ldsflda` pushes address** → use correct opcode for field access
- **Value-type constructors take `this` by address** → use `ldloca` + `initobj` for default-required structs
- **Exception handlers must cover valid try/catch ranges** → wrong ranges cause `InvalidProgramException`
- **`BackupAndWrite` creates backup BEFORE writing patch** → original DLL state is preserved in .bak
