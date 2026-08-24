# Technical reference

The **canonical documentation for every fix lives in its source file** under
[`cs2patcher/Fixes/`](../cs2patcher/Fixes/) — each file is named for the player-visible
problem it fixes, and its header comment carries the full story: symptom, root cause (the
Wine behavior), the IL rewrite (before → after), the idempotency marker, and version
history. This document is the index.

## Fix index

| Problem fixed | Slug (`CS2_SKIP`) | Was | DLL | Source |
|---|---|---|---|---|
| "IOException: Sharing violation" dialog while the crash reporter uploads logs | `error-dialog-on-crash-report-upload` | — | Backtrace.Unity.dll | [ErrorDialogOnCrashReportUpload.cs](../cs2patcher/Fixes/ErrorDialogOnCrashReportUpload.cs) |
| Game crashes immediately on launch | `game-launch-crash` | — | Colossal.IO.dll | [GameLaunchCrash.cs](../cs2patcher/Fixes/GameLaunchCrash.cs) |
| "IOException: …Success" error dialog on missing files | `error-dialog-on-missing-files` | FIX 20 | Colossal.IO.dll | [ErrorDialogOnMissingFiles.cs](../cs2patcher/Fixes/ErrorDialogOnMissingFiles.cs) |
| Mods fail to load (asset scan crash) | `mods-fail-to-load` | — | Colossal.IO.AssetDatabase.dll | [ModsFailToLoad.cs](../cs2patcher/Fixes/ModsFailToLoad.cs) |
| Pause menu (Esc / gear) won't open | `pause-menu-wont-open` | FIX 18 | Game.dll | [PauseMenuWontOpen.cs](../cs2patcher/Fixes/PauseMenuWontOpen.cs) |
| Elevated networks snap down onto structures below | `elevated-networks-snap` | FIX 19 | Game.dll | [ElevatedNetworksSnapToGround.cs](../cs2patcher/Fixes/ElevatedNetworksSnapToGround.cs) |
| Phantom IO errors/crashes during mod file operations | `mod-io-errors` | FIX 1, 2, 8 | PDX.SDK.dll | [ModIoErrorsAndCrashes.cs](../cs2patcher/Fixes/ModIoErrorsAndCrashes.cs) |
| Mod files/directories never get created | `mod-install-files-not-created` | FIX 3, 4, 5, 6, 13 | PDX.SDK.dll | [ModInstallFilesNotCreated.cs](../cs2patcher/Fixes/ModInstallFilesNotCreated.cs) |
| Mod downloads abort instantly as "cancelled" | `mod-downloads-cancel` | FIX 7, 11, 12 | PDX.SDK.dll | [ModDownloadsCancelInstantly.cs](../cs2patcher/Fixes/ModDownloadsCancelInstantly.cs) |
| Mod updates never re-download (stale state) | `mod-updates-never-redownload` | FIX 9, 10, 14 | PDX.SDK.dll | [ModUpdatesNeverRedownload.cs](../cs2patcher/Fixes/ModUpdatesNeverRedownload.cs) |
| Mod downloads freeze; all later downloads deadlock | `mod-downloads-freeze` | FIX 15, 16 | PDX.SDK.dll | [ModDownloadsFreeze.cs](../cs2patcher/Fixes/ModDownloadsFreeze.cs) |
| Paradox Mods broken on a fresh install | `fresh-install-mod-scan` | FIX 17 | PDX.SDK.dll | [FreshInstallModScanFails.cs](../cs2patcher/Fixes/FreshInstallModScanFails.cs) |
| Paradox Launcher window never opens (2026.8+) | — (patch.py) | — | — | [`ensure_launcher_render_fix` in patch.py](../patch.py) |
| Paradox Launcher reports "exit code null" (CrossOver 26.2+) | — (patch.py) | — | — | [`ensure_launcher_path_fix` in patch.py](../patch.py) |

The common thread: Wine lies. `File.Exists`/`GetFileAttributes` report true for missing
files, failed operations report error code 0 ("Success"), `FindNextFile` reports failure
on success, Win32 waitable timers fire in milliseconds, and Rosetta 2 miscompiles one
Burst SIMD height check. Each fix makes the code behave as it would on real Windows.

The two lies compound: a spurious error is not just noise, because Colossal's crash
reporter uploads a report for every logged ERROR, and that upload reads the game's own
log files while the logger still holds them open — which is how a phantom disk error in
PDX.SDK surfaces as a "Sharing violation" dialog from Backtrace.Unity. `mod-io-errors`
stops the phantom error; `error-dialog-on-crash-report-upload` stops the dialog.

