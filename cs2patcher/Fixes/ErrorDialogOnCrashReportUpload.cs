// In-game "IOException: Sharing violation" dialog while the crash reporter uploads logs
//                                       slug: error-dialog-on-crash-report-upload
//
// TARGET: Backtrace.Unity.dll — Backtrace.Unity.Model.BacktraceHttpClient
//         .AddAttachmentToFormData(List<IMultipartFormSection>, IEnumerable<string>)
//
// SYMPTOM: an in-game IOEXCEPTION overlay (Continue / Save & Quit / Quit) reading
//   "Sharing violation on path C:\…\Cities Skylines II\Logs\PdxSdk.log
//    With object Backtrace (Backtrace.Unity.BacktraceClient)",
// stack through BacktraceApi.Send → BacktraceHttpClient.Post → CreateJsonFormData →
// AddAttachmentToFormData → File.ReadAllBytes. The game is otherwise fine, but ANY
// error the game logs can trigger a crash report, so it interrupts play at random.
//
// ROOT CAUSE: Colossal's crash reporter attaches the game's own log files to every
// report, and Backtrace reads each attachment with File.ReadAllBytes — i.e.
// FileStream(path, Open, Read, FileShare.Read). That share mode refuses to coexist with
// any handle holding write access, and the game's own logger holds exactly one:
// Colossal.Logging.UnityLogger.Open() opens each log as
// FileStream(logPath, Append, Write, FileShare.ReadWrite). Win32 sharing is checked in
// BOTH directions — the reader's share mode must also permit the existing handle's
// access — so the open fails with ERROR_SHARING_VIOLATION whenever the read lands while
// that log is open. Windows opens/appends/closes in microseconds and rarely loses the
// race. Under Wine every open/write/close is a wineserver round-trip, AND the report is
// triggered BY the log line being written: one [PdxSdk] [ERROR] entry dumps a multi-KB
// native+managed stack into PdxSdk.log while Backtrace's send coroutine reads that same
// file. Nothing in AddAttachmentToFormData is guarded (the method's only handler is the
// foreach enumerator's finally), so one unreadable attachment both aborts the whole
// upload and escapes the coroutine into Colossal's log handler, which raises the overlay.
//
// FIX: wrap the read-and-attach statement in try-catch(IOException) that continues the
// foreach. An attachment that cannot be read is skipped, the rest of the report still
// uploads, and nothing throws. This is an upstream backtrace-unity bug — a log file open
// for writing is not an exceptional condition — that Wine merely makes fire regularly.
//
//   IL:  ldarg.1 … call File::ReadAllBytes … callvirt List::Add   ← try region
//        leave  Cont      ; normal exit from the try
//        pop              ; catch (IOException)
//        leave  Cont      ; = `continue`
//   Cont: ldloca.s V_1 …  ; the loop-condition block that the method's own
//                         ; IsNullOrEmpty / !File.Exists / size guards already
//                         ; branch to — i.e. the foreach's existing "skip this file"
//
// Both ends of the region come from stack accounting, not from instruction shape. The
// END is the statement-level (void) List.Add that consumes the bytes: the first Add in
// straight-line code after ReadAllBytes, so the stack is empty once it returns. The
// START is worked out backwards from that Add by PdxIl.ProtectedRegionStart — the
// `ldarg.1` that pushes formData, where the stack is empty. The ReadAllBytes call has
// to fall inside the region: that, not "the next Add", is what ties the Add to the
// throw site.
//
// That `ldarg.1` is a branch target — the method's if/else over duplicate attachment
// names merges into it. ECMA-335 lets control enter a try block at its first
// instruction; only a jump INTO the middle is illegal, and the helper rejects that. The
// result verifies (ilverify: 0 errors, see docs/technical.md).
//
// The handler nests inside the enumerator's finally and is inserted FIRST in the handler
// table — ECMA-335 requires innermost-first ordering. The 11 inserted bytes could push a
// short-form branch spanning the loop body past ±127, and Cecil does not widen those, so
// the body is SimplifyMacros()'d before the edit and OptimizeMacros()'d after (as in
// ElevatedNetworksSnapToGround).
//
// IDEMPOTENCY / MARKER: the original method carries exactly one handler, the foreach
// enumerator's Finally; ours adds the only Catch. Positive signature, used by IsApplied.
// CAVEAT: "any catch (IOException) on this method" is not specific to OUR catch. Should
// Colossal ship a Backtrace build that guards the read itself, the patcher SKIPs this
// DLL and patch.py reports the version as possibly unsupported — bytes untouched, game
// fine, message misleading. Same trade-off as ModIoBclCallWraps.IsApplied.

