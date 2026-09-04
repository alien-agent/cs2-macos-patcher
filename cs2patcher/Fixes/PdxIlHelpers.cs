// Shared IL helpers. PdxFix and the DiskIo/FileIo lookups are PDX.SDK.dll-specific; the
// rest (IoExceptionRef, CallArgumentsStart, ProtectedRegionStart, ...) serve every DLL
// the patcher touches — Backtrace.Unity's fix uses them too.

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
    // Hand-built System.IO.IOException reference against the module's own mscorlib.
    // Never ImportReference from the patcher's .NET runtime — that would emit a
    // System.Runtime ref Unity's Mono cannot bind. (Multiple instances are fine — Cecil's
    // writer dedupes identical TypeRef rows.)
    //
    // Null when the module has no mscorlib reference (a future Unity build compiled
    // against netstandard/System.Runtime). Callers skip their fix then — and must check
    // BEFORE the DryRun branch, so a dry run reports the same outcome as an apply. A throw
    // here would abort the whole run: Program.cs patches the DLLs in sequence with no
    // handler, and Backtrace.Unity.dll is first.
    public static TypeReference? IoExceptionRef(ModuleDefinition module)
    {
        var mscorlib = module.AssemblyReferences.FirstOrDefault(r => r.Name == "mscorlib");
        return mscorlib == null ? null : new TypeReference("System.IO", "IOException", module, mscorlib);
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

        // ECMA-335 orders the handler table innermost-first. This region spans the whole
        // body, so it encloses every handler the method already had and goes LAST. (A
        // wrap around a single call is the opposite case — innermost — and is
        // Insert(0)'d; see ModIoBclCallWraps and ErrorDialogOnCrashReportUpload.)
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
    // the point where the evaluation stack is empty AS FAR AS THIS CALL IS CONCERNED.
    // That is not necessarily the start of the statement: for a nested call
    // (`list.Add(File.ReadAllBytes(f))`, asked about the ReadAllBytes) the enclosing
    // expression's values are still on the stack underneath. Callers that need a truly
    // empty stack go through ProtectedRegionStart and pass the outermost call.
    //
    // HOW: start owing the call its arguments, then walk back paying that debt off —
    // each instruction pushes (pays) and pops (borrows). Debt zero = the start.
    //
    // Bails (returns null) on anything that is not straight-line argument setup, and on
    // a setup some branch jumps INTO — a path arriving in the middle would find the
    // instructions it expects rewritten out from under it. A branch to the START is
    // fine and is NOT a bail: every path reaches it with the same stack.
    public static Instruction? CallArgumentsStart(MethodDefinition method, Instruction call)
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

    // Where a try region wrapping `call` may open. CallArgumentsStart, plus what a
    // protected region needs on top of it:
    //
    // - The call must be a whole statement: void, or its result popped right away. Then
    //   the stack is empty after the call, and the accounting that found the start says
    //   it is empty before it — balanced at both ends. A call whose result feeds an
    //   enclosing expression is rejected: its arguments-start is not a stack-empty point
    //   (for `list.Add(File.ReadAllBytes(f))` pass the Add, not the ReadAllBytes). This
    //   is a necessary check, not a proof — a value pushed BEFORE the arguments (a `dup`
    //   pattern) is invisible to the walk. Every current target is a plain statement,
    //   and the ilverify baseline in docs/technical.md is what proves it: a wrong start
    //   shows up there as TryNonEmptyStack.
    //
    // - The start MAY be a branch target. ECMA-335 (I.12.4.2.8.1) lets control enter a
    //   protected block at its first instruction by branch or fall-through; only landing
    //   in the middle is forbidden, and CallArgumentsStart already rejects that. Both a
    //   loop whose body is a try and an if/else merging into the statement branch to the
    //   try's first instruction — Backtrace's AddAttachmentToFormData is the latter, and
    //   ilverify accepts it. What the start may NOT be is the entry of a catch or filter
    //   block: the stack holds the exception object there.
    public static Instruction? ProtectedRegionStart(MethodDefinition method, Instruction call)
    {
        if (Pushes(call) != 0 && call.Next?.OpCode != OpCodes.Pop) return null;
        var start = CallArgumentsStart(method, call);
        return start != null && !IsHandlerEntry(method, start) ? start : null;
    }

    static bool IsHandlerEntry(MethodDefinition method, Instruction ins) =>
        method.Body.ExceptionHandlers.Any(h =>
            ReferenceEquals(h.HandlerStart, ins) || ReferenceEquals(h.FilterStart, ins));

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
        var start = CallArgumentsStart(method, call);
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
