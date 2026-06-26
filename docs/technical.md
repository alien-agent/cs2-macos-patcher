# Technical Reference — CS2 macOS Patcher

All patches are applied to .NET assemblies using [Mono.Cecil](https://github.com/jbevain/cecil).

---

## Fix 18: DlcCache injection (`Colossal.PSI.Common.dll`)

**Symptom (v1.6.0f1):** `NullReferenceException` in `DlcAttribute..ctor(int, Variant)` at `variant.TryGet("version")`. No AssetDatabases register; game crashes before reaching main menu.

**Root cause:** `DlcHelper.GetDlcAttributes` iterates `.ntl` manifest files for each content directory. These are AES-encrypted with a key derived from `HashHelper.ComputeHash(path, ".ntl")`, which uses `xxHash3` over directory contents sorted by `orderby entry`. The entry sort order differs between Windows and Wine (string comparer locale), so the xxHash3 key is different. Decryption produces garbage → `JSON.Load` returns null → `DlcAttribute(int, Variant)` ctor calls `variant.TryGet("version")` without a null check → NRE.

**Fix:** Insert hardcoded `DlcAttribute` entries for all 8 content DLCs into `m_CachedAttributes` right after the `new Dictionary<>(); stsfld` instructions, then return early (`ldsfld m_CachedAttributes; ret`). The original loop (`.ntl` decryption, `ForEachField`, `ListContent`) is never reached. Uses the **single-arg** `DlcAttribute(int)` ctor (NOT `int, Variant` — that one NREs on null).

**Correct DlcIds (v1.6.0f1):**

| internalName | DlcId | Source |
|---|---|---|
| Game | -2009 | `DlcId.BaseGame` |
| BridgesAndPorts | 5 | `Game.Dlc.Dlc.BridgesAndPorts` |
| CityStations | 10 | arbitrary unique |
| DeluxeRelaxRadio | 11 | arbitrary unique |
| LeisureVenues | 12 | arbitrary unique |
| ModernArchitecture | 13 | arbitrary unique |
| Skyscrapers | 14 | arbitrary unique |
| UrbanPromenades | 15 | arbitrary unique |

Game uses `DlcId.BaseGame` because `PlatformManager.IsDlcOwned` returns `true` for it unconditionally. `BridgesAndPorts` uses DlcId 5 to match `Game.Dlc.Dlc.BridgesAndPorts` (store DLC), which is what `SteamworksDlcsMapping..ctor` also uses for Steam store mapping. The remaining 6 DLCs use arbitrary IDs above 9 to avoid collisions with store DLCs (0, 1, 2 from `Game.Dlc.Dlc.LandmarkBuildings/SanFranciscoSet/CS1TreasureHunt`).

**Mono.Cecil notes:** The `DlcAttribute(int)` ctor must be imported from the `DlcAttribute` type definition via `module.ImportReference(typeDef.Methods.First(m => m.IsConstructor && m.Parameters.Count == 1))` — searching the original method body fails because only `(int, Variant)` ctor calls exist there.

---

## Fix 19: PlatformManager.IsDlcOwned auto-own (`Colossal.PSI.Common.dll`)

**Symptom:** Content DLC AssetDatabases don't register (`Registered 'Game' database` never appears).

**Root cause:** `ContentHelper.RegisterContent` calls `PlatformManager.IsDlcOwned(dlcId)` before creating the AssetDatabase. The original method only returns `true` for `DlcId.BaseGame (-2009)`. For any other DlcId, it iterates `IDlcSupport` backends (Steam, Epic, etc.) and asks each `IsDlcOwned`. On Wine, Steam's backend always returns `false` (real Steam isn't running, `SteamApps.BIsDlcInstalled` returns false for all app IDs). So all 7 non-BaseGame DLCs are skipped.

**Fix:** Replace the entire method body with a minimal version:

```il
ldarg.1                      // push dlcId
ldsfld DlcId.Invalid
call DlcId::op_Equality
brfalse.s <true>
ldc.i4.0                     // dlcId == Invalid → false
ret
<true>:
ldc.i4.1                     // all other DlcIds → true
ret
```

The original backend-check loop (with `try/catch`) must be COMPLETELY REMOVED — leaving it as unreachable code after `ret` fails the CLR IL verifier with `InvalidProgramException`.

---

## Fix 20: SteamworksDlcMapper.Map set_Item (`Colossal.PSI.Steamworks.dll`)

**Symptom:** `ArgumentException: An item with the same key has already been added. Key: 0` during `SteamworksDlcsMapping..ctor`.

**Root cause:** `Game.Dlc.SteamworksDlcsMapping..ctor` calls `Map(LandmarkBuildings=0, ...)`, `Map(SanFranciscoSet=1, ...)`, `Map(BridgesAndPorts=5, ...)`. Each call does `m_Mapping.Add(dlcId, appId)`. On Wine, the constructor's `m_Mapping` Dictionary ends up with a duplicate DlcId entry (possibly from `Activator.CreateInstance` being invoked twice or from interaction with `RemapAttributeUtility` and our injected DLC cache).

