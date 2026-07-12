// Mods fail to load ("Failed to add Mod" asset errors)                 slug: mods-fail-to-load   was: unnumbered (the original AssetDatabase fix)
//
// TARGET: Colossal.IO.AssetDatabase.dll — FileSystemDataSource.PopulateFromDirectory
//
// SYMPTOM: mods fail to load; the log shows "Failed to add Mod" or similar asset errors.
//
// ROOT CAUSE: Wine's GetFileAttributesW (behind .NET File.Exists) returns success instead
// of ERROR_FILE_NOT_FOUND when the file doesn't exist but its parent directory does.
// PopulateFromDirectory checks for a ".priority" file to sort mods; File.Exists lies
// "true", then File.ReadAllLines(".priority") throws FileNotFoundException and the asset
// scan dies.
//
// FIX: NOP the receiver load + the File.Exists call and flip the following brfalse into an
// unconditional br — always take the "no .priority file" path (the truth under Wine).
//
//   IL:  ldstr ".priority" … ldloc; call File::Exists; brfalse skip
//     →  ldstr ".priority" … nop;   nop;               br      skip
//
// IDEMPOTENCY / MARKER: the rewritten shape — `nop; nop; br` within a few instructions
// after `ldstr ".priority"` — is a positive signature original compilers never emit;
// IsApplied uses it (protects the pristine .bak on incremental applies). Re-runs find no
// intact `call File::Exists` + `brfalse` after the ldstr, so nothing re-applies.

using Mono.Cecil;
using Mono.Cecil.Cil;
using System;
using System.Linq;

namespace Cs2MacPatcher.Fixes;

sealed class ModsFailToLoad : Fix
{
    public override string Id => "mods-fail-to-load";
    public override string TargetDll => "Colossal.IO.AssetDatabase.dll";

    static MethodDefinition? FindTarget(ModuleDefinition module) =>
        module.Types.FirstOrDefault(t => t.Name == "FileSystemDataSource")
            ?.Methods.FirstOrDefault(m => m.Name == "PopulateFromDirectory");

    public override bool IsApplied(ModuleDefinition module)
    {
        var il = FindTarget(module)?.Body?.Instructions;
        if (il == null) return false;
        for (int i = 0; i < il.Count - 3; i++)
        {
            if (il[i].OpCode != OpCodes.Ldstr || (string)il[i].Operand != ".priority") continue;
            for (int j = i + 1; j < Math.Min(i + 10, il.Count - 2); j++)
                if (il[j].OpCode == OpCodes.Nop && il[j + 1].OpCode == OpCodes.Nop
                    && il[j + 2].OpCode == OpCodes.Br)
                    return true;
            return false;
        }
        return false;
    }

    public override void Apply(PatchContext ctx)
    {
        var popDir = FindTarget(ctx.Module);
        if (popDir?.HasBody != true) return;

        var il = popDir.Body.Instructions;
        for (int i = 0; i < il.Count - 5; i++)
        {
            if (il[i].OpCode != OpCodes.Ldstr || (string)il[i].Operand != ".priority") continue;

            for (int j = i + 1; j < Math.Min(i + 10, il.Count); j++)
            {
                if (il[j].OpCode != OpCodes.Call) continue;
                var mr = il[j].Operand as MethodReference;
                if (mr == null || mr.Name != "Exists" || mr.DeclaringType.Name != "File") continue;

                var brInst = il[j + 1];
                if (brInst.OpCode != OpCodes.Brfalse && brInst.OpCode != OpCodes.Brfalse_S) continue;

                if (!ctx.DryRun)
                {
                    il[j - 1].OpCode = OpCodes.Nop; il[j - 1].Operand = null;
                    il[j].OpCode = OpCodes.Nop;     il[j].Operand = null;
                    brInst.OpCode = OpCodes.Br;
                }
                ctx.Applied++;
                break;
            }
            break;
        }
    }
}