using Mono.Cecil;
using Mono.Cecil.Cil;
using Mono.Cecil.Rocks;
using System.Linq;

namespace Cs2MacPatcher.Fixes;

sealed class ErrorDialogOnCrashReportUpload : Fix
{
    public override string Id => "error-dialog-on-crash-report-upload";
    public override string TargetDll => "Backtrace.Unity.dll";

    static MethodDefinition? FindTarget(ModuleDefinition module) =>
        module.Types.FirstOrDefault(t => t.Name == "BacktraceHttpClient")
            ?.Methods.FirstOrDefault(m => m.Name == "AddAttachmentToFormData" && m.Parameters.Count == 2);

    static bool HasMarker(MethodDefinition? method) =>
        method?.HasBody == true && method.Body.ExceptionHandlers.Any(
            h => h.HandlerType == ExceptionHandlerType.Catch && h.CatchType?.Name == "IOException");

    public override bool IsApplied(ModuleDefinition module) => HasMarker(FindTarget(module));

    public override void Apply(PatchContext ctx)
    {
        var method = FindTarget(ctx.Module);
        if (method?.HasBody != true) return;
        if (HasMarker(method)) return;                      // already patched
        var ioException = PdxIl.IoExceptionRef(ctx.Module);
        if (ioException == null) return;                    // no mscorlib ref: nothing to catch with

        var il = method.Body.Instructions;

        // The throw site: File.ReadAllBytes(<attachment path>).
        var read = il.FirstOrDefault(i => i.OpCode == OpCodes.Call
            && i.Operand is MethodReference mr
            && mr.Name == "ReadAllBytes" && mr.DeclaringType.FullName == "System.IO.File");
        if (read == null) return;

        // End of the statement: the void List.Add that consumes the bytes. Straight-line
        // code only — a branch or return before any Add means the read is not part of
        // an add-statement, and the fix does not apply.
        var add = read;
        while ((add = add.Next) != null
            && !(add.OpCode == OpCodes.Callvirt && add.Operand is MethodReference { Name: "Add" } mr
                 && mr.ReturnType.MetadataType == MetadataType.Void))
            if (add.OpCode.FlowControl is not (FlowControl.Next or FlowControl.Call)) return;
        if (add?.Next == null) return;

        // Start of the statement, by stack accounting back from the Add. The region must
        // contain the read: that is what makes this Add the one the read belongs to.
        var start = PdxIl.ProtectedRegionStart(method, add);
        if (start == null || il.IndexOf(start) > il.IndexOf(read)) return;

        // The foreach's `continue` target. Requiring that some existing branch already
        // targets it proves this is the loop-condition block the method's own guards
        // skip to, and not just whatever instruction happens to follow the Add.
        var cont = add.Next;
        if (!il.Any(i => ReferenceEquals(i.Operand, cont))) return;

        if (!ctx.DryRun)
        {
            var body = method.Body;
            body.SimplifyMacros();
            var ilp = body.GetILProcessor();
            var leaveTry = ilp.Create(OpCodes.Leave, cont);
            var catchPop = ilp.Create(OpCodes.Pop);
            var leaveCatch = ilp.Create(OpCodes.Leave, cont);
            ilp.InsertAfter(add, leaveTry);
            ilp.InsertAfter(leaveTry, catchPop);
            ilp.InsertAfter(catchPop, leaveCatch);

            // Innermost-first: this handler nests inside the enumerator's finally.
            body.ExceptionHandlers.Insert(0, new ExceptionHandler(ExceptionHandlerType.Catch)
            {
                TryStart = start,
                TryEnd = catchPop,
                HandlerStart = catchPop,
                HandlerEnd = cont,
                CatchType = ioException,
            });
            body.OptimizeMacros();
        }
        ctx.Applied++;
    }
}
