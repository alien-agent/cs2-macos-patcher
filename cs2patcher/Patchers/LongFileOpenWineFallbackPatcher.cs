// Patches Colossal.IO.dll — AleanniMods approach: LongFile.Open with Wine fallback
//
// The AleanniMods/cities2patcher fork (which itself forked alien-agent/cs2-macos-patcher)
// has a much better fix than my original Fix 22 (LongPathNormalizePatcher) for the
// Wine "LongFile.Open throws IOException: Success" bug. The key difference:
//
//   My Fix 22:    NOP `call + throw` in LongPath.NormalizeLongPath. Caller sees
//                 null → treats path as "not found" → file never read.
//
//   Their fix:   Replace LongFile.Open's body with a try/catch around the original
//                 NormalizeLongPath + GetFileHandle path. On `IOException` with
//                 message "Success" (Wine's signature), retry with the `\\?\` long-path
//                 prefix stripped (using FileStreamWithDisposeCallback). The file is
//                 actually opened and read, which is critical for content files like
//                 `<DLC>/.ntl` whose decryption is required for the game UI to load.
//
// This is the same approach they describe in their docs/technical.md "LongFile.Open
// throws IOException: Success on Wine long paths" section:
//
//   try {
//       var normalizedPath = LongPath.NormalizeLongPath(path);
//       var handle = GetFileHandle(normalizedPath, guid, mode, access, share, options);
//       return new FileStreamWithHandle(handle, guid, access, bufferSize, async, disposeCallback);
//   }
//   catch (IOException ex) when (ex.Message.Contains("Success")) {
//       var fallbackPath = path.StartsWith(@"\\?\") ? path.Substring(4) : path;
//       return new FileStreamWithDisposeCallback(fallbackPath, guid, mode, access, share, bufferSize, async, disposeCallback);
//   }
//
// Plus a parallel fix for `LongFile.GetFileHandle` itself: if CreateFile returns an
// invalid handle with `GetLastWin32Error() == 0` and the path starts with `\\?\`,
// retry once with the prefix stripped.
//
// We also add (vs their fork):
//   - FallbackAssemblyResolver integration so cross-DLL loading works under
//     ReadingMode.Immediate (needed for v1.6.0f1 AssetDatabase.<PopulateFromDataSource>d__109)
//   - Idempotency checks (skip if `__Cs2MacPatcher_OpenWineFallback` already present and current)

using Mono.Cecil;
using Mono.Cecil.Cil;
using Mono.Cecil.Rocks;
using System;
using System.IO;
using System.Linq;

namespace Cs2MacPatcher;

static class LongFileOpenWineFallbackPatcher
{
    public static PatchSummary Patch(string managedDir, bool dryRun)
    {
        var dllPath = Path.Combine(managedDir, "Colossal.IO.dll");
        if (!File.Exists(dllPath))
            return PatchSummary.Skipped("Colossal.IO.dll not found");

        var resolver = new FallbackAssemblyResolver(managedDir);
        var module = ModuleDefinition.ReadModule(dllPath, new ReaderParameters
        {
            ReadingMode = ReadingMode.Immediate,
            AssemblyResolver = resolver,
            ReadSymbols = false
        });

        var longFileType = module.Types.FirstOrDefault(t => t.FullName == "System.IO.LongFile");
        if (longFileType == null)
        {
            module.Dispose();
            return PatchSummary.Skipped("System.IO.LongFile not found");
        }

        int applied = 0;
        applied += ApplyOpenFallback(module, longFileType, dryRun);
        applied += ApplyGetFileHandleRetry(module, longFileType, dryRun);

        if (applied == 0)
        {
            module.Dispose();
            return PatchSummary.AlreadyPatched("Colossal.IO.dll");
        }

        if (dryRun)
        {
            module.Dispose();
            return new PatchSummary("Colossal.IO.dll", applied, DryRun: true);
        }

        BackupAndWrite(module, dllPath);
        return new PatchSummary("Colossal.IO.dll", applied, DryRun: false);
    }

    // ──────────────────────────────────────────────────────────────────────────
    // LongFile.Open: try/catch fallback to non-prefixed path on "Success" IOException
    // ──────────────────────────────────────────────────────────────────────────

