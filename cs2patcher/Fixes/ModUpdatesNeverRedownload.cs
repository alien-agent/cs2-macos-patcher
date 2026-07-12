// Mod updates never re-download (stale download state)                  slug: mod-updates-never-redownload   was: FIX 9, 10, 14
//
// TARGET: PDX.SDK.dll — RemoteRepository (DownloadFilesInManifest, FileAlreadyDownloaded),
//         Executor.InstallToFolder
//
// SYMPTOM: mods stuck on old versions or half-installed — the SDK believes files are
// already downloaded/installed (they aren't, or they're stale) and skips the work.
//
// ROOT CAUSE: the SDK's "already have it" checks trust file-existence and version reads
// that Wine lies about, so downloads and installs get skipped based on phantom state.
//
// FIX (per historical fix):
// - was FIX 9 — DownloadFilesInManifest state machine: the awaited FileAlreadyDownloaded
//   result feeds a brfalse; pop the result and branch unconditionally to the download
//   path — always re-download.
// - was FIX 10 — InstallToFolder state machine: NOP the GetInstalledVersion
//   success-check early exit so installation proceeds regardless of the (lying) version
//   probe.
// - was FIX 14 — FileAlreadyDownloaded: replace the whole body with
//   `return Task.FromResult(false)` — also prevents CheckIntegrity from acquiring a
//   reader lock on a non-existent file. Superseded by the ModDownloadsFreeze root-cause
//   fixes on v1.5.8f1+, kept for older-version compatibility.
//
// IDEMPOTENCY / MARKER: FIX-9/10 implicit (patterns gone after rewrite). FIX-14 explicit
// and positive: the exact 3-instruction body `ldc.i4.0; call Task.FromResult; ret` —
// IsApplied uses it.

using Mono.Cecil;
using Mono.Cecil.Cil;
using System.Linq;

namespace Cs2MacPatcher.Fixes;

// was FIX 9: DownloadFilesInManifest — always re-download.
sealed class ModRedownloadManifestFiles : PdxFix
{
    public override string Id => "mod-updates-never-redownload";

    public override void Apply(PatchContext ctx)
    {
        var remoteRepo = ctx.Module.Types.FirstOrDefault(t => t.Name == "RemoteRepository");
        var dfimSM = remoteRepo?.NestedTypes.FirstOrDefault(t => t.Name.Contains("DownloadFilesInManifest"));
        var moveNext = dfimSM?.Methods.FirstOrDefault(m => m.Name == "MoveNext");
        if (moveNext?.HasBody != true) return;

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
            if (!ctx.DryRun) { il[i + 1].OpCode = OpCodes.Pop; il[i + 1].Operand = null; il[i + 2].OpCode = OpCodes.Br; il[i + 2].Operand = downloadTarget; }
            ctx.Applied++; break;
        }
    }
}

// was FIX 10: InstallToFolder — bypass the GetInstalledVersion error exit.
sealed class ModRedownloadInstallVersion : PdxFix
{
    public override string Id => "mod-updates-never-redownload";

    public override void Apply(PatchContext ctx)
    {
        var executor = ctx.Module.Types.FirstOrDefault(t => t.Name == "Executor");
        var installSM = executor?.NestedTypes.FirstOrDefault(t => t.Name == "<InstallToFolder>d__13");
        var moveNext = installSM?.Methods.FirstOrDefault(m => m.Name == "MoveNext");
        if (moveNext?.HasBody != true) return;

        var il = moveNext.Body.Instructions;
        for (int i = 0; i < il.Count - 5; i++)
        {
            if (il[i].OpCode != OpCodes.Callvirt) continue;
            var mr = il[i].Operand as MethodReference;
            if (mr?.Name != "get_Success") continue;
            if (il[i + 1].OpCode != OpCodes.Brtrue_S && il[i + 1].OpCode != OpCodes.Brtrue) continue;
            if (il[i + 4].OpCode != OpCodes.Leave && il[i + 4].OpCode != OpCodes.Leave_S) continue;
            if (!ctx.DryRun) { il[i + 1].OpCode = OpCodes.Pop; il[i + 1].Operand = null; il[i + 2].OpCode = OpCodes.Nop; il[i + 2].Operand = null; il[i + 3].OpCode = OpCodes.Nop; il[i + 3].Operand = null; il[i + 4].OpCode = OpCodes.Nop; il[i + 4].Operand = null; }
            ctx.Applied++; break;
        }
    }
}

// was FIX 14: FileAlreadyDownloaded — always return Task<false>.
sealed class ModRedownloadFileCheck : PdxFix
{
    public override string Id => "mod-updates-never-redownload";

    static MethodDefinition? FindTarget(ModuleDefinition module) =>
        module.Types.FirstOrDefault(t => t.Name == "RemoteRepository")
            ?.Methods.FirstOrDefault(m => m.Name == "FileAlreadyDownloaded");

    static bool HasMarker(MethodDefinition? fad)
    {
        var body = fad?.Body?.Instructions;
        return body != null && body.Count == 3 && body[0].OpCode == OpCodes.Ldc_I4_0
            && body[1].OpCode == OpCodes.Call
            && (body[1].Operand as MethodReference)?.Name == "FromResult";
    }

    public override bool IsApplied(ModuleDefinition module) => HasMarker(FindTarget(module));

    public override void Apply(PatchContext ctx)
    {
        var module = ctx.Module;
        var fad = FindTarget(module);
        if (fad?.HasBody != true) return;
        if (HasMarker(fad)) return;                         // already patched

        if (!ctx.DryRun)
        {
            var mscorlib = module.AssemblyReferences.First(r => r.Name == "mscorlib");
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
        ctx.Applied++;
    }
}
