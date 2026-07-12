// In-game "IOEXCEPTION: …: Success" error dialog on missing files     slug: error-dialog-on-missing-files   was: FIX 20
//
// TARGET: Colossal.IO.dll — System.IO.Helper.GetExceptionFromWin32Error(int, string, string)
//
// SYMPTOM: An in-game IOEXCEPTION overlay (Continue / Save & Quit / Quit) reading e.g.
//   "Failed to read settings file … Benchmark.coc: Success" / "… Editor.coc: Success",
// with a stack through Colossal.IO.AssetDatabase.FileSystemDataSource.GetReadStream →
// System.IO.LongFile.OpenRead → LongFile.GetFileHandle. Dismissable (the game is fine),
// but pops whenever an optional file is absent — commonly on quit.
//
// ROOT CAUSE: every failed Colossal.IO file op routes the OS error code through
// GetExceptionFromWin32Error, which maps 2→FileNotFoundException, 3→DirectoryNotFound,
// 5→UnauthorizedAccess, 15→DriveNotFound, 206→PathTooLong, 995→OperationCanceled,
// 123→ArgumentException — and anything unmapped → generic IOException. When a file
// legitimately can't be opened under Wine, GetLastError() reports 0 (ERROR_SUCCESS)
// instead of 2 (FILE_NOT_FOUND). Code 0 isn't in the map, so the failure becomes
// "IOException: …: Success". The callers (AssetDatabase.SettingsFileExists,
// DefaultAssetFactory.CreateSettingAssets, FileSystemDataSource.PopulateFromDirectory)
// only catch FileNotFoundException, so the IOException escapes to the error overlay.
// On real Windows the same missing file yields code 2 and is swallowed silently.
//
// FIX: prepend `if (errorCode == 0) errorCode = 2;` so a Wine "Success" failure maps to
// the FileNotFoundException the callers already handle. One spot repairs the whole
// ": Success" exception family across every LongFile/LongDirectory operation, and only
// changes behaviour in the Wine-bug case — a genuine Windows failure never reports 0.
//
//   IL:  ldarg.0; brtrue.s orig; ldc.i4.2; starg.s errorCode; orig: <original body>
//
// IDEMPOTENCY / MARKER: the original body starts `ldarg.0; call GetMessageFromErrorCode`;
// ours starts `ldarg.0; brtrue…` — a positive signature, also used by IsApplied.

using Mono.Cecil;
using Mono.Cecil.Cil;
using System.Linq;

namespace Cs2MacPatcher.Fixes;

sealed class ErrorDialogOnMissingFiles : Fix
{
    public override string Id => "error-dialog-on-missing-files";
    public override string TargetDll => "Colossal.IO.dll";

    static MethodDefinition? FindTarget(ModuleDefinition module)
    {
        var helper = module.Types.FirstOrDefault(t => t.Name == "Helper" && t.Namespace == "System.IO");
        return helper?.Methods.FirstOrDefault(m =>
            m.Name == "GetExceptionFromWin32Error" && m.IsStatic
            && m.Parameters.Count == 3 && m.Parameters[0].ParameterType.MetadataType == MetadataType.Int32);
    }

    static bool HasMarker(MethodDefinition? method)
    {
        var ins = method?.Body?.Instructions;
        return ins != null && ins.Count >= 2 && ins[0].OpCode == OpCodes.Ldarg_0
            && (ins[1].OpCode == OpCodes.Brtrue || ins[1].OpCode == OpCodes.Brtrue_S);
    }

    public override bool IsApplied(ModuleDefinition module) => HasMarker(FindTarget(module));

    public override void Apply(PatchContext ctx)
    {
        var method = FindTarget(ctx.Module);
        if (method?.HasBody != true) return;
        if (HasMarker(method)) return;                      // already patched

        if (!ctx.DryRun)
        {
            var il = method.Body.GetILProcessor();
            var origFirst = method.Body.Instructions[0];
            var errorCode = method.Parameters[0];
            il.InsertBefore(origFirst, Instruction.Create(OpCodes.Ldarg_0));
            il.InsertBefore(origFirst, Instruction.Create(OpCodes.Brtrue, origFirst));
            il.InsertBefore(origFirst, Instruction.Create(OpCodes.Ldc_I4_2));
            il.InsertBefore(origFirst, Instruction.Create(OpCodes.Starg, errorCode));
        }
        ctx.Applied++;
    }
}
