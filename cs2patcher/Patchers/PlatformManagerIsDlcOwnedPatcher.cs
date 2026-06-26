// Patches Colossal.PSI.Common.dll — Fix 33: IsDlcOwned auto-owns all DLCs
//
// Symptom: AssetDatabases like 'Game' don't get registered on Wine because
// ContentHelper.RegisterContent() calls PlatformManager.IsDlcOwned(dlcId)
// before registering the database, and on Wine the Steam backend's
// IsDlcOwned() always returns false (real Steam isn't running). The original
// code only auto-owns DlcId.BaseGame (-2009); any other DlcId must be
// confirmed by a backend.
//
// Fix: replace the entire body with a minimal version that returns false
// only for DlcId.Invalid and true for everything else. We can't just patch
// the existing branch in-place because leaving the original backend-check
// try/catch block as unreachable code after our new ret fails the CLR IL
// verifier (InvalidProgramException on load).

using Mono.Cecil;
using Mono.Cecil.Cil;
using System;
using System.IO;
using System.Linq;

namespace Cs2MacPatcher;

static class PlatformManagerIsDlcOwnedPatcher
{
    public static PatchSummary Patch(string managedDir, bool dryRun)
    {
        var dllPath = Path.Combine(managedDir, "Colossal.PSI.Common.dll");
        if (!File.Exists(dllPath)) return PatchSummary.Skipped("Colossal.PSI.Common.dll not found");

        var module = ModuleDefinition.ReadModule(dllPath,
            new ReaderParameters { ReadingMode = ReadingMode.Immediate });

        var platformMgr = module.Types.FirstOrDefault(t => t.FullName == "Colossal.PSI.Common.PlatformManager");
        if (platformMgr == null) { module.Dispose(); return PatchSummary.Skipped("PlatformManager not found"); }

        // Find IsDlcOwned(DlcId) — value-type overload (not the IDlc one).
        var isDlcOwned = platformMgr.Methods.FirstOrDefault(m =>
            m.Name == "IsDlcOwned" &&
            m.Parameters.Count == 1 &&
            m.Parameters[0].ParameterType.FullName == "Colossal.PSI.Common.DlcId");
        if (isDlcOwned == null || !isDlcOwned.HasBody)
        { module.Dispose(); return PatchSummary.Skipped("IsDlcOwned(DlcId) not found"); }

        // Idempotency: original body has 30+ instructions; patched body has 10.
        var instrs = isDlcOwned.Body.Instructions;
        if (instrs.Count < 15)
        { module.Dispose(); return PatchSummary.AlreadyPatched("Colossal.PSI.Common.dll"); }

        // Resolve DlcId type for the Invalid field reference.
        var dlcIdType = module.GetType("Colossal.PSI.Common.DlcId");
        if (dlcIdType == null) { module.Dispose(); return PatchSummary.Skipped("DlcId type not found"); }
        var dlcIdDef = dlcIdType.Resolve();
        if (dlcIdDef == null) { module.Dispose(); return PatchSummary.Skipped("DlcId Resolve failed"); }
        var invalidField = dlcIdDef.Fields.FirstOrDefault(f => f.Name == "Invalid");
        if (invalidField == null) { module.Dispose(); return PatchSummary.Skipped("DlcId.Invalid field not found"); }
        var invalidRef = module.ImportReference(invalidField);

        // Resolve DlcId::op_Equality(DlcId, DlcId) — used by the Invalid check.
        var opEquality = dlcIdDef.Methods.FirstOrDefault(m =>
            m.Name == "op_Equality" && m.IsStatic && m.Parameters.Count == 2 &&
            m.Parameters[0].ParameterType.FullName == "Colossal.PSI.Common.DlcId" &&
            m.Parameters[1].ParameterType.FullName == "Colossal.PSI.Common.DlcId");
        if (opEquality == null) { module.Dispose(); return PatchSummary.Skipped("DlcId.op_Equality not found"); }
        var opEqualityRef = module.ImportReference(opEquality);

        if (dryRun) { module.Dispose(); return new PatchSummary("Colossal.PSI.Common.dll", 1, DryRun: true); }

        // Clear the body and rebuild it. We need two labels (true / false return
        // targets) — capture them via Instruction.Create with null operand and
        // patch them later with il.Append(il.Create(OpCodes.Nop)) + Replace.
        var body = isDlcOwned.Body;
        body.Instructions.Clear();
        body.ExceptionHandlers.Clear();
        body.Variables.Clear();
        body.InitLocals = false;
        body.MaxStackSize = 2;

        var il = body.GetILProcessor();

        // Build the new body:
        //   ldarg.1
        //   ldsfld DlcId.Invalid
        //   call op_Equality
        //   brfalse.s <true>
        //   ldc.i4.0
        //   ret
        // <true>:
        //   ldc.i4.1
        //   ret
        var trueLabel = il.Create(OpCodes.Nop);
        il.Append(il.Create(OpCodes.Ldarg_1));
        il.Append(il.Create(OpCodes.Ldsfld, invalidRef));
        il.Append(il.Create(OpCodes.Call, opEqualityRef));
        il.Append(il.Create(OpCodes.Brfalse_S, trueLabel));
        il.Append(il.Create(OpCodes.Ldc_I4_0));
        il.Append(il.Create(OpCodes.Ret));
        il.Append(trueLabel);
        il.Append(il.Create(OpCodes.Ldc_I4_1));
        il.Append(il.Create(OpCodes.Ret));

        TimestampedBackup.BackupAndWrite(module, dllPath);
        return new PatchSummary("Colossal.PSI.Common.dll", 1, DryRun: false);
    }
}
