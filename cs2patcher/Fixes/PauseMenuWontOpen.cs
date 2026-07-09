// In-game pause menu (Esc / gear) won't open                           slug: pause-menu-wont-open   was: FIX 18
//
// TARGET: Game.dll — Game.Modding.Toolchain.Dependencies.RiderPathLocator
//         (GetToolboxRiderRootPath, CollectPathsFromToolbox)
//
// SYMPTOM: neither Esc nor the gear icon opens the pause menu. Everything else works —
// gameplay, autosave, mods, even the Tab/Home developer menu (so overlays render; it's
// not a CrossOver fullscreen issue). With -uiDeveloperMode, pressing Esc produces no JS
// error and no cohtml activity — the menu simply never mounts.
//
// ROOT CAUSE: on game load the modding toolchain probes for a JetBrains Rider IDE. Two
// of RiderPathLocator's Windows-path methods gate a filesystem access behind an existence
// check that Wine lies about (returns true for paths that don't exist):
//
//   GetToolboxRiderRootPath:  if (File.Exists(".settings.json")) File.ReadAllText(...)
//   CollectPathsFromToolbox:  if (Directory.Exists(dir))         Directory.GetDirectories(dir)
//
// Under a fresh CrossOver prefix (no JetBrains Toolbox) the guard passes on the lie and
// the read/enumeration throws DirectoryNotFoundException. GetAllRiderPaths catches and
// logs it — the toolchain RESULT is unchanged — but the mere act of throwing during the
// probe leaves the pause-menu UI unable to open (proven by A/B test: creating the real
// .settings.json + an empty apps/ dir makes the menu work; removing them breaks it).
//
// FIX: force each existence check to return false — the truth under Wine — so the probe
// skips the read/enumeration and returns its empty default with no exception thrown.
// Replace the `call bool Exists(string)` in place with `pop` (drops the path argument)
// and insert `ldc.i4.0` after it; the following brfalse/brtrue takes the "does not
// exist" branch. In-place replacement keeps stack balance and branch targets intact.
//
//   IL:  ldloc.1; call File::Exists; brfalse End   →   ldloc.1; pop; ldc.i4.0; brfalse End
//
// Forcing "no IDE found" is correct and harmless under CrossOver — nobody runs JetBrains
// Rider inside a CS2 gaming bottle, and the game handles "no Rider" cleanly.
//
// IDEMPOTENCY / MARKER: the rewritten guard `pop; ldc.i4.0; br…` — a pop feeding a
// constant into a conditional branch — is a shape original compiler output never emits
// there; IsApplied uses it. Only an Exists whose result feeds a conditional branch is
// rewritten (a genuine guard), so a future non-guard Exists is left alone.

using Mono.Cecil;
using Mono.Cecil.Cil;
using System.Linq;

namespace Cs2MacPatcher.Fixes;

sealed class PauseMenuWontOpen : Fix
{
    public override string Id => "pause-menu-wont-open";
    public override string TargetDll => "Game.dll";

    static readonly (string Method, string ExistsDecl)[] Targets =
    {
        ("GetToolboxRiderRootPath", "System.IO.File"),
        ("CollectPathsFromToolbox", "System.IO.Directory"),
    };

    static TypeDefinition? Rider(ModuleDefinition module) =>
        module.Types.FirstOrDefault(t => t.Name == "RiderPathLocator");

    public override bool IsApplied(ModuleDefinition module)
    {
        var rider = Rider(module);
        return rider != null && Targets
            .Select(t => rider.Methods.FirstOrDefault(m => m.Name == t.Method))
            .Any(m => m?.HasBody == true && m.Body.Instructions.Any(i =>
                i.OpCode == OpCodes.Pop
                && i.Next?.OpCode == OpCodes.Ldc_I4_0
                && IsConditionalBranch(i.Next.Next)));
    }

    public override void Apply(PatchContext ctx)
    {
        var rider = Rider(ctx.Module);
        if (rider == null) return;
        foreach (var (methodName, existsDecl) in Targets)
            ForceExistsFalse(ctx, rider, methodName, existsDecl);
    }

    static void ForceExistsFalse(PatchContext ctx, TypeDefinition type, string methodName, string existsDeclFullName)
    {
        var method = type.Methods.FirstOrDefault(m => m.Name == methodName);
        if (method?.HasBody != true) return;

        // File/Directory.Exists are static → always `call`; match the full type name so an
        // unrelated same-named type can't be picked; require a following conditional branch.
        var call = method.Body.Instructions.FirstOrDefault(i =>
            i.OpCode == OpCodes.Call
            && i.Operand is MethodReference mr
            && mr.Name == "Exists"
            && mr.DeclaringType?.FullName == existsDeclFullName
            && IsConditionalBranch(i.Next));
        if (call == null) return;   // already patched (guard removed) or method changed

        if (!ctx.DryRun)
        {
            var ilp = method.Body.GetILProcessor();
            ilp.InsertAfter(call, ilp.Create(OpCodes.Ldc_I4_0));
            call.OpCode = OpCodes.Pop;
            call.Operand = null;
        }
        ctx.Applied++;
    }

    internal static bool IsConditionalBranch(Instruction? i) =>
        i != null && (i.OpCode == OpCodes.Brfalse || i.OpCode == OpCodes.Brfalse_S
                   || i.OpCode == OpCodes.Brtrue  || i.OpCode == OpCodes.Brtrue_S);
}
