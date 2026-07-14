using System;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.Cryptography;

namespace LocalManiaAuto.MetadataProbe
{
    internal static class Program
    {
        private const string SupportedSha256 = "6e182c10d1813209d12753dbc70b3a5bba00fef4ecf64bc42051870e6dfe4b7d";
        private const int SelectedModsFieldToken = 0x04000CC6;
        private const int CurrentPlayModeMethodToken = 0x06002232;

        private static int Main(string[] args)
        {
            if (args.Length != 2)
            {
                Console.Error.WriteLine("usage: LocalManiaAuto.MetadataProbe.exe <osu!.exe> <plugin.dll>");
                return 2;
            }

            try
            {
                string osuPath = Path.GetFullPath(args[0]);
                string pluginPath = Path.GetFullPath(args[1]);
                string hash = ComputeSha256(osuPath);
                if (!String.Equals(hash, SupportedSha256, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidOperationException("unsupported osu!.exe sha256=" + hash);

                string gameDirectory = Path.GetDirectoryName(osuPath);
                AppDomain.CurrentDomain.ReflectionOnlyAssemblyResolve += delegate(object sender, ResolveEventArgs eventArgs)
                {
                    string dependency = Path.Combine(
                        gameDirectory,
                        new AssemblyName(eventArgs.Name).Name + ".dll");
                    return File.Exists(dependency)
                        ? Assembly.ReflectionOnlyLoadFrom(dependency)
                        : Assembly.ReflectionOnlyLoad(eventArgs.Name);
                };

                Assembly game = Assembly.ReflectionOnlyLoadFrom(osuPath);
                Module module = game.ManifestModule;
                FieldInfo selectedMods = module.ResolveField(SelectedModsFieldToken);
                MethodInfo currentPlayMode = module.ResolveMethod(CurrentPlayModeMethodToken) as MethodInfo;
                ValidateEntryTargets(selectedMods, currentPlayMode);
                Console.WriteLine("entry targets validated: "
                    + selectedMods.DeclaringType.FullName + "::" + selectedMods.Name + ", "
                    + currentPlayMode.DeclaringType.FullName + "::" + currentPlayMode.Name);

                Assembly plugin = Assembly.LoadFrom(pluginPath);
                if (plugin.GetName().Version != new Version(0, 5, 0, 0))
                    throw new InvalidOperationException("unexpected plugin version " + plugin.GetName().Version);
                if (plugin.GetType("LocalManiaAuto.Plugin.ReplayInjector", false, false) != null)
                    throw new InvalidOperationException("active plugin unexpectedly contains ReplayInjector");
                RequireType(plugin, "LocalManiaAuto.Plugin.AgentControlState");
                RequireType(plugin, "LocalManiaAuto.Plugin.AgentOverlay");
                RequireType(plugin, "LocalManiaAuto.Plugin.Humanizer");

                Type agentType = plugin.GetType("LocalManiaAuto.Plugin.LiveAgent", true, false);
                ConstructorInfo constructor = agentType.GetConstructor(
                    BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic,
                    null,
                    new Type[] { typeof(Assembly), typeof(Action<string>) },
                    null);
                if (constructor == null)
                    throw new MissingMethodException(agentType.FullName, ".ctor(Assembly, Action<string>)");

                object agent = constructor.Invoke(new object[] { game, new Action<string>(Console.WriteLine) });
                Type nativeInput = agentType.GetNestedType("NativeInput", BindingFlags.NonPublic);
                if (nativeInput == null)
                    throw new MissingMemberException(agentType.FullName, "NativeInput");
                int inputSize = Marshal.SizeOf(nativeInput);
                if (IntPtr.Size != 4 || inputSize != 28)
                    throw new InvalidOperationException(
                        "unexpected x86 INPUT layout: pointer=" + IntPtr.Size + ", INPUT=" + inputSize);
                Console.WriteLine("x86 SendInput layout validated: INPUT=" + inputSize + " bytes");
                MethodInfo beginTimer = agentType.GetMethod(
                    "BeginTimerResolution",
                    BindingFlags.Instance | BindingFlags.NonPublic);
                MethodInfo endTimer = agentType.GetMethod(
                    "EndTimerResolution",
                    BindingFlags.Instance | BindingFlags.NonPublic);
                if (beginTimer == null || endTimer == null)
                    throw new MissingMethodException(agentType.FullName, "timer-resolution methods");
                beginTimer.Invoke(agent, null);
                endTimer.Invoke(agent, null);
                Console.WriteLine("winmm timer-resolution entry points validated");
                Console.WriteLine("active architecture=normal Player input; ReplayInjector absent");
                Console.WriteLine("v0.5 overlay/control/distribution humanizer types validated");
                Console.WriteLine("plugin=" + plugin.FullName);
                Console.WriteLine("osu sha256=" + hash);
                Console.WriteLine("METADATA PROBE: PASS");
                return 0;
            }
            catch (Exception exception)
            {
                TargetInvocationException invocation = exception as TargetInvocationException;
                Console.Error.WriteLine(invocation != null && invocation.InnerException != null
                    ? invocation.InnerException.ToString()
                    : exception.ToString());
                return 1;
            }
        }

        private static Type RequireType(Assembly assembly, string name)
        {
            Type type = assembly.GetType(name, false, false);
            if (type == null)
                throw new TypeLoadException(name);
            return type;
        }

        private static void ValidateEntryTargets(FieldInfo selectedMods, MethodInfo currentPlayMode)
        {
            if (selectedMods == null
                || !selectedMods.IsStatic
                || !selectedMods.FieldType.IsEnum
                || !String.Equals(selectedMods.FieldType.FullName, "osu_common.Mods", StringComparison.Ordinal))
            {
                throw new InvalidOperationException("selected-mods metadata token failed structural validation");
            }
            if (currentPlayMode == null
                || !currentPlayMode.IsStatic
                || currentPlayMode.GetParameters().Length != 0
                || !currentPlayMode.ReturnType.IsEnum
                || !String.Equals(currentPlayMode.ReturnType.FullName, "osu_common.PlayModes", StringComparison.Ordinal))
            {
                throw new InvalidOperationException("play-mode metadata token failed structural validation");
            }
        }

        private static string ComputeSha256(string path)
        {
            using (FileStream stream = File.OpenRead(path))
            using (SHA256 algorithm = SHA256.Create())
            {
                return BitConverter.ToString(algorithm.ComputeHash(stream))
                    .Replace("-", String.Empty)
                    .ToLowerInvariant();
            }
        }
    }
}