    static int ApplyOpenFallback(ModuleDefinition module, TypeDefinition longFileType, bool dryRun)
    {
        // Find the 7-arg overload: Open(string, FileMode, FileAccess, FileShare, int, FileOptions, Action)
        var open = longFileType.Methods.FirstOrDefault(m =>
            m.Name == "Open" &&
            m.HasBody &&
            m.Parameters.Count == 7 &&
            m.Parameters[0].ParameterType.MetadataType == MetadataType.String &&
            m.ReturnType.FullName == "System.IO.FileStream");
        if (open == null) return 0;

        // Idempotency: if `__Cs2MacPatcher_OpenWineFallback` helper exists with current shape
        // AND Open's body is already a one-line `call helper; ret`, skip.
        var existingHelper = longFileType.Methods.FirstOrDefault(m => m.Name == "__Cs2MacPatcher_OpenWineFallback");
        if (existingHelper != null && IsOpenWineFallbackHelperCurrent(existingHelper))
        {
            bool openUsesHelper = open.Body.Instructions.Any(i =>
                i.Operand is MethodReference mr &&
                mr.Name == "__Cs2MacPatcher_OpenWineFallback");
            if (openUsesHelper) return 0;
        }

        if (dryRun) return 1;

        if (existingHelper != null)
            longFileType.Methods.Remove(existingHelper);

        var helper = EnsureOpenWineFallbackHelper(module, longFileType);
        open.Body.Instructions.Clear();
        open.Body.ExceptionHandlers.Clear();
        open.Body.Variables.Clear();
        open.Body.InitLocals = false;
        open.Body.MaxStackSize = 8;

        var il = open.Body.GetILProcessor();
        for (int i = 0; i < open.Parameters.Count; i++)
            il.Append(il.Create(OpCodes.Ldarg, i));
        il.Append(il.Create(OpCodes.Call, helper));
        il.Append(il.Create(OpCodes.Ret));
        return 1;
    }

    static bool IsOpenWineFallbackHelperCurrent(MethodDefinition method)
    {
        var instructions = method.Body?.Instructions;
        if (instructions == null || instructions.Count < 2) return false;
        return instructions[^2].OpCode == OpCodes.Ldnull &&
               instructions[^1].OpCode == OpCodes.Ret;
    }

