// Shared on-disk helpers for the patchers: backup-and-write and a byte comparison.
// Each patcher used to carry its own copy of these.

using Mono.Cecil;
using System.IO;
using System.Linq;

namespace Cs2MacPatcher;

static class PatchIo
{
    // Back up the original DLL (creating or refreshing a stale .bak), write the mutated
    // module to a temp file, then atomically move it into place.
    //
    // Callers only reach this after fixes were applied, so the on-disk DLL is this game
    // version's unpatched original. Refreshing a stale .bak (one left from a previous
    // game version) keeps Restore from later downgrading the install to old binaries.
    public static void BackupAndWrite(ModuleDefinition module, string dllPath)
    {
        var backup = dllPath + ".bak";
        if (!File.Exists(backup) || !FilesEqual(dllPath, backup))
            File.Copy(dllPath, backup, overwrite: true);
        var tmp = dllPath + ".tmp";
        module.Write(tmp);
        module.Dispose();
        File.Move(tmp, dllPath, overwrite: true);
    }

    public static bool FilesEqual(string a, string b)
    {
        var ia = new FileInfo(a);
        var ib = new FileInfo(b);
        if (!ia.Exists || !ib.Exists || ia.Length != ib.Length) return false;
        return File.ReadAllBytes(a).SequenceEqual(File.ReadAllBytes(b));
    }
}
