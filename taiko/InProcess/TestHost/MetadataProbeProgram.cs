using System;
using System.IO;
using System.Reflection;
using System.Security.Cryptography;

internal static class MetadataProbeProgram
{
    private const string SupportedSha256 = "6e182c10d1813209d12753dbc70b3a5bba00fef4ecf64bc42051870e6dfe4b7d";

    private static int Main(string[] args)
    {
        if (args.Length != 1)
        {
            Console.Error.WriteLine("usage: LocalTaikoAgent.MetadataProbe.exe <osu!.exe>");
            return 2;
        }
        try
        {
            string path = Path.GetFullPath(args[0]);
            string directory = Path.GetDirectoryName(path);
            AppDomain.CurrentDomain.ReflectionOnlyAssemblyResolve += delegate(object sender, ResolveEventArgs eventArgs)
            {
                AssemblyName name = new AssemblyName(eventArgs.Name);
                string candidate = Path.Combine(directory, name.Name + ".dll");
                if (File.Exists(candidate)) return Assembly.ReflectionOnlyLoadFrom(candidate);
                try { return Assembly.ReflectionOnlyLoad(eventArgs.Name); }
                catch { return null; }
            };
            string hash = ComputeSha256(path);
            if (!String.Equals(hash, SupportedSha256, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("unsupported sha256=" + hash);
            Assembly game = Assembly.ReflectionOnlyLoadFrom(path);
            Module module = game.ManifestModule;

            MethodInfo currentMap = module.ResolveMethod(0x06002C63) as MethodInfo;
            MethodInfo mapPath = module.ResolveMethod(0x06001BF0) as MethodInfo;
            FieldInfo score = module.ResolveField(0x040013C3);
            FieldInfo pause = module.ResolveField(0x0400136A);
            FieldInfo replayMode = module.ResolveField(0x04002A7C);
            FieldInfo replayScore = module.ResolveField(0x04002A7F);
            FieldInfo songClock = module.ResolveField(0x04002358);
            FieldInfo scoreValidity = module.ResolveField(0x04001990);
            MethodInfo submissionState = module.ResolveMethod(0x06002B4D) as MethodInfo;
            MethodInfo loggedIn = module.ResolveMethod(0x0600469B) as MethodInfo;
            FieldInfo globalMode = module.ResolveField(0x04002C6D);
            FieldInfo selectedMods = module.ResolveField(0x04000CC6);
            MethodInfo playMode = module.ResolveMethod(0x06002232) as MethodInfo;
            MethodInfo binding = module.ResolveMethod(0x06002C4F) as MethodInfo;

            Require(currentMap != null && currentMap.IsStatic && currentMap.GetParameters().Length == 0, "current map");
            Require(mapPath != null && !mapPath.IsStatic && mapPath.ReturnType.FullName == "System.String", "map path");
            Require(score != null && score.IsStatic, "current score");
            Require(pause != null && pause.IsStatic && pause.FieldType.FullName == "System.Boolean", "pause");
            Require(replayMode != null && replayMode.IsStatic && replayMode.FieldType.FullName == "System.Boolean", "replay mode");
            Require(replayScore != null && replayScore.IsStatic && replayScore.FieldType == score.FieldType, "replay score");
            Require(songClock != null && songClock.IsStatic && songClock.FieldType.FullName == "System.Int32", "song clock");
            Require(scoreValidity != null && !scoreValidity.IsStatic
                && scoreValidity.DeclaringType == score.FieldType
                && scoreValidity.FieldType.FullName == "System.Boolean", "score validity");
            Require(submissionState != null && !submissionState.IsStatic
                && submissionState.DeclaringType == score.FieldType
                && submissionState.GetParameters().Length == 0
                && submissionState.ReturnType.IsEnum, "submission state");
            Require(loggedIn != null && loggedIn.IsStatic
                && loggedIn.GetParameters().Length == 0
                && loggedIn.ReturnType.FullName == "System.Boolean", "logged-in predicate");
            Require(globalMode != null && globalMode.FieldType.FullName == "osu.OsuModes", "global mode");
            Require(selectedMods != null && selectedMods.FieldType.FullName == "osu_common.Mods", "selected mods");
            Require(playMode != null && playMode.ReturnType.FullName == "osu_common.PlayModes", "play mode");
            Require(binding != null && binding.IsStatic
                && binding.GetParameters().Length == 1
                && binding.GetParameters()[0].ParameterType.FullName == "osu.Input.Bindings"
                && binding.ReturnType.FullName == "Microsoft.Xna.Framework.Input.Keys", "binding getter");

            Console.WriteLine("TAIKO METADATA PROBE: PASS");
            Console.WriteLine("sha256=" + hash);
            Console.WriteLine("binding-getter=0x06002c4f "
                + binding.GetParameters()[0].ParameterType.FullName + " -> " + binding.ReturnType.FullName);
            Console.WriteLine("submission-diag=validity 0x04001990, state 0x06002b4d,"
                + " logged-in 0x0600469b");
            return 0;
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine(exception);
            return 1;
        }
    }

    private static void Require(bool condition, string label)
    {
        if (!condition) throw new InvalidOperationException(label + " token failed validation");
    }

    private static string ComputeSha256(string path)
    {
        using (FileStream stream = File.OpenRead(path))
        using (SHA256 algorithm = SHA256.Create())
            return BitConverter.ToString(algorithm.ComputeHash(stream)).Replace("-", String.Empty).ToLowerInvariant();
    }
}
