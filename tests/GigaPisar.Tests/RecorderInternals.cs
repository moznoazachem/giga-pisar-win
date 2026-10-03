using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.ExceptionServices;
using GigaPisar.App;
using NAudio.CoreAudioApi;
using NAudio.Wave;

namespace GigaPisar.Tests;

/// <summary>
/// Recorder keeps its moving parts private; the tests reach them by name. A MissingMemberException
/// from here means Recorder was refactored: update the names below.
/// </summary>
internal static class RecorderInternals
{
    private const BindingFlags Members = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static;

    public delegate ReadOnlySpan<float> ConvertFn(ReadOnlySpan<byte> packet);
    public delegate void AppendFn(ReadOnlySpan<float> chunk);

    private static readonly Type ConverterType = Nested("Converter");
    internal static readonly Type CaptureType = Nested("Capture");

    /// <summary>A Recorder.Converter for packets in this format.</summary>
    public static ConvertFn Converter(WaveFormat format)
    {
        object converter;
        try { converter = Activator.CreateInstance(ConverterType, format)!; }
        catch (TargetInvocationException e) when (e.InnerException != null) { ExceptionDispatchInfo.Throw(e.InnerException); throw; }
        // Span parameters cannot go through MethodInfo.Invoke, but a delegate can bind to the method.
        return (ConvertFn)Delegate.CreateDelegate(typeof(ConvertFn), converter, Method(ConverterType, "Convert"));
    }

    public static string Describe(WaveFormat format) => (string)Method(ConverterType, "Describe").Invoke(null, [format])!;

    /// <summary>Feeds samples into the take the way the capture thread does.</summary>
    public static AppendFn Append(Recorder recorder) =>
        (AppendFn)Delegate.CreateDelegate(typeof(AppendFn), recorder, Method(typeof(Recorder), "Append"));

    public static bool PrefersDeviceFormat(Recorder recorder) => (bool)Field(typeof(Recorder), "_deviceFormat").GetValue(recorder)!;

    /// <summary>A Recorder.Capture on a simulated WasapiRecorder, as Capture.Open builds it before StartRecording.</summary>
    public static CaptureHandle NewCapture(Recorder owner, FakeWasapiRecorder fake)
    {
        var device = (MMDevice)RuntimeHelpers.GetUninitializedObject(typeof(MMDevice));   // nothing here touches it but Dispose
        var constructor = CaptureType.GetConstructors(BindingFlags.NonPublic | BindingFlags.Instance).Single();
        return new CaptureHandle(constructor.Invoke([owner, device, fake.Recorder, false]));
    }

    /// <summary>Hands the recorder a finished open, as if StartAsync had produced this capture.</summary>
    public static void SetTake(Recorder recorder, CaptureHandle capture) =>
        Field(typeof(Recorder), "_take").SetValue(recorder, Method(typeof(Task), "FromResult").MakeGenericMethod(CaptureType).Invoke(null, [capture.Inner]));

    /// <summary>Hands the recorder an open that is still in progress; the returned action completes it.</summary>
    public static Action<CaptureHandle> SetPendingTake(Recorder recorder)
    {
        var sourceType = typeof(TaskCompletionSource<>).MakeGenericType(CaptureType);
        var source = Activator.CreateInstance(sourceType)!;
        Field(typeof(Recorder), "_take").SetValue(recorder, sourceType.GetProperty("Task")!.GetValue(source));
        var setResult = sourceType.GetMethod("SetResult")!;
        return capture => setResult.Invoke(source, [capture.Inner]);
    }

    internal static MethodInfo Method(Type type, string name) =>
        type.GetMethod(name, Members) ?? throw Missing($"{type.Name}.{name}()");

    private static FieldInfo Field(Type type, string name) =>
        type.GetField(name, Members) ?? throw Missing($"{type.Name}.{name}");

    private static Type Nested(string name) =>
        typeof(Recorder).GetNestedType(name, BindingFlags.NonPublic) ?? throw Missing($"Recorder.{name}");

    private static MissingMemberException Missing(string member) =>
        new($"{member} not found: Recorder was refactored, update RecorderInternals.");
}

/// <summary>A Recorder.Capture as the tests see it.</summary>
internal sealed class CaptureHandle(object inner)
{
    public object Inner { get; } = inner;

    public int Gaps => (int)RecorderInternals.CaptureType.GetProperty("Gaps")!.GetValue(Inner)!;

    public void WaitUntilRunning()
    {
        try { RecorderInternals.Method(RecorderInternals.CaptureType, "WaitUntilRunning").Invoke(Inner, null); }
        catch (TargetInvocationException e) when (e.InnerException != null) { ExceptionDispatchInfo.Throw(e.InnerException); }
    }

    public Task CloseAsync() => (Task)RecorderInternals.Method(RecorderInternals.CaptureType, "CloseAsync").Invoke(Inner, null)!;
}
