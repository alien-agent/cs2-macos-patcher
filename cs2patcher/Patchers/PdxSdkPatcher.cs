// Patches PDX.SDK.dll — enables Paradox Mods in-game browser on macOS/Wine
//
// FIX 1-6:  DiskIODefaultWindows — Wine lies about P/Invoke results and file/path existence
// FIX 7:    CancellationToken checks — force never-cancelled (broad safety net)
// FIX 8:    CreateLongPathFileStream — NOP invalid-handle IOException
// FIX 9:    DownloadFilesInManifest — always re-download (belt-and-suspenders workaround)
// FIX 10:   InstallToFolder — bypass GetInstalledVersion error
// FIX 11:   TaskCanceledException — treat as regular exception, not user cancellation
// FIX 12:   IsCancelledOperation — force false everywhere
// FIX 13:   PerformDownload — skip PathExists (Wine lies: returns true for non-existent files)
// FIX 14:   FileAlreadyDownloaded — always return false (prevents lock-acquire on missing file)
// FIX 15:   GetLockToken — remove Win32 timer-based 10s timeout (fires in milliseconds under Wine)
// FIX 16:   CreateFileStream MoveNext — dispose reader lock in IOException catch (prevents deadlock)
// FIX 17:   ListFiles/ListDirectories — wrap body in try-catch(IOException) returning empty
//           list (Wine throws IOException: Success for non-existent directories). NOTE:
//           ListFilesRecursive is deliberately NOT patched — on v1.6.0f1 its empty return
//           hangs the init-time mod scan (see docs/technical.md FIX 17).
//
// FIX 15, 16, 17 are the root-cause fixes discovered on v1.5.8f1+ where FIX 7/9/14 alone
// were insufficient. FIX 14 is kept as a safety net for older versions.
// FIX 7 and FIX 12 include a wholesale-replace fallback when the surgical NOP approach
// would clobber an early-return `ret` (see ModsDownloadProgressController.get_IsPaused).

using Mono.Cecil;
using Mono.Cecil.Cil;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace Cs2MacPatcher;

static class PdxSdkPatcher
{
    // Debug/bisection switch: set env CS2_PDX_SKIP="7,12,17" to skip those FIX numbers.
    // Used to isolate which PDX.SDK fix breaks on a given game version.
    static readonly HashSet<int> _skip = ParseSkip();

    static HashSet<int> ParseSkip()
    {
        var set = new HashSet<int>();
        var raw = Environment.GetEnvironmentVariable("CS2_PDX_SKIP");
        if (!string.IsNullOrWhiteSpace(raw))
            foreach (var part in raw.Split(','))
                if (int.TryParse(part.Trim(), out var n)) set.Add(n);
        return set;
    }

    static bool SkipFix(int n) => _skip.Contains(n);

