// Patches Colossal.PSI.Steamworks.dll — Fix 32: Map uses set_Item instead of Add
//
// Symptom: Game.Dlc.SteamworksDlcsMapping..ctor() calls
//   SteamworksDlcMapper.Map(DlcId, uint) three times. Map internally calls
//   m_Mapping.Add(dlcId, appId), which throws "An item with the same key
//   has already been added. Key: 0" because the same DlcId ends up being
//   added twice on Wine/CrossOver.
//
// Fix: replace the callvirt Dictionary::Add with callvirt Dictionary::set_Item,
// so duplicate mappings silently overwrite instead of throwing.

using Mono.Cecil;
using Mono.Cecil.Cil;
using System;
using System.IO;
using System.Linq;

namespace Cs2MacPatcher;

static class SteamworksDlcMapperMapPatcher
{
    public static PatchSummary Patch(string managedDir, bool dryRun)
    {
        var dllPath = Path.Combine(managedDir, "Colossal.PSI.Steamworks.dll");
        if (!File.Exists(dllPath)) return PatchSummary.Skipped("Colossal.PSI.Steamworks.dll not found");

        var module = ModuleDefinition.ReadModule(dllPath,
            new ReaderParameters
            {
                ReadingMode = ReadingMode.Immediate,
                AssemblyResolver = new FallbackAssemblyResolver(managedDir)
            });

        var mapper = module.Types.FirstOrDefault(t => t.FullName == "Colossal.PSI.Steamworks.SteamworksDlcMapper");
        if (mapper == null) { module.Dispose(); return PatchSummary.Skipped("SteamworksDlcMapper not found"); }

        var map = mapper.Methods.FirstOrDefault(m => m.Name == "Map" && m.Parameters.Count == 2);
        if (map == null || !map.HasBody) { module.Dispose(); return PatchSummary.Skipped("Map() not found"); }

        // Find the Dictionary<DlcId, uint> callvirt instruction (either Add or set_Item).
        Instruction? dictCall = null;
        foreach (var instr in map.Body.Instructions)
        {
            if (instr.OpCode != OpCodes.Callvirt) continue;
            if (instr.Operand is not MethodReference mr) continue;
            if (mr.Name != "Add" && mr.Name != "set_Item") continue;
            if (!mr.DeclaringType.Name.StartsWith("Dictionary")) continue;
            dictCall = instr;
            break;
        }
        if (dictCall == null) { module.Dispose(); return PatchSummary.Skipped("Dictionary::Add/set_Item not found"); }

        // Idempotency: if already patched (set_Item), skip.
        if (dictCall.Operand is MethodReference existingMr && existingMr.Name == "set_Item")
        { module.Dispose(); return PatchSummary.AlreadyPatched("Colossal.PSI.Steamworks.dll"); }

        // Clone the existing Add reference and just rename it to set_Item.
        // This preserves the proper generic-instance resolution that Mono's
        // ImportReference would otherwise mishandle for brand-new MethodRefs
        // (the runtime reports MissingMethodException otherwise).
        var addRef = (MethodReference)dictCall.Operand;
        var setItemRef = new MethodReference("set_Item", addRef.ReturnType, addRef.DeclaringType)
        {
            HasThis = addRef.HasThis,
            ExplicitThis = addRef.ExplicitThis,
            CallingConvention = addRef.CallingConvention,
        };
        foreach (var p in addRef.Parameters) setItemRef.Parameters.Add(p);

        if (dryRun) { module.Dispose(); return new PatchSummary("Colossal.PSI.Steamworks.dll", 1, DryRun: true); }

        var il = map.Body.GetILProcessor();
        il.Replace(dictCall, il.Create(OpCodes.Callvirt, setItemRef));

        TimestampedBackup.BackupAndWrite(module, dllPath);
        return new PatchSummary("Colossal.PSI.Steamworks.dll", 1, DryRun: false);
    }
}
