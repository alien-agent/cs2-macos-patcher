// Patches Colossal.PSI.Common.dll — Fix 31: inject DLC cache BEFORE the loop
//
// Instead of replacing GetDlcAttributes entirely (which breaks Dictionary.Add
// tokens), we INSERT hardcoded DlcAttribute entries right after the
// `m_CachedAttributes = new Dictionary<DlcId, DlcAttribute>()` instruction.
// The existing `Add` call tokens from the original body are reused (not
// re-imported), so they remain valid at runtime.
//
// We use the single-arg `DlcAttribute(int)` ctor — NOT the (int, Variant)
// ctor — because the (int, Variant) ctor calls `variant.TryGet("version")`
// without null-checking, which NREs when we pass null Variant. The (int) ctor
// chains to (int, Version) which sets variant = null safely.

using Mono.Cecil;
using Mono.Cecil.Cil;
using System;
using System.IO;
using System.Linq;

namespace Cs2MacPatcher;

static class InjectDlcCachePatcher
{
    static readonly (string name, int dlcId)[] DLCs = {
        ("Game", -2009),           // DlcId.BaseGame — auto-owned on all platforms
        ("BridgesAndPorts", 5),    // matches Game.Dlc.Dlc.BridgesAndPorts (store DLC id=5)
        ("CityStations", 3),       // fills gap between ids 2 and 5 in Game.Dlc.Dlc
        ("DeluxeRelaxRadio", 4),
        ("LeisureVenues", 6),
        ("ModernArchitecture", 7),
        ("Skyscrapers", 8),
        ("UrbanPromenades", 9),
    };

