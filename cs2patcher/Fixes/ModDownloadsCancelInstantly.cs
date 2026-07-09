// Mod downloads abort instantly as "cancelled"                          slug: mod-downloads-cancel   was: FIX 7, 11, 12
//
// TARGET: PDX.SDK.dll — every get_IsCancellationRequested / IsCancelledOperation call
//         site module-wide; ResultFactory.CreateFileIoResultFromException
//
// SYMPTOM: mod downloads die immediately, reported as user-cancelled, although nobody
// cancelled anything.
//
// ROOT CAUSE: cancellation misfires under Wine — Win32 waitable timers behind
// CancellationTokenSource timeouts fire in milliseconds (see ModDownloadsFreeze for the
// root-cause timer), and the SDK treats the resulting TaskCanceledException as a user
// cancellation, killing the whole operation instead of retrying.
//
// FIX (per historical fix):
// - was FIX 7 — force every `get_IsCancellationRequested` to false: NOP the token loads
//   and rewrite the call itself to `ldc.i4.0`. Broad safety net.
// - was FIX 11 — CreateFileIoResultFromException: NOP the `isinst TaskCanceledException`
//   type test and make its brfalse unconditional — a TaskCanceledException is handled as
//   a regular IO error (retried) instead of a cancellation (aborted).
// - was FIX 12 — same treatment as FIX 7 for the SDK's own `IsCancelledOperation`.
//
// Wholesale-replace fallback (FIX 7/12): when the instruction before the cancellation
// call is a `ret` (an early-return branch, e.g. ModsDownloadProgressController.get_IsPaused),
// surgically NOP-ing would make the early return fall through and unbalance the stack
// (InvalidProgramException). Bool-returning methods in that shape get their body replaced
// wholesale with `ldc.i4.0; ret`.
//
// IDEMPOTENCY / MARKER: implicit — rewritten call sites no longer match (the call became
// `ldc.i4.0`); wholesale-replaced bodies contain no cancellation call at all. No positive
// IsApplied marker.

using Mono.Cecil;
using Mono.Cecil.Cil;
using System.Linq;

namespace Cs2MacPatcher.Fixes;

// was FIX 7: force IsCancellationRequested = false everywhere.
sealed class ModCancelTokenChecks : PdxFix
{
    public override string Id => "mod-downloads-cancel";

    public override void Apply(PatchContext ctx)
    {
        foreach (var type in ctx.Module.Types)
        {
            var allMethods = type.Methods.Concat(type.NestedTypes.SelectMany(n => n.Methods));
            foreach (var method in allMethods)
            {
                if (!method.HasBody) continue;
                var il = method.Body.Instructions;
                bool bodyReplaced = false;
                for (int i = 1; i < il.Count && !bodyReplaced; i++)
                {
                    if (il[i].OpCode != OpCodes.Call && il[i].OpCode != OpCodes.Callvirt) continue;
                    var mr = il[i].Operand as MethodReference;
                    if (mr?.Name != "get_IsCancellationRequested") continue;
                    if (!ctx.DryRun)
                    {
                        var prev = il[i - 1];

                        // Surgical NOP-ing would clobber an early-return `ret` (e.g. the null-check
                        // branch in ModsDownloadProgressController.get_IsPaused), causing fall-through
                        // and a stack imbalance the verifier rejects with InvalidProgramException.
                        // For bool-returning methods in this shape, replace the body wholesale.
                        if (prev.OpCode == OpCodes.Ret &&
                            method.ReturnType.MetadataType == MetadataType.Boolean)
                        {
                            PdxIl.ReplaceWithReturnFalse(method);
                            ctx.Applied++;
                            bodyReplaced = true;
                            break;
                        }

                        if (prev.OpCode == OpCodes.Ldflda)
                        {
                            if (i >= 3 && il[i - 2].OpCode == OpCodes.Ldfld) { il[i - 3].OpCode = OpCodes.Nop; il[i - 3].Operand = null; il[i - 2].OpCode = OpCodes.Nop; il[i - 2].Operand = null; }
                            else if (i >= 2) { il[i - 2].OpCode = OpCodes.Nop; il[i - 2].Operand = null; }
                            il[i - 1].OpCode = OpCodes.Nop; il[i - 1].Operand = null;
                        }
                        else { il[i - 1].OpCode = OpCodes.Nop; il[i - 1].Operand = null; }
                        il[i].OpCode = OpCodes.Ldc_I4_0; il[i].Operand = null;
                    }
                    ctx.Applied++;
                }
            }
        }
    }
}

