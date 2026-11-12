using System.Text.Json;
using Omnipotent.Services.OmniscienceV3.Kernel.Jobs;

namespace Omnipotent.Services.OmniscienceV3.Replica
{
    /// <summary>
    /// The assistant-voice house model (plan A2, F017/F303): a logistic regression over hashed character 3-grams, word
    /// unigrams and a few typographic measurements, trained with the LLM as teacher (assistant-style replies it wrote)
    /// against real chat messages. It replaces v2's regex "forbidden patterns" list: it judges the voice, not words.
    /// The judge uses it relative to each person's own baseline, so a naturally formal writer is not penalised.
    /// </summary>
    public sealed class AssistantVoiceModel
    {
        public const string ModelId = "replica.assistant_voice";
        public const int HashDims = 4096;
        public const int DenseDims = 10;

        public required int Version { get; init; }
        public required LogisticModel Model { get; init; }
        public required AssistantVoiceMetrics Metrics { get; init; }

        public static (int[] Hashed, double[] Dense) Features(string text)
        {
            text ??= "";
            var hashed = new HashSet<int>();
            string padded = "\u0002" + text + "\u0003";
            for (int i = 0; i + 3 <= padded.Length; i++) hashed.Add(LogisticModel.HashOf("c:" + padded.Substring(i, 3), HashDims));
            foreach (var t in TextFeatures.Tokens(text)) hashed.Add(LogisticModel.HashOf("w:" + t, HashDims));
            var line = StyleLine.Of(text);
            int sentences = text.Count(c => c is '.' or '!' or '?');
            int words = Math.Max(1, line.Words);
            double avgWord = text.Count(char.IsLetter) / (double)words;
            var dense = new[]
            {
                Math.Log(1 + text.Length) / 6,
                Math.Min(1, sentences / 5.0),
                line.Ending is LineEnding.Period or LineEnding.Exclamation or LineEnding.Question ? 1 : 0,
                line.Casing == CasingClass.UpperStart ? 1 : 0,
                line.Casing == CasingClass.AllLower ? 1 : 0,
                Math.Min(1, line.EmojiCount / 3.0),
                Math.Min(1, text.Count(c => c == ',') / 5.0),
                Math.Min(1, text.Count(c => c == '!') / 3.0),
                Math.Min(1, avgWord / 10),
                text.Contains('\n') ? 1 : 0,
            };
            return (hashed.ToArray(), dense);
        }

        public double Probability(string text) => Model.Predict(Features(text));

        public static AssistantVoiceModel Train(IReadOnlyList<string> real, IReadOnlyList<string> assistant, int version)
        {
            var samples = real.Select(t => (Text: t, Y: false)).Concat(assistant.Select(t => (Text: t, Y: true)))
                .Where(s => !string.IsNullOrWhiteSpace(s.Text)).ToList();
            // A stable 80/20 split by content hash.
            var train = samples.Where(s => TextFeatures.Unit("av|" + s.Text) >= 0.2).ToList();
            var test = samples.Where(s => TextFeatures.Unit("av|" + s.Text) < 0.2).ToList();
            if (train.Count < 20 || train.All(s => s.Y) || train.All(s => !s.Y)) train = samples;
            var model = LogisticModel.Train(train.Select(s => (Features(s.Text), s.Y)).ToList(), HashDims, DenseDims, epochs: 250, learningRate: 0.6, l2: 5e-4);
            var scored = test.Select(s => (P: model.Predict(Features(s.Text)), s.Y)).ToList();
            return new AssistantVoiceModel { Version = version, Model = model, Metrics = AssistantVoiceMetrics.Of(scored, real.Count, assistant.Count) };
        }

        public string ToJson() => JsonSerializer.Serialize(new Stored(Version, Model.HashDims, Model.DenseDims, Model.Weights, Model.Bias), KernelJson.Options);

        public static AssistantVoiceModel? FromJson(string? json, string? metricsJson)
        {
            var s = RJson.Read<Stored>(json);
            if (s == null || s.Weights == null || s.Weights.Length != s.HashDims + s.DenseDims) return null;
            return new AssistantVoiceModel
            {
                Version = s.Version,
                Model = new LogisticModel { HashDims = s.HashDims, DenseDims = s.DenseDims, Weights = s.Weights, Bias = s.Bias },
                Metrics = RJson.Read<AssistantVoiceMetrics>(metricsJson) ?? new AssistantVoiceMetrics(0, 0, 0, 0, 0, 0, 0),
            };
        }

        private sealed record Stored(int Version, int HashDims, int DenseDims, double[] Weights, double Bias);
    }

    public sealed record AssistantVoiceMetrics(int RealSamples, int AssistantSamples, int TestSamples, double Accuracy, double Precision, double Recall, double Auc)
    {
        public static AssistantVoiceMetrics Of(IReadOnlyList<(double P, bool Y)> scored, int real, int assistant)
        {
            if (scored.Count == 0) return new AssistantVoiceMetrics(real, assistant, 0, 0, 0, 0, 0);
            int tp = scored.Count(s => s.P >= 0.5 && s.Y), fp = scored.Count(s => s.P >= 0.5 && !s.Y), fn = scored.Count(s => s.P < 0.5 && s.Y);
            double accuracy = (double)scored.Count(s => (s.P >= 0.5) == s.Y) / scored.Count;
            double precision = tp + fp == 0 ? 0 : (double)tp / (tp + fp);
            double recall = tp + fn == 0 ? 0 : (double)tp / (tp + fn);
            // AUC: the probability a random positive outranks a random negative.
            var pos = scored.Where(s => s.Y).Select(s => s.P).ToList();
            var neg = scored.Where(s => !s.Y).Select(s => s.P).ToList();
            double auc = 0;
            if (pos.Count > 0 && neg.Count > 0)
            {
                double wins = 0;
                foreach (var p in pos) foreach (var n in neg) wins += p > n ? 1 : p == n ? 0.5 : 0;
                auc = wins / (pos.Count * (double)neg.Count);
            }
            return new AssistantVoiceMetrics(real, assistant, scored.Count, accuracy, precision, recall, auc);
        }
    }
}
