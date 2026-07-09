// Shared IL helpers for the PDX.SDK.dll fixes (used across the Mod* fix files).

using Mono.Cecil;
using Mono.Cecil.Cil;
using System.Linq;

namespace Cs2MacPatcher.Fixes;

// All PDX fixes patch PDX.SDK.dll; sub-fixes within one file share that file's slug so
// CS2_SKIP disables the whole meaning-group at once.
abstract class PdxFix : Fix
{
    public override sealed string TargetDll => "PDX.SDK.dll";
}

static class PdxIl
{
    // Hand-built System.IO.IOException reference against the module's mscorlib.
    // (Multiple instances are fine — Cecil's writer dedupes identical TypeRef rows.)
    public static TypeReference IoExceptionRef(ModuleDefinition module)
    {
        var mscorlib = module.AssemblyReferences.First(r => r.Name == "mscorlib");
        return new TypeReference("System.IO", "IOException", module, mscorlib);
    }

    // Replace a method body wholesale with `ldc.i4.0; ret` (bool false). Used where a
    // surgical NOP would clobber an early-return `ret` and unbalance the stack.
    public static void ReplaceWithReturnFalse(MethodDefinition method)
    {
        method.Body.Instructions.Clear();
        method.Body.ExceptionHandlers.Clear();
        method.Body.Variables.Clear();
        var ilp = method.Body.GetILProcessor();
        ilp.Append(ilp.Create(OpCodes.Ldc_I4_0));
        ilp.Append(ilp.Create(OpCodes.Ret));
    }

    // Wraps the entire body of a List<T>-returning method in try-catch(IOException) that
    // returns an empty List<T>. All existing `ret` instructions are converted to
    // `stloc localVar; leave End` (in-place to preserve branch target references).
    public static void WrapBodyReturningListInTryCatch(MethodDefinition method, MethodReference listCtor, TypeReference ioException)
    {
        var body = method.Body;
        var ilp = body.GetILProcessor();
        var il = body.Instructions;

        var localVar = new VariableDefinition(method.ReturnType);
        body.Variables.Add(localVar);

        var firstInstr = il[0];

        var endLdloc = ilp.Create(OpCodes.Ldloc, localVar);
        var endRet = ilp.Create(OpCodes.Ret);
        var catchPop = ilp.Create(OpCodes.Pop);
        var catchNewobj = ilp.Create(OpCodes.Newobj, listCtor);
        var catchStloc = ilp.Create(OpCodes.Stloc, localVar);
        var catchLeave = ilp.Create(OpCodes.Leave, endLdloc);

        // Convert every existing `ret` into `stloc; leave End`. Mutate in place so that
        // any branch instructions referencing the old ret keep pointing to the new stloc.
        foreach (var ret in il.Where(i => i.OpCode == OpCodes.Ret).ToList())
        {
            ret.OpCode = OpCodes.Stloc;
            ret.Operand = localVar;
            ilp.InsertAfter(ret, ilp.Create(OpCodes.Leave, endLdloc));
        }

        // Append catch handler IL and the final ldloc/ret
        ilp.Append(catchPop);
        ilp.Append(catchNewobj);
        ilp.Append(catchStloc);
        ilp.Append(catchLeave);
        ilp.Append(endLdloc);
        ilp.Append(endRet);

        body.ExceptionHandlers.Add(new ExceptionHandler(ExceptionHandlerType.Catch)
        {
            TryStart = firstInstr,
            TryEnd = catchPop,
            HandlerStart = catchPop,
            HandlerEnd = endLdloc,
            CatchType = ioException
        });
    }

    // Finds the first `PathExists` call followed by a brtrue and NOPs the call, its
    // argument loads (`nopBefore` preceding instructions), and the branch — the guarded
    // work then always runs, sidestepping Wine's lying existence check.
    public static void ApplyPathExistsBypass(PatchContext ctx, TypeDefinition type, string methodName, int nopBefore)
    {
        var method = type.Methods.FirstOrDefault(m => m.Name == methodName);
        if (method?.HasBody != true) return;
        var il = method.Body.Instructions;
        for (int i = 0; i < il.Count - 1; i++)
        {
            if (il[i].OpCode != OpCodes.Callvirt && il[i].OpCode != OpCodes.Call) continue;
            var mr = il[i].Operand as MethodReference;
            if (mr?.Name != "PathExists") continue;
            if (il[i + 1].OpCode != OpCodes.Brtrue_S && il[i + 1].OpCode != OpCodes.Brtrue) continue;
            if (!ctx.DryRun)
                for (int j = i - nopBefore; j <= i + 1; j++)
                { il[j].OpCode = OpCodes.Nop; il[j].Operand = null; }
            ctx.Applied++; break;
        }
    }

    public static TypeDefinition? DiskIo(ModuleDefinition module) =>
        module.Types.FirstOrDefault(t => t.Name == "DiskIODefaultWindows");

    public static TypeDefinition? FileIo(ModuleDefinition module) =>
        module.Types.FirstOrDefault(t => t.Name == "FileIO");
}