    static MethodReference EnsureOpenWineFallbackHelper(ModuleDefinition module, TypeDefinition longFileType)
    {
        const string helperName = "__Cs2MacPatcher_OpenWineFallback";

        var existing = longFileType.Methods.FirstOrDefault(m => m.Name == helperName);
        if (existing != null) return existing;

        var mscorlib = module.AssemblyReferences.First(r => r.Name == "mscorlib");
        var boolType = module.TypeSystem.Boolean;
        var intType = module.TypeSystem.Int32;
        var stringType = module.TypeSystem.String;
        var ioExceptionType = new TypeReference("System.IO", "IOException", module, mscorlib);
        var exceptionType = new TypeReference("System", "Exception", module, mscorlib);

        var longPathType = module.Types.FirstOrDefault(t => t.FullName == "System.IO.LongPath");
        var normalizeLongPath = longPathType?.Methods.FirstOrDefault(m =>
            m.Name == "NormalizeLongPath" && m.Parameters.Count == 1);
        var getFileHandle = longFileType.Methods.FirstOrDefault(m =>
            m.Name == "GetFileHandle" && m.Parameters.Count == 6);
        var fileStreamWithHandleCtor = longFileType.NestedTypes
            .FirstOrDefault(t => t.Name == "FileStreamWithHandle")
            ?.Methods.FirstOrDefault(m => m.Name == ".ctor" && m.Parameters.Count == 6);
        var fileStreamWithDisposeCallbackCtor = longFileType.NestedTypes
            .FirstOrDefault(t => t.Name == "FileStreamWithDisposeCallback")
            ?.Methods.FirstOrDefault(m => m.Name == ".ctor" && m.Parameters.Count == 8);

        if (normalizeLongPath == null || getFileHandle == null ||
            fileStreamWithHandleCtor == null || fileStreamWithDisposeCallbackCtor == null)
            throw new InvalidOperationException("Could not find LongFile members needed for Wine Open fallback.");

        var open = longFileType.Methods.First(m => m.Name == "Open" && m.Parameters.Count == 7);
        var fileStreamType = open.ReturnType;
        var fileModeType = open.Parameters[1].ParameterType;
        var fileAccessType = open.Parameters[2].ParameterType;
        var fileShareType = open.Parameters[3].ParameterType;
        var fileOptionsType = open.Parameters[5].ParameterType;
        var actionType = open.Parameters[6].ParameterType;
        var guidType = getFileHandle.Parameters[1].ParameterType;
        var safeFileHandleType = getFileHandle.ReturnType;

        var newGuid = new MethodReference("NewGuid", guidType, guidType);
        var getMessage = new MethodReference("get_Message", stringType, exceptionType) { HasThis = true };
        var contains = new MethodReference("Contains", boolType, stringType) { HasThis = true };
        contains.Parameters.Add(new ParameterDefinition(stringType));
        var startsWith = new MethodReference("StartsWith", boolType, stringType) { HasThis = true };
        startsWith.Parameters.Add(new ParameterDefinition(stringType));
        var substring = new MethodReference("Substring", stringType, stringType) { HasThis = true };
        substring.Parameters.Add(new ParameterDefinition(intType));

        var method = new MethodDefinition(
            helperName,
            MethodAttributes.Private | MethodAttributes.Static | MethodAttributes.HideBySig,
            fileStreamType);

        method.Parameters.Add(new ParameterDefinition("path", ParameterAttributes.None, stringType));
        method.Parameters.Add(new ParameterDefinition("mode", ParameterAttributes.None, fileModeType));
        method.Parameters.Add(new ParameterDefinition("access", ParameterAttributes.None, fileAccessType));
        method.Parameters.Add(new ParameterDefinition("share", ParameterAttributes.None, fileShareType));
        method.Parameters.Add(new ParameterDefinition("bufferSize", ParameterAttributes.None, intType));
        method.Parameters.Add(new ParameterDefinition("options", ParameterAttributes.None, fileOptionsType));
        method.Parameters.Add(new ParameterDefinition("disposeCallback", ParameterAttributes.None, actionType));

        method.Body.InitLocals = true;
        method.Body.MaxStackSize = 8;
        var guidLocal = new VariableDefinition(guidType);
        var normalizedPathLocal = new VariableDefinition(stringType);
        var handleLocal = new VariableDefinition(safeFileHandleType);
        var exceptionLocal = new VariableDefinition(ioExceptionType);
        method.Body.Variables.Add(guidLocal);
        method.Body.Variables.Add(normalizedPathLocal);
        method.Body.Variables.Add(handleLocal);
        method.Body.Variables.Add(exceptionLocal);

        var il = method.Body.GetILProcessor();
        var setDefaultBuffer = il.Create(OpCodes.Ldc_I4, 4096);
        var afterBufferCheck = il.Create(OpCodes.Ldarg_0);
        var tryStart = il.Create(OpCodes.Ldloc, normalizedPathLocal);
        var handlerStart = il.Create(OpCodes.Stloc, exceptionLocal);
        var fallback = il.Create(OpCodes.Ldarg_0);
        var useOriginalPath = il.Create(OpCodes.Ldarg_0);
        var gotFallbackPath = il.Create(OpCodes.Ldloc, guidLocal);
        var handlerEnd = il.Create(OpCodes.Ldnull);

        il.Append(il.Create(OpCodes.Call, newGuid));
        il.Append(il.Create(OpCodes.Stloc, guidLocal));
        il.Append(il.Create(OpCodes.Ldarg_S, method.Parameters[4]));
        il.Append(il.Create(OpCodes.Brfalse_S, setDefaultBuffer));
        il.Append(il.Create(OpCodes.Br_S, afterBufferCheck));
        il.Append(setDefaultBuffer);
        il.Append(il.Create(OpCodes.Starg_S, method.Parameters[4]));
        il.Append(afterBufferCheck);
        il.Append(il.Create(OpCodes.Call, normalizeLongPath));
        il.Append(il.Create(OpCodes.Stloc, normalizedPathLocal));

        il.Append(tryStart);
        il.Append(il.Create(OpCodes.Ldloc, guidLocal));
        il.Append(il.Create(OpCodes.Ldarg_1));
        il.Append(il.Create(OpCodes.Ldarg_2));
        il.Append(il.Create(OpCodes.Ldarg_3));
        il.Append(il.Create(OpCodes.Ldarg_S, method.Parameters[5]));
        il.Append(il.Create(OpCodes.Call, getFileHandle));
        il.Append(il.Create(OpCodes.Stloc, handleLocal));
        il.Append(il.Create(OpCodes.Ldloc, handleLocal));
        il.Append(il.Create(OpCodes.Ldloc, guidLocal));
        il.Append(il.Create(OpCodes.Ldarg_2));
        il.Append(il.Create(OpCodes.Ldarg_S, method.Parameters[4]));
        il.Append(il.Create(OpCodes.Ldarg_S, method.Parameters[5]));
        il.Append(il.Create(OpCodes.Ldc_I4, (int)FileOptions.Asynchronous));
        il.Append(il.Create(OpCodes.And));
        il.Append(il.Create(OpCodes.Ldc_I4, (int)FileOptions.Asynchronous));
        il.Append(il.Create(OpCodes.Ceq));
        il.Append(il.Create(OpCodes.Ldarg_S, method.Parameters[6]));
        il.Append(il.Create(OpCodes.Newobj, fileStreamWithHandleCtor));
        il.Append(il.Create(OpCodes.Ret));

        il.Append(handlerStart);
        il.Append(il.Create(OpCodes.Ldloc, exceptionLocal));
        il.Append(il.Create(OpCodes.Callvirt, getMessage));
        il.Append(il.Create(OpCodes.Ldstr, "Success"));
        il.Append(il.Create(OpCodes.Callvirt, contains));
        il.Append(il.Create(OpCodes.Brtrue_S, fallback));
        il.Append(il.Create(OpCodes.Rethrow));
        il.Append(fallback);
        il.Append(il.Create(OpCodes.Ldstr, "\\\\?\\"));
        il.Append(il.Create(OpCodes.Callvirt, startsWith));
        il.Append(il.Create(OpCodes.Brfalse_S, useOriginalPath));
        il.Append(il.Create(OpCodes.Ldarg_0));
        il.Append(il.Create(OpCodes.Ldc_I4_4));
        il.Append(il.Create(OpCodes.Callvirt, substring));
        il.Append(il.Create(OpCodes.Br_S, gotFallbackPath));
        il.Append(useOriginalPath);
        il.Append(gotFallbackPath);
        il.Append(il.Create(OpCodes.Ldarg_1));
        il.Append(il.Create(OpCodes.Ldarg_2));
        il.Append(il.Create(OpCodes.Ldarg_3));
        il.Append(il.Create(OpCodes.Ldarg_S, method.Parameters[4]));
        il.Append(il.Create(OpCodes.Ldarg_S, method.Parameters[5]));
        il.Append(il.Create(OpCodes.Ldc_I4, (int)FileOptions.Asynchronous));
        il.Append(il.Create(OpCodes.And));
        il.Append(il.Create(OpCodes.Ldc_I4, (int)FileOptions.Asynchronous));
        il.Append(il.Create(OpCodes.Ceq));
        il.Append(il.Create(OpCodes.Ldarg_S, method.Parameters[6]));
        il.Append(il.Create(OpCodes.Newobj, fileStreamWithDisposeCallbackCtor));
        il.Append(il.Create(OpCodes.Ret));
        il.Append(handlerEnd);
        il.Append(il.Create(OpCodes.Ret));

        method.Body.ExceptionHandlers.Add(new ExceptionHandler(ExceptionHandlerType.Catch)
        {
            TryStart = tryStart,
            TryEnd = handlerStart,
            HandlerStart = handlerStart,
            HandlerEnd = handlerEnd,
            CatchType = ioExceptionType
        });

        longFileType.Methods.Add(method);
        return method;
    }

