// Elevated networks snap down onto structures below them               slug: elevated-networks-snap   was: FIX 19
//
// TARGET: Game.dll — OnUpdate of Game.Tools.NetToolSystem, Game.Tools.CourseSplitSystem,
//         Game.Tools.ValidationSystem (extendable via CS2_NETSNAP_EXTRA, see below)
//
// SYMPTOM: while placing networks on Apple Silicon, elevated bridges, power lines and
// water pipes snap down onto the road/structure below instead of keeping their elevation
// — snapping behaves purely 2D, as if the height (Y) axis didn't exist.
//
// CREDIT: the root cause below was discovered by icetear — see
// https://github.com/icetear/cs2-net-snap-fix. This fix is built on that insight; thanks!
//
// ROOT CAUSE: the snap/height checks run inside Burst-compiled jobs. On Apple Silicon the
// AOT SIMD code in lib_burst_generated.dll misbehaves under Rosetta 2 — the Y lane is
// dropped from the comparison. The managed (Mono) version of the same jobs computes
// correctly. icetear's default variant wraps EVERY system in
// Game.Net/Game.Tools/Game.Objects gated on a stateful flag toggled in
// OnStartRunning/OnStopRunning — dozens of systems run managed and main-thread-synced
// every frame while the net tool is selected (and forever, if the flag sticks), which is
// the reported performance regression.
//
// FIX: wrap ONLY the tool-phase systems that compute snapping/course heights,
// unconditionally, no flag. ECS only calls their OnUpdate while a tool is doing work, so
// the cost during normal play is zero (the methods are not called) and nothing can get
// stuck. The wrap (tool-system shape; void systems complete this.Dependency instead):
//
//   saved = BurstCompiler.Options.EnableBurstCompilation;
//   BurstCompiler.Options.EnableBurstCompilation = false;   // jobs schedule managed
//   try { <original body; every ret → stloc retVal + leave> }
//   finally {
//       retVal.Complete();     // run the jobs NOW, while Burst is still off: they execute
//                              // parallel-managed on worker threads — correct height math,
//                              // no single-thread crash
//       BurstCompiler.Options.EnableBurstCompilation = saved;
//   }
//   return retVal;
//
// Why this works (verified against the real assemblies):
// - set_EnableBurstCompilation calls JobsUtility.set_JobCompilerEnabled (checked in the
//   shipped Unity.Burst.dll IL) — Unity's execution-time dispatch switch; even
//   AOT-Burst-compiled jobs run their managed IL while it is off.
// - ToolBaseSystem.OnUpdate() is literally `Dependency = this.OnUpdate(Dependency)`, so
//   completing the RETURNED handle inside the finally covers the whole job chain.
// - NetToolSystem alone was NOT sufficient in-game; CourseSplitSystem (the CourseHeight*
//   jobs) + ValidationSystem were required. Everything else — simulation, traffic,
//   rendering — stays on Burst uninterrupted.
//
// IL details: SimplifyMacros() before inserting (a short-form branch near its ±127-byte
// limit would otherwise overflow), every ret rewritten IN PLACE to `stloc retVal` +
// `leave` (void shape: `leave` directly — keeps existing branch targets valid), a finally
// ExceptionHandler registered over the original body, OptimizeMacros() after.
//
// IDEMPOTENCY / MARKER: the original OnUpdate never touches Burst compiler options, so a
// body referencing set_EnableBurstCompilation is ours — per system (extending the system
// list later re-wraps only the additions); also the IsApplied signature. Safety guards,
// per system: skipped if the method shape changed, if it already carries the wrap, if it
// has its own exception handlers (nested handlers corrupt IL), or — void shape — if it
// schedules no jobs.
//
// HISTORY/NOTES: CS2_NETSNAP_EXTRA="Full.Type.Name,…" wraps additional systems without
// recompiling — the escalation path if a future game version moves the snap math
// elsewhere. Verified in-game 2026-07-09 (bridges hold height, FPS unchanged).

using Mono.Cecil;
using Mono.Cecil.Cil;
using Mono.Cecil.Rocks;
using System;
using System.Collections.Generic;
using System.Linq;

namespace Cs2MacPatcher.Fixes;

sealed class ElevatedNetworksSnapToGround : Fix
{
    public override string Id => "elevated-networks-snap";
    public override string TargetDll => "Game.dll";

    // All tool-phase systems: they only do work while a tool is editing, so wrapping them
    // unconditionally costs nothing during normal play. NetToolSystem owns the snap jobs
    // (SnapJob & co.); CourseSplitSystem owns the course height computation (CourseHeight*
    // jobs); ValidationSystem validates placement against existing geometry.
    static readonly string[] Systems = BuildSystems();

