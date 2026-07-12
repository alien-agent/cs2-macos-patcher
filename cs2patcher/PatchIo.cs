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
    // Refreshing a stale .bak (one left from a previous game version) keeps Restore from
    // later downgrading the install to old binaries. But that refresh is only safe when
    // the on-disk DLL is this game version's unpatched original. When a NEW fix is applied
    // incrementally on top of an already-patched DLL (e.g. FIX 19 landing on a Game.dll
    // that already carries FIX 18), the on-disk DLL is ours, not the original — refreshing
    // would clobber the real original in the backup. Callers that can detect their own
    // patch markers in the on-disk DLL pass preserveExistingBackup=true in that case.
    public static void BackupAndWrite(ModuleDefinition module, string dllPath,
        bool preserveExistingBackup = false)
    {
        var backup = dllPath + ".bak";
        bool keepBackup = preserveExistingBackup && File.Exists(backup);
        if (!keepBackup && (!File.Exists(backup) || !FilesEqual(dllPath, backup)))
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
