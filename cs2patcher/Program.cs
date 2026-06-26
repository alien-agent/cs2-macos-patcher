// cs2patcher — IL patcher for Cities: Skylines 2 (macOS/Wine)
// Called by patch.py; not intended for direct use.

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
    if (r.IsSkipped) Console.WriteLine($"WARN:{r.DllName}:{r.SkipReason}");
    else if (r.AlreadyOk) Console.WriteLine($"SKIP:{r.DllName}:already patched");
    else if (r.DryRun) Console.WriteLine($"DRY:{r.DllName}:{r.FixesApplied} fixes would be applied");
    else Console.WriteLine($"OK:{r.DllName}:{r.FixesApplied} fixes applied");
}

// === Lightweight (always applied) ===
Print(InjectDlcCachePatcher.Patch(managedDir, dryRun: !apply));          // Fix 18: DLC cache (Colossal.PSI.Common)
Print(PlatformManagerIsDlcOwnedPatcher.Patch(managedDir, dryRun: !apply)); // Fix 19: auto-own DLCs (Colossal.PSI.Common)
Print(SteamworksDlcMapperMapPatcher.Patch(managedDir, dryRun: !apply));    // Fix 20: Map set_Item (Colossal.PSI.Steamworks)
Print(ColossalIoPatcher.Patch(managedDir, dryRun: !apply));               // Fix 21: LongDirectory IOException (Colossal.IO)
Print(LongFileOpenWineFallbackPatcher.Patch(managedDir, dryRun: !apply));  // Fix 22: LongFile \\?\ Wine fix (Colossal.IO)
Print(AssetDatabasePatcher.Patch(managedDir, dryRun: !apply));            // Fix 23: .priority File.Exists NOP (Colossal.IO.AssetDatabase)
Print(RiderPathLocatorPatcher.Patch(managedDir, dryRun: !apply));         // Fix 24: Rider settings.json (Game)

// === Full (Paradox Mods) ===
if (fullMode) Print(PdxSdkPatcher.Patch(managedDir, dryRun: !apply));

return 0;