Prior work this builds on:
[alexqzd/cs2-crossover-patcher](https://github.com/alexqzd/cs2-crossover-patcher) (the
foundation Colossal.IO / Paradox Mods fixes) and
[icetear/cs2-net-snap-fix](https://github.com/icetear/cs2-net-snap-fix) (root cause of the
elevated-network snapping bug).

## How the patcher works

- **`patch.py`** is the guided front end: finds the game across CrossOver bottles, shows
  patch status, runs a dry-run preview, then applies. It also applies Paradox Launcher
  fixes to Steam's launch options and the bottle PATH (see `ensure_launcher_render_fix` and
  `ensure_launcher_path_fix`, whose comment headers are their canonical docs).
- **`cs2patcher`** (C#, Mono.Cecil) does the IL rewriting: `cs2patcher <managed-dir>
  [--apply]`. Without `--apply` it's a dry run that reports what would change. Every fix
  in `FixRegistry` runs; each DLL is read once and written once with all its fixes
  applied to the same in-memory module (`DllPatcher`).
- **Backups**: the first apply saves `<dll>.bak`. Each fix exposes a positive structural
  marker of its own patch (`Fix.IsApplied`); when the on-disk DLL already carries any
  marker, an incremental apply preserves the existing `.bak` instead of "refreshing" it —
  the backup always holds the pristine original, never a previously-patched build. A DLL
  with no markers (fresh game update) does refresh a stale `.bak`.
- **Manifest** (`.cs2patch.json` in the Managed dir): records the sha256 of each DLL as
  patched. Restore uses it to refuse downgrading a DLL the game has since updated;
  status uses it to tell "our patch" from "a new game version".
- **Idempotency**: re-running is always safe. Each fix's search pattern no longer matches
  after it has been applied (`SKIP … already patched`), and re-applying after a game
  update patches only what the update reverted.

## Debugging a game update

When a new game version misbehaves under the patch, bisect by skipping fixes:

```bash
CS2_SKIP="fresh-install-mod-scan,elevated-networks-snap" ./patch.py
```

`CS2_SKIP` takes comma-separated slugs from the index above and disables those fixes for
the run (this is how the v1.6.0f1 hang was traced to the mod-scan wrap — see the
HISTORY note in FreshInstallModScanFails.cs). `CS2_NETSNAP_EXTRA="Full.Type.Name,…"`
wraps additional systems in the snap fix without recompiling, the escalation path if a
game update moves the snap math into a different system.

## Verifying a change to the patcher

The behavior-parity harness used for the file-per-fix refactor, reusable for any patcher
change: (1) dry-run against a live already-patched install must report all `SKIP`;
(2) apply to pristine copies must reproduce the expected per-DLL fix counts (currently
1 / 3 / 1 / 5 / 36 for Backtrace.Unity / Colossal.IO / Colossal.IO.AssetDatabase /
Game / PDX.SDK) and be idempotent on re-run; (3) `ilverify` must not add errors (see
baseline below); (4) Restore must return byte-identical pristine DLLs.

Build a pristine tree by symlinking the Managed dir and replacing each target with its
`.bak`, then `ilverify <dll> -r '<tree>/*.dll'` before and after applying:

| DLL | pristine | patched | notes |
|---|---|---|---|
| Backtrace.Unity.dll | 0 | 0 | — |
| Colossal.IO.dll | 0 | 0 | — |
| Colossal.IO.AssetDatabase.dll | 78 | 78 | genuine Unity codegen noise, untouched by us |
| Game.dll | 13567 | 13567 | genuine Unity codegen noise, untouched by us |
| PDX.SDK.dll | 0 | 0 | — |

**The patcher adds no verification errors to any DLL.** The AssetDatabase and Game.dll
figures are Unity's own unverifiable codegen, present before we touch anything; what
matters is that patched equals pristine on every row. Any increase is a regression.

PDX.SDK used to score 5, all ours: two `TryNonEmptyStack` (try regions opened
mid-expression), two `InitLocals` (a helper added a local without the flag), and one
`PathStackDepth` — a cancellation call site whose argument setup was four instructions
long where the rewrite assumed three, leaving an orphaned `ldarg.0` that nothing popped.
The lesson is in the shared helpers now: work out how far back a call's arguments reach
by **stack accounting** (`PdxIl.StatementStart` / `ProtectedRegionStart` /
`TryForceCallToFalse`), never by matching a fixed instruction shape — the SDK's call sites
range from `ldarg.0; call` to `ldarg.0; ldfld; ldfld; ldflda; call`. `ilverify` is what
catches the difference; Mono runs the broken shape without complaint.