    public static PatchSummary Patch(string managedDir, bool dryRun)
    {
        var dllPath = Path.Combine(managedDir, "PDX.SDK.dll");
        if (!File.Exists(dllPath))
            return PatchSummary.Skipped("PDX.SDK.dll not found");

        var module = ModuleDefinition.ReadModule(dllPath,
            new ReaderParameters { ReadingMode = ReadingMode.Immediate });

        var diskIO = module.Types.FirstOrDefault(t => t.Name == "DiskIODefaultWindows");
        if (diskIO == null)
        {
            module.Dispose();
            return PatchSummary.Skipped("DiskIODefaultWindows not found — wrong DLL?");
        }

        var mscorlib = module.AssemblyReferences.First(r => r.Name == "mscorlib");
        var ioExceptionRef = new TypeReference("System.IO", "IOException", module, mscorlib);

        int applied = 0;

        // ---- FIX 1: NOP IOException throws after P/Invoke calls in long-path methods ----
        string[] fix1Targets = { "DeleteLongPathFile", "DeleteLongPathDirectory", "CreateLongPathDirectory", "LongPathMove" };
        if (!SkipFix(1)) foreach (var methodName in fix1Targets)
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
                if (!dryRun) { instr[i - 1].OpCode = OpCodes.Nop; instr[i - 1].Operand = null; instr[i].OpCode = OpCodes.Nop; instr[i].Operand = null; }
                applied++;
            }
        }

        // ---- FIX 2: Wrap BCL calls in try-catch(IOException) for short-path methods ----
        var fix2Targets = new[] {
            ("Delete",          "System.IO.File",      "Delete"),
            ("DeleteDirectory", "System.IO.Directory", "Delete"),
            ("CreateDirectory", "System.IO.Directory", "CreateDirectory"),
            ("Move",            "System.IO.Directory", "Move"),
        };
        if (!SkipFix(2)) foreach (var (methodName, typeName, callName) in fix2Targets)
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

            if (!dryRun)
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
            applied++;
        }

        // ---- FIX 3: CreateLongPathDirectory — skip PathExists check per segment ----
        if (!SkipFix(3)) ApplyPathExistsBypass(diskIO, "CreateLongPathDirectory", nopBefore: 2, dryRun, ref applied);

        // ---- FIX 4: CreateDirectory — skip PathExists early exit ----
        if (!SkipFix(4)) ApplyPathExistsBypass(diskIO, "CreateDirectory", nopBefore: 2, dryRun, ref applied);

        // ---- FIX 5: CreateWriteStream — always create parent directory ----
        // (fileIO is declared here ungated — FIX 15/16 need it regardless of skip)
        var fileIO = module.Types.FirstOrDefault(t => t.Name == "FileIO");
        if (!SkipFix(5) && fileIO != null) ApplyPathExistsBypass(fileIO, "CreateWriteStream", nopBefore: 3, dryRun, ref applied);

        // ---- FIX 6: GetLongPath — replace '/' with '\\' in Replace call ----
        if (!SkipFix(6))
        {
            var getLongPath = diskIO.Methods.FirstOrDefault(m => m.Name == "GetLongPath");
            if (getLongPath?.HasBody == true)
            {
                var il = getLongPath.Body.Instructions;
                for (int i = 0; i < il.Count - 1; i++)
                {
                    if (il[i].OpCode != OpCodes.Ldc_I4_S || (sbyte)il[i].Operand != 47) continue;
                    if (il[i + 1].OpCode != OpCodes.Ldsfld) continue;
                    var field = il[i + 1].Operand as FieldReference;
                    if (field?.Name != "DirectorySeparatorChar") continue;
                    if (!dryRun) il[i].Operand = (sbyte)92;
                    applied++;
                }
            }
        }

        // ---- FIX 7: CancellationToken checks — force IsCancellationRequested = false ----
        if (!SkipFix(7)) foreach (var type in module.Types)
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
                    if (!dryRun)
                    {
                        var prev = il[i - 1];

                        // Surgical NOP-ing would clobber an early-return `ret` (e.g. the null-check
                        // branch in ModsDownloadProgressController.get_IsPaused), causing fall-through
                        // and a stack imbalance the verifier rejects with InvalidProgramException.
                        // For bool-returning methods in this shape, replace the body wholesale.
                        if (prev.OpCode == OpCodes.Ret &&
                            method.ReturnType.MetadataType == MetadataType.Boolean)
                        {
                            ReplaceWithReturnFalse(method);
                            applied++;
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
                    applied++;
                }
            }
        }

        // ---- FIX 8: CreateLongPathFileStream — NOP invalid-handle IOException ----
        if (!SkipFix(8))
        {
            var clpfs = diskIO.Methods.FirstOrDefault(m => m.Name == "CreateLongPathFileStream");
            if (clpfs?.HasBody == true)
            {
                var il = clpfs.Body.Instructions.ToList();
                for (int i = 0; i < il.Count; i++)
                {
                    if (il[i].OpCode != OpCodes.Throw) continue;
                    if (i < 1 || il[i - 1].OpCode != OpCodes.Newobj) continue;
                    var ctor = il[i - 1].Operand as MethodReference;
                    if (ctor == null || !ctor.DeclaringType.Name.Contains("IOException")) continue;
                    if (!dryRun) { il[i - 1].OpCode = OpCodes.Nop; il[i - 1].Operand = null; il[i].OpCode = OpCodes.Nop; il[i].Operand = null; }
                    applied++; break;
                }
            }
        }

        // ---- FIX 9: DownloadFilesInManifest — always re-download ----
        if (!SkipFix(9))
        {
            var remoteRepo = module.Types.FirstOrDefault(t => t.Name == "RemoteRepository");
            var dfimSM = remoteRepo?.NestedTypes.FirstOrDefault(t => t.Name.Contains("DownloadFilesInManifest"));
            var moveNext = dfimSM?.Methods.FirstOrDefault(m => m.Name == "MoveNext");
            if (moveNext?.HasBody == true)
            {
                var il = moveNext.Body.Instructions;
                for (int i = 0; i < il.Count - 1; i++)
                {
                    if (il[i].OpCode != OpCodes.Call && il[i].OpCode != OpCodes.Callvirt) continue;
                    var mr = il[i].Operand as MethodReference;
                    if (mr?.Name != "GetResult") continue;
                    if (il[i + 1].OpCode != OpCodes.Brfalse_S && il[i + 1].OpCode != OpCodes.Brfalse) continue;
                    if (!mr.DeclaringType.FullName.Contains("TaskAwaiter")) continue;
                    if (mr.DeclaringType is not GenericInstanceType git || git.GenericArguments.Count == 0) continue;
                    if (git.GenericArguments[0].FullName != "System.Boolean") continue;
                    var downloadTarget = (Instruction)il[i + 1].Operand;
                    if (!dryRun) { il[i + 1].OpCode = OpCodes.Pop; il[i + 1].Operand = null; il[i + 2].OpCode = OpCodes.Br; il[i + 2].Operand = downloadTarget; }
                    applied++; break;
                }
            }
        }

        // ---- FIX 10: InstallToFolder — bypass GetInstalledVersion error ----
        if (!SkipFix(10))
        {
            var executor = module.Types.FirstOrDefault(t => t.Name == "Executor");
            var installSM = executor?.NestedTypes.FirstOrDefault(t => t.Name == "<InstallToFolder>d__13");
            var moveNext = installSM?.Methods.FirstOrDefault(m => m.Name == "MoveNext");
            if (moveNext?.HasBody == true)
            {
                var il = moveNext.Body.Instructions;
                for (int i = 0; i < il.Count - 5; i++)
                {
                    if (il[i].OpCode != OpCodes.Callvirt) continue;
                    var mr = il[i].Operand as MethodReference;
                    if (mr?.Name != "get_Success") continue;
                    if (il[i + 1].OpCode != OpCodes.Brtrue_S && il[i + 1].OpCode != OpCodes.Brtrue) continue;
                    if (il[i + 4].OpCode != OpCodes.Leave && il[i + 4].OpCode != OpCodes.Leave_S) continue;
                    if (!dryRun) { il[i + 1].OpCode = OpCodes.Pop; il[i + 1].Operand = null; il[i + 2].OpCode = OpCodes.Nop; il[i + 2].Operand = null; il[i + 3].OpCode = OpCodes.Nop; il[i + 3].Operand = null; il[i + 4].OpCode = OpCodes.Nop; il[i + 4].Operand = null; }
                    applied++; break;
                }
            }
        }

        // ---- FIX 11: TaskCanceledException → treat as regular exception ----
        if (!SkipFix(11))
        {
            var resultFactory = module.Types.FirstOrDefault(t => t.Name == "ResultFactory");
            var method = resultFactory?.Methods.FirstOrDefault(m => m.Name == "CreateFileIoResultFromException");
            if (method?.HasBody == true)
            {
                var il = method.Body.Instructions;
                for (int i = 0; i < il.Count - 2; i++)
                {
                    if (il[i].OpCode != OpCodes.Isinst) continue;
                    var typeRef = il[i].Operand as TypeReference;
                    if (typeRef == null || !typeRef.Name.Contains("TaskCanceledException")) continue;
                    if (il[i + 1].OpCode != OpCodes.Brfalse_S && il[i + 1].OpCode != OpCodes.Brfalse) break;
                    if (!dryRun) { il[i].OpCode = OpCodes.Nop; il[i].Operand = null; il[i + 1].OpCode = il[i + 1].OpCode == OpCodes.Brfalse_S ? OpCodes.Br_S : OpCodes.Br; }
                    applied++; break;
                }
            }
        }

        // ---- FIX 12: IsCancelledOperation checks — force false ----
        if (!SkipFix(12)) foreach (var type in module.Types)
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
                    if (!dryRun)
                    {
                        var prev = il[i - 1];

                        // Same safety as FIX 7: surgical NOP of an early-return `ret` would
                        // cause fall-through. Replace bool-returning bodies wholesale instead.
                        if (prev.OpCode == OpCodes.Ret &&
                            method.ReturnType.MetadataType == MetadataType.Boolean)
                        {
                            ReplaceWithReturnFalse(method);
                            applied++;
                            bodyReplaced = true;
                            break;
                        }

                        if (prev.OpCode == OpCodes.Ldfld && i >= 3 && il[i - 2].OpCode == OpCodes.Ldfld) { il[i - 3].OpCode = OpCodes.Nop; il[i - 3].Operand = null; il[i - 2].OpCode = OpCodes.Nop; il[i - 2].Operand = null; il[i - 1].OpCode = OpCodes.Nop; il[i - 1].Operand = null; }
                        else if (prev.OpCode == OpCodes.Ldfld && i >= 2) { il[i - 2].OpCode = OpCodes.Nop; il[i - 2].Operand = null; il[i - 1].OpCode = OpCodes.Nop; il[i - 1].Operand = null; }
                        else { il[i - 1].OpCode = OpCodes.Nop; il[i - 1].Operand = null; }
                        il[i].OpCode = OpCodes.Ldc_I4_0; il[i].Operand = null;
                    }
                    applied++;
                }
            }
        }

        // ---- FIX 13: PerformDownload — skip PathExists (Wine returns true for non-existent files) ----
        if (!SkipFix(13))
        {
            var fileDownloader = module.Types.FirstOrDefault(t => t.Name == "FileDownloader");
            var pdSM = fileDownloader?.NestedTypes.FirstOrDefault(t => t.Name.Contains("PerformDownload"));
            var moveNext = pdSM?.Methods.FirstOrDefault(m => m.Name == "MoveNext");
            if (moveNext?.HasBody == true)
            {
                var il = moveNext.Body.Instructions;
                for (int i = 0; i < il.Count - 1; i++)
                {
                    if (il[i].OpCode != OpCodes.Callvirt && il[i].OpCode != OpCodes.Call) continue;
                    var mr = il[i].Operand as MethodReference;
                    if (mr?.Name != "PathExists") continue;
                    if (il[i + 1].OpCode != OpCodes.Brfalse_S && il[i + 1].OpCode != OpCodes.Brfalse) continue;
                    var target = (Instruction)il[i + 1].Operand;
                    if (!dryRun) { for (int j = i - 4; j <= i; j++) { il[j].OpCode = OpCodes.Nop; il[j].Operand = null; } il[i + 1].OpCode = OpCodes.Br; il[i + 1].Operand = target; }
                    applied++; break;
                }
            }
        }

        // ---- FIX 14: FileAlreadyDownloaded — always return Task<false> ----
        // Prevents CheckIntegrity from acquiring a reader lock on a non-existent file.
        // On v1.5.8f1+ this is superseded by FIX 16, but kept for older version compatibility.
        if (!SkipFix(14))
        {
            var remoteRepo = module.Types.FirstOrDefault(t => t.Name == "RemoteRepository");
            var fad = remoteRepo?.Methods.FirstOrDefault(m => m.Name == "FileAlreadyDownloaded");
            if (fad?.HasBody == true)
            {
                var fbody = fad.Body.Instructions;
                bool alreadyFad = fbody.Count == 3 && fbody[0].OpCode == OpCodes.Ldc_I4_0
                    && fbody[1].OpCode == OpCodes.Call
                    && (fbody[1].Operand as MethodReference)?.Name == "FromResult";
                if (!alreadyFad && !dryRun)
                {
                    var taskType = new TypeReference("System.Threading.Tasks", "Task", module, mscorlib);
                    var fromResultOpen = new MethodReference("FromResult", module.TypeSystem.Void, taskType);
                    var genParam = new GenericParameter("TResult", fromResultOpen);
                    fromResultOpen.GenericParameters.Add(genParam);
                    fromResultOpen.ReturnType = new GenericInstanceType(
                        new TypeReference("System.Threading.Tasks", "Task`1", module, mscorlib))
                    { GenericArguments = { genParam } };
                    fromResultOpen.Parameters.Add(new ParameterDefinition(genParam));
                    var fromResultBool = new GenericInstanceMethod(fromResultOpen);
                    fromResultBool.GenericArguments.Add(module.TypeSystem.Boolean);
                    var fromResultRef = module.ImportReference(fromResultBool);

                    fad.Body.Instructions.Clear();
                    fad.Body.ExceptionHandlers.Clear();
                    fad.Body.Variables.Clear();
                    var ilp = fad.Body.GetILProcessor();
                    ilp.Append(ilp.Create(OpCodes.Ldc_I4_0));
                    ilp.Append(ilp.Create(OpCodes.Call, fromResultRef));
                    ilp.Append(ilp.Create(OpCodes.Ret));
                }
                if (!alreadyFad) applied++;
            }
        }

        // ---- FIX 15: GetLockToken — remove Win32 timer-based 10s timeout ----
        // CancellationTokenSource(TimeSpan.FromSeconds(10)) uses a Win32 waitable timer
        // which fires in milliseconds under Wine, cancelling every download attempt.
        // Fix: replace body with ldarg.1; ret — return the input token unchanged.
        if (!SkipFix(15))
        {
            var getLockToken = fileIO?.Methods.FirstOrDefault(m => m.Name == "GetLockToken");
            if (getLockToken?.HasBody == true)
            {
                var gbody = getLockToken.Body.Instructions;
                bool alreadyGlt = gbody.Count == 2
                    && gbody[0].OpCode == OpCodes.Ldarg_1 && gbody[1].OpCode == OpCodes.Ret;
                if (!alreadyGlt && !dryRun)
                {
                    getLockToken.Body.Instructions.Clear();
                    getLockToken.Body.ExceptionHandlers.Clear();
                    getLockToken.Body.Variables.Clear();
                    var ilp = getLockToken.Body.GetILProcessor();
                    ilp.Append(ilp.Create(OpCodes.Ldarg_1));
                    ilp.Append(ilp.Create(OpCodes.Ret));
                }
                if (!alreadyGlt) applied++;
            }
        }

        // ---- FIX 16: CreateFileStream MoveNext — dispose reader lock in IOException catch ----
        // Wine's File.Exists returns true for non-existent files → CreateFileStream acquires a
        // reader lock, then tries to open the file → FileNotFoundException → exception handler
        // returns without calling lockResult.Dispose() → _readSemaphore stays at 0 → next
        // AcquireWriterLock blocks on _readSemaphore forever. All subsequent downloads hang.
        // Fix: insert lockResult.Dispose() before each leave in the IOException catch block.
        if (!SkipFix(16))
        {
            var createFsSM = fileIO?.NestedTypes.FirstOrDefault(t => t.Name.Contains("CreateFileStream"));
            var moveNext = createFsSM?.Methods.FirstOrDefault(m => m.Name == "MoveNext");
            if (moveNext?.HasBody == true)
            {
                var lockVar = moveNext.Body.Variables
                    .FirstOrDefault(v => v.VariableType.Name == "AcquireLockResult");
                var lockResultType = module.Types
                    .Concat(module.Types.SelectMany(t => t.NestedTypes))
                    .FirstOrDefault(t => t.Name == "AcquireLockResult");
                var disposeRef = lockResultType?.Methods.FirstOrDefault(m => m.Name == "Dispose");

                if (lockVar != null && disposeRef != null)
                {
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

                    if (targets.Count > 0)
                    {
                        if (!dryRun)
                        {
                            var ilp = moveNext.Body.GetILProcessor();
                            var dispose = module.ImportReference(disposeRef);
                            foreach (var leave in targets)
                            {
                                ilp.InsertBefore(leave, ilp.Create(OpCodes.Ldloc_S, lockVar));
                                ilp.InsertBefore(leave, ilp.Create(OpCodes.Callvirt, dispose));
                            }
                        }
                        applied++;
                    }
                }
            }
        }

        // ---- FIX 17: ListFiles/ListDirectories — IOException: Success on Wine ----
        // Wine's Directory.GetFiles/GetDirectories throws IOException with empty/"Success" message
        // when the directory doesn't exist (instead of returning empty or throwing
        // DirectoryNotFoundException). The PathExists early-exit guard is useless because Wine's
        // GetFileAttributes lies. Fix: wrap each method body in try-catch(IOException) returning
        // an empty list. The existing newobj List<string>::.ctor() reference is reused.
        // ListFilesRecursive is intentionally NOT wrapped: on v1.6.0f1 its empty return hangs
        // the init-time mod scan; it's left to throw (and be caught by PerformDiskOperationAndCatch).
        if (!SkipFix(17)) foreach (var name in new[] { "ListFiles", "ListDirectories" })
        {
            var method = diskIO.Methods.FirstOrDefault(m => m.Name == name);
            if (method?.HasBody != true) continue;

            // Find the existing List<string>::.ctor() ref used by the !PathExists early branch
            var listCtor = method.Body.Instructions
                .Where(i => i.OpCode == OpCodes.Newobj)
                .Select(i => i.Operand as MethodReference)
                .FirstOrDefault(mr => mr?.Name == ".ctor" && mr.DeclaringType.Name == "List`1");
            if (listCtor == null) continue;

            // Skip if already wrapped (heuristic: method already has an IOException catch)
            if (method.Body.ExceptionHandlers.Any(h => h.CatchType?.Name == "IOException")) continue;

            if (!dryRun) WrapBodyReturningListInTryCatch(method, listCtor, ioExceptionRef);
            applied++;
        }

        if (applied == 0)
        {
            module.Dispose();
            return PatchSummary.AlreadyPatched("PDX.SDK.dll");
        }

        if (!dryRun)
        {
            PatchIo.BackupAndWrite(module, dllPath);
            return new PatchSummary("PDX.SDK.dll", applied, DryRun: false);
        }

        module.Dispose();
        return new PatchSummary("PDX.SDK.dll", applied, DryRun: true);
    }

    static void ReplaceWithReturnFalse(MethodDefinition method)
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
    static void WrapBodyReturningListInTryCatch(MethodDefinition method, MethodReference listCtor, TypeReference ioException)
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

    static void ApplyPathExistsBypass(TypeDefinition type, string methodName, int nopBefore, bool dryRun, ref int applied)
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
            if (!dryRun)
                for (int j = i - nopBefore; j <= i + 1; j++)
                { il[j].OpCode = OpCodes.Nop; il[j].Operand = null; }
            applied++; break;
        }
    }

}
