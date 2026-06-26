// Patches Colossal.IO.AssetDatabase.dll — fixes asset loading crash on macOS/Wine
//
// Wine's GetFileAttributesW returns success (not ERROR_FILE_NOT_FOUND) for non-existent
// files when the parent directory exists. PopulateFromDirectory calls File.Exists(".priority")
// which returns true, then File.ReadAllLines throws FileNotFoundException.
// Fix: NOP the File.Exists call and branch unconditionally past the .priority block.

using Mono.Cecil;
using Mono.Cecil.Cil;
using System;
using System.IO;
using System.Linq;

namespace Cs2MacPatcher;

static class AssetDatabasePatcher
{
    public static PatchSummary Patch(string managedDir, bool dryRun)
    {
        var dllPath = Path.Combine(managedDir, "Colossal.IO.AssetDatabase.dll");
        if (!File.Exists(dllPath))
            return PatchSummary.Skipped("Colossal.IO.AssetDatabase.dll not found");

        var resolver = new FallbackAssemblyResolver(managedDir);
        var module = ModuleDefinition.ReadModule(dllPath, new ReaderParameters
        {
            ReadingMode = ReadingMode.Immediate,
            AssemblyResolver = resolver,
            ReadSymbols = false
        });

        var fsd = module.Types.FirstOrDefault(t => t.Name == "FileSystemDataSource");
        if (fsd == null)
        {
            module.Dispose();
            return PatchSummary.Skipped("FileSystemDataSource not found — wrong DLL?");
        }

        var popDir = fsd.Methods.FirstOrDefault(m => m.Name == "PopulateFromDirectory");
        if (popDir == null)
        {
            module.Dispose();
            return PatchSummary.Skipped("PopulateFromDirectory not found");
        }

        int applied = 0;
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

                if (!dryRun)
                {
                    il[j - 1].OpCode = OpCodes.Nop; il[j - 1].Operand = null;
                    il[j].OpCode = OpCodes.Nop;     il[j].Operand = null;
                    brInst.OpCode = OpCodes.Br;
                }
                applied++;
                break;
            }
            break;
        }

        if (applied == 0)
        {
            module.Dispose();
            return PatchSummary.AlreadyPatched("Colossal.IO.AssetDatabase.dll");
        }

        if (!dryRun)
        {
            BackupAndWrite(module, dllPath);
            return new PatchSummary("Colossal.IO.AssetDatabase.dll", applied, DryRun: false);
        }

        module.Dispose();
        return new PatchSummary("Colossal.IO.AssetDatabase.dll", applied, DryRun: true);
    }

    static void BackupAndWrite(ModuleDefinition module, string dllPath) =>
        TimestampedBackup.BackupAndWrite(module, dllPath);
}
