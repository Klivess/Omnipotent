using System.Text;

namespace Omnipotent.Services.OmniTrader.Strategy
{
    /// <summary>
    /// Self-contained, dependency-free Temporal Convolutional Network for next-bar
    /// direction prediction. Implements forward inference, full backpropagation, and an
    /// Adam optimiser in pure C# — no PyTorch / ONNX / Python required. The model trains
    /// itself on historical candles (see TCNSignalStrategy) and serialises to a single
    /// binary file so subsequent runs load instantly.
    ///
    /// Architecture: a stack of causal, dilated 1-D convolutions with residual skips
    /// (Bai, Kolter, Koltun 2018). Causal = no future leakage. Dilated = the receptive
    /// field covers the whole 64-bar window with few layers.
    ///
    /// The network owns its own feature normalisation (means/stds fit on the training
    /// rows only) and its calibration temperature, so everything needed for inference
    /// lives in one object and one file.
    /// </summary>
    public sealed class TcnNetwork
    {
        private const uint MagicV1 = 0x544E4331; // "TNC1"

        // ── Architecture ────────────────────────────────────────────────────────
        public int SeqLen { get; private set; }
        public int FeatureCount { get; private set; }
        private int[] _dilations = Array.Empty<int>();
        private int _kernel;
        private int _hidden;

        // ── Calibration + normalisation ─────────────────────────────────────────
        public double Temperature { get; private set; } = 1.0;
        private float[] _featMeans = Array.Empty<float>();
        private float[] _featStds  = Array.Empty<float>();

        // ── Layers ──────────────────────────────────────────────────────────────
        private Block[] _blocks = Array.Empty<Block>();
        private float[] _headW = Array.Empty<float>(); // [hidden]
        private float _headB;

        // Adam moments for the head
        private float[] _headMW = Array.Empty<float>(), _headVW = Array.Empty<float>();
        private float _headMB, _headVB;

        private TcnNetwork() { }

        // ════════════════════════════════════════════════════════════════════════
        //  TRAINING
        // ════════════════════════════════════════════════════════════════════════

        public sealed class TrainOptions
        {
            public int Hidden = 16;
            public int Kernel = 3;
            public int[] Dilations = { 1, 2, 4, 8, 16 };
            public int Epochs = 40;
            public int Patience = 6;
            public int BatchSize = 64;
            public float LearningRate = 1.5e-3f;
            public float WeightDecay = 1e-4f;
            public double ValFraction = 0.2;
            public int MaxWindows = 3000;
            public int Seed = 1337;
        }

