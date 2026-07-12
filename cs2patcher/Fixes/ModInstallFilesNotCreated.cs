// Mod files and directories never get created; downloads write nowhere  slug: mod-install-files-not-created   was: FIX 3, 4, 5, 6, 13
//
// TARGET: PDX.SDK.dll — DiskIODefaultWindows (CreateLongPathDirectory, CreateDirectory,
//         GetLongPath), FileIO.CreateWriteStream, FileDownloader.PerformDownload
//
// SYMPTOM: mod installs silently produce nothing — directories aren't created, download
// streams have no file to write into, downloads bail out early.
//
// ROOT CAUSE: two Wine lies. (1) PathExists returns true for paths that don't exist
// (GetFileAttributes lies when the parent directory exists), so every "skip if it
// already exists" guard skips the CREATION of things that don't exist. (2) GetLongPath
// normalizes separators with the wrong slash direction under Wine.
//
// FIX (per historical fix):
// - was FIX 3 — CreateLongPathDirectory: NOP the per-segment PathExists+brtrue guard so
//   each segment is always created (CreateDirectory on an existing dir is harmless).
// - was FIX 4 — CreateDirectory: same bypass for the early-exit guard.
// - was FIX 5 — FileIO.CreateWriteStream: same bypass — the parent directory is always
//   created before opening the write stream.
// - was FIX 6 — GetLongPath: the `Replace('/', Path.DirectorySeparatorChar)` call has its
//   '/' (47) literal rewritten to '\' (92) so path normalization matches what the Wine
//   filesystem layer expects.
// - was FIX 13 — FileDownloader.PerformDownload state machine: NOP the PathExists check
//   (plus its 4 argument-load instructions) and branch unconditionally to the download
//   path — never "resume" a file that doesn't actually exist.
//
// IDEMPOTENCY / MARKER: all implicit — NOP'd guards / rewritten operands no longer match
// their search patterns on re-runs. No positive IsApplied marker (see Fix.IsApplied);
// this DLL's strong "ours" signals live in ModDownloadsFreeze / ModUpdatesNeverRedownload /
// FreshInstallModScanFails.

using Mono.Cecil;
using Mono.Cecil.Cil;
using System.Linq;

namespace Cs2MacPatcher.Fixes;

// was FIX 3: CreateLongPathDirectory — skip PathExists check per segment.
sealed class ModInstallLongPathSegments : PdxFix
{
    public override string Id => "mod-install-files-not-created";
    public override void Apply(PatchContext ctx)
    {
        var diskIO = PdxIl.DiskIo(ctx.Module);
        if (diskIO != null) PdxIl.ApplyPathExistsBypass(ctx, diskIO, "CreateLongPathDirectory", nopBefore: 2);
    }
}

// was FIX 4: CreateDirectory — skip PathExists early exit.
sealed class ModInstallCreateDirectory : PdxFix
{
    public override string Id => "mod-install-files-not-created";
    public override void Apply(PatchContext ctx)
    {
        var diskIO = PdxIl.DiskIo(ctx.Module);
        if (diskIO != null) PdxIl.ApplyPathExistsBypass(ctx, diskIO, "CreateDirectory", nopBefore: 2);
    }
}

// was FIX 5: FileIO.CreateWriteStream — always create the parent directory.
sealed class ModInstallCreateWriteStream : PdxFix
{
    public override string Id => "mod-install-files-not-created";
    public override void Apply(PatchContext ctx)
    {
        var fileIO = PdxIl.FileIo(ctx.Module);
        if (fileIO != null) PdxIl.ApplyPathExistsBypass(ctx, fileIO, "CreateWriteStream", nopBefore: 3);
    }
}

// was FIX 6: GetLongPath — replace '/' with '\' in the separator-normalizing Replace call.
sealed class ModInstallPathSeparators : PdxFix
{
    public override string Id => "mod-install-files-not-created";
    public override void Apply(PatchContext ctx)
    {
        var getLongPath = PdxIl.DiskIo(ctx.Module)?.Methods.FirstOrDefault(m => m.Name == "GetLongPath");
        if (getLongPath?.HasBody != true) return;

        var il = getLongPath.Body.Instructions;
        for (int i = 0; i < il.Count - 1; i++)
        {
            if (il[i].OpCode != OpCodes.Ldc_I4_S || (sbyte)il[i].Operand != 47) continue;
            if (il[i + 1].OpCode != OpCodes.Ldsfld) continue;
            var field = il[i + 1].Operand as FieldReference;
            if (field?.Name != "DirectorySeparatorChar") continue;
            if (!ctx.DryRun) il[i].Operand = (sbyte)92;
            ctx.Applied++;
        }
    }
}

// was FIX 13: PerformDownload — skip PathExists (Wine returns true for non-existent files).
sealed class ModInstallDownloadPathExists : PdxFix
{
    public override string Id => "mod-install-files-not-created";
    public override void Apply(PatchContext ctx)
    {
        var fileDownloader = ctx.Module.Types.FirstOrDefault(t => t.Name == "FileDownloader");
        var pdSM = fileDownloader?.NestedTypes.FirstOrDefault(t => t.Name.Contains("PerformDownload"));
        var moveNext = pdSM?.Methods.FirstOrDefault(m => m.Name == "MoveNext");
        if (moveNext?.HasBody != true) return;

        var il = moveNext.Body.Instructions;
        for (int i = 0; i < il.Count - 1; i++)
        {
            if (il[i].OpCode != OpCodes.Callvirt && il[i].OpCode != OpCodes.Call) continue;
            var mr = il[i].Operand as MethodReference;
            if (mr?.Name != "PathExists") continue;
            if (il[i + 1].OpCode != OpCodes.Brfalse_S && il[i + 1].OpCode != OpCodes.Brfalse) continue;
            var target = (Instruction)il[i + 1].Operand;
            if (!ctx.DryRun) { for (int j = i - 4; j <= i; j++) { il[j].OpCode = OpCodes.Nop; il[j].Operand = null; } il[i + 1].OpCode = OpCodes.Br; il[i + 1].Operand = target; }
            ctx.Applied++; break;
        }
    }
}
