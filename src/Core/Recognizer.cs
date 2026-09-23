// Speech recognition with GigaAM v3 (RNN-T) through ONNX Runtime, CPU only.
//
// Audio -> log-mel features -> encoder -> greedy RNN-T decoding (decoder and
// joint networks per frame) -> text. Mirrors giga_core.py step by step.

using Microsoft.ML.OnnxRuntime;

namespace GigaPisar.Core;

public sealed class Recognizer : IDisposable
{
    public const string ModelName = "v3_e2e_rnnt";
    public const double MaxChunkSeconds = 24.0;   // the model is trained on clips up to 25 s
    private const int MaxSymbolsPerFrame = 3;

    private readonly InferenceSession _encoder;
    private readonly InferenceSession _decoder;
    private readonly InferenceSession _joint;
    private readonly Tokenizer _tokenizer;
    private readonly Features _features;
    private readonly ModelConfig _cfg;
    private readonly RunOptions _runOptions = new();

    public string ModelDir { get; }
    public int SampleRate => _cfg.Features.SampleRate;

    public static bool ModelExists(string dir) =>
        File.Exists(Path.Combine(dir, ModelName + ".yaml")) &&
        File.Exists(Path.Combine(dir, ModelName + "_encoder.onnx")) &&
        File.Exists(Path.Combine(dir, ModelName + "_decoder.onnx")) &&
        File.Exists(Path.Combine(dir, ModelName + "_joint.onnx")) &&
        File.Exists(Path.Combine(dir, ModelName + "_tokenizer.model"));

    public Recognizer(string modelDir, int threads = 0)
    {
        ModelDir = modelDir;
        _cfg = ModelConfig.Load(Path.Combine(modelDir, ModelName + ".yaml"));
        _features = new Features(_cfg.Features);

        using var options = new SessionOptions
        {
            GraphOptimizationLevel = GraphOptimizationLevel.ORT_ENABLE_ALL,
            ExecutionMode = ExecutionMode.ORT_SEQUENTIAL,
            IntraOpNumThreads = threads > 0 ? threads : Math.Min(4, Environment.ProcessorCount),
            LogSeverityLevel = OrtLoggingLevel.ORT_LOGGING_LEVEL_ERROR,
        };

        _encoder = new InferenceSession(Path.Combine(modelDir, ModelName + "_encoder.onnx"), options);
        _decoder = new InferenceSession(Path.Combine(modelDir, ModelName + "_decoder.onnx"), options);
        _joint = new InferenceSession(Path.Combine(modelDir, ModelName + "_joint.onnx"), options);
        _tokenizer = new Tokenizer(Path.Combine(modelDir, ModelName + "_tokenizer.model"));
    }

    /// <summary>Recognizes a recording of any length; long ones are split at pauses.</summary>
    public string Transcribe(float[] samples, int rate)
    {
        if (rate != SampleRate)
            throw new ArgumentException($"Expected {SampleRate} Hz audio, got {rate} Hz");

        double total = (double)samples.Length / rate;
        if (total <= MaxChunkSeconds + 1) return TranscribeChunk(samples);

        var bounds = AudioUtils.ChunkBounds(total, AudioUtils.Silences(samples, rate), MaxChunkSeconds);
        var parts = new List<string>();
        foreach (var (a, b) in bounds)
        {
            int from = Math.Min(samples.Length, (int)(a * rate));
            int to = Math.Min(samples.Length, (int)(b * rate));
            if (to > from)
            {
                var text = TranscribeChunk(samples.AsSpan(from, to - from));
                if (text.Length > 0) parts.Add(text);
            }
        }
        return string.Join(' ', parts);
    }