        /// <summary>
        /// Train a fresh network. <paramref name="rawWindows"/> are unnormalised feature
        /// windows, each laid out as [t*FeatureCount + f] (matching the strategy's
        /// BuildFeatureWindow). Labels are 0/1 next-bar direction. Windows are ordered
        /// chronologically; the final <see cref="TrainOptions.ValFraction"/> is held out
        /// for early stopping and temperature calibration (leakage-safe time split).
        /// </summary>
        public static TcnNetwork Train(
            IReadOnlyList<float[]> rawWindows, IReadOnlyList<float> labels,
            int seqLen, int featureCount, TrainOptions opt, Action<string>? log = null)
        {
            if (rawWindows.Count != labels.Count)
                throw new ArgumentException("rawWindows and labels length mismatch");
            if (rawWindows.Count < 50)
                throw new ArgumentException("Not enough windows to train");

            var rng = new Random(opt.Seed);

            // ── Subsample (chronologically) if there are too many windows ─────────
            int total = rawWindows.Count;
            int[] sel;
            if (total > opt.MaxWindows)
            {
                sel = new int[opt.MaxWindows];
                double step = (double)total / opt.MaxWindows;
                for (int i = 0; i < opt.MaxWindows; i++) sel[i] = (int)(i * step);
            }
            else
            {
                sel = new int[total];
                for (int i = 0; i < total; i++) sel[i] = i;
            }
            int n = sel.Length;

            // ── Chronological train/val split ─────────────────────────────────────
            int valCount   = Math.Max(1, (int)(n * opt.ValFraction));
            int trainCount = n - valCount;
            if (trainCount < 10) throw new ArgumentException("Not enough training windows after split");

            // ── Fit normalisation on TRAIN rows only ──────────────────────────────
            var (means, stds) = FitNormalization(rawWindows, sel, trainCount, seqLen, featureCount);

            // Pre-normalise all selected windows into a contiguous buffer.
            int wlen = seqLen * featureCount;
            float[][] X = new float[n][];
            float[]   y = new float[n];
            for (int s = 0; s < n; s++)
            {
                float[] src = rawWindows[sel[s]];
                float[] dst = new float[wlen];
                for (int t = 0; t < seqLen; t++)
                    for (int f = 0; f < featureCount; f++)
                    {
                        int idx = t * featureCount + f;
                        dst[idx] = stds[f] > 1e-8f ? (src[idx] - means[f]) / stds[f] : 0f;
                    }
                X[s] = dst;
                y[s] = labels[sel[s]];
            }

            // ── Build network ─────────────────────────────────────────────────────
            var net = new TcnNetwork
            {
                SeqLen = seqLen,
                FeatureCount = featureCount,
                _dilations = (int[])opt.Dilations.Clone(),
                _kernel = opt.Kernel,
                _hidden = opt.Hidden,
                _featMeans = means,
                _featStds = stds,
            };
            net.BuildLayers(rng);

            // ── Adam training with early stopping on validation BCE ───────────────
            int[] order = new int[trainCount];
            for (int i = 0; i < trainCount; i++) order[i] = i;

            double bestVal = double.PositiveInfinity;
            byte[]? bestSnapshot = null;
            int noImprove = 0;
            int adamStep = 0;

            for (int epoch = 1; epoch <= opt.Epochs; epoch++)
            {
                Shuffle(order, rng);
                double trLoss = 0;
                int b = 0;
                while (b < trainCount)
                {
                    int bs = Math.Min(opt.BatchSize, trainCount - b);
                    net.ZeroGrads();
                    for (int k = 0; k < bs; k++)
                    {
                        int si = order[b + k];
                        trLoss += net.ForwardBackward(X[si], y[si]);
                    }
                    adamStep++;
                    net.AdamStep(bs, opt.LearningRate, opt.WeightDecay, adamStep);
                    b += bs;
                }
                trLoss /= trainCount;

                // Validation BCE
                double valLoss = 0;
                for (int s = trainCount; s < n; s++)
                {
                    float logit = net.ForwardInferenceNormalized(X[s]);
                    valLoss += Bce(logit, y[s]);
                }
                valLoss /= valCount;

                if (epoch == 1 || epoch % 5 == 0)
                    log?.Invoke($"TCN train: epoch {epoch}/{opt.Epochs} tr={trLoss:F4} val={valLoss:F4}");

                if (valLoss < bestVal - 1e-5)
                {
                    bestVal = valLoss;
                    bestSnapshot = net.SnapshotWeights();
                    noImprove = 0;
                }
                else if (++noImprove >= opt.Patience)
                {
                    log?.Invoke($"TCN train: early stop at epoch {epoch} (best val={bestVal:F4})");
                    break;
                }
            }

            if (bestSnapshot != null) net.RestoreWeights(bestSnapshot);

            // ── Temperature calibration on validation set (Guo et al. 2017) ───────
            var valLogits = new float[valCount];
            var valLabels = new float[valCount];
            for (int s = trainCount; s < n; s++)
            {
                valLogits[s - trainCount] = net.ForwardInferenceNormalized(X[s]);
                valLabels[s - trainCount] = y[s];
            }
            net.Temperature = FitTemperature(valLogits, valLabels);

            // Report validation AUC / accuracy as a sanity signal.
            double auc = AreaUnderRoc(valLogits, valLabels);
            log?.Invoke($"TCN train: done. val_auc={auc:F4} T={net.Temperature:F3} " +
                        $"windows={n} (train={trainCount})");

            return net;
        }

        // ════════════════════════════════════════════════════════════════════════
        //  INFERENCE
        // ════════════════════════════════════════════════════════════════════════

