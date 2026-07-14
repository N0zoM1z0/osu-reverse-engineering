using System;
using System.Reflection;
using System.Threading;

[assembly: AssemblyTitle("LocalManiaAuto.TestHost")]
[assembly: AssemblyVersion("1.0.0.0")]

namespace LocalManiaAuto.TestHost
{
    internal static class Program
    {
        private static int Main()
        {
            AppDomainManager manager = AppDomain.CurrentDomain.DomainManager;
            Console.WriteLine("entry=" + Assembly.GetEntryAssembly().FullName);
            Console.WriteLine("domain-manager=" + (manager == null ? "<null>" : manager.GetType().AssemblyQualifiedName));
            Thread.Sleep(500);
            return manager == null ? 10 : 0;
        }
    }
}