    static string[] BuildSystems()
    {
        var list = new List<string>
        {
            "Game.Tools.NetToolSystem",
            "Game.Tools.CourseSplitSystem",
            "Game.Tools.ValidationSystem",
        };
        var extra = Environment.GetEnvironmentVariable("CS2_NETSNAP_EXTRA");
        if (!string.IsNullOrWhiteSpace(extra))
            foreach (var s in extra.Split(','))
                if (s.Trim() is { Length: > 0 } name && !list.Contains(name))
                    list.Add(name);
        return list.ToArray();
    }

    static bool HasWrapMarker(MethodDefinition m) =>
        m.Body.Instructions.Any(i =>
            i.Operand is MethodReference mr && mr.Name == "set_EnableBurstCompilation");

    public override bool IsApplied(ModuleDefinition module) =>
        Systems
            .Select(n => module.Types.FirstOrDefault(t => t.FullName == n))
            .Any(t => t != null && t.Methods.Any(m =>
                m.Name == "OnUpdate" && m.HasBody && HasWrapMarker(m)));

    public override void Apply(PatchContext ctx)
    {
        foreach (var sysName in Systems)
        {
            var sys = ctx.Module.Types.FirstOrDefault(t => t.FullName == sysName);
            if (sys != null)
                WrapOnUpdateManaged(ctx, sys);
        }
    }