        /// <summary>
        /// Calibrated P(next bar up) in (0,1). Input is an unnormalised window laid out
        /// as [t*FeatureCount + f]; normalisation and temperature are applied internally.
        /// </summary>
        public double PredictUpProbability(float[] rawWindow)
        {
            float[] x = new float[SeqLen * FeatureCount];
            for (int t = 0; t < SeqLen; t++)
                for (int f = 0; f < FeatureCount; f++)
                {
                    int idx = t * FeatureCount + f;
                    x[idx] = _featStds[f] > 1e-8f ? (rawWindow[idx] - _featMeans[f]) / _featStds[f] : 0f;
                }
            float logit = ForwardInferenceNormalized(x);
            return Sigmoid(logit / (float)Temperature);
        }

        // ════════════════════════════════════════════════════════════════════════
        //  LAYER MATH
        // ════════════════════════════════════════════════════════════════════════

        // A residual block: causal-dilated conv -> ReLU, plus a skip (identity or 1x1).
        private sealed class Block
        {
            public Conv1D Conv = null!;
            public Conv1D? Proj;       // 1x1 projection when inC != outC, else null (identity)
            public bool[] ReluMask = Array.Empty<bool>(); // cached for backward, [outC*T]
            public float[] ConvOut = Array.Empty<float>();
            public float[] InputCache = Array.Empty<float>();
            public int InC, OutC, T;
        }

        // Causal dilated 1-D convolution. Weights [outC*inC*K], channel-major IO [c*T+t].
        private sealed class Conv1D
        {
            public int InC, OutC, K, Dilation;
            public float[] W = Array.Empty<float>();
            public float[] B = Array.Empty<float>();
            public float[] GW = Array.Empty<float>();
            public float[] GB = Array.Empty<float>();
            public float[] MW = Array.Empty<float>(), VW = Array.Empty<float>();
            public float[] MB = Array.Empty<float>(), VB = Array.Empty<float>();
            public float[] InputCache = Array.Empty<float>();

            public void Init(int inC, int outC, int k, int dilation, Random rng)
            {
                InC = inC; OutC = outC; K = k; Dilation = dilation;
                W = new float[outC * inC * k];
                B = new float[outC];
                GW = new float[W.Length]; GB = new float[B.Length];
                MW = new float[W.Length]; VW = new float[W.Length];
                MB = new float[B.Length]; VB = new float[B.Length];
                double std = Math.Sqrt(2.0 / (inC * k)); // Kaiming (ReLU)
                for (int i = 0; i < W.Length; i++) W[i] = (float)(NextGaussian(rng) * std);
            }

            public float[] Forward(float[] x, int T)
            {
                InputCache = x;
                var y = new float[OutC * T];
                for (int oc = 0; oc < OutC; oc++)
                {
                    int wBase = oc * InC * K;
                    for (int t = 0; t < T; t++)
                    {
                        float acc = B[oc];
                        for (int ic = 0; ic < InC; ic++)
                        {
                            int wIdx = wBase + ic * K;
                            int xBase = ic * T;
                            for (int j = 0; j < K; j++)
                            {
                                int srcT = t - (K - 1 - j) * Dilation;
                                if (srcT >= 0) acc += W[wIdx + j] * x[xBase + srcT];
                            }
                        }
                        y[oc * T + t] = acc;
                    }
                }
                return y;
            }

            // Accumulates GW/GB; returns dInput if requested.
            public float[]? Backward(float[] dy, int T, bool needDx)
            {
                float[]? dx = needDx ? new float[InC * T] : null;
                for (int oc = 0; oc < OutC; oc++)
                {
                    int wBase = oc * InC * K;
                    for (int t = 0; t < T; t++)
                    {
                        float g = dy[oc * T + t];
                        if (g == 0f) continue;
                        GB[oc] += g;
                        for (int ic = 0; ic < InC; ic++)
                        {
                            int wIdx = wBase + ic * K;
                            int xBase = ic * T;
                            for (int j = 0; j < K; j++)
                            {
                                int srcT = t - (K - 1 - j) * Dilation;
                                if (srcT < 0) continue;
                                GW[wIdx + j] += g * InputCache[xBase + srcT];
                                if (dx != null) dx[xBase + srcT] += g * W[wIdx + j];
                            }
                        }
                    }
                }
                return dx;
            }
        }

