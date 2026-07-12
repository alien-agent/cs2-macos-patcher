// Generic per-DLL orchestrator: owns the read-once / write-once lifecycle and runs
// every registered fix for one DLL against the same in-memory module.
//
// Flow (the pattern proven by the original per-DLL patchers):
//   1. read the module once (Immediate: a corrupt assembly fails before disk is touched)
//   2. wrong-DLL guard: at least one sentinel type must exist
//   3. alreadyOurs = any fix's positive marker present → BackupAndWrite must PRESERVE
//      the existing .bak (an incremental apply on an already-patched DLL must never
//      clobber the pristine original in the backup)
//   4. apply every fix registered for this DLL, in registry order
//   5. applied == 0 → AlreadyPatched; else write (with backup) or report the dry run
//
// Debug/bisection: CS2_SKIP="slug,slug" skips fixes by Id — the tool that isolates
// which fix breaks on a new game version (how the v1.6.0f1 mod-scan hang was found).
// This is the ONLY place skipping exists; fix files never reference it.

using Mono.Cecil;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace Cs2MacPatcher;

// How the module should be read — parity with the original patchers; do not unify
// without re-verifying output (the resolver affects how member refs are written).
enum ResolverKind
{
    None,       // plain read (PDX.SDK, Colossal.IO)
    SearchDir,  // DefaultAssemblyResolver + managedDir (Colossal.IO.AssetDatabase)
    Fallback,   // FallbackAssemblyResolver — stubs unresolvable Unity refs (Game.dll)
}

// One patchable DLL: name, wrong-DLL sentinel types (any present = right DLL), reader kind.
record DllTarget(string Dll, string[] SentinelTypes, ResolverKind Resolver);

static class DllPatcher
{
    static readonly HashSet<string> _skip = ParseSkip();

    static HashSet<string> ParseSkip()
    {
        var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var raw = Environment.GetEnvironmentVariable("CS2_SKIP");
        if (!string.IsNullOrWhiteSpace(raw))
            foreach (var part in raw.Split(','))
                if (part.Trim() is { Length: > 0 } slug)
                    set.Add(slug);
        return set;
    }

    public static PatchSummary Patch(string managedDir, DllTarget target, IReadOnlyList<Fix> allFixes, bool dryRun)
    {
        var dllPath = Path.Combine(managedDir, target.Dll);
        if (!File.Exists(dllPath))
            return PatchSummary.Skipped($"{target.Dll} not found");

        IAssemblyResolver? resolver = target.Resolver switch
        {
            ResolverKind.SearchDir => MakeSearchDirResolver(managedDir),
            ResolverKind.Fallback => new FallbackAssemblyResolver(managedDir),
            _ => null,
        };
        var readerParams = new ReaderParameters { ReadingMode = ReadingMode.Immediate };
        if (resolver != null)
        {
            readerParams.AssemblyResolver = resolver;
            readerParams.ReadSymbols = false;
        }
        var module = ModuleDefinition.ReadModule(dllPath, readerParams);

        if (!target.SentinelTypes.Any(s => module.Types.Any(t => t.Name == s)))
        {
            module.Dispose();
            return PatchSummary.Skipped($"{string.Join("/", target.SentinelTypes)} not found — wrong DLL?");
        }

        var fixes = allFixes.Where(f => f.TargetDll == target.Dll).ToList();

        // Detect our own patch markers BEFORE applying anything: if the on-disk DLL
        // already carries any of our fixes, it is NOT this game version's pristine
        // original, and the existing .bak (which is) must be preserved.
        bool alreadyOurs = fixes.Any(f => f.IsApplied(module));

        var ctx = new PatchContext { Module = module, Resolver = resolver, DryRun = dryRun };
        foreach (var fix in fixes)
        {
            if (_skip.Contains(fix.Id)) continue;
            fix.Apply(ctx);
        }

        if (ctx.Applied == 0)
        {
            module.Dispose();
            return PatchSummary.AlreadyPatched(target.Dll);
        }

        if (!dryRun)
        {
            PatchIo.BackupAndWrite(module, dllPath, preserveExistingBackup: alreadyOurs);
            return new PatchSummary(target.Dll, ctx.Applied, DryRun: false);
        }

        module.Dispose();
        return new PatchSummary(target.Dll, ctx.Applied, DryRun: true);
    }

    static DefaultAssemblyResolver MakeSearchDirResolver(string managedDir)
    {
        var r = new DefaultAssemblyResolver();
        r.AddSearchDirectory(managedDir);
        return r;
    }
}
