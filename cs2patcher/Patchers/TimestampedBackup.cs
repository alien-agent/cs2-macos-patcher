// Shared backup helper — creates timestamped `.bak.YYYYMMDD-HHMMSS` files
// (one per patch run, preserved across re-runs) so re-running the patcher
// after a Steam integrity check or a manual restore never overwrites an
// existing backup. The original `.bak` (without timestamp) is also kept
// as a convenience pointer to the most-recent pre-patch state.
//
// Used by every patcher via BackupAndWrite(module, dllPath) — the function
// signature is unchanged from the prior `.bak`-only version, so each
// patcher just imports this file and gets the new behaviour automatically.

using Mono.Cecil;
using System;
using System.IO;

namespace Cs2MacPatcher;

static class TimestampedBackup
{
    public static void BackupAndWrite(ModuleDefinition module, string dllPath)
    {
        // 1. Always write a fresh timestamped backup of the current DLL
        //    contents before patching. If two patches run in the same
        //    second the .NET DateTime resolution on macOS is too coarse to
        //    distinguish, so we add a counter suffix.
        var stamp = DateTime.Now.ToString("yyyyMMdd-HHmmss");
        var uniqueStamp = stamp;
        int counter = 0;
        while (File.Exists($"{dllPath}.bak.{uniqueStamp}"))
        {
            counter++;
            uniqueStamp = $"{stamp}-{counter}";
        }
        var timestampedBackup = $"{dllPath}.bak.{uniqueStamp}";

        // 2. If no plain `.bak` exists, create one too (the "current original"
        //    snapshot) for easy restore and existing-tool compatibility.
        var plainBackup = dllPath + ".bak";
        if (!File.Exists(plainBackup))
            File.Copy(dllPath, plainBackup);

        File.Copy(dllPath, timestampedBackup);

        // 3. Write the patched module to a .tmp, then atomically replace.
        var tmp = dllPath + ".tmp";
        module.Write(tmp);
        module.Dispose();
        File.Move(tmp, dllPath, overwrite: true);
    }
}