        private void BuildLayers(Random rng)
        {
            _blocks = new Block[_dilations.Length];
            int inC = FeatureCount;
            for (int bi = 0; bi < _dilations.Length; bi++)
            {
                var conv = new Conv1D();
                conv.Init(inC, _hidden, _kernel, _dilations[bi], rng);
                Conv1D? proj = null;
                if (inC != _hidden)
                {
                    proj = new Conv1D();
                    proj.Init(inC, _hidden, 1, 1, rng); // 1x1
                }
                _blocks[bi] = new Block { Conv = conv, Proj = proj, InC = inC, OutC = _hidden };
                inC = _hidden;
            }

            _headW = new float[_hidden];
            _headMW = new float[_hidden]; _headVW = new float[_hidden];
            double hstd = Math.Sqrt(1.0 / _hidden);
            for (int i = 0; i < _hidden; i++) _headW[i] = (float)(NextGaussian(rng) * hstd);
            _headB = 0f;
        }

        // Forward pass that caches activations for backprop. Returns logit.
        private float _fwdLogit;
        private float[] _finalOut = Array.Empty<float>(); // last block output [hidden*T]

        private float ForwardTrain(float[] normalizedWindow)
        {
            int T = SeqLen;
            float[] cur = TransposeToChannelMajor(normalizedWindow, T, FeatureCount);
            int curC = FeatureCount;

            foreach (var blk in _blocks)
            {
                blk.T = T;
                blk.InputCache = cur;
                float[] convOut = blk.Conv.Forward(cur, T);
                blk.ConvOut = convOut;

                // ReLU
                var mask = new bool[convOut.Length];
                var relu = new float[convOut.Length];
                for (int i = 0; i < convOut.Length; i++)
                {
                    bool pos = convOut[i] > 0f;
                    mask[i] = pos;
                    relu[i] = pos ? convOut[i] : 0f;
                }
                blk.ReluMask = mask;

                // Skip
                float[] skip = blk.Proj != null ? blk.Proj.Forward(cur, T) : cur;
                var outp = new float[blk.OutC * T];
                for (int i = 0; i < outp.Length; i++) outp[i] = relu[i] + skip[i];

                cur = outp;
                curC = blk.OutC;
            }

            _finalOut = cur;

            // Head on last timestep
            float logit = _headB;
            for (int c = 0; c < _hidden; c++)
                logit += _headW[c] * cur[c * T + (T - 1)];
            _fwdLogit = logit;
            return logit;
        }

        // Returns BCE loss and accumulates gradients for one sample.
        private double ForwardBackward(float[] normalizedWindow, float label)
        {
            int T = SeqLen;
            float logit = ForwardTrain(normalizedWindow);
            double loss = Bce(logit, label);

            // dL/dlogit = sigmoid(logit) - y
            float dLogit = Sigmoid(logit) - label;

            // Head backward
            var dFinal = new float[_hidden * T];
            for (int c = 0; c < _hidden; c++)
            {
                int idx = c * T + (T - 1);
                _headGW[c] += dLogit * _finalOut[idx];
                dFinal[idx] = dLogit * _headW[c];
            }
            _headGB += dLogit;

            // Blocks backward (reverse)
            float[] dCur = dFinal;
            for (int bi = _blocks.Length - 1; bi >= 0; bi--)
            {
                var blk = _blocks[bi];
                // out = relu(convOut) + skip  ⇒ gradient splits to both branches
                var dRelu = new float[dCur.Length];
                for (int i = 0; i < dCur.Length; i++)
                    dRelu[i] = blk.ReluMask[i] ? dCur[i] : 0f;

                float[]? dConvIn = blk.Conv.Backward(dRelu, T, needDx: true);

                float[] dSkipIn;
                if (blk.Proj != null)
                    dSkipIn = blk.Proj.Backward(dCur, T, needDx: true)!;
                else
                    dSkipIn = dCur; // identity skip

                // Sum branch gradients into dInput
                var dIn = new float[blk.InC * T];
                for (int i = 0; i < dIn.Length; i++)
                    dIn[i] = (dConvIn != null ? dConvIn[i] : 0f) + dSkipIn[i];

                dCur = dIn;
            }

            return loss;
        }

        // ── Gradient buffers for the head ────────────────────────────────────────
        private float[] _headGW = Array.Empty<float>();
        private float _headGB;

