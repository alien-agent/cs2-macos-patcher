// The registry: which DLLs are patched, and every fix in its apply order.
//
// ORDER MATTERS within a DLL: fixes run against the same in-memory module, and the PDX
// entries preserve the exact historical order (old FIX 1..17) — surgical module-wide
// scans (mod-downloads-cancel) ran before the wholesale body replacements
// (mod-updates-never-redownload, mod-downloads-freeze), which affects which sites exist
// to be counted/patched. Keep new fixes appended per-DLL unless there is a proven reason
// to interleave.

using System.Collections.Generic;
using Cs2MacPatcher.Fixes;

namespace Cs2MacPatcher;

static class FixRegistry
{
    public static readonly DllTarget[] Targets =
    {
        new("Colossal.IO.dll",               new[] { "LongDirectory" },                       ResolverKind.None),
        new("Colossal.IO.AssetDatabase.dll", new[] { "FileSystemDataSource" },                ResolverKind.SearchDir),
        new("Game.dll",                      new[] { "RiderPathLocator", "NetToolSystem" },   ResolverKind.Fallback),
        new("PDX.SDK.dll",                   new[] { "DiskIODefaultWindows" },                ResolverKind.None),
    };

    public static readonly IReadOnlyList<Fix> Fixes = new Fix[]
    {
        // Colossal.IO.dll (historical order: error-code remap, then FindNextFile NOPs)
        new ErrorDialogOnMissingFiles(),
        new GameLaunchCrash(),

        // Colossal.IO.AssetDatabase.dll
        new ModsFailToLoad(),

        // Game.dll (historical order: FIX 18, then FIX 19)
        new PauseMenuWontOpen(),
        new ElevatedNetworksSnapToGround(),

        // PDX.SDK.dll — EXACT historical order, old FIX 1..17
        new ModIoPInvokeThrows(),            // 1
        new ModIoBclCallWraps(),             // 2
        new ModInstallLongPathSegments(),    // 3
        new ModInstallCreateDirectory(),     // 4
        new ModInstallCreateWriteStream(),   // 5
        new ModInstallPathSeparators(),      // 6
        new ModCancelTokenChecks(),          // 7
        new ModIoInvalidHandleThrow(),       // 8
        new ModRedownloadManifestFiles(),    // 9
        new ModRedownloadInstallVersion(),   // 10
        new ModCancelTaskCanceled(),         // 11
        new ModCancelOperationChecks(),      // 12
        new ModInstallDownloadPathExists(),  // 13
        new ModRedownloadFileCheck(),        // 14
        new ModFreezeLockTimeout(),          // 15
        new ModFreezeReaderLockLeak(),       // 16
        new FreshInstallModScanFails(),      // 17
    };
}
