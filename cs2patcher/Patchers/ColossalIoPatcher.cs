// Patches Colossal.IO.dll — fixes game launch crash on macOS/Wine
//
// Wine returns false from FindNextFile even on success, triggering an error check that
// throws an exception during directory enumeration at startup. Fix: NOP the block
// GetLastWin32Error → GetExceptionFromWin32Error → throw in LongDirectory state machines.

using Mono.Cecil;
using Mono.Cecil.Cil;
using System;
using System.IO;
using System.Linq;

namespace Cs2MacPatcher;

static class ColossalIoPatcher
{
    public static PatchSummary Patch(string managedDir, bool dryRun)
    {
        var dllPath = Path.Combine(managedDir, "Colossal.IO.dll");
        if (!File.Exists(dllPath))
            return PatchSummary.Skipped("Colossal.IO.dll not found");

        var module = ModuleDefinition.ReadModule(dllPath,
            new ReaderParameters
            {
                ReadingMode = ReadingMode.Immediate,
                AssemblyResolver = new FallbackAssemblyResolver(Path.GetDirectoryName(dllPath)!)
            });

        var longDirType = module.Types.FirstOrDefault(t => t.Name == "LongDirectory");
        if (longDirType == null)
        {
            module.Dispose();
            return PatchSummary.Skipped("LongDirectory type not found — wrong DLL?");
        }

        int applied = 0;

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

                if (!dryRun)
                    for (int j = blockStart; j <= i; j++)
                    { instructions[j].OpCode = OpCodes.Nop; instructions[j].Operand = null; }
                applied++;
            }
        }

        if (applied == 0)
        {
            module.Dispose();
            return PatchSummary.AlreadyPatched("Colossal.IO.dll");
        }

        if (!dryRun)
        {
            BackupAndWrite(module, dllPath);
            return new PatchSummary("Colossal.IO.dll", applied, DryRun: false);
        }

        module.Dispose();
        return new PatchSummary("Colossal.IO.dll", applied, DryRun: true);
    }

    static void BackupAndWrite(ModuleDefinition module, string dllPath) =>
        TimestampedBackup.BackupAndWrite(module, dllPath);
}
