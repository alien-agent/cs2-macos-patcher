// Paradox Mods broken on a fresh install ("IOException: Success")       slug: fresh-install-mod-scan   was: FIX 17
//
// TARGET: PDX.SDK.dll — DiskIODefaultWindows.ListFiles, .ListDirectories
//
// SYMPTOM: on a fresh install (empty mods cache), the in-game Paradox Mods browser
// fails — directory listings throw "IOException: Success" and downloads never start.
//
// ROOT CAUSE: Wine's Directory.GetFiles/GetDirectories throws an IOException with an
// empty/"Success" message when the directory doesn't exist (instead of returning empty
// or throwing DirectoryNotFoundException). The PathExists early-exit guard is useless
// because Wine's GetFileAttributes lies (see the sibling PathExists fixes).
//
// FIX: wrap each method body in try-catch(IOException) returning an empty List<string>.
// The existing `newobj List<string>::.ctor()` reference from the method's !PathExists
// early branch is reused for the catch handler's empty-list construction.
//
// !!! ListFilesRecursive is DELIBERATELY NOT PATCHED. On game v1.6.0f1 the init-time
// recursive mod scan calls it; when a wrap makes it return an empty list instead of
// throwing, the SDK reads that as "success + empty" and loops instead of breaking out
// on the error — the game hangs at the Paradox logo. Confirmed by bisection: patching
// only ListFiles + ListDirectories boots AND downloads mods correctly; adding
// ListFilesRecursive reintroduces the hang. The download path (PrepareFolderForPatching
// → ClearFolderAndKeepPatchFile) only needs ListFiles/ListDirectories, so
// ListFilesRecursive is left to throw — FileIO.PerformDiskOperationAndCatch handles it.
//
// IDEMPOTENCY / MARKER: positive — an IOException catch handler on ListFiles/
// ListDirectories (the original methods have none); used by IsApplied.

using Mono.Cecil;
using Mono.Cecil.Cil;
using System.Linq;

namespace Cs2MacPatcher.Fixes;

sealed class FreshInstallModScanFails : PdxFix
{
    public override string Id => "fresh-install-mod-scan";

    static readonly string[] Targets = { "ListFiles", "ListDirectories" };

    public override bool IsApplied(ModuleDefinition module)
    {
        var diskIO = PdxIl.DiskIo(module);
        return diskIO != null && Targets
            .Select(n => diskIO.Methods.FirstOrDefault(m => m.Name == n))
            .Any(m => m?.HasBody == true
                && m.Body.ExceptionHandlers.Any(h => h.CatchType?.Name == "IOException"));
    }

    public override void Apply(PatchContext ctx)
    {
        var diskIO = PdxIl.DiskIo(ctx.Module);
        if (diskIO == null) return;
        var ioExceptionRef = PdxIl.IoExceptionRef(ctx.Module);
        if (ioExceptionRef == null) return;                 // no mscorlib ref: nothing to catch with

        foreach (var name in Targets)
        {
            var method = diskIO.Methods.FirstOrDefault(m => m.Name == name);
            if (method?.HasBody != true) continue;

            // Find the existing List<string>::.ctor() ref used by the !PathExists early branch
            var listCtor = method.Body.Instructions
                .Where(i => i.OpCode == OpCodes.Newobj)
                .Select(i => i.Operand as MethodReference)
                .FirstOrDefault(mr => mr?.Name == ".ctor" && mr.DeclaringType.Name == "List`1");
            if (listCtor == null) continue;

            // Skip if already wrapped (heuristic: method already has an IOException catch)
            if (method.Body.ExceptionHandlers.Any(h => h.CatchType?.Name == "IOException")) continue;

            if (!ctx.DryRun) PdxIl.WrapBodyReturningListInTryCatch(method, listCtor, ioExceptionRef);
            ctx.Applied++;
        }
    }
}
