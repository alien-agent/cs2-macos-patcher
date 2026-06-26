// Patches Game.dll — Fix 20: RiderPathLocator.GetToolboxRiderRootPath File.Exists lie
//
// Symptom (Player.log, after Fix 18+19):
//
//   [SceneFlow] [ERROR]  A platform service integration failed to initialize
//   ...
//   DirectoryNotFoundException: Could not find a part of the path
//     "C:\users\crossover\AppData\Local\JetBrains\Toolbox\.settings.json".
//     at System.IO.FileStream..ctor (...)
//     at Game.Modding.Toolchain.Dependencies.RiderPathLocator.GetToolboxRiderRootPath
//     at Game.Modding.Toolchain.Dependencies.RiderPathLocator.GetToolboxBaseDir
//     at Game.Modding.Toolchain.Dependencies.RiderPathLocator.CollectRiderInfosWindows
//     at Game.Modding.Toolchain.Dependencies.RiderPathLocator.GetAllRiderPaths
//     ...
//
// Root cause: same Wine bug as the `.priority` file in AssetDatabase (see the
// header comment in AssetDatabasePatcher.cs for the full explanation — short
// version: Wine's `GetFileAttributesW` returns success for non-existent files
// when the parent directory exists, so `File.Exists(".settings.json")` returns
// `true` even though `JetBrains\Toolbox` doesn't exist). The code then calls
// `File.ReadAllText`, which throws `DirectoryNotFoundException` because the
// parent directory is missing.
//
// This exception propagates through `GetAllRiderPaths` → `RiderDependency.GetIDEVersion`
// → the toolchain background refresh. If the background task catches it as a
// generic Exception, the rider IDE is reported as not installed and the game
// continues; if not caught cleanly, it cancels Initialize and the game
// terminates with "GameManager termination requested before initialization
// completed" (logged by GameManager.cs).
//
// Fix: apply the same pattern as `AssetDatabasePatcher.Patch`:
//   - locate `ldstr ".settings.json"` followed by `call Path::Combine` followed
//     by `stloc.1` followed by `call File::Exists` followed by `brfalse`
//   - NOP the `call File::Exists` and change the `brfalse` to an unconditional
//     `br` so we always skip the `.settings.json` reading block
//
// The C# equivalent is:
//
//     // Original (broken under Wine):
//     if (File.Exists(settingsFile)) {
//         var installLoc = GetInstallLocationFromJson(File.ReadAllText(settingsFile));
//         if (!string.IsNullOrEmpty(installLoc)) path = installLoc;
//     }
//     return Path.Combine(path, "apps/Rider");
//
//     // Patched (Wine-safe): always skip the .settings.json block, return
//     // the default path ("<localAppData>/JetBrains/Toolbox/apps/Rider").
//     return Path.Combine(path, "apps/Rider");
//
// On a non-Wine machine without JetBrains Toolbox installed, the .settings.json
// block would also be skipped (since the file doesn't exist). The behaviour for
// the case "JetBrains Toolbox IS installed and we want its Rider paths" is
// degraded — we won't pick up the configured Rider install location. This is
// the same trade-off the existing `.priority` patch makes: it's a game-feature
// degradation (we don't know which Rider install JetBrains Toolbox prefers)
// in exchange for the game not crashing on macOS/Wine.
//
// Same Mono.Cecil IL-rewriting pattern as AssetDatabasePatcher: idempotent via
// the `ldstr ".settings.json"` sentinel being adjacent to the `brfalse`.

using Mono.Cecil;
using Mono.Cecil.Cil;
using System;
using System.IO;
using System.Linq;

namespace Cs2MacPatcher;