    /// <summary>Recognizes one chunk that fits into a single model pass.</summary>
    public string TranscribeChunk(ReadOnlySpan<float> wave)
    {
        if (wave.Length < _cfg.Features.WinLength) return "";
        var (feats, frames) = _features.Compute(wave);
        if (frames == 0) return "";

        int nMels = _cfg.Features.NMels;
        var lengths = new long[] { _features.OutLen(wave.Length) };

        using var featsValue = OrtValue.CreateTensorValueFromMemory(OrtMemoryInfo.DefaultInstance,
            feats.AsMemory(), new long[] { 1, nMels, frames });
        using var lenValue = OrtValue.CreateTensorValueFromMemory(OrtMemoryInfo.DefaultInstance,
            lengths.AsMemory(), new long[] { 1 });

        using var encOut = _encoder.Run(_runOptions,
            new Dictionary<string, OrtValue> { ["audio_signal"] = featsValue, ["length"] = lenValue },
            _encoder.OutputNames);

        var encShape = encOut[0].GetTensorTypeAndShape().Shape;   // [1, encD, encT]
        int encD = (int)encShape[1], encT = (int)encShape[2];
        var encoded = encOut[0].GetTensorDataAsSpan<float>().ToArray();
        // The export takes int64 lengths but returns encoded_len as int32; a re-export may change this.
        int encLen = Math.Min((int)encOut[1].GetTensorDataAsSpan<int>()[0], encT);

        return _tokenizer.Decode(GreedyRnnt(encoded, encD, encT, encLen));
    }

    private List<int> GreedyRnnt(float[] encoded, int encD, int encT, int encLen)
    {
        int hidden = _cfg.PredHidden, layers = _cfg.PredLayers;
        int blank = _tokenizer.BlankId;
        var hyp = new List<int>();

        var stateShape = new long[] { layers, 1, hidden };
        var zeros = new float[layers * hidden];
        var h = new float[layers * hidden];
        var c = new float[layers * hidden];
        var label = new long[] { blank };
        var frame = new float[encD];
        var dec = new float[hidden];
        bool started = false;   // decoder state is all zeros until the first symbol

        var decInputs = new Dictionary<string, OrtValue>(3);
        var jointInputs = new Dictionary<string, OrtValue>(2);

        for (int t = 0; t < encLen; t++)
        {
            // element (0, d, t) lives at d * encT + t
            for (int d = 0; d < encD; d++) frame[d] = encoded[d * encT + t];

            for (int s = 0; s < MaxSymbolsPerFrame; s++)
            {
                label[0] = started ? label[0] : blank;
                using var xValue = OrtValue.CreateTensorValueFromMemory(OrtMemoryInfo.DefaultInstance, label.AsMemory(), new long[] { 1, 1 });
                using var hValue = OrtValue.CreateTensorValueFromMemory(OrtMemoryInfo.DefaultInstance, (started ? h : zeros).AsMemory(), stateShape);
                using var cValue = OrtValue.CreateTensorValueFromMemory(OrtMemoryInfo.DefaultInstance, (started ? c : zeros).AsMemory(), stateShape);
                decInputs["x"] = xValue; decInputs["hi"] = hValue; decInputs["ci"] = cValue;

                using var decOut = _decoder.Run(_runOptions, decInputs, _decoder.OutputNames);
                decOut[0].GetTensorDataAsSpan<float>().CopyTo(dec);

                // dec arrives as [1,1,320]; the joint wants [1,320,1] — same numbers, different declared shape
                using var encValue = OrtValue.CreateTensorValueFromMemory(OrtMemoryInfo.DefaultInstance, frame.AsMemory(), new long[] { 1, encD, 1 });
                using var decValue = OrtValue.CreateTensorValueFromMemory(OrtMemoryInfo.DefaultInstance, dec.AsMemory(), new long[] { 1, hidden, 1 });
                jointInputs["enc"] = encValue; jointInputs["dec"] = decValue;

                using var jointOut = _joint.Run(_runOptions, jointInputs, _joint.OutputNames);
                var logits = jointOut[0].GetTensorDataAsSpan<float>();
                if (logits.Length != blank + 1)
                    throw new InvalidDataException($"Tokenizer has {blank} pieces but the joint network outputs {logits.Length} classes");
                int best = 0;
                float bestValue = float.NegativeInfinity;
                for (int i = 0; i < logits.Length; i++)
                    if (logits[i] > bestValue) { bestValue = logits[i]; best = i; }

                if (best == blank) break;

                hyp.Add(best);
                label[0] = best;
                decOut[1].GetTensorDataAsSpan<float>().CopyTo(h);
                decOut[2].GetTensorDataAsSpan<float>().CopyTo(c);
                started = true;
            }
        }
        return hyp;
    }

    public void Dispose()
    {
        _runOptions.Dispose();
        _encoder.Dispose();
        _decoder.Dispose();
        _joint.Dispose();
    }
}
