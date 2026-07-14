using System;
using System.IO;
using System.Reflection;
using System.Threading;

[assembly: AssemblyTitle("LocalManiaAuto.Loader")]
[assembly: AssemblyDescription("Minimal CLR AppDomainManager bootstrap for the local osu!mania experiment")]
[assembly: AssemblyCompany("Local research")]
[assembly: AssemblyProduct("LocalManiaAuto")]
[assembly: AssemblyVersion("1.0.0.0")]
[assembly: AssemblyFileVersion("1.0.0.0")]

namespace LocalManiaAuto.Loader
{
    public sealed class LocalManiaAutoDomainManager : AppDomainManager
    {
        private static int started;
        private static readonly object LogLock = new object();

        public override void InitializeNewDomain(AppDomainSetup appDomainInfo)
        {
            base.InitializeNewDomain(appDomainInfo);

            try
            {
                Log("InitializeNewDomain: name=" + AppDomain.CurrentDomain.FriendlyName
                    + ", default=" + AppDomain.CurrentDomain.IsDefaultAppDomain());

                if (!AppDomain.CurrentDomain.IsDefaultAppDomain())
                    return;
                if (Interlocked.Exchange(ref started, 1) != 0)
                    return;

                string pluginPath = Environment.GetEnvironmentVariable("MANIA_AUTO_PLUGIN");
                if (String.IsNullOrEmpty(pluginPath))
                {
                    pluginPath = Path.Combine(
                        AppDomain.CurrentDomain.BaseDirectory,
                        "LocalManiaAuto",
                        "LocalManiaAuto.Plugin.dll");
                }

                pluginPath = Path.GetFullPath(pluginPath);
                if (!File.Exists(pluginPath))
                {
                    Log("plugin missing: " + pluginPath);
                    return;
                }

                Assembly plugin = Assembly.LoadFrom(pluginPath);
                Type entryType = plugin.GetType("LocalManiaAuto.Plugin.Entry", true, false);
                MethodInfo start = entryType.GetMethod(
                    "Start",
                    BindingFlags.Public | BindingFlags.Static,
                    null,
                    Type.EmptyTypes,
                    null);
                if (start == null)
                    throw new MissingMethodException(entryType.FullName, "Start()");

                start.Invoke(null, null);
                Log("plugin started: " + plugin.FullName + " from " + pluginPath);
            }
            catch (Exception exception)
            {
                Exception useful = exception;
                TargetInvocationException invocation = exception as TargetInvocationException;
                if (invocation != null && invocation.InnerException != null)
                    useful = invocation.InnerException;
                Log("bootstrap failure: " + useful);
                // A plugin failure must not prevent the original game from starting.
            }
        }

        private static void Log(string message)
        {
            try
            {
                string path = Environment.GetEnvironmentVariable("MANIA_AUTO_LOG");
                if (String.IsNullOrEmpty(path))
                    path = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "LocalManiaAuto.log");

                string directory = Path.GetDirectoryName(path);
                if (!String.IsNullOrEmpty(directory))
                    Directory.CreateDirectory(directory);

                lock (LogLock)
                {
                    string line = DateTime.UtcNow.ToString("O") + " [loader pid="
                        + System.Diagnostics.Process.GetCurrentProcess().Id + "] "
                        + message + Environment.NewLine;
                    for (int attempt = 0; attempt < 10; attempt++)
                    {
                        try
                        {
                            File.AppendAllText(path, line);
                            return;
                        }
                        catch (IOException)
                        {
                            Thread.Sleep(10);
                        }
                    }
                }
            }
            catch
            {
                // Logging is diagnostic only and must never break host startup.
            }
        }
    }
}
