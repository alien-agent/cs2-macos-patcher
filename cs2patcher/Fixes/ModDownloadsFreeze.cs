// Mod downloads freeze / all subsequent downloads deadlock              slug: mod-downloads-freeze   was: FIX 15, 16
//
// TARGET: PDX.SDK.dll — FileIO.GetLockToken, FileIO.CreateFileStream state machine
//
// SYMPTOM: the first mod download freezes partway, and every download after it hangs
// forever until the game restarts. (The v1.5.8f1 Paradox Mods breakage.)
//
// ROOT CAUSE — two independent root causes discovered on v1.5.8f1, where the older
// belt-and-suspenders fixes (ModUpdatesNeverRedownload, ModDownloadsCancelInstantly)
// were no longer sufficient:
// - was FIX 15 — GetLockToken wraps the caller's token with
//   `new CancellationTokenSource(TimeSpan.FromSeconds(10))` for a lock timeout. That
//   uses a Win32 waitable timer, which fires in MILLISECONDS under Wine — every
//   download gets cancelled almost immediately.
// - was FIX 16 — Wine's File.Exists lies "true" for a non-existent file, so
//   CreateFileStream acquires a reader lock, then the open throws
//   FileNotFoundException, and the IOException catch returns WITHOUT disposing the
//   lock. _readSemaphore stays at 0; the next AcquireWriterLock blocks forever — all
//   subsequent downloads hang.
//
// FIX:
// - GetLockToken: replace the whole body with `ldarg.1; ret` — return the caller's
//   token unchanged, no timer.
// - CreateFileStream MoveNext: insert `ldloc.s lockVar; callvirt AcquireLockResult
//   .Dispose()` before every `leave` in the IOException catch block (identified by its
//   CreateIoResultFromException call), releasing the reader lock on the error path.
//
// IDEMPOTENCY / MARKER: both positive, both used by IsApplied. FIX-15: the exact
// 2-instruction body `ldarg.1; ret`. FIX-16: a Dispose callvirt immediately before a
// catch-block leave (the AlreadyDisposed guard also prevents re-runs from accumulating
// duplicate Dispose calls, which would corrupt the method).

using Mono.Cecil;
using Mono.Cecil.Cil;
using System.Collections.Generic;
using System.Linq;

namespace Cs2MacPatcher.Fixes;

// was FIX 15: GetLockToken — remove the Win32 timer-based 10s timeout.
sealed class ModFreezeLockTimeout : PdxFix
{
    public override string Id => "mod-downloads-freeze";

    static MethodDefinition? FindTarget(ModuleDefinition module) =>
        PdxIl.FileIo(module)?.Methods.FirstOrDefault(m => m.Name == "GetLockToken");

    static bool HasMarker(MethodDefinition? m)
    {
        var body = m?.Body?.Instructions;
        return body != null && body.Count == 2
            && body[0].OpCode == OpCodes.Ldarg_1 && body[1].OpCode == OpCodes.Ret;
    }

    public override bool IsApplied(ModuleDefinition module) => HasMarker(FindTarget(module));

    public override void Apply(PatchContext ctx)
    {
        var getLockToken = FindTarget(ctx.Module);
        if (getLockToken?.HasBody != true) return;
        if (HasMarker(getLockToken)) return;                // already patched

        if (!ctx.DryRun)
        {
            getLockToken.Body.Instructions.Clear();
            getLockToken.Body.ExceptionHandlers.Clear();
            getLockToken.Body.Variables.Clear();
            var ilp = getLockToken.Body.GetILProcessor();
            ilp.Append(ilp.Create(OpCodes.Ldarg_1));
            ilp.Append(ilp.Create(OpCodes.Ret));
        }
        ctx.Applied++;
    }
}

// was FIX 16: CreateFileStream MoveNext — dispose the reader lock in the IOException catch.
sealed class ModFreezeReaderLockLeak : PdxFix
{
    public override string Id => "mod-downloads-freeze";

    static MethodDefinition? FindTarget(ModuleDefinition module) =>
        PdxIl.FileIo(module)?.NestedTypes.FirstOrDefault(t => t.Name.Contains("CreateFileStream"))
            ?.Methods.FirstOrDefault(m => m.Name == "MoveNext");

    public override bool IsApplied(ModuleDefinition module)
    {
        var moveNext = FindTarget(module);
        if (moveNext?.HasBody != true) return false;
        var instrs = moveNext.Body.Instructions;
        return moveNext.Body.ExceptionHandlers
            .Where(h => h.HandlerType == ExceptionHandlerType.Catch)
            .SelectMany(h => instrs.SkipWhile(i => i != h.HandlerStart).TakeWhile(i => i != h.HandlerEnd))
            .Any(i => (i.OpCode == OpCodes.Leave || i.OpCode == OpCodes.Leave_S)
                && i.Previous?.OpCode == OpCodes.Callvirt
                && (i.Previous.Operand as MethodReference)?.Name == "Dispose");
    }

    public override void Apply(PatchContext ctx)
    {
        var module = ctx.Module;
        var moveNext = FindTarget(module);
        if (moveNext?.HasBody != true) return;

        var lockVar = moveNext.Body.Variables
            .FirstOrDefault(v => v.VariableType.Name == "AcquireLockResult");
        var lockResultType = module.Types
            .Concat(module.Types.SelectMany(t => t.NestedTypes))
            .FirstOrDefault(t => t.Name == "AcquireLockResult");
        var disposeRef = lockResultType?.Methods.FirstOrDefault(m => m.Name == "Dispose");

        if (lockVar == null || disposeRef == null) return;

        var instrs = moveNext.Body.Instructions;
        bool AlreadyDisposed(Instruction lv)
        {
            int li = instrs.IndexOf(lv);
            return li >= 1 && instrs[li - 1].OpCode == OpCodes.Callvirt
                && (instrs[li - 1].Operand as MethodReference)?.Name == "Dispose";
        }

        // Leaves in the IOException catch that don't already dispose the lock. The
        // AlreadyDisposed guard makes this fix idempotent — a re-run must not insert
        // a second Dispose before a leave that already has one (that would accumulate
        // extra Dispose calls on every apply and corrupt the method).
        var targets = new List<Instruction>();
        foreach (var handler in moveNext.Body.ExceptionHandlers)
        {
            if (handler.HandlerType != ExceptionHandlerType.Catch) continue;
            var hbody = instrs.SkipWhile(i => i != handler.HandlerStart)
                              .TakeWhile(i => i != handler.HandlerEnd).ToList();
            bool isIoCatch = hbody.Any(i =>
                (i.OpCode == OpCodes.Call || i.OpCode == OpCodes.Callvirt) &&
                i.Operand?.ToString()?.Contains("CreateIoResultFromException") == true);
            if (!isIoCatch) continue;
            targets.AddRange(hbody.Where(i =>
                (i.OpCode == OpCodes.Leave || i.OpCode == OpCodes.Leave_S) && !AlreadyDisposed(i)));
        }

        if (targets.Count == 0) return;

        if (!ctx.DryRun)
        {
            var ilp = moveNext.Body.GetILProcessor();
            var dispose = module.ImportReference(disposeRef);
            foreach (var leave in targets)
            {
                ilp.InsertBefore(leave, ilp.Create(OpCodes.Ldloc_S, lockVar));
                ilp.InsertBefore(leave, ilp.Create(OpCodes.Callvirt, dispose));
            }
        }
        ctx.Applied++;
    }
}
