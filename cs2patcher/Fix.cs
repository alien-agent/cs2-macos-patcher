// The fix abstraction. Every patch this tool applies is a Fix subclass living in
// Fixes/<WhatItFixes>.cs — files are named for the player-visible problem they fix,
// and each file's header comment is the CANONICAL documentation for that fix
// (symptom, root cause, IL before/after, idempotency marker, history).
//
// A Fix mutates the already-loaded module; it never reads or writes disk. DllPatcher
// owns the one-read/one-write lifecycle per DLL and applies every registered fix for
// that DLL to the same in-memory module.

using Mono.Cecil;

namespace Cs2MacPatcher;

abstract class Fix
{
    // Slug identifying the fix, e.g. "elevated-networks-snap". Used by the CS2_SKIP
    // debug env var (see DllPatcher) and in docs. Sub-fixes that ship as one unit
    // share their file's slug, so CS2_SKIP disables the whole meaning-group.
    public abstract string Id { get; }

    // Which DLL this fix patches, e.g. "Game.dll".
    public abstract string TargetDll { get; }

    // POSITIVE structural signature of our patch in the module (e.g. the exact
    // instruction shape we emit and original compilers never do). Used to detect
    // "this DLL is already ours" so BackupAndWrite preserves the pristine .bak on
    // incremental applies. Absence-of-pattern is NOT a marker — a game update also
    // removes patterns — so fixes with only implicit idempotency keep the default.
    public virtual bool IsApplied(ModuleDefinition module) => false;

    // Apply the fix to ctx.Module. Must be idempotent (re-running on an already
    // patched module finds nothing to do) and must honor ctx.DryRun (count the
    // sites that WOULD change via ctx.Applied, but do not mutate).
    public abstract void Apply(PatchContext ctx);
}

// Shared state for one DLL's patch pass.
class PatchContext
{
    public required ModuleDefinition Module { get; init; }
    // Resolver used to load the module; fixes that import members from sibling
    // assemblies (e.g. Unity.Burst) resolve through it. Null for DLLs read without
    // a custom resolver (PDX.SDK, Colossal.IO — parity with the original patchers).
    public IAssemblyResolver? Resolver { get; init; }
    public bool DryRun { get; init; }
    // Total patch sites applied (or, in a dry run, that would be applied) across
    // all fixes for this DLL. Drives the OK/DRY/SKIP summary counts.
    public int Applied;
}