        private void ZeroGrads()
        {
            if (_headGW.Length != _hidden) _headGW = new float[_hidden];
            else Array.Clear(_headGW);
            _headGB = 0f;
            foreach (var blk in _blocks)
            {
                Array.Clear(blk.Conv.GW); Array.Clear(blk.Conv.GB);
                if (blk.Proj != null) { Array.Clear(blk.Proj.GW); Array.Clear(blk.Proj.GB); }
            }
        }

        private void AdamStep(int batchSize, float lr, float weightDecay, int step)
        {
            float invB = 1f / batchSize;
            const float beta1 = 0.9f, beta2 = 0.999f, eps = 1e-8f;
            float bc1 = 1f - (float)Math.Pow(beta1, step);
            float bc2 = 1f - (float)Math.Pow(beta2, step);

            void Upd(float[] w, float[] g, float[] m, float[] v)
            {
                for (int i = 0; i < w.Length; i++)
                {
                    float grad = g[i] * invB + weightDecay * w[i];
                    m[i] = beta1 * m[i] + (1 - beta1) * grad;
                    v[i] = beta2 * v[i] + (1 - beta2) * grad * grad;
                    float mh = m[i] / bc1, vh = v[i] / bc2;
                    w[i] -= lr * mh / ((float)Math.Sqrt(vh) + eps);
                }
            }

            foreach (var blk in _blocks)
            {
                Upd(blk.Conv.W, blk.Conv.GW, blk.Conv.MW, blk.Conv.VW);
                Upd(blk.Conv.B, blk.Conv.GB, blk.Conv.MB, blk.Conv.VB);
                if (blk.Proj != null)
                {
                    Upd(blk.Proj.W, blk.Proj.GW, blk.Proj.MW, blk.Proj.VW);
                    Upd(blk.Proj.B, blk.Proj.GB, blk.Proj.MB, blk.Proj.VB);
                }
            }
            // Head (no weight decay on bias)
            for (int i = 0; i < _hidden; i++)
            {
                float grad = _headGW[i] * invB + weightDecay * _headW[i];
                _headMW[i] = beta1 * _headMW[i] + (1 - beta1) * grad;
                _headVW[i] = beta2 * _headVW[i] + (1 - beta2) * grad * grad;
                _headW[i] -= lr * (_headMW[i] / bc1) / ((float)Math.Sqrt(_headVW[i] / bc2) + eps);
            }
            float gb = _headGB * invB;
            _headMB = beta1 * _headMB + (1 - beta1) * gb;
            _headVB = beta2 * _headVB + (1 - beta2) * gb * gb;
            _headB -= lr * (_headMB / bc1) / ((float)Math.Sqrt(_headVB / bc2) + eps);
        }

        // Pure inference forward (no caching). Input already normalised, [t*F+f] layout.
        private float ForwardInferenceNormalized(float[] normalizedWindow)
        {
            int T = SeqLen;
            float[] cur = TransposeToChannelMajor(normalizedWindow, T, FeatureCount);

            foreach (var blk in _blocks)
            {
                float[] convOut = blk.Conv.Forward(cur, T);
                float[] skip = blk.Proj != null ? blk.Proj.Forward(cur, T) : cur;
                var outp = new float[blk.OutC * T];
                for (int i = 0; i < outp.Length; i++)
                {
                    float r = convOut[i] > 0f ? convOut[i] : 0f;
                    outp[i] = r + skip[i];
                }
                cur = outp;
            }

            float logit = _headB;
            for (int c = 0; c < _hidden; c++)
                logit += _headW[c] * cur[c * T + (T - 1)];
            return logit;
        }

        private static float[] TransposeToChannelMajor(float[] window, int T, int F)
        {
            // window[t*F+f]  ->  out[f*T+t]
            var outp = new float[F * T];
            for (int t = 0; t < T; t++)
                for (int f = 0; f < F; f++)
                    outp[f * T + t] = window[t * F + f];
            return outp;
        }

        // ════════════════════════════════════════════════════════════════════════
        //  NORMALISATION / CALIBRATION HELPERS
        // ════════════════════════════════════════════════════════════════════════

