using System;
using System.IO;
using LocalTaikoAgent.Plugin;

internal static class CorpusProgram
{
    private static int Main(string[] args)
    {
        try
        {
            if (args.Length != 1 || !Directory.Exists(args[0]))
            {
                Console.Error.WriteLine("usage: LocalTaikoAgent.CorpusTest.exe <Songs-directory>");
                return 2;
            }

            AgentOptionsSnapshot human = new AgentOptionsSnapshot(
                true, HumanStyle.Human, 60, -3, 20, 10, 100, 6,
                FrameCadence.Hz240, false, 1, true);
            int maps = 0;
            long objects = 0;
            long strikes = 0;
            long batches = 0;
            long predicted100 = 0;
            foreach (string path in Directory.GetFiles(args[0], "*.osu", SearchOption.AllDirectories))
            {
                if (!IsNativeTaiko(path))
                    continue;
                LiveTaikoPlan source = LivePlanBuilder.ParseAndBuild(path, 8, 0);
                HumanizedPlanResult result = Humanizer.Apply(source, human, 0, 8, null);
                if (result.Miss != 0)
                    throw new InvalidOperationException(Path.GetFileName(path)
                        + ": humanizer predicted " + result.Miss + " misses");
                maps++;
                objects += source.ObjectCount;
                strikes += result.Plan.Strikes.Count;
                batches += result.Plan.Batches.Count;
                predicted100 += result.Grade100;
            }
            if (maps == 0)
                throw new InvalidOperationException("no native Mode:1 maps found");

            Console.WriteLine("TAIKO IN-PROCESS CORPUS TEST: PASS");
            Console.WriteLine("maps=" + maps + ", objects=" + objects
                + ", strikes=" + strikes + ", batches=" + batches
                + ", predicted-100=" + predicted100 + ", predicted-miss=0");
            return 0;
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine(exception);
            return 1;
        }
    }

    private static bool IsNativeTaiko(string path)
    {
        string section = String.Empty;
        using (StreamReader reader = new StreamReader(path))
        {
            string raw;
            while ((raw = reader.ReadLine()) != null)
            {
                string line = raw.Trim();
                if (line.Length == 0 || line.StartsWith("//", StringComparison.Ordinal))
                    continue;
                if (line[0] == '[' && line[line.Length - 1] == ']')
                {
                    section = line.Substring(1, line.Length - 2).Trim();
                    if (String.Equals(section, "HitObjects", StringComparison.OrdinalIgnoreCase))
                        return false;
                    continue;
                }
                if (!String.Equals(section, "General", StringComparison.OrdinalIgnoreCase))
                    continue;
                int separator = line.IndexOf(':');
                if (separator <= 0
                    || !String.Equals(line.Substring(0, separator).Trim(), "Mode", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }
                return String.Equals(line.Substring(separator + 1).Trim(), "1", StringComparison.Ordinal);
            }
        }
        return false;
    }
}