    // ──────────────────────────────────────────────────────────────────────────
    // LongFile.GetFileHandle: retry CreateFile with \\?\ prefix stripped on
    // invalid handle + LastError=0
    // ──────────────────────────────────────────────────────────────────────────

    static int ApplyGetFileHandleRetry(ModuleDefinition module, TypeDefinition longFileType, bool dryRun)
    {
        var getFileHandle = longFileType.Methods.FirstOrDefault(m =>
            m.Name == "GetFileHandle" && m.HasBody && m.Parameters.Count == 6);
        if (getFileHandle == null) return 0;

        var createFileCall = getFileHandle.Body.Instructions.FirstOrDefault(i =>
            i.OpCode == OpCodes.Call &&
            i.Operand is MethodReference mr &&
            mr.Name == "CreateFile" &&
            mr.DeclaringType.Name == "NativeMethods");
        if (createFileCall == null) return 0;

        var createFileRef = (MethodReference)createFileCall.Operand;
        if (createFileCall.Operand is GenericInstanceMethod ||
            (createFileCall.Operand is MethodDefinition md && md.IsSpecialName))
        {
            // already patched? check for the helper reference
        }
        // Check if the call already targets the helper
        if (createFileRef.Name == "__Cs2MacPatcher_CreateFileWineRetry") return 0;

        if (dryRun) return 1;

        createFileCall.Operand = EnsureCreateFileWineRetryHelper(module, longFileType, createFileRef);
        return 1;
    }

