// Deleting a mod does nothing — it reappears, and old versions pile up   slug: mod-deletion-does-nothing
//
// TARGET: PDX.SDK.dll — DiskIODefaultWindows.GetLongPath(string)
//
// SYMPTOM: in the in-game Paradox Mods browser, removing a downloaded mod appears to work —
// it vanishes from the list — but switching tabs and coming back shows it again, forever.
// Its folder is never deleted. The same failure silently strands the previous version of
// every updated mod, so `.cache/Mods/pdx_mods` accumulates `<id>_6` next to `<id>_7`
// indefinitely, and `.downloading` staging folders are never cleaned up.
//
// ROOT CAUSE: this one was OURS. The SDK stores its mod root with forward slashes
// (mod_directory.json: "C:/users/.../.cache/Mods\pdx_mods"), and GetLongPath exists to
// normalize that before prefixing \\?\ for the long-path Win32 calls:
//
//     if (!p.StartsWith(@"\\?\")) return @"\\?\" + p.Replace('/', Path.DirectorySeparatorChar);
//     return p.Replace('/', Path.DirectorySeparatorChar);
//
// The old FIX 6 rewrote that '/' literal (47) to '\' (92) "so path normalization matches
// what the Wine filesystem layer expects". On a Windows player Path.DirectorySeparatorChar
// IS '\', so the call became Replace('\', '\') — a no-op. Normalization was switched off,
// and every stored path went out as \\?\C:/users/.../Mods\pdx_mods\<id>_<ver> — an
// extended-length path containing forward slashes. The whole point of the \\?\ prefix is
// that the NT layer takes the path UNNORMALIZED, and '/' is not a separator there, so:
//
//   - GetLongPathFiles / GetLongPathDirectories: FindFirstFile fails, and because the
//     result is just an empty List<string>, DeleteLongPathDirectory deletes NOTHING and
//     reports no error;
//   - RemoveDirectory then fails on the still-populated directory (ERROR_PATH_NOT_FOUND) —
//     and FIX 1 has NOP'd exactly that `newobj IOException; throw`, so the failure is
//     silent. The SDK reports success, drops the mod from the playset, and the next
//     directory rescan finds the untouched folder and puts it back in the list.
//
// Measured under CrossOver's Wine, same folder, only the literal differing:
//
//   Replace('/', sep)   ->  \\?\C:\tmp\mod    enumerate: 1 file    RemoveDirectory: err 145
//   Replace('\', sep)   ->  \\?\C:/tmp\mod    enumerate: FAILED    RemoveDirectory: err 3
//
// This is a malformed extended-length path, not a Wine separator bug: ordinary Win32
// paths accept forward or mixed slashes, but paths prefixed with \\?\ require backslashes.
//
// FIX: keep GetLongPath's normalization intact — the '/' literal stays '/'. The old FIX 6
// rewrite is gone from ModInstallFilesNotCreated (the other four sub-fixes in that group,
// which are the ones that actually make mod installs work, are untouched).
//
// This fix additionally REPAIRS an install that still carries the old rewrite: a DLL
// patched by an earlier version of this patcher (or an upstream patcher carrying FIX 6)
// has 92 baked into GetLongPath, and a
// re-run would otherwise leave it broken, because every other fix's pattern already
// matches and reports "already patched". Rewriting 92 back to 47 lets an explicit
// Re-Patch repair this particular defect in place. The startup "Already patched" status
// only recognizes previously patched DLLs; it does not mean they have the latest fixes.
// For a patcher upgrade, the README still recommends Restore, then Patch so older forms
// of OTHER fixes are also replaced. Re-Patch alone is only the targeted deletion repair.
//
// LIMITATION: deletion is still best-effort because mod-io-errors suppresses genuine IO
// failures as well as Wine's spurious ones. A Win32 handle opened without FILE_SHARE_DELETE
// makes DeleteFileW fail with ERROR_SHARING_VIOLATION (32): the SDK returns normally with
// the locked file and parent folder left behind, after deleting unlocked siblings. Once
// the handle is closed, retrying deletes the remainder. This repair restores traversal;
// it does not make deletion atomic or restore reliable error reporting. The regression
// harness in tests/ModDeletionSmokeTest.cs exercises this case, nested directories, and
// the disk phase of an update (move the new version, delete the old version and staging).
// It does not drive an online mod download or the in-game playset lifecycle.
//
//   IL:  ldc.i4.s 92; ldsfld Path::DirectorySeparatorChar   ->   ldc.i4.s 47; ldsfld …
//
// IDEMPOTENCY / MARKER: implicit and self-limiting — the repair only matches the 92 that
// the old rewrite left behind, so it applies once and finds nothing on a pristine DLL or
// on a re-run. No positive IsApplied marker: 47 is what the original ships with, and
// absence of our change is not proof of it (see Fix.IsApplied).

using Mono.Cecil;
using Mono.Cecil.Cil;
using System.Linq;

namespace Cs2MacPatcher.Fixes;

sealed class ModDeletionDoesNothing : PdxFix
{
    public override string Id => "mod-deletion-does-nothing";

    public override void Apply(PatchContext ctx)
    {
        var getLongPath = PdxIl.DiskIo(ctx.Module)?.Methods.FirstOrDefault(m => m.Name == "GetLongPath");
        if (getLongPath?.HasBody != true) return;

        var il = getLongPath.Body.Instructions;
        for (int i = 0; i < il.Count - 1; i++)
        {
            if (il[i].OpCode != OpCodes.Ldc_I4_S || (sbyte)il[i].Operand != 92) continue;
            if (il[i + 1].OpCode != OpCodes.Ldsfld) continue;
            if ((il[i + 1].Operand as FieldReference)?.Name != "DirectorySeparatorChar") continue;
            if (!ctx.DryRun) il[i].Operand = (sbyte)47;
            ctx.Applied++;
        }
    }
}
