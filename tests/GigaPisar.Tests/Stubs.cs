// Stand-ins for the app's L and Log, the only other parts of the app that Recorder.cs uses.

using Xunit.Sdk;
using Xunit.v3;

// Log is shared and several tests measure time: run them one at a time.
[assembly: Parallelization(Mode = ParallelMode.None)]

namespace GigaPisar.App;

public static class L
{
    public static string T(string ru, string en) => en;
}

public static class Log
{
    private static readonly List<string> Lines = new();

    public static void Write(string message)
    {
        lock (Lines) Lines.Add(message);
    }

    /// <summary>Returns a function that lists the lines written after this call.</summary>
    public static Func<IReadOnlyList<string>> Mark()
    {
        int start;
        lock (Lines) start = Lines.Count;
        return () => { lock (Lines) return Lines.Skip(start).ToList(); };
    }
}
