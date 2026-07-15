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
            Console.Error.WriteLine("usage: LocalCatchAgent.MetadataProbe.exe <osu!.exe>");
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
            Require(String.Equals(hash, SupportedSha256, StringComparison.OrdinalIgnoreCase), "executable fingerprint");
            Assembly game = Assembly.ReflectionOnlyLoadFrom(path);
            Module module = game.ManifestModule;

            FieldInfo currentPlayer = module.ResolveField(0x0400136D);
            FieldInfo ruleset = module.ResolveField(0x040013A4);
            Type catchManager = module.ResolveType(0x020001BA);
            FieldInfo objectManager = module.ResolveField(0x040005F4);
            FieldInfo catcher = module.ResolveField(0x040006DF);
            FieldInfo catcherWidth = module.ResolveField(0x040006E0);
            FieldInfo allObjects = module.ResolveField(0x040017FB);
            Type fruit = module.ResolveType(0x0200052C);
            Type tiny = module.ResolveType(0x0200031B);
            Type droplet = module.ResolveType(0x0200088E);
            Type banana = module.ResolveType(0x0200081F);
            FieldInfo hyperTarget = module.ResolveField(0x04001747);
            FieldInfo startTime = module.ResolveField(0x04002523);
            FieldInfo objectPosition = module.ResolveField(0x0400252C);
            FieldInfo catcherPosition = module.ResolveField(0x04002CF6);
            MethodInfo binding = module.ResolveMethod(0x06002C4F) as MethodInfo;

            Require(currentPlayer.IsStatic, "current Player");
            Require(!ruleset.IsStatic && ruleset.DeclaringType == currentPlayer.FieldType, "Player ruleset manager");
            Require(objectManager.DeclaringType.IsAssignableFrom(catchManager), "Catch manager base object manager");
            Require(catcher.DeclaringType == catchManager, "Catch catcher sprite");
            Require(catcherWidth.DeclaringType == catchManager && catcherWidth.FieldType.FullName == "System.Single", "catcher width");
            Require(allObjects.DeclaringType == objectManager.FieldType, "converted all-object list");
            Require(fruit.IsAssignableFrom(tiny), "tiny droplet hierarchy");
            Require(fruit.IsAssignableFrom(droplet), "droplet hierarchy");
            Require(fruit.IsAssignableFrom(banana), "banana hierarchy");
            Require(hyperTarget.DeclaringType == fruit, "hyperdash target");
            Require(startTime.FieldType.FullName == "System.Int32", "hit-object start time");
            Require(objectPosition.FieldType == catcherPosition.FieldType, "Vector2 positions");
            Require(objectPosition.FieldType.GetField("X") != null, "Vector2.X");
            Require(binding != null
                && binding.GetParameters().Length == 1
                && binding.GetParameters()[0].ParameterType.FullName == "osu.Input.Bindings"
                && binding.ReturnType.FullName == "Microsoft.Xna.Framework.Input.Keys", "binding getter");

            Console.WriteLine("CATCH METADATA PROBE: PASS");
            Console.WriteLine("sha256=" + hash);
            Console.WriteLine("catch-manager=0x020001ba " + Escape(catchManager.FullName));
            Console.WriteLine("object-list=0x040017fb, catcher=0x040006df, width=0x040006e0");
            Console.WriteLine("fruit=0x0200052c, tiny=0x0200031b, droplet=0x0200088e, banana=0x0200081f");
            Console.WriteLine("bindings=FruitsLeft(11), FruitsRight(12), FruitsDash(13)");
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

    private static string Escape(string value)
    {
        if (value == null) return String.Empty;
        System.Text.StringBuilder builder = new System.Text.StringBuilder();
        for (int index = 0; index < value.Length; index++)
        {
            char character = value[index];
            if (character >= 0x20 && character <= 0x7e) builder.Append(character);
            else builder.Append("\\u" + ((int)character).ToString("x4"));
        }
        return builder.ToString();
    }

    private static string ComputeSha256(string path)
    {
        using (FileStream stream = File.OpenRead(path))
        using (SHA256 algorithm = SHA256.Create())
            return BitConverter.ToString(algorithm.ComputeHash(stream)).Replace("-", String.Empty).ToLowerInvariant();
    }
}
