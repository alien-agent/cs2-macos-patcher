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
        // A method with locals must declare initlocals to be verifiable, and Unity's
        // compiler omits it on bodies that had none. Ours adds one, so set it.
        body.InitLocals = true;

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

    // Walks backwards from a call to the first instruction that pushes its arguments —
    // the point where the evaluation stack is empty as far as this call is concerned.
    //
    // HOW: start owing the call its arguments, then walk back paying that debt off —
    // each instruction pushes (pays) and pops (borrows). Debt zero = the start.
    //
    // Bails (returns null) on anything that is not straight-line argument setup, and on
    // a setup some branch jumps INTO — a path arriving in the middle would find the
    // instructions it expects rewritten out from under it. A branch to the START is
    // fine and is NOT a bail: every path reaches it with the same stack. Callers that
    // need a protected-region start have the stricter requirement; they use
    // ProtectedRegionStart.
    public static Instruction? StatementStart(MethodDefinition method, Instruction call)
    {
        int need = Pops(call);
        var cur = call;
        while (need > 0)
        {
            cur = cur.Previous;
            if (cur == null) return null;
            var flow = cur.OpCode.FlowControl;
            if (flow is FlowControl.Branch or FlowControl.Cond_Branch or FlowControl.Return
                or FlowControl.Throw or FlowControl.Break) return null;
            need = need - Pushes(cur) + Pops(cur);
        }
        for (var i = cur.Next; i != null && i != call.Next; i = i.Next)
            if (IsTargeted(method, i)) return null;
        return cur;
    }

    // StatementStart, plus the extra rule a try region has to satisfy: control may not
    // branch to its first instruction either (ECMA-335 forbids entering a protected
    // block by branch; ilverify reports TryNonEmptyStack / branch-into-try).
    public static Instruction? ProtectedRegionStart(MethodDefinition method, Instruction call)
    {
        var start = StatementStart(method, call);
        return start != null && !IsTargeted(method, start) ? start : null;
    }

    // Is this instruction the destination of a branch, a switch arm, or an exception
    // handler boundary?
    static bool IsTargeted(MethodDefinition method, Instruction ins)
    {
        foreach (var b in method.Body.Instructions)
            if (ReferenceEquals(b.Operand, ins)
                || (b.Operand is Instruction[] arms && arms.Any(t => ReferenceEquals(t, ins))))
                return true;
        foreach (var h in method.Body.ExceptionHandlers)
            if (ReferenceEquals(h.TryStart, ins) || ReferenceEquals(h.TryEnd, ins)
                || ReferenceEquals(h.HandlerStart, ins) || ReferenceEquals(h.HandlerEnd, ins)
                || ReferenceEquals(h.FilterStart, ins))
                return true;
        return false;
    }

    static int Pops(Instruction ins)
    {
        var op = ins.OpCode;
        if (op.FlowControl == FlowControl.Call && ins.Operand is IMethodSignature sig)
            return sig.Parameters.Count + (sig.HasThis && op.Code != Code.Newobj ? 1 : 0)
                 + (op.Code == Code.Calli ? 1 : 0);
        return op.StackBehaviourPop switch
        {
            StackBehaviour.Pop0 => 0,
            StackBehaviour.Pop1 or StackBehaviour.Popi or StackBehaviour.Popref => 1,
            StackBehaviour.Pop1_pop1 or StackBehaviour.Popi_pop1 or StackBehaviour.Popi_popi
                or StackBehaviour.Popi_popi8 or StackBehaviour.Popi_popr4 or StackBehaviour.Popi_popr8
                or StackBehaviour.Popref_pop1 or StackBehaviour.Popref_popi => 2,
            StackBehaviour.Popi_popi_popi or StackBehaviour.Popref_popi_popi
                or StackBehaviour.Popref_popi_popi8 or StackBehaviour.Popref_popi_popr4
                or StackBehaviour.Popref_popi_popr8 or StackBehaviour.Popref_popi_popref => 3,
            _ => 0,
        };
    }

    static int Pushes(Instruction ins)
    {
        var op = ins.OpCode;
        if (op.FlowControl == FlowControl.Call)
            return op.Code == Code.Newobj ? 1
                 : ins.Operand is IMethodSignature s && s.ReturnType.MetadataType != MetadataType.Void ? 1 : 0;
        return op.StackBehaviourPush switch
        {
            StackBehaviour.Push0 => 0,
            StackBehaviour.Push1_push1 => 2,
            _ => 1,
        };
    }

    // Rewrites a call site to the constant `false`: NOPs the instructions that push the
    // call's receiver and arguments, then turns the call itself into `ldc.i4.0`.
    //
    // The extent of that argument setup is computed by stack accounting, not by matching
    // instruction shapes. The SDK's cancellation call sites range from `ldarg.0; call` to
    // `ldarg.0; ldfld; ldfld; ldflda; call`, and a hard-coded rule that NOPs three
    // instructions leaves the fourth pushing a value nobody pops — the leftover `ldarg.0`
    // that ilverify reported as PathStackDepth in ModsDownloadManagerService.ProcessQueue.
    //
    // Returns false, changing nothing, when the extent cannot be established safely: the
    // walk hit a branch merge, some branch jumps into the middle of the setup, or the
    // setup contains a call whose side effect must not be NOP'd away. Callers fall back to
    // their own shape match there, which is what they did for every site before this
    // existed. (A branch to the FIRST instruction of the setup is fine — it lands on a
    // nop and flows through to the constant.)
    public static bool TryForceCallToFalse(MethodDefinition method, Instruction call)
    {
        var start = StatementStart(method, call);
        if (start == null) return false;
        for (var ins = start; ins != call; ins = ins.Next)
            if (ins.OpCode.FlowControl != FlowControl.Next || !ins.OpCode.Name.StartsWith("ld"))
                return false;
        for (var ins = start; ins != call; ins = ins.Next) { ins.OpCode = OpCodes.Nop; ins.Operand = null; }
        call.OpCode = OpCodes.Ldc_I4_0; call.Operand = null;
        return true;
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