    // Wraps a system's OnUpdate in try/finally: Burst off on entry, the scheduled jobs
    // force-completed (they execute parallel-managed, with correct math) and the Burst
    // state restored in the finally. Handles both CS2 update shapes:
    //   (a) tool systems:  JobHandle OnUpdate(JobHandle)  -> complete the RETURNED handle
    //   (b) plain systems: void OnUpdate()                -> complete this.Dependency
    static void WrapOnUpdateManaged(PatchContext ctx, TypeDefinition sys)
    {
        var module = ctx.Module;
        var toolUpdate = sys.Methods.FirstOrDefault(m =>
            m.Name == "OnUpdate" && m.HasBody
            && m.Parameters.Count == 1 && m.ReturnType.Name == "JobHandle");
        var voidUpdate = sys.Methods.FirstOrDefault(m =>
            m.Name == "OnUpdate" && m.HasBody
            && m.Parameters.Count == 0 && m.ReturnType.MetadataType == MetadataType.Void);
        var onUpdate = toolUpdate ?? voidUpdate;
        if (onUpdate == null) return;                       // shape changed in a game update
        bool isVoid = onUpdate == voidUpdate && toolUpdate == null;

        if (HasWrapMarker(onUpdate)) return;                // already patched

        // Hard invariant: never nest our finally inside existing handlers (IL corruption).
        if (onUpdate.Body.HasExceptionHandlers) return;

        // Void systems must actually schedule jobs, or the wrap is pure overhead.
        if (isVoid && !onUpdate.Body.Instructions.Any(i =>
                (i.OpCode == OpCodes.Call || i.OpCode == OpCodes.Callvirt)
                && i.Operand is MethodReference smr
                && (smr.Name == "Schedule" || smr.Name == "ScheduleParallel")))
            return;

        // Resolve the members we inject calls to. Unity.Burst / Unity.Entities live next
        // to Game.dll in the Managed dir, so the resolver returns the real assemblies. If
        // one is absent/reshaped, First() throws — and TypeReference.Resolve() returns
        // null when the resolver handed back an empty stub (the fallback for genuinely
        // absent assemblies), so that path is null-checked explicitly. Either way we
        // skip: better no fix than a broken reference.
        FieldReference optionsField;
        MethodReference getEnable, setEnable, jhComplete, getDependency = null!;
        TypeReference jobHandleRef;
        try
        {
            var resolver = ctx.Resolver!;
            var burstRef = module.AssemblyReferences.First(a => a.Name == "Unity.Burst");
            var burst = resolver.Resolve(burstRef).MainModule;
            var compiler = burst.Types.First(t => t.FullName == "Unity.Burst.BurstCompiler");
            var options = burst.Types.First(t => t.FullName == "Unity.Burst.BurstCompilerOptions");
            optionsField = module.ImportReference(compiler.Fields.First(f => f.Name == "Options"));
            getEnable = module.ImportReference(options.Methods.First(m => m.Name == "get_EnableBurstCompilation"));
            setEnable = module.ImportReference(options.Methods.First(m => m.Name == "set_EnableBurstCompilation"));

            if (isVoid)
            {
                var entitiesRef = module.AssemblyReferences.First(a => a.Name == "Unity.Entities");
                var entities = resolver.Resolve(entitiesRef).MainModule;
                var sysBase = entities.Types.First(t => t.FullName == "Unity.Entities.SystemBase");
                var getDepDef = sysBase.Methods.First(m => m.Name == "get_Dependency" && m.Parameters.Count == 0);
                getDependency = module.ImportReference(getDepDef);
                jobHandleRef = module.ImportReference(getDepDef.ReturnType);
                var jobHandleDef = getDepDef.ReturnType.Resolve();
                if (jobHandleDef == null) return;           // JobHandle's assembly is a stub
                jhComplete = module.ImportReference(jobHandleDef.Methods
                    .First(m => m.Name == "Complete" && m.Parameters.Count == 0));
            }
            else
            {
                jobHandleRef = onUpdate.ReturnType;         // Unity.Jobs.JobHandle
                var jobHandleDef = jobHandleRef.Resolve();
                if (jobHandleDef == null) return;           // JobHandle's assembly is a stub
                jhComplete = module.ImportReference(jobHandleDef.Methods
                    .First(m => m.Name == "Complete" && m.Parameters.Count == 0));
            }
        }
        catch (InvalidOperationException) { return; }       // Unity assembly missing/reshaped
        catch (AssemblyResolutionException) { return; }

        if (ctx.DryRun) { ctx.Applied++; return; }

        var body = onUpdate.Body;
        // Expand short-form branches before inserting instructions: a br.s whose target sits
        // near the ±127-byte limit would otherwise overflow and Cecil writes a corrupt offset.
        // OptimizeMacros() at the end re-picks the smallest valid encoding.
        body.SimplifyMacros();
        var il = body.GetILProcessor();
        var origFirst = body.Instructions[0];

        body.InitLocals = true;                             // finally may see default(JobHandle)
        var saved = new VariableDefinition(module.TypeSystem.Boolean);
        var handleVar = new VariableDefinition(jobHandleRef);  // retVal (tool) / depTmp (void)
        body.Variables.Add(saved);
        body.Variables.Add(handleVar);

        // Exit sequence after the finally: (tool) ldloc retVal; ret  |  (void) ret
        var retIns = Instruction.Create(OpCodes.Ret);
        var afterFinally = isVoid ? retIns : Instruction.Create(OpCodes.Ldloc, handleVar);

        // Every ret inside the body -> (tool: stloc retVal;) leave afterFinally. Rewriting
        // the ret in place keeps existing branch targets valid.
        foreach (var ins in body.Instructions.Where(i => i.OpCode == OpCodes.Ret).ToList())
        {
            if (isVoid)
            {
                ins.OpCode = OpCodes.Leave;
                ins.Operand = afterFinally;
            }
            else
            {
                ins.OpCode = OpCodes.Stloc;
                ins.Operand = handleVar;
                il.InsertAfter(ins, Instruction.Create(OpCodes.Leave, afterFinally));
            }
        }

        // finally: complete the scheduled jobs NOW — Burst is still off, so they execute
        // parallel-managed with correct math — THEN restore the saved Burst state.
        // Complete() on a default(JobHandle) (exception before first assignment) is a no-op.
        Instruction finallyStart;
        if (isVoid)
        {
            finallyStart = Instruction.Create(OpCodes.Ldarg_0);
            il.Append(finallyStart);
            il.Append(Instruction.Create(OpCodes.Callvirt, getDependency));  // this.Dependency
            il.Append(Instruction.Create(OpCodes.Stloc, handleVar));
            il.Append(Instruction.Create(OpCodes.Ldloca, handleVar));
        }
        else
        {
            finallyStart = Instruction.Create(OpCodes.Ldloca, handleVar);
            il.Append(finallyStart);
        }
        il.Append(Instruction.Create(OpCodes.Call, jhComplete));
        il.Append(Instruction.Create(OpCodes.Ldsfld, optionsField));
        il.Append(Instruction.Create(OpCodes.Ldloc, saved));
        il.Append(Instruction.Create(OpCodes.Callvirt, setEnable));
        il.Append(Instruction.Create(OpCodes.Endfinally));
        il.Append(afterFinally);
        if (!isVoid) il.Append(retIns);

        // Prologue (outside the try): saved = Options.EnableBurstCompilation; Options.… = false;
        foreach (var ins in new[]
        {
            Instruction.Create(OpCodes.Ldsfld, optionsField),
            Instruction.Create(OpCodes.Callvirt, getEnable),
            Instruction.Create(OpCodes.Stloc, saved),
            Instruction.Create(OpCodes.Ldsfld, optionsField),
            Instruction.Create(OpCodes.Ldc_I4_0),
            Instruction.Create(OpCodes.Callvirt, setEnable),
        }) il.InsertBefore(origFirst, ins);

        body.ExceptionHandlers.Add(new ExceptionHandler(ExceptionHandlerType.Finally)
        {
            TryStart = origFirst,
            TryEnd = finallyStart,
            HandlerStart = finallyStart,
            HandlerEnd = afterFinally,
        });
        body.OptimizeMacros();
        ctx.Applied++;
    }
}