// was FIX 11: TaskCanceledException → treat as a regular exception.
sealed class ModCancelTaskCanceled : PdxFix
{
    public override string Id => "mod-downloads-cancel";

    public override void Apply(PatchContext ctx)
    {
        var resultFactory = ctx.Module.Types.FirstOrDefault(t => t.Name == "ResultFactory");
        var method = resultFactory?.Methods.FirstOrDefault(m => m.Name == "CreateFileIoResultFromException");
        if (method?.HasBody != true) return;

        var il = method.Body.Instructions;
        for (int i = 0; i < il.Count - 2; i++)
        {
            if (il[i].OpCode != OpCodes.Isinst) continue;
            var typeRef = il[i].Operand as TypeReference;
            if (typeRef == null || !typeRef.Name.Contains("TaskCanceledException")) continue;
            if (il[i + 1].OpCode != OpCodes.Brfalse_S && il[i + 1].OpCode != OpCodes.Brfalse) break;
            if (!ctx.DryRun) { il[i].OpCode = OpCodes.Nop; il[i].Operand = null; il[i + 1].OpCode = il[i + 1].OpCode == OpCodes.Brfalse_S ? OpCodes.Br_S : OpCodes.Br; }
            ctx.Applied++; break;
        }
    }
}

// was FIX 12: force IsCancelledOperation = false everywhere.
sealed class ModCancelOperationChecks : PdxFix
{
    public override string Id => "mod-downloads-cancel";

    public override void Apply(PatchContext ctx)
    {
        foreach (var type in ctx.Module.Types)
        {
            var allMethods = type.Methods.Concat(type.NestedTypes.SelectMany(n => n.Methods));
            foreach (var method in allMethods)
            {
                if (!method.HasBody) continue;
                var il = method.Body.Instructions;
                bool bodyReplaced = false;
                for (int i = 1; i < il.Count && !bodyReplaced; i++)
                {
                    if (il[i].OpCode != OpCodes.Call && il[i].OpCode != OpCodes.Callvirt) continue;
                    var mr = il[i].Operand as MethodReference;
                    if (mr?.Name != "IsCancelledOperation") continue;
                    if (!ctx.DryRun)
                    {
                        var prev = il[i - 1];

                        // Same safety as the FIX-7 shape: surgical NOP of an early-return `ret`
                        // would cause fall-through. Replace bool-returning bodies wholesale instead.
                        if (prev.OpCode == OpCodes.Ret &&
                            method.ReturnType.MetadataType == MetadataType.Boolean)
                        {
                            PdxIl.ReplaceWithReturnFalse(method);
                            ctx.Applied++;
                            bodyReplaced = true;
                            break;
                        }

                        if (prev.OpCode == OpCodes.Ldfld && i >= 3 && il[i - 2].OpCode == OpCodes.Ldfld) { il[i - 3].OpCode = OpCodes.Nop; il[i - 3].Operand = null; il[i - 2].OpCode = OpCodes.Nop; il[i - 2].Operand = null; il[i - 1].OpCode = OpCodes.Nop; il[i - 1].Operand = null; }
                        else if (prev.OpCode == OpCodes.Ldfld && i >= 2) { il[i - 2].OpCode = OpCodes.Nop; il[i - 2].Operand = null; il[i - 1].OpCode = OpCodes.Nop; il[i - 1].Operand = null; }
                        else { il[i - 1].OpCode = OpCodes.Nop; il[i - 1].Operand = null; }
                        il[i].OpCode = OpCodes.Ldc_I4_0; il[i].Operand = null;
                    }
                    ctx.Applied++;
                }
            }
        }
    }
}
