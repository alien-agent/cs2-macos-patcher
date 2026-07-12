// Phantom IO errors and crashes during Paradox Mods file operations     slug: mod-io-errors   was: FIX 1, 2, 8
//
// TARGET: PDX.SDK.dll — DiskIODefaultWindows file/directory operations
//
// SYMPTOM: mod install/update operations fail with IO errors (or crash) even though the
// underlying file operation actually succeeded or is harmlessly redundant.
//
// ROOT CAUSE: Wine lies about Win32 results. P/Invoked DeleteFile/RemoveDirectory/
// CreateDirectory/MoveFile report failure (or a bogus last-error) on operations that
// succeeded, and the SDK's error checks turn those lies into thrown IOExceptions.
//
// FIX (three shapes, one per historical fix):
// - was FIX 1 — long-path methods (DeleteLongPathFile, DeleteLongPathDirectory,
//   CreateLongPathDirectory, LongPathMove): NOP every `newobj IOException; throw` pair
//   that follows a P/Invoke error check. The operation's real outcome stands.
// - was FIX 2 — short-path methods (Delete, DeleteDirectory, CreateDirectory, Move):
//   wrap the BCL call (File.Delete / Directory.*) in try-catch(IOException) — Wine's
//   spurious exceptions are swallowed, real work is already done.
// - was FIX 8 — CreateLongPathFileStream: NOP the invalid-handle `newobj IOException;
//   throw` (first occurrence) — Wine hands back handles it then calls invalid.
//
// IDEMPOTENCY / MARKER: FIX-1/8 shapes are implicit (NOP'd pairs no longer match).
// FIX-2's wrap is positive: an IOException catch handler on those specific methods
// (original SDK has none there) — IsApplied uses it.

using Mono.Cecil;
using Mono.Cecil.Cil;
using System.Linq;

namespace Cs2MacPatcher.Fixes;

// was FIX 1: NOP IOException throws after P/Invoke calls in long-path methods.
sealed class ModIoPInvokeThrows : PdxFix
{
    public override string Id => "mod-io-errors";

    static readonly string[] Fix1Targets = { "DeleteLongPathFile", "DeleteLongPathDirectory", "CreateLongPathDirectory", "LongPathMove" };

    public override void Apply(PatchContext ctx)
    {
        var diskIO = PdxIl.DiskIo(ctx.Module);
        if (diskIO == null) return;
        foreach (var methodName in Fix1Targets)
        {
            var method = diskIO.Methods.FirstOrDefault(m => m.Name == methodName);
            if (method?.HasBody != true) continue;
            var instr = method.Body.Instructions.ToList();
            for (int i = 0; i < instr.Count; i++)
            {
                if (instr[i].OpCode != OpCodes.Throw) continue;
                if (i < 1 || instr[i - 1].OpCode != OpCodes.Newobj) continue;
                var ctor = instr[i - 1].Operand as MethodReference;
                if (ctor == null || !ctor.DeclaringType.Name.Contains("IOException")) continue;
                if (!ctx.DryRun) { instr[i - 1].OpCode = OpCodes.Nop; instr[i - 1].Operand = null; instr[i].OpCode = OpCodes.Nop; instr[i].Operand = null; }
                ctx.Applied++;
            }
        }
    }
}

// was FIX 2: wrap BCL calls in try-catch(IOException) for short-path methods.
sealed class ModIoBclCallWraps : PdxFix
{
    public override string Id => "mod-io-errors";

    static readonly (string Method, string Type, string Call)[] Fix2Targets =
    {
        ("Delete",          "System.IO.File",      "Delete"),
        ("DeleteDirectory", "System.IO.Directory", "Delete"),
        ("CreateDirectory", "System.IO.Directory", "CreateDirectory"),
        ("Move",            "System.IO.Directory", "Move"),
    };

    public override bool IsApplied(ModuleDefinition module)
    {
        var diskIO = PdxIl.DiskIo(module);
        return diskIO != null && Fix2Targets
            .Select(t => diskIO.Methods.FirstOrDefault(m => m.Name == t.Method))
            .Any(m => m?.HasBody == true
                && m.Body.ExceptionHandlers.Any(h => h.CatchType?.Name == "IOException"));
    }

    public override void Apply(PatchContext ctx)
    {
        var diskIO = PdxIl.DiskIo(ctx.Module);
        if (diskIO == null) return;
        var ioExceptionRef = PdxIl.IoExceptionRef(ctx.Module);

        foreach (var (methodName, typeName, callName) in Fix2Targets)
        {
            var method = diskIO.Methods.FirstOrDefault(m => m.Name == methodName);
            if (method?.HasBody != true) continue;
            var instr = method.Body.Instructions;
            Instruction? targetCall = null, retAfter = null;
            for (int i = 0; i < instr.Count; i++)
            {
                if (instr[i].OpCode != OpCodes.Call) continue;
                var target = instr[i].Operand as MethodReference;
                if (target == null || target.DeclaringType.FullName != typeName || target.Name != callName) continue;
                targetCall = instr[i]; retAfter = instr[i + 1]; break;
            }
            if (targetCall == null) continue;
            // Idempotency: skip if this method was already wrapped (has an IOException catch).
            if (method.Body.ExceptionHandlers.Any(h => h.CatchType?.Name == "IOException")) continue;

            var afterHandler = retAfter!.OpCode == OpCodes.Pop
                ? instr[instr.IndexOf(retAfter) + 1]
                : retAfter;

            if (!ctx.DryRun)
            {
                var il = method.Body.GetILProcessor();
                var tryLeave = il.Create(OpCodes.Leave_S, afterHandler);
                if (retAfter.OpCode == OpCodes.Pop) il.InsertAfter(retAfter, tryLeave); else il.InsertAfter(targetCall, tryLeave);
                var catchPop = il.Create(OpCodes.Pop);
                il.InsertAfter(tryLeave, catchPop);
                var catchLeave = il.Create(OpCodes.Leave_S, afterHandler);
                il.InsertAfter(catchPop, catchLeave);
                method.Body.ExceptionHandlers.Add(new ExceptionHandler(ExceptionHandlerType.Catch)
                {
                    TryStart = targetCall, TryEnd = catchPop,
                    HandlerStart = catchPop, HandlerEnd = afterHandler,
                    CatchType = ioExceptionRef
                });
            }
            ctx.Applied++;
        }
    }
}

// was FIX 8: CreateLongPathFileStream — NOP the invalid-handle IOException.
sealed class ModIoInvalidHandleThrow : PdxFix
{
    public override string Id => "mod-io-errors";

    public override void Apply(PatchContext ctx)
    {
        var clpfs = PdxIl.DiskIo(ctx.Module)?.Methods.FirstOrDefault(m => m.Name == "CreateLongPathFileStream");
        if (clpfs?.HasBody != true) return;

        var il = clpfs.Body.Instructions.ToList();
        for (int i = 0; i < il.Count; i++)
        {
            if (il[i].OpCode != OpCodes.Throw) continue;
            if (i < 1 || il[i - 1].OpCode != OpCodes.Newobj) continue;
            var ctor = il[i - 1].Operand as MethodReference;
            if (ctor == null || !ctor.DeclaringType.Name.Contains("IOException")) continue;
            if (!ctx.DryRun) { il[i - 1].OpCode = OpCodes.Nop; il[i - 1].Operand = null; il[i].OpCode = OpCodes.Nop; il[i].Operand = null; }
            ctx.Applied++; break;
        }
    }
}
