using System;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

// Runs the SDK's real disk methods against disposable fixtures, never a live mod folder.
class ModDeletionSmokeTest
{
    const BindingFlags Methods = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
    static object disk;
    static Type diskType;
    static bool legacy;

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    static extern SafeFileHandle CreateFileW(string path, uint access, uint share, IntPtr security,
        uint disposition, uint attributes, IntPtr template);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    static extern bool DeleteFileW(string path);

    static object Call(string name, params object[] args)
    {
        try { return diskType.GetMethod(name, Methods).Invoke(disk, args); }
        catch (TargetInvocationException ex) { throw ex.InnerException ?? ex; }
    }
    static void Check(bool condition, string message)
    {
        if (!condition) throw new Exception(message);
        Console.WriteLine("PASS: " + message);
    }
    static string Stored(string path)
    {
        // The SDK stores a forward-slash mod root and appends backslash components.
        int split = path.IndexOf("\\pdx_mods\\", StringComparison.Ordinal);
        if (split < 0) throw new ArgumentException("Fixture must live below pdx_mods");
        return path.Substring(0, split).Replace('\\', '/') + path.Substring(split);
    }
    static string Fixture(string root, string name)
    {
        string dir = Path.Combine(root, "pdx_mods", name);
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "manifest.json"), "fixture manifest");
        File.WriteAllText(Path.Combine(dir, "payload.bin"), "fixture payload");
        return dir;
    }
    static void Delete(string path) { Call("DeleteDirectory", Stored(path), true); }

    static int Main(string[] args)
    {
        if (args.Length < 2 || args.Length > 3 || (args.Length == 3 && args[2] != "--legacy"))
        {
            Console.Error.WriteLine("Usage: ModDeletionSmokeTest.exe <PDX.SDK.dll> <scratch-parent> [--legacy]");
            return 2;
        }
        if (Path.DirectorySeparatorChar != '\\')
        {
            Console.Error.WriteLine("Run this test with a Windows runtime under Wine/CrossOver.");
            return 2;
        }
        legacy = args.Length == 3;
        string root = Path.Combine(Path.GetFullPath(args[1]), "cs2-mod-deletion-" + Guid.NewGuid().ToString("N"));
        try
        {
            diskType = Assembly.LoadFrom(Path.GetFullPath(args[0])).GetType(
                "PDX.SDK.Internal.Util.IO.DiskIODefaultWindows", true);
            disk = Activator.CreateInstance(diskType, true);
            Console.WriteLine("DLL: " + args[0] + "; expected: " + (legacy ? "legacy failure" : "repaired"));
            Console.WriteLine("Fixtures: " + root);

            string simple = Fixture(root, "simple_1");
            string stored = Stored(simple);
            string expected = @"\\?\" + (legacy ? stored : simple);
            Check((string)Call("GetLongPath", stored) == expected, "mixed-slash path normalization");
            Check((string)Call("GetLongPath", @"\\?\" + stored) == expected, "already-prefixed path normalization");
            Delete(simple);
            Check(Directory.Exists(simple) == legacy, "flat mod deletion");
            if (legacy) Check(Directory.GetFiles(simple).Length == 2, "legacy failure leaves every flat file intact");

            string nested = Fixture(root, "nested_1");
            string deep = Path.Combine(nested, "assets with spaces", "textures");
            Directory.CreateDirectory(deep);
            File.WriteAllText(Path.Combine(deep, "texture.bin"), "nested payload");
            Directory.CreateDirectory(Path.Combine(nested, "empty"));
            string sibling = Fixture(root, "untouched_1");
            Delete(nested);
            Check(Directory.Exists(nested) == legacy, "nested and empty directory deletion");
            Check(File.ReadAllText(Path.Combine(sibling, "payload.bin")) == "fixture payload", "sibling mod preserved");
            if (legacy) Check(File.Exists(Path.Combine(deep, "texture.bin")), "legacy nested payload remains");

            // Exercise the disk phase of an update; this is not an online download/playset test.
            string oldVersion = Fixture(root, "update_6");
            string staged = Fixture(root, Path.Combine(".downloading", "update_7"));
            string nextVersion = Path.Combine(root, "pdx_mods", "update_7");
            string stagingRoot = Path.GetDirectoryName(staged);
            File.WriteAllText(Path.Combine(stagingRoot, "download.tmp"), "staging metadata");
            Call("MoveDirectory", Stored(staged), Stored(nextVersion));
            Delete(oldVersion);
            Delete(stagingRoot);
            Check(Directory.Exists(oldVersion) == legacy, "superseded version cleanup");
            Check(Directory.Exists(stagingRoot) == legacy, ".downloading staging cleanup");
            Check(File.ReadAllText(Path.Combine(nextVersion, "payload.bin")) == "fixture payload", "new version preserved after update cleanup");

            string lockedDir = Fixture(root, "locked_1");
            string lockedFile = Path.Combine(lockedDir, "payload.bin");
            // Allow reads/writes, deny delete sharing. Verify the lock through Win32 first.
            using (var held = CreateFileW(lockedFile, 0x80000000, 3, IntPtr.Zero, 3, 0, IntPtr.Zero))
            {
                Check(!held.IsInvalid, "opened a real Win32 handle without FILE_SHARE_DELETE");
                bool nativeDeleted = DeleteFileW(lockedFile);
                int error = Marshal.GetLastWin32Error();
                Check(!nativeDeleted && error == 32, "Win32 deletion rejected with ERROR_SHARING_VIOLATION (32)");
                Delete(lockedDir);
                Check(File.Exists(lockedFile), "locked file remains although SDK deletion returned normally");
                Check(File.Exists(Path.Combine(lockedDir, "manifest.json")) == legacy,
                    "unlocked sibling deletion exposes existing best-effort behavior");
            }
            Delete(lockedDir);
            Check(Directory.Exists(lockedDir) == legacy, "retry after releasing the handle");
            Console.WriteLine("PASS: all mod deletion checks (including documented locked-file limitation)");
            return 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine(ex);
            return 1;
        }
        finally
        {
            // Only remove the unique fixture tree this process created.
            if (Directory.Exists(root)) Directory.Delete(root, true);
        }
    }
}
