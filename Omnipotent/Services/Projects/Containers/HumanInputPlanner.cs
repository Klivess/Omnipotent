namespace Omnipotent.Services.Projects.Containers
{
    /// <summary>One absolute pointer sample plus the pause to hold after emitting it.</summary>
    public readonly record struct MotionStep(int X, int Y, int DelayMs);

    /// <summary>Timing/offset envelope for a single physical button press.</summary>
    public readonly record struct ClickPlan(int SettleBeforeMs, int DownHoldMs, int MicroDx, int MicroDy, int GapAfterMs);

    /// <summary>One wheel notch: <c>Reverse</c> marks an overscroll correction the opposite way.</summary>
    public readonly record struct ScrollStep(bool Reverse, int DelayMs);

    /// <summary>One keystroke. When <see cref="ThenBackspace"/> is set this is a deliberate typo:
    /// the character is typed, briefly "noticed", then erased before the real character follows.</summary>
    public readonly record struct TypeStep(char Char, int PreDelayMs, int HoldMs, bool ThenBackspace);

    /// <summary>
    /// Pure, deterministic generators that turn a <see cref="HumanInputProfile"/> plus a caller-owned
    /// <see cref="Random"/> into concrete, human-shaped event sequences. Nothing here touches a socket,
    /// so the human-ness (curved paths, non-uniform timing, typo/correction, momentum scrolling) is
    /// directly unit-testable and reproducible for a given seed. <see cref="HumanizedInput"/> simply
    /// replays what these produce on the RFB transport.
    /// </summary>
    public static class HumanInputPlanner
    {
        // ── mouse movement ─────────────────────────────────────────────────────────────────────

        /// <summary>Plans a curved, variable-velocity path from the current pointer to (toX,toY).
        /// The path bows to one side, decelerates into the target, sometimes overshoots and corrects,
        /// and lands a pixel or two off-centre — never a straight constant-speed teleport.</summary>
        public static IReadOnlyList<MotionStep> PlanMove(int fromX, int fromY, int toX, int toY,
            int width, int height, HumanInputProfile profile, Random rng)
        {
            var steps = new List<MotionStep>();
            double intensity = profile.Intensity;
            double steady = profile.MouseSteadiness;

            // Land slightly off the mathematical centre (humans don't hit dead-centre repeatedly).
            double landJitter = (1 - steady) * 2.4 * intensity;
            int targetX = Clamp((int)Math.Round(toX + Gaussian(rng) * landJitter), width);
            int targetY = Clamp((int)Math.Round(toY + Gaussian(rng) * landJitter), height);

            double dist = Math.Sqrt(Sq(targetX - fromX) + Sq(targetY - fromY));
            if (dist < 2.0)
            {
                steps.Add(new MotionStep(targetX, targetY, 0));
                return steps;
            }

            // Occasionally overshoot the target then correct back — a classic ballistic sub-movement.
            bool overshoot = dist > 90 && rng.NextDouble() < (0.28 * (1 - steady) + 0.05) * intensity;
            int viaX = targetX, viaY = targetY;
            if (overshoot)
            {
                double ox = (targetX - fromX) / dist, oy = (targetY - fromY) / dist;
                double extra = 8 + rng.NextDouble() * 22 * (1 - steady);
                viaX = Clamp((int)Math.Round(targetX + ox * extra), width);
                viaY = Clamp((int)Math.Round(targetY + oy * extra), height);
            }

            // Fitts's-law-ish travel time: grows with the log of distance, scaled by persona speed
            // and the global intensity (intensity→0 collapses toward the fast robotic baseline).
            double idealW = 26.0;
            double fitts = 90 + 160 * Math.Log2(dist / idealW + 1);
            double durationMs = Lerp(35, fitts / Math.Max(0.3, profile.MoveSpeed), intensity)
                                * (0.85 + rng.NextDouble() * 0.35);

            AppendBezier(steps, fromX, fromY, viaX, viaY, dist, steady, intensity,
                overshoot ? durationMs * 0.82 : durationMs, width, height, rng, finalTarget: !overshoot,
                targetX, targetY);

            if (overshoot)
            {
                double corrDist = Math.Sqrt(Sq(targetX - viaX) + Sq(targetY - viaY));
                AppendBezier(steps, viaX, viaY, targetX, targetY, corrDist, steady, intensity,
                    durationMs * 0.28 + 40, width, height, rng, finalTarget: true, targetX, targetY);
            }
            return steps;
        }

        private static void AppendBezier(List<MotionStep> steps, int fromX, int fromY, int toX, int toY,
            double dist, double steady, double intensity, double durationMs, int width, int height,
            Random rng, bool finalTarget, int trueTargetX, int trueTargetY)
        {
            int n = Math.Clamp((int)Math.Round(dist / (9 + rng.NextDouble() * 8)), 4, 90);

            // Two control points offset perpendicular to the straight line give a natural bow; the
            // offset magnitude and side are randomised and damped by the persona's steadiness.
            double dx = toX - fromX, dy = toY - fromY;
            double len = Math.Max(1e-3, Math.Sqrt(dx * dx + dy * dy));
            double px = -dy / len, py = dx / len;
            double bow = dist * (0.06 + rng.NextDouble() * 0.16) * (1 - steady) * intensity
                         * (rng.NextDouble() < 0.5 ? -1 : 1);
            double c1x = fromX + dx * 0.30 + px * bow, c1y = fromY + dy * 0.30 + py * bow;
            double c2x = fromX + dx * 0.66 + px * bow * 0.6, c2y = fromY + dy * 0.66 + py * bow * 0.6;

            double tremor = (1 - steady) * 1.3 * intensity;
            for (int i = 1; i <= n; i++)
            {
                double t = (double)i / n;
                double e = MinJerk(t);           // bell-shaped velocity: slow-fast-slow
                double bx = Cubic(fromX, c1x, c2x, toX, e);
                double by = Cubic(fromY, c1y, c2y, toY, e);
                if (i < n)
                {
                    bx += Gaussian(rng) * tremor;
                    by += Gaussian(rng) * tremor;
                }
                int sx = finalTarget && i == n ? trueTargetX : Clamp((int)Math.Round(bx), width);
                int sy = finalTarget && i == n ? trueTargetY : Clamp((int)Math.Round(by), height);

                // Time per segment follows the same velocity envelope, so pauses are longest near
                // the ends of the stroke where the hand accelerates and decelerates.
                double segFrac = MinJerk(t) - MinJerk((double)(i - 1) / n);
                int delay = (int)Math.Round(durationMs * segFrac);
                steps.Add(new MotionStep(sx, sy, Math.Clamp(delay, 1, 90)));
            }
        }

        // ── clicks ─────────────────────────────────────────────────────────────────────────────

        public static ClickPlan PlanClick(HumanInputProfile profile, Random rng)
        {
            double intensity = profile.Intensity;
            int settle = (int)Math.Round(Lerp(0, 40 + rng.NextDouble() * 130, intensity));
            int hold = (int)Math.Round(Lerp(30, LogNormal(rng, 78, 0.4), intensity));
            int micro = rng.NextDouble() < 0.6 * intensity ? (rng.Next(0, 3)) : 0;
            int gap = (int)Math.Round(Lerp(60, 90 + rng.NextDouble() * 110, intensity)); // between multi-clicks
            return new ClickPlan(
                SettleBeforeMs: Math.Clamp(settle, 0, 400),
                DownHoldMs: Math.Clamp(hold, 30, 220),
                MicroDx: micro == 0 ? 0 : rng.Next(-micro, micro + 1),
                MicroDy: micro == 0 ? 0 : rng.Next(-micro, micro + 1),
                GapAfterMs: Math.Clamp(gap, 50, 260));
        }

        /// <summary>A gaussian-within-bounds click point biased toward — but rarely exactly at — the
        /// centre of a control whose bounding box is known. Avoids the dead-centre-every-time tell.</summary>
        public static (int X, int Y) HumanizeClickPoint(int centreX, int centreY, int boundsWidth,
            int boundsHeight, HumanInputProfile profile, Random rng)
        {
            if (!profile.Enabled || boundsWidth <= 4 || boundsHeight <= 4) return (centreX, centreY);
            double spreadX = Math.Min(boundsWidth * 0.22, 14) * profile.Intensity;
            double spreadY = Math.Min(boundsHeight * 0.22, 10) * profile.Intensity;
            int x = centreX + (int)Math.Round(Gaussian(rng) * spreadX);
            int y = centreY + (int)Math.Round(Gaussian(rng) * spreadY);
            // Stay inside the control.
            x = Math.Clamp(x, centreX - boundsWidth / 2 + 2, centreX + boundsWidth / 2 - 2);
            y = Math.Clamp(y, centreY - boundsHeight / 2 + 2, centreY + boundsHeight / 2 - 2);
            return (x, y);
        }

        /// <summary>Reaction time before acting on something that just appeared/was located.</summary>
        public static int ReactionDelayMs(HumanInputProfile profile, Random rng) =>
            (int)Math.Round(Lerp(0, 140 + rng.NextDouble() * 320, profile.Intensity));

        // ── scrolling ──────────────────────────────────────────────────────────────────────────

        /// <summary>Momentum scroll: a burst that accelerates then decelerates, with the occasional
        /// overscroll-and-correct and reading pause, instead of uniform fixed-gap notches.</summary>
        public static IReadOnlyList<ScrollStep> PlanScroll(int notches, HumanInputProfile profile, Random rng)
        {
            var steps = new List<ScrollStep>();
            notches = Math.Clamp(notches, 1, 100);
            double intensity = profile.Intensity;
            bool smooth = profile.Scroll == ScrollStyle.Smooth;
            double baseGap = smooth ? 14 : 26;

            for (int i = 0; i < notches; i++)
            {
                double phase = notches <= 1 ? 0.5 : (double)i / (notches - 1);
                double envelope = 0.5 + 0.9 * Math.Abs(phase - 0.5) * 2; // faster in the middle
                int gap = (int)Math.Round(Lerp(smooth ? 8 : 22,
                    baseGap * envelope * (0.8 + rng.NextDouble() * 0.5), intensity));
                // Occasional mid-scroll reading pause.
                if (intensity > 0 && rng.NextDouble() < 0.06) gap += 150 + rng.Next(0, 500);
                steps.Add(new ScrollStep(false, Math.Clamp(gap, 6, 900)));
            }
            // Sometimes overshoot the intended stop and nudge back.
            if (notches >= 3 && rng.NextDouble() < 0.22 * intensity)
                steps.Add(new ScrollStep(true, 120 + rng.Next(0, 200)));
            return steps;
        }

        // ── typing ─────────────────────────────────────────────────────────────────────────────

        /// <summary>
        /// Plans keystroke-by-keystroke timing for a string: log-normal inter-key gaps around the
        /// persona's WPM, digraph-aware (same-finger/same-hand sequences slow down, alternating hands
        /// speed up), longer "thinking" pauses at word and sentence boundaries, a longer reach for the
        /// first key, and an occasional mistype that is noticed and corrected with backspace.
        /// </summary>
        public static IReadOnlyList<TypeStep> PlanTyping(string text, HumanInputProfile profile, Random rng)
        {
            var steps = new List<TypeStep>();
            if (string.IsNullOrEmpty(text)) return steps;
            double intensity = profile.Intensity;
            double baseMs = 60000.0 / Math.Max(20, profile.TypingWpm * 5); // ms per char at persona WPM
            char prev = '\0';

            for (int i = 0; i < text.Length; i++)
            {
                char c = text[i];
                double factor = DigraphFactor(prev, c);
                if (prev == ' ' || prev == '\0') factor += 0.35;              // re-orient after a space
                if (prev is '.' or '!' or '?' or ',' or ';' or ':') factor += 0.7; // sentence/clause think
                if (i == 0) factor += 1.4;                                   // find the field first

                double mean = baseMs * factor;
                double pre = LogNormal(rng, mean, profile.TypingVariability);
                // Fatigue: cadence drifts slightly slower across a long field.
                pre *= 1 + Math.Min(0.18, i / 900.0);
                int preDelay = (int)Math.Round(Lerp(6, pre, intensity));
                int hold = (int)Math.Round(Lerp(12, 35 + rng.NextDouble() * 55, intensity));

                // Deliberate mistype + correction (letters only; guarantees net-correct output because
                // the wrong key is always erased with a single backspace before the real one).
                if (intensity > 0 && char.IsLetter(c) && rng.NextDouble() < profile.TypoRate)
                {
                    char wrong = NeighbourKey(c, rng);
                    if (wrong != c)
                        steps.Add(new TypeStep(wrong, preDelay, hold, ThenBackspace: true));
                    // The real keystroke follows immediately after the correction, quickly.
                    steps.Add(new TypeStep(c, 40 + rng.Next(0, 90), hold, ThenBackspace: false));
                }
                else
                {
                    steps.Add(new TypeStep(c, Math.Clamp(preDelay, 0, 1500), hold, ThenBackspace: false));
                }
                prev = c;
            }
            return steps;
        }

        // ── keyboard geometry for digraph timing ─────────────────────────────────────────────────

        // (row, column, hand: 0=left 1=right) for the QWERTY letters, used to slow same-finger and
        // same-hand transitions and speed up alternating-hand ones — the dominant driver of the
        // uneven rhythm real typing has.
        private static readonly Dictionary<char, (int Row, int Col, int Hand)> Keymap = BuildKeymap();

        private static double DigraphFactor(char prev, char cur)
        {
            char a = char.ToLowerInvariant(prev), b = char.ToLowerInvariant(cur);
            if (!Keymap.TryGetValue(a, out var ka) || !Keymap.TryGetValue(b, out var kb)) return 1.0;
            if (a == b) return 1.15;                        // double letter: quick but not identical
            if (ka.Hand != kb.Hand) return 0.82;            // alternating hands: fast
            if (ka.Col == kb.Col) return 1.45;              // same finger (same column), same hand: slow
            return 1.12;                                    // same hand, different finger
        }

        private static char NeighbourKey(char c, Random rng)
        {
            char lower = char.ToLowerInvariant(c);
            if (!Keymap.TryGetValue(lower, out var k)) return c;
            foreach (var (ch, pos) in Shuffle(Keymap, rng))
            {
                if (ch == lower) continue;
                if (pos.Row == k.Row && Math.Abs(pos.Col - k.Col) == 1)
                    return char.IsUpper(c) ? char.ToUpperInvariant(ch) : ch;
            }
            return c;
        }

        private static Dictionary<char, (int, int, int)> BuildKeymap()
        {
            var map = new Dictionary<char, (int, int, int)>();
            string[] rows = { "qwertyuiop", "asdfghjkl", "zxcvbnm" };
            for (int r = 0; r < rows.Length; r++)
                for (int col = 0; col < rows[r].Length; col++)
                    map[rows[r][col]] = (r, col, col <= 4 ? 0 : 1);
            return map;
        }

        private static IEnumerable<KeyValuePair<char, (int Row, int Col, int Hand)>> Shuffle(
            Dictionary<char, (int, int, int)> source, Random rng)
        {
            var list = source.ToList();
            for (int i = list.Count - 1; i > 0; i--)
            {
                int j = rng.Next(i + 1);
                (list[i], list[j]) = (list[j], list[i]);
            }
            return list;
        }

        // ── math helpers ─────────────────────────────────────────────────────────────────────────

        private static double MinJerk(double t) => t * t * t * (t * (t * 6 - 15) + 10);
        private static double Cubic(double p0, double p1, double p2, double p3, double t)
        {
            double u = 1 - t;
            return u * u * u * p0 + 3 * u * u * t * p1 + 3 * u * t * t * p2 + t * t * t * p3;
        }
        private static double Lerp(double robotic, double human, double intensity) =>
            robotic + (human - robotic) * Math.Clamp(intensity, 0, 1);
        private static double Sq(double v) => v * v;
        private static int Clamp(int v, int size) => Math.Clamp(v, 0, Math.Max(0, size - 1));

        /// <summary>Standard-normal sample (Box–Muller).</summary>
        private static double Gaussian(Random rng)
        {
            double u1 = 1.0 - rng.NextDouble();
            double u2 = 1.0 - rng.NextDouble();
            return Math.Sqrt(-2.0 * Math.Log(u1)) * Math.Cos(2.0 * Math.PI * u2);
        }

        /// <summary>Positive, right-skewed sample around <paramref name="mean"/> — matches how human
        /// inter-event gaps cluster low with an occasional long tail.</summary>
        private static double LogNormal(Random rng, double mean, double sigma)
        {
            double mu = Math.Log(Math.Max(1, mean)) - sigma * sigma / 2;
            return Math.Exp(mu + sigma * Gaussian(rng));
        }
    }
}