**Fix:** Replace `callvirt Dictionary<DlcId, uint>::Add` with `callvirt Dictionary<DlcId, uint>::set_Item` in the `Map` method. Duplicate keys silently overwrite instead of throwing.

**Mono.Cecil notes:** The new `set_Item` `MethodReference` must be CLONED from the existing `Add` reference (copy `DeclaringType`, `HasThis`, `CallingConvention`, `Parameters`). Creating a brand-new `MethodReference` and calling `module.ImportReference` fails at runtime with `MissingMethodException` — Mono.Cecil's generic-instance method import is unreliable for newly-created references.

---

## Fix 21: LongDirectory MoveNext IOException (`Colossal.IO.dll`)

**Symptom:** `System.IO.IOException` in `LongDirectory.<EnumerateFileSystemIterator>d__15.MoveNext` during `ContentHelper.ListContent`.

**Root cause:** `Colossal.IO.LongDirectory` uses P/Invoke to call Win32 `FindNextFile`. On Wine, this returns `false` (and sets `GetLastError` to `ERROR_NO_MORE_FILES`) even when enumeration succeeded. The error-checking code unconditionally calls `GetExceptionFromWin32Error` and throws.

**Fix:** NOP the block `GetLastWin32Error → GetExceptionFromWin32Error → throw` in all `LongDirectory` state machine `MoveNext` methods.

---

## Fix 22: LongFile.Open/GetFileHandle \\?\ prefix (`Colossal.IO.dll`)

**Symptom:** `IOException: Succès` (French locale) or `IOException: Success` when opening files with `\\?\` long-path prefix under Wine/CrossOver.

**Root cause:** Wine's `CreateFileW` returns `ERROR_SUCCESS` for `\\?\`-prefixed paths but the .NET `FileStream` constructor treats any non-success error code as a failure — the `0` error code maps to the system locale's "Success" message.

**Fix:** In `LongFile.Open`, wrap the `FileStream` creation in try/catch and retry without the `\\?\` prefix on `IOException`. In `LongFile.GetFileHandle`, add a fallback path that strips the prefix and retries.

---

## Fix 23: .priority File.Exists lie (`Colossal.IO.AssetDatabase.dll`)

**Symptom:** `FileNotFoundException: Could not find file ".priority"` when populating asset data.

**Root cause:** Wine's `GetFileAttributesW` returns success for non-existent files when the parent directory exists. `FileSystemDataSource.PopulateFromDirectory` calls `File.Exists(".priority")` → returns `true` (Wine lie) → calls `File.ReadAllLines(".priority")` → throws `FileNotFoundException`.

**Fix:** In `PopulateFromDirectory`, replace `call File::Exists` with `nop` and change the following `brfalse` to unconditional `br` — always skipping the `.priority` reading block.

---

## Fix 24: RiderPathLocator .settings.json (`Game.dll`)

**Symptom:** `DirectoryNotFoundException` or `Failed to get install_location from json` during platform initialization.

**Root cause:** `RiderPathLocator.GetToolboxRiderRootPath` checks `File.Exists(settingsFile)` for a JetBrains Toolbox `.settings.json` file. Wine's `File.Exists` returns `true` (the parent `AppData` directory exists, but `Toolbox` subdirectory does not) → `File.ReadAllText` throws `DirectoryNotFoundException`.

**Fix:** In `GetToolboxRiderRootPath`, NOP `call File::Exists` and change `brfalse` to unconditional `br` — always skipping the `.settings.json` reading block and falling through to the default return path.

---

## Wine virtual desktop (CrossOver registry)

**Symptom:** Game detects screen as 1×1 pixel, UI renders as a single-colored box. HDRP throws `RenderTexture.Create failed: width & height must be larger than 0`.

**Root cause:** CrossOver/D3DMetal reports the display as 1×1 because the game window isn't fully created when Direct3D queries the display mode.

**Fix:** Add `[Software\\Wine\\Explorer] "Desktop"="1920x1080"` to the bottle's `user.reg`. Uses whichever resolution matches the Mac's display. Wine's virtual desktop forces a fixed display size before the game starts.

---

## PDX.SDK.dll — Paradox Mods (full mode)

See `Patchers/PdxSdkPatcher.cs` for the complete list of P/Invoke, timer, and lock-leak fixes. Key patches:

- **GetLockToken:** Win32 waitable timer fires in milliseconds under Wine instead of 10 seconds, cancelling every download. Method body replaced with `ldarg.1; ret` (returns the outer token unchanged).
- **CreateFileStream lock leak:** `AcquireLockResult.Dispose()` is inserted before every `leave` in the IOException catch block, releasing the reader lock so write operations don't deadlock.
- **ListFiles/ListDirectories/ListFilesRecursive:** Wrapped in try-catch(IOException) to handle `IOException: Success` when enumerating non-existent directories (Wine's `PathExists` lies).
