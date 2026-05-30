using Omnipotent.Services.OmniTrader.Strategy;

namespace Omnipotent.Tests.OmniTrader
{
    /// <summary>
    /// Validates the pure-C# TCN: because forward/backprop/Adam are hand-written, the key
    /// risk is a silent gradient bug that lets the net "train" without learning. These tests
    /// give it a pattern it provably can fit, then assert it actually does.
    /// </summary>
    public class TcnNetworkTests
    {
        private const int SeqLen = 64;
        private const int Feat   = 12;

        // Label is a function of two features at two different timesteps, so the model must
        // use both its channels AND its temporal receptive field to solve it.
        private static float Label(float[] w)
            => (w[63 * Feat + 0] + w[61 * Feat + 1] > 0f) ? 1f : 0f;

        private static (List<float[]> X, List<float> y) MakeData(int n, int seed)
        {
            var rng = new Random(seed);
            var X = new List<float[]>(n);
            var y = new List<float>(n);
            for (int s = 0; s < n; s++)
            {
                var w = new float[SeqLen * Feat];
                for (int i = 0; i < w.Length; i++)
                    w[i] = (float)(rng.NextDouble() * 2 - 1); // ~U(-1,1)
                X.Add(w);
                y.Add(Label(w));
            }
            return (X, y);
        }

        private static TcnNetwork.TrainOptions FastOpts() => new()
        {
            Hidden = 8,
            Kernel = 3,
            Dilations = new[] { 1, 2, 4 },
            Epochs = 30,
            Patience = 8,
            BatchSize = 32,
            LearningRate = 3e-3f,
            ValFraction = 0.2,
            Seed = 7,
        };

        [Fact]
        public void Trains_And_Learns_A_Predictable_Pattern()
        {
            var (X, y) = MakeData(900, seed: 1);
            var net = TcnNetwork.Train(X, y, SeqLen, Feat, FastOpts());

            // Held-out test set the model never saw.
            var (Xt, yt) = MakeData(300, seed: 999);
            int correct = 0;
            for (int i = 0; i < Xt.Count; i++)
            {
                double p = net.PredictUpProbability(Xt[i]);
                if ((p > 0.5 ? 1f : 0f) == yt[i]) correct++;
            }
            double acc = (double)correct / Xt.Count;

            // Random guessing ≈ 0.50. A correct implementation fits this easily (>0.85 typical).
            // 0.70 leaves margin for the short training schedule while still failing hard on a
            // broken gradient.
            Assert.True(acc > 0.70, $"Accuracy {acc:F3} is too low — backprop likely broken.");
        }

        [Fact]
        public void Save_Then_Load_Reproduces_Predictions()
        {
            var (X, y) = MakeData(400, seed: 2);
            var net = TcnNetwork.Train(X, y, SeqLen, Feat, FastOpts());

            string path = Path.Combine(Path.GetTempPath(), $"tcn_test_{Guid.NewGuid():N}.bin");
            try
            {
                net.Save(path);
                var loaded = TcnNetwork.Load(path);
                Assert.NotNull(loaded);

                var (Xt, _) = MakeData(50, seed: 42);
                foreach (var w in Xt)
                {
                    double a = net.PredictUpProbability(w);
                    double b = loaded!.PredictUpProbability(w);
                    Assert.Equal(a, b, 6); // identical to 6 decimals
                }
            }
            finally
            {
                if (File.Exists(path)) File.Delete(path);
            }
        }

        [Fact]
        public void Training_Is_Deterministic_For_A_Fixed_Seed()
        {
            var (X, y) = MakeData(400, seed: 3);
            var a = TcnNetwork.Train(X, y, SeqLen, Feat, FastOpts());
            var b = TcnNetwork.Train(X, y, SeqLen, Feat, FastOpts());

            var (Xt, _) = MakeData(40, seed: 5);
            foreach (var w in Xt)
                Assert.Equal(a.PredictUpProbability(w), b.PredictUpProbability(w), 6);
        }
    }
}