static class RiderPathLocatorPatcher
{
    public static PatchSummary Patch(string managedDir, bool dryRun)
    {
        var dllPath = Path.Combine(managedDir, "Game.dll");
        if (!File.Exists(dllPath))
            return PatchSummary.Skipped("Game.dll not found");

        var resolver = new FallbackAssemblyResolver(managedDir);
        var module = ModuleDefinition.ReadModule(dllPath, new ReaderParameters
        {
            ReadingMode = ReadingMode.Immediate,
            AssemblyResolver = resolver,
            ReadSymbols = false
        });

        var rpl = module.Types.FirstOrDefault(t =>
            t.FullName == "Game.Modding.Toolchain.Dependencies.RiderPathLocator");
        if (rpl == null)
        {
            module.Dispose();
            return PatchSummary.Skipped("RiderPathLocator class not found");
        }

        var method = rpl.Methods.FirstOrDefault(m => m.Name == "GetToolboxRiderRootPath");
        if (method == null || !method.HasBody)
        {
            module.Dispose();
            return PatchSummary.Skipped("RiderPathLocator.GetToolboxRiderRootPath body not found");
        }

        // Pattern we want (or, after a previous patch run, the equivalent
        // "already-patched" pattern):
        //
        //   ldstr "JetBrains/Toolbox"
        //   call Path::Combine
        //   stloc.0
        //   ldloc.0
        //   ldstr ".settings.json"
        //   call Path::Combine
        //   stloc.1
        //   ldloc.1              ← unpatched shape
        //   call File::Exists
        //   brfalse.s <skip>
        //
        //   …or, after Fix 20 has been applied once already…
        //
        //   ldstr "JetBrains/Toolbox"
        //   call Path::Combine
        //   stloc.0
        //   ldloc.0
        //   ldstr ".settings.json"
        //   call Path::Combine
        //   stloc.1
        //   nop                  ← already-patched shape (replaces ldloc.1)
        //   nop                  ← already-patched shape (replaces call File::Exists)
        //   br.s <skip>
        var instrs = method.Body.Instructions;
        int ldstrSettingsIndex = -1;
        bool alreadyPatched = false;
        for (int i = 0; i < instrs.Count - 6; i++)
        {
            if (instrs[i].OpCode != OpCodes.Ldstr) continue;
            if (instrs[i].Operand is not string s) continue;
            if (s != ".settings.json") continue;
            if (instrs[i + 1].OpCode != OpCodes.Call) continue;
            if (instrs[i + 1].Operand is not MethodReference mr1) continue;
            if (mr1.Name != "Combine" || mr1.DeclaringType.Name != "Path") continue;
            if (instrs[i + 2].OpCode != OpCodes.Stloc_1) continue;

            // Two shapes are valid: ldloc.1+call+brfalse (unpatched) or nop+nop+br (patched).
            bool unpatched = instrs[i + 3].OpCode == OpCodes.Ldloc_1 &&
                             instrs[i + 4].OpCode == OpCodes.Call &&
                             (instrs[i + 5].OpCode == OpCodes.Brfalse || instrs[i + 5].OpCode == OpCodes.Brfalse_S);
            bool patched   = instrs[i + 3].OpCode == OpCodes.Nop &&
                             instrs[i + 4].OpCode == OpCodes.Nop &&
                             (instrs[i + 5].OpCode == OpCodes.Br || instrs[i + 5].OpCode == OpCodes.Br_S);
            if (unpatched)
            {
                // Sanity-check that the unpatched `call` is File::Exists.
                if (instrs[i + 4].Operand is MethodReference mr2 &&
                    mr2.Name == "Exists" && mr2.DeclaringType.Name == "File")
                {
                    ldstrSettingsIndex = i;
                    alreadyPatched = false;
                    break;
                }
            }
            else if (patched)
            {
                ldstrSettingsIndex = i;
                alreadyPatched = true;
                break;
            }
        }

        if (ldstrSettingsIndex < 0)
        {
            module.Dispose();
            return PatchSummary.Skipped("RiderPathLocator .settings.json File.Exists pattern not found");
        }

        if (alreadyPatched)
        {
            module.Dispose();
            return PatchSummary.AlreadyPatched("Game.dll");
        }

        if (dryRun)
        {
            module.Dispose();
            return new PatchSummary("Game.dll", 1, DryRun: true);
        }

        // NOP the `ldloc.1` (we don't need settingsFile on the stack anymore since
        // we're skipping the entire File.Exists / ReadAllText block) AND the
        // `call File::Exists` (the Wine-lies bug), then turn the brfalse into an
        // unconditional br to skip the read block entirely.
        //
        // We must NOP both — leaving `ldloc.1` would leave settingsFile on the
        // stack when we branch, causing Mono's verifier to reject the IL with
        // InvalidProgramException ("Invalid IL code ... IL_003d: ret") because
        // the return value slot is occupied by leftover settingsFile instead of
        // the combined path.
        instrs[ldstrSettingsIndex + 3].OpCode = OpCodes.Nop;
        instrs[ldstrSettingsIndex + 3].Operand = null;
        instrs[ldstrSettingsIndex + 4].OpCode = OpCodes.Nop;
        instrs[ldstrSettingsIndex + 4].Operand = null;
        var br = instrs[ldstrSettingsIndex + 5];
        br.OpCode = br.OpCode == OpCodes.Brfalse_S ? OpCodes.Br_S : OpCodes.Br;

        BackupAndWrite(module, dllPath);
        return new PatchSummary("Game.dll", 1, DryRun: false);
    }

    static void BackupAndWrite(ModuleDefinition module, string dllPath) =>
        TimestampedBackup.BackupAndWrite(module, dllPath);
}
