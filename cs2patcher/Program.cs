// cs2patcher — IL patcher for Cities: Skylines 2 (macOS/Wine)
// Called by patch.py; not intended for direct use.
//
// Usage:
//   cs2patcher <managed-dir> [--apply]
//
// Applies every fix in FixRegistry (each fix lives in Fixes/<WhatItFixes>.cs, whose
// header comment is its canonical documentation). Without --apply it's a dry run:
// nothing is written, but the counts match what an apply would do.
//
// Output: one line per DLL in format "STATUS:DLL:detail"
//   OK:Colossal.IO.dll:2 fixes applied
//   SKIP:PDX.SDK.dll:already patched
//   WARN:PDX.SDK.dll:DiskIODefaultWindows not found
//
// Debug: CS2_SKIP="slug,slug" skips fixes by id (see DllPatcher).
//
// Exit code: 0 = success or already patched, 1 = argument error

using Cs2MacPatcher;

if (args.Length < 1)
{
    Console.Error.WriteLine("Usage: cs2patcher <managed-dir> [--apply]");
    return 1;
}

var managedDir = args[0];
if (!Directory.Exists(managedDir))
{
    Console.Error.WriteLine($"ERROR:Directory not found:{managedDir}");
    return 1;
}

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

foreach (var target in FixRegistry.Targets)
    Print(DllPatcher.Patch(managedDir, target, FixRegistry.Fixes, dryRun: !apply));

return 0;