    public static PatchSummary Patch(string managedDir, bool dryRun)
    {
        var dllPath = Path.Combine(managedDir, "Colossal.PSI.Common.dll");
        if (!File.Exists(dllPath)) return PatchSummary.Skipped("Colossal.PSI.Common.dll not found");

        var module = ModuleDefinition.ReadModule(dllPath,
            new ReaderParameters { ReadingMode = ReadingMode.Immediate });

        var dlcHelper = module.Types.FirstOrDefault(t => t.FullName == "Colossal.PSI.Common.DlcHelper");
        if (dlcHelper == null) { module.Dispose(); return PatchSummary.Skipped("DlcHelper not found"); }

        var getAttributes = dlcHelper.Methods.FirstOrDefault(m => m.Name == "GetDlcAttributes");
        if (getAttributes == null || !getAttributes.HasBody)
        { module.Dispose(); return PatchSummary.Skipped("GetDlcAttributes body not found"); }

        // Idempotency: original body has ~120 instructions; patched body has ~340+.
        if (getAttributes.Body.Instructions.Count > 200)
        { module.Dispose(); return PatchSummary.AlreadyPatched("Colossal.PSI.Common.dll"); }

        // Find the Dictionary<DlcId, DlcAttribute>::ctor call + the next stsfld to m_CachedAttributes.
        var instrs = getAttributes.Body.Instructions;
        Instruction? stsfldCache = null;
        for (int i = 0; i < instrs.Count - 1; i++)
        {
            if (instrs[i].OpCode != OpCodes.Newobj) continue;
            if (instrs[i].Operand is not MethodReference mr) continue;
            if (!mr.Name.StartsWith(".ctor") || !mr.DeclaringType.Name.StartsWith("Dictionary")) continue;
            if (i + 1 < instrs.Count && instrs[i + 1].OpCode == OpCodes.Stsfld)
            {
                stsfldCache = instrs[i + 1];
                break;
            }
        }
        if (stsfldCache == null)
        { module.Dispose(); return PatchSummary.Skipped("Dict.ctor + stsfld not found"); }

        // Reuse existing Dictionary<DlcId, DlcAttribute>::Add call token from original body.
        MethodReference? dictAdd = null;
        foreach (var instr in instrs)
        {
            if (instr.OpCode == OpCodes.Callvirt &&
                instr.Operand is MethodReference mrAdd &&
                mrAdd.Name == "Add" && mrAdd.DeclaringType.Name.StartsWith("Dictionary"))
            { dictAdd = mrAdd; break; }
        }
        if (dictAdd == null)
        { module.Dispose(); return PatchSummary.Skipped("Dict.Add not found in original body"); }

        // Resolve types and import the single-arg DlcAttribute(int) ctor.
        var attrType = module.GetType("Colossal.PSI.Common.DlcAttribute");
        var dlcIdType = module.GetType("Colossal.PSI.Common.DlcId");
        if (attrType == null || dlcIdType == null)
        { module.Dispose(); return PatchSummary.Skipped("DlcId/DlcAttribute type not found"); }

        var attrTypeDef = attrType.Resolve();
        var dlcIdTypeDef = dlcIdType.Resolve();
        if (attrTypeDef == null || dlcIdTypeDef == null)
        { module.Dispose(); return PatchSummary.Skipped("DlcId/DlcAttribute Resolve failed"); }

        var attrCtorSingle = attrTypeDef.Methods.FirstOrDefault(m =>
            m.IsConstructor && m.Parameters.Count == 1 &&
            m.Parameters[0].ParameterType.MetadataType == MetadataType.Int32);
        var dlcIdCtor = dlcIdTypeDef.Methods.FirstOrDefault(m =>
            m.IsConstructor && m.Parameters.Count == 1 &&
            m.Parameters[0].ParameterType.MetadataType == MetadataType.Int32);
        var internalNameField = attrTypeDef.Fields.FirstOrDefault(f => f.Name == "internalName");
        var cachedAttrField = dlcHelper.Fields.FirstOrDefault(f => f.Name == "m_CachedAttributes");

        if (attrCtorSingle == null)
        { module.Dispose(); return PatchSummary.Skipped("DlcAttribute(int) ctor not found"); }
        if (dlcIdCtor == null)
        { module.Dispose(); return PatchSummary.Skipped("DlcId(int) ctor not found"); }
        if (internalNameField == null)
        { module.Dispose(); return PatchSummary.Skipped("internalName field not found"); }
        if (cachedAttrField == null)
        { module.Dispose(); return PatchSummary.Skipped("m_CachedAttributes field not found"); }

        var attrCtorRef = module.ImportReference(attrCtorSingle);
        var dlcIdCtorRef = module.ImportReference(dlcIdCtor);
        var internalNameRef = module.ImportReference(internalNameField);
        var cachedAttrRef = module.ImportReference(cachedAttrField);

        if (dryRun) { module.Dispose(); return new PatchSummary("Colossal.PSI.Common.dll", 1, DryRun: true); }

        // Add locals for our injected code: v_attr (DlcAttribute), v_dlcId (DlcId).
        var v_attr = new VariableDefinition(attrType);
        var v_dlcId = new VariableDefinition(dlcIdType);
        getAttributes.Body.Variables.Add(v_attr);
        getAttributes.Body.Variables.Add(v_dlcId);

        var il = getAttributes.Body.GetILProcessor();
        var anchor = stsfldCache;

        foreach (var (name, id) in DLCs)
        {
            // DlcAttribute attr = new DlcAttribute(id);
            // attr.internalName = name;
            // DlcId dlcId = new DlcId(id);
            // m_CachedAttributes.Add(dlcId, attr);
            var list = new[] {
                il.Create(OpCodes.Ldc_I4, id),         // push id
                il.Create(OpCodes.Newobj, attrCtorRef), // new DlcAttribute(id)
                il.Create(OpCodes.Stloc, v_attr),
                il.Create(OpCodes.Ldloc, v_attr),
                il.Create(OpCodes.Ldstr, name),
                il.Create(OpCodes.Stfld, internalNameRef),
                il.Create(OpCodes.Ldloca_S, v_dlcId),
                il.Create(OpCodes.Ldc_I4, id),
                il.Create(OpCodes.Call, dlcIdCtorRef),
                il.Create(OpCodes.Ldsfld, cachedAttrRef),
                il.Create(OpCodes.Ldloc, v_dlcId),
                il.Create(OpCodes.Ldloc, v_attr),
                il.Create(OpCodes.Callvirt, dictAdd),
            };
            foreach (var instr in list)
            {
                il.InsertAfter(anchor, instr);
                anchor = instr;
            }
        }

        // After populating the cache, return it immediately. We must NOT fall
        // through into the original body because it calls the broken
        // DlcAttribute(int, Variant) ctor (which NREs on Wine because the
        // JSON variant deserializes to null).
        var returnList = new[] {
            il.Create(OpCodes.Ldsfld, cachedAttrRef),
            il.Create(OpCodes.Ret),
        };
        foreach (var instr in returnList)
        {
            il.InsertAfter(anchor, instr);
            anchor = instr;
        }

        TimestampedBackup.BackupAndWrite(module, dllPath);
        return new PatchSummary("Colossal.PSI.Common.dll", 1, DryRun: false);
    }
}
