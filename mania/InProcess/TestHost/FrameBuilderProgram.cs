using System;
using LocalManiaAuto.Plugin;

namespace LocalManiaAuto.FrameBuilderTest
{
    internal static class Program
    {
        private static int Main(string[] args)
        {
            bool printAll = args.Length == 2 && args[1] == "--all";
            if (args.Length < 1 || args.Length > 2 || (args.Length == 2 && !printAll))
            {
                Console.Error.WriteLine("usage: LocalManiaAuto.FrameBuilderTest.exe <map.osu> [--all]");
                return 2;
            }

            try
            {
                ParsedManiaMap map = NativeFrameBuilder.ParseAndBuild(args[0]);
                Console.WriteLine("keys=" + map.KeyCount
                    + " objects=" + map.ObjectCount
                    + " frames=" + map.Frames.Count
                    + " first=" + map.FirstObjectTime);
                int limit = printAll ? map.Frames.Count : Math.Min(12, map.Frames.Count);
                for (int index = 0; index < limit; index++)
                {
                    Console.WriteLine(map.Frames[index].Time + "," + map.Frames[index].KeyMask);
                }
                return 0;
            }
            catch (Exception exception)
            {
                Console.Error.WriteLine(exception);
                return 1;
            }
        }
    }
}