    static MethodReference EnsureCreateFileWineRetryHelper(
        ModuleDefinition module,
        TypeDefinition longFileType,
        MethodReference createFileRef)
    {
        const string helperName = "__Cs2MacPatcher_CreateFileWineRetry";

        var existing = longFileType.Methods.FirstOrDefault(m => m.Name == helperName);
        if (existing != null) return existing;

        var mscorlib = module.AssemblyReferences.First(r => r.Name == "mscorlib");
        var boolType = module.TypeSystem.Boolean;
        var intType = module.TypeSystem.Int32;
        var stringType = module.TypeSystem.String;
        var marshalType = new TypeReference("System.Runtime.InteropServices", "Marshal", module, mscorlib);
        var safeHandleType = new TypeReference("System.Runtime.InteropServices", "SafeHandle", module, mscorlib);

        var getLastWin32Error = new MethodReference("GetLastWin32Error", intType, marshalType);
        var getIsInvalid = new MethodReference("get_IsInvalid", boolType, safeHandleType) { HasThis = true };
        var startsWith = new MethodReference("StartsWith", boolType, stringType) { HasThis = true };
        startsWith.Parameters.Add(new ParameterDefinition(stringType));
        var substring = new MethodReference("Substring", stringType, stringType) { HasThis = true };
        substring.Parameters.Add(new ParameterDefinition(intType));

        var method = new MethodDefinition(
            helperName,
            MethodAttributes.Private | MethodAttributes.Static | MethodAttributes.HideBySig,
            createFileRef.ReturnType);

        foreach (var p in createFileRef.Parameters)
            method.Parameters.Add(new ParameterDefinition(p.Name, p.Attributes, p.ParameterType));

        method.Body.InitLocals = true;
        method.Body.MaxStackSize = 8;
        var handleLocal = new VariableDefinition(createFileRef.ReturnType);
        var errorLocal = new VariableDefinition(intType);
        method.Body.Variables.Add(handleLocal);
        method.Body.Variables.Add(errorLocal);

        var il = method.Body.GetILProcessor();
        var returnHandle = il.Create(OpCodes.Ldloc, handleLocal);
        var retry = il.Create(OpCodes.Ldarg_0);
        var retryCall = il.Create(OpCodes.Call, createFileRef);

        for (int i = 0; i < createFileRef.Parameters.Count; i++)
            il.Append(il.Create(OpCodes.Ldarg, i));
        il.Append(il.Create(OpCodes.Call, createFileRef));
        il.Append(il.Create(OpCodes.Stloc, handleLocal));
        il.Append(il.Create(OpCodes.Call, getLastWin32Error));
        il.Append(il.Create(OpCodes.Stloc, errorLocal));
        il.Append(il.Create(OpCodes.Ldloc, handleLocal));
        il.Append(il.Create(OpCodes.Callvirt, getIsInvalid));
        il.Append(il.Create(OpCodes.Brfalse_S, returnHandle));
        il.Append(il.Create(OpCodes.Ldloc, errorLocal));
        il.Append(il.Create(OpCodes.Brtrue_S, returnHandle));
        il.Append(il.Create(OpCodes.Ldarg_0));
        il.Append(il.Create(OpCodes.Ldstr, "\\\\?\\"));
        il.Append(il.Create(OpCodes.Callvirt, startsWith));
        il.Append(il.Create(OpCodes.Brtrue_S, retry));
        il.Append(il.Create(OpCodes.Br_S, returnHandle));
        il.Append(retry);
        il.Append(il.Create(OpCodes.Ldc_I4_4));
        il.Append(il.Create(OpCodes.Callvirt, substring));
        for (int i = 1; i < createFileRef.Parameters.Count; i++)
            il.Append(il.Create(OpCodes.Ldarg, i));
        il.Append(retryCall);
        il.Append(il.Create(OpCodes.Stloc, handleLocal));
        il.Append(returnHandle);
        il.Append(il.Create(OpCodes.Ret));

        longFileType.Methods.Add(method);
        return method;
    }

    static void BackupAndWrite(ModuleDefinition module, string dllPath) =>
        TimestampedBackup.BackupAndWrite(module, dllPath);
}