        private static (float[] means, float[] stds) FitNormalization(
            IReadOnlyList<float[]> rawWindows, int[] sel, int trainCount,
            int seqLen, int featureCount)
        {
            var means = new float[featureCount];
            var stds = new float[featureCount];
            var sum = new double[featureCount];
            var sumSq = new double[featureCount];
            long count = 0;

            for (int s = 0; s < trainCount; s++)
            {
                float[] w = rawWindows[sel[s]];
                for (int t = 0; t < seqLen; t++)
                    for (int f = 0; f < featureCount; f++)
                    {
                        float v = w[t * featureCount + f];
                        sum[f] += v; sumSq[f] += (double)v * v;
                    }
                count += seqLen;
            }
            for (int f = 0; f < featureCount; f++)
            {
                double mean = sum[f] / count;
                double var = sumSq[f] / count - mean * mean;
                means[f] = (float)mean;
                stds[f] = (float)Math.Sqrt(Math.Max(var, 0.0)) + 1e-8f;
            }
            return (means, stds);
        }

        // Fit scalar temperature minimising validation BCE via simple gradient descent.
        private static double FitTemperature(float[] logits, float[] labels)
        {
            double logT = 0.0; // T = exp(logT) keeps T > 0
            double lr = 0.05;
            for (int iter = 0; iter < 200; iter++)
            {
                double T = Math.Exp(logT);
                double grad = 0;
                for (int i = 0; i < logits.Length; i++)
                {
                    double z = logits[i] / T;
                    double p = 1.0 / (1.0 + Math.Exp(-z));
                    // dBCE/dz = p - y ; dz/dlogT = -logits/T = -z
                    grad += (p - labels[i]) * (-z);
                }
                grad /= logits.Length;
                logT -= lr * grad;
                if (logT > 3) logT = 3; if (logT < -3) logT = -3; // clamp T in [~0.05, ~20]
            }
            return Math.Exp(logT);
        }

        // ════════════════════════════════════════════════════════════════════════
        //  WEIGHT SNAPSHOT (for early stopping) + SERIALIZATION
        // ════════════════════════════════════════════════════════════════════════

        private byte[] SnapshotWeights()
        {
            using var ms = new MemoryStream();
            using var bw = new BinaryWriter(ms);
            foreach (var blk in _blocks)
            {
                WriteFloats(bw, blk.Conv.W); WriteFloats(bw, blk.Conv.B);
                if (blk.Proj != null) { WriteFloats(bw, blk.Proj.W); WriteFloats(bw, blk.Proj.B); }
            }
            WriteFloats(bw, _headW); bw.Write(_headB);
            return ms.ToArray();
        }

        private void RestoreWeights(byte[] snapshot)
        {
            using var ms = new MemoryStream(snapshot);
            using var br = new BinaryReader(ms);
            foreach (var blk in _blocks)
            {
                ReadFloatsInto(br, blk.Conv.W); ReadFloatsInto(br, blk.Conv.B);
                if (blk.Proj != null) { ReadFloatsInto(br, blk.Proj.W); ReadFloatsInto(br, blk.Proj.B); }
            }
            ReadFloatsInto(br, _headW); _headB = br.ReadSingle();
        }

        public void Save(string path)
        {
            using var fs = File.Create(path);
            using var bw = new BinaryWriter(fs, Encoding.UTF8);
            bw.Write(MagicV1);
            bw.Write(SeqLen); bw.Write(FeatureCount);
            bw.Write(_kernel); bw.Write(_hidden);
            bw.Write(_dilations.Length);
            foreach (int d in _dilations) bw.Write(d);
            bw.Write(Temperature);
            WriteFloats(bw, _featMeans);
            WriteFloats(bw, _featStds);
            foreach (var blk in _blocks)
            {
                bw.Write(blk.Proj != null);
                WriteFloats(bw, blk.Conv.W); WriteFloats(bw, blk.Conv.B);
                if (blk.Proj != null) { WriteFloats(bw, blk.Proj.W); WriteFloats(bw, blk.Proj.B); }
            }
            WriteFloats(bw, _headW); bw.Write(_headB);
        }

