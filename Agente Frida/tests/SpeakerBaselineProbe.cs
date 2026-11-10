// Read-only probe of an installed build. No application entry point, microphone or API is invoked.
using System;
using System.IO;
using System.Reflection;

internal static class SpeakerBaselineProbe
{
    public static int Main(string[] args)
    {
        if (args.Length != 3) return 2;
        var assembly = Assembly.LoadFrom(Path.GetFullPath(args[0]));
        var type = assembly.GetType("FridaAgent.SpeakerVoiceProfile", true);
        var profile = Activator.CreateInstance(type, new object[] { Path.GetFullPath(args[1]) });
        if (!(bool)type.GetProperty("IsReady").GetValue(profile, null)) throw new Exception("Baseline profile unavailable");
        int accepted = 0, count = 0;
        foreach (string path in Directory.GetFiles(args[2], "*.wav"))
        {
            var result = type.GetMethod("Check").Invoke(profile, new object[] { File.ReadAllBytes(path), 1.0 });
            var resultType = result.GetType();
            bool allow = (bool)resultType.GetField("Accepted").GetValue(result);
            double score = (double)resultType.GetField("Score").GetValue(result);
            double threshold = (double)resultType.GetField("Threshold").GetValue(result);
            count++; if (allow) accepted++;
            Console.WriteLine(Path.GetFileName(path) + " accepted=" + allow + " score=" + score.ToString("F3") + "/" + threshold.ToString("F3"));
        }
        Console.WriteLine("Build=" + assembly.GetName().Version + " false_accepts=" + accepted + "/" + count);
        return 0;
    }
}
