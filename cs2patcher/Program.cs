// cs2patcher — IL patcher for Cities: Skylines 2 (macOS/Wine)
// Called by patch.py; not intended for direct use.
//
// Usage:
//   cs2patcher <managed-dir> lightweight [--apply]
//   cs2patcher <managed-dir> full        [--apply]
//
// Output: one line per DLL in format "STATUS:DLL:detail"
//   OK:Colossal.IO.dll:2 fixes applied
//   SKIP:PDX.SDK.dll:already patched
//   WARN:PDX.SDK.dll:DiskIODefaultWindows not found
//
// Exit code: 0 = success or already patched, 1 = argument error

using Cs2MacPatcher;

if (args.Length < 2)
{
    Console.Error.WriteLine("Usage: cs2patcher <managed-dir> <lightweight|full> [--apply]");
    return 1;
}

var managedDir = args[0];
if (!Directory.Exists(managedDir))
{
    Console.Error.WriteLine($"ERROR:Directory not found:{managedDir}");
    return 1;
}

bool fullMode = args[1].Equals("full", StringComparison.OrdinalIgnoreCase);
bool apply = args.Contains("--apply");

void Print(PatchSummary r)
{
    if (r.IsSkipped)
        Console.WriteLine($"WARN:{r.DllName}:{r.SkipReason}");
    else if (r.AlreadyOk)
        Console.WriteLine($"SKIP:{r.DllName}:already patched or pattern not found");
    else if (r.DryRun)
        Console.WriteLine($"DRY:{r.DllName}:{r.FixesApplied} fixes would be applied");
    else
        Console.WriteLine($"OK:{r.DllName}:{r.FixesApplied} fixes applied");
}

Print(ColossalIoPatcher.Patch(managedDir, dryRun: !apply));
Print(AssetDatabasePatcher.Patch(managedDir, dryRun: !apply));
Print(GamePatcher.Patch(managedDir, dryRun: !apply));
if (fullMode)
    Print(PdxSdkPatcher.Patch(managedDir, dryRun: !apply));

return 0;