        public static TcnNetwork? Load(string path)
        {
            try
            {
                using var fs = File.OpenRead(path);
                using var br = new BinaryReader(fs, Encoding.UTF8);
                if (br.ReadUInt32() != MagicV1) return null;

                var net = new TcnNetwork
                {
                    SeqLen = br.ReadInt32(),
                    FeatureCount = br.ReadInt32(),
                };
                net._kernel = br.ReadInt32();
                net._hidden = br.ReadInt32();
                int nd = br.ReadInt32();
                net._dilations = new int[nd];
                for (int i = 0; i < nd; i++) net._dilations[i] = br.ReadInt32();
                net.Temperature = br.ReadDouble();
                net._featMeans = ReadFloats(br);
                net._featStds  = ReadFloats(br);

                net._blocks = new Block[nd];
                int inC = net.FeatureCount;
                for (int bi = 0; bi < nd; bi++)
                {
                    bool hasProj = br.ReadBoolean();
                    var conv = new Conv1D { InC = inC, OutC = net._hidden, K = net._kernel, Dilation = net._dilations[bi] };
                    conv.W = ReadFloats(br); conv.B = ReadFloats(br);
                    Conv1D? proj = null;
                    if (hasProj)
                    {
                        proj = new Conv1D { InC = inC, OutC = net._hidden, K = 1, Dilation = 1 };
                        proj.W = ReadFloats(br); proj.B = ReadFloats(br);
                    }
                    net._blocks[bi] = new Block { Conv = conv, Proj = proj, InC = inC, OutC = net._hidden };
                    inC = net._hidden;
                }
                net._headW = ReadFloats(br); net._headB = br.ReadSingle();
                return net;
            }
            catch { return null; }
        }

        // ════════════════════════════════════════════════════════════════════════
        //  SMALL HELPERS
        // ════════════════════════════════════════════════════════════════════════

        private static void WriteFloats(BinaryWriter bw, float[] a)
        {
            bw.Write(a.Length);
            foreach (float v in a) bw.Write(v);
        }
        private static float[] ReadFloats(BinaryReader br)
        {
            int len = br.ReadInt32();
            var a = new float[len];
            for (int i = 0; i < len; i++) a[i] = br.ReadSingle();
            return a;
        }
        private static void ReadFloatsInto(BinaryReader br, float[] dst)
        {
            int len = br.ReadInt32();
            for (int i = 0; i < len && i < dst.Length; i++) dst[i] = br.ReadSingle();
        }

        private static float Sigmoid(float x) => 1f / (1f + (float)Math.Exp(-x));

        private static double Bce(float logit, float y)
        {
            // numerically stable: max(z,0) - z*y + log(1+exp(-|z|))
            double z = logit;
            return Math.Max(z, 0) - z * y + Math.Log(1 + Math.Exp(-Math.Abs(z)));
        }

        private static void Shuffle(int[] a, Random rng)
        {
            for (int i = a.Length - 1; i > 0; i--)
            {
                int j = rng.Next(i + 1);
                (a[i], a[j]) = (a[j], a[i]);
            }
        }

        private static double NextGaussian(Random rng)
        {
            double u1 = 1.0 - rng.NextDouble();
            double u2 = 1.0 - rng.NextDouble();
            return Math.Sqrt(-2.0 * Math.Log(u1)) * Math.Cos(2.0 * Math.PI * u2);
        }

        private static double AreaUnderRoc(float[] scores, float[] labels)
        {
            int n = scores.Length;
            var idx = new int[n];
            for (int i = 0; i < n; i++) idx[i] = i;
            Array.Sort(idx, (a, b) => scores[a].CompareTo(scores[b]));
            // rank-sum (Mann-Whitney) with average ranks for ties
            double rankSumPos = 0; int nPos = 0, nNeg = 0;
            int i2 = 0;
            while (i2 < n)
            {
                int j = i2;
                while (j < n && scores[idx[j]] == scores[idx[i2]]) j++;
                double avgRank = (i2 + 1 + j) / 2.0; // ranks are 1-based
                for (int k = i2; k < j; k++)
                {
                    if (labels[idx[k]] > 0.5f) { rankSumPos += avgRank; nPos++; }
                    else nNeg++;
                }
                i2 = j;
            }
            if (nPos == 0 || nNeg == 0) return 0.5;
            return (rankSumPos - nPos * (nPos + 1) / 2.0) / ((double)nPos * nNeg);
        }
    }
}
