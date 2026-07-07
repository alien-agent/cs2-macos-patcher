// Patches Game.dll — fixes the in-game pause menu (Esc / gear) not opening on macOS/Wine.
//
// On game load the modding toolchain probes for a JetBrains Rider IDE via
// Game.Modding.Toolchain.Dependencies.RiderPathLocator. Two of its Windows-path methods
// gate a filesystem access behind an existence check that Wine lies about (returns true
// for paths that do not exist — the same lie behind FIX 4/5/13):
//
//   GetToolboxRiderRootPath:  if (File.Exists(".settings.json")) File.ReadAllText(...)
//   CollectPathsFromToolbox:  if (Directory.Exists(dir))         Directory.GetDirectories(dir)
//
// Under a fresh CrossOver prefix (no JetBrains Toolbox) the guard passes on the lie and the
// following read/enumeration throws DirectoryNotFoundException. GetAllRiderPaths catches and
// logs it and returns empty, so the toolchain *result* is unchanged — but the mere act of
// throwing during the probe leaves the pause-menu UI unable to open (confirmed empirically:
// creating the real file+dir so neither check throws makes the menu work again).
//
// Fix: force each existence check to return false — which is the truth under Wine — so the
// probe skips the read/enumeration and returns its empty default with no exception thrown.
// IL: replace the `call bool Exists(string)` with `pop` (drop the path arg the preceding
// ldloc/ldarg pushed) and insert `ldc.i4.0` after it; the following brfalse/brtrue then
// takes the "does not exist" branch. Replacing in place keeps stack balance and branch
// targets intact. Neither method has exception handlers, so no handler offsets to preserve.

using Mono.Cecil;
using Mono.Cecil.Cil;
using System.IO;
using System.Linq;

namespace Cs2MacPatcher;

static class GamePatcher
{
    public static PatchSummary Patch(string managedDir, bool dryRun)
    {
        var dllPath = Path.Combine(managedDir, "Game.dll");
        if (!File.Exists(dllPath))
            return PatchSummary.Skipped("Game.dll not found");

        // Game.dll references many Unity/Colossal assemblies; the fallback resolver stubs
        // unresolved ones so Cecil doesn't crash. ReadSymbols=false — there's no .pdb.
        // Immediate (like the sibling patchers): parse every body up front so a corrupt
        // assembly fails at ReadModule, before BackupAndWrite touches the disk.
        var resolver = new FallbackAssemblyResolver(managedDir);
        var module = ModuleDefinition.ReadModule(dllPath, new ReaderParameters
        {
            ReadingMode = ReadingMode.Immediate,
            AssemblyResolver = resolver,
            ReadSymbols = false,
        });

        var rider = module.Types.FirstOrDefault(t => t.Name == "RiderPathLocator");
        if (rider == null)
        {
            module.Dispose();
            return PatchSummary.Skipped("RiderPathLocator not found — wrong DLL?");
        }

        int applied = 0;
        ForceExistsFalse(rider, "GetToolboxRiderRootPath", "System.IO.File", dryRun, ref applied);
        ForceExistsFalse(rider, "CollectPathsFromToolbox", "System.IO.Directory", dryRun, ref applied);

        if (applied == 0)
        {
            module.Dispose();
            return PatchSummary.AlreadyPatched("Game.dll");
        }

        if (!dryRun)
        {
            PatchIo.BackupAndWrite(module, dllPath);
            return new PatchSummary("Game.dll", applied, DryRun: false);
        }

        module.Dispose();
        return new PatchSummary("Game.dll", applied, DryRun: true);
    }

    // Forces `call bool [existsDeclType]::Exists(string)` in the named method to push `false`,
    // so the guarded read/enumeration (which throws under Wine's lying existence check) is
    // skipped. Only rewrites a genuine guard — an `Exists` whose result feeds a brfalse/brtrue
    // — so a future non-guard `Exists` (whose value is otherwise consumed) is left alone rather
    // than silently corrupted. No-op if the guard is already gone (idempotent re-runs).
    static void ForceExistsFalse(TypeDefinition type, string methodName,
        string existsDeclFullName, bool dryRun, ref int applied)
    {
        var method = type.Methods.FirstOrDefault(m => m.Name == methodName);
        if (method?.HasBody != true) return;

        // File/Directory.Exists are static → always `call`; match the full type name so an
        // unrelated same-named type can't be picked; require a following conditional branch.
        var call = method.Body.Instructions.FirstOrDefault(i =>
            i.OpCode == OpCodes.Call
            && i.Operand is MethodReference mr
            && mr.Name == "Exists"
            && mr.DeclaringType?.FullName == existsDeclFullName
            && IsConditionalBranch(i.Next));
        if (call == null) return;   // already patched (guard removed) or method changed

        if (!dryRun)
        {
            var ilp = method.Body.GetILProcessor();
            ilp.InsertAfter(call, ilp.Create(OpCodes.Ldc_I4_0));
            call.OpCode = OpCodes.Pop;
            call.Operand = null;
        }
        applied++;
    }

    static bool IsConditionalBranch(Instruction i) =>
        i != null && (i.OpCode == OpCodes.Brfalse || i.OpCode == OpCodes.Brfalse_S
                   || i.OpCode == OpCodes.Brtrue  || i.OpCode == OpCodes.Brtrue_S);
}
