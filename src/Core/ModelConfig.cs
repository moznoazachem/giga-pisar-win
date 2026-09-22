// Model settings read from the yaml file that ships next to the ONNX files.
// A full YAML parser is overkill: we need a handful of scalars and every
// key we care about is unique in that file.

namespace GigaPisar.Core;

public sealed class FeatureConfig
{
    public int SampleRate { get; set; } = 16000;
    public int NMels { get; set; } = 64;
    public int NFft { get; set; } = 320;
    public int WinLength { get; set; } = 320;
    public int HopLength { get; set; } = 160;
    public bool Center { get; set; } = false;
}

public sealed class ModelConfig
{
    public FeatureConfig Features { get; } = new();
    public int PredHidden { get; set; } = 320;
    public int PredLayers { get; set; } = 1;

    public static ModelConfig Load(string path)
    {
        var cfg = new ModelConfig();
        if (!File.Exists(path)) return cfg;

        var values = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var raw in File.ReadLines(path))
        {
            var line = raw.Trim();
            var colon = line.IndexOf(':');
            if (colon <= 0) continue;
            var key = line[..colon].Trim();
            var value = line[(colon + 1)..].Trim();
            if (value.Length == 0) continue;
            values.TryAdd(key, value);
        }

        int Int(string key, int fallback) =>
            values.TryGetValue(key, out var v) && int.TryParse(v, out var n) ? n : fallback;
        bool Bool(string key, bool fallback) =>
            values.TryGetValue(key, out var v) ? v == "true" : fallback;

        cfg.Features.NMels = Int("features", cfg.Features.NMels);
        cfg.Features.SampleRate = Int("sample_rate", cfg.Features.SampleRate);
        cfg.Features.NFft = Int("n_fft", cfg.Features.NFft);
        cfg.Features.WinLength = Int("win_length", cfg.Features.WinLength);
        cfg.Features.HopLength = Int("hop_length", cfg.Features.HopLength);
        cfg.Features.Center = Bool("center", cfg.Features.Center);
        cfg.PredHidden = Int("pred_hidden", cfg.PredHidden);
        cfg.PredLayers = Int("pred_rnn_layers", cfg.PredLayers);
        return cfg;
    }
}
