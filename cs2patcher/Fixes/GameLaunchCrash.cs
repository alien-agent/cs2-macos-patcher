// Game crashes immediately on launch                                   slug: game-launch-crash   was: unnumbered (the original Colossal.IO fix)
//
// TARGET: Colossal.IO.dll — System.IO.LongDirectory enumeration state machines (MoveNext)
//
// SYMPTOM: the game crashes on launch before reaching the main menu.
//
// ROOT CAUSE: LongDirectory uses P/Invoke to call Win32 FindNextFile. Under Wine,
// FindNextFile returns false (with GetLastError() = ERROR_NO_MORE_FILES) even when the
// enumeration succeeded. The error-checking code unconditionally calls
// GetLastWin32Error → GetExceptionFromWin32Error → throw, killing startup directory
// enumeration.
//
// FIX: NOP the whole `call GetLastWin32Error … call GetExceptionFromWin32Error; throw`
// block in every LongDirectory state-machine MoveNext. The FindNextFile return value is
// ignored; enumeration completes normally.
//
// IDEMPOTENCY / MARKER: implicit — once NOP'd, no `call GetExceptionFromWin32Error; throw`
// pair remains to match, so re-runs find nothing. No positive IsApplied marker (absence
// of a pattern is not proof of our patch — a game update could also remove it); the
// sibling ErrorDialogOnMissingFiles fix provides this DLL's strong "ours" signal.

using Mono.Cecil;
using Mono.Cecil.Cil;
using System;
using System.Linq;

namespace Cs2MacPatcher.Fixes;

sealed class GameLaunchCrash : Fix
{
    public override string Id => "game-launch-crash";
    public override string TargetDll => "Colossal.IO.dll";

    public override void Apply(PatchContext ctx)
    {
        var longDirType = ctx.Module.Types.FirstOrDefault(t => t.Name == "LongDirectory");
        if (longDirType == null) return;

        foreach (var nestedType in longDirType.NestedTypes)
        {
            var moveNext = nestedType.Methods.FirstOrDefault(m => m.Name == "MoveNext");
            if (moveNext == null) continue;

            var instructions = moveNext.Body.Instructions.ToList();
            for (int i = 0; i < instructions.Count; i++)
            {
                if (instructions[i].OpCode != OpCodes.Throw) continue;
                if (i < 1 || instructions[i - 1].OpCode != OpCodes.Call) continue;
                var callTarget = instructions[i - 1].Operand as MethodReference;
                if (callTarget == null || !callTarget.Name.Contains("GetExceptionFromWin32Error")) continue;

                int blockStart = -1;
                for (int j = i - 1; j >= Math.Max(0, i - 15); j--)
                {
                    if (instructions[j].OpCode == OpCodes.Call)
                    {
                        var m = instructions[j].Operand as MethodReference;
                        if (m != null && m.Name.Contains("GetLastWin32Error")) { blockStart = j; break; }
                    }
                }
                if (blockStart == -1) continue;

                if (!ctx.DryRun)
                    for (int j = blockStart; j <= i; j++)
                    { instructions[j].OpCode = OpCodes.Nop; instructions[j].Operand = null; }
                ctx.Applied++;
            }
        }
    }
}
