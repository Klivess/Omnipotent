using Omnipotent.Services.Projects.Containers;

namespace Omnipotent.Tests.Projects
{
    /// <summary>
    /// Verifies the humanisation planners produce human-shaped, deterministic event sequences —
    /// curved non-teleport paths, non-uniform timing, self-correcting typos — without an RFB socket.
    /// These are the properties that remove the mechanical tells; if a change flattens them back to
    /// the robotic baseline, these tests fail.
    /// </summary>
    public class HumanInputPlannerTests
    {
        private static HumanInputProfile Profile(double intensity = 1.0) =>
            HumanInputProfile.Test(seed: 4242, intensity: intensity);

        [Fact]
        public void PlanMove_EndsAtOrNearTarget_AndIsNotATeleport()
        {
            var steps = HumanInputPlanner.PlanMove(0, 0, 800, 400, 1920, 1080, Profile(), new Random(1));

            Assert.True(steps.Count >= 4, "a real move is many samples, not one jump");
            var last = steps[^1];
            Assert.InRange(last.X, 795, 805);   // lands on the target (landing jitter is a few px)
            Assert.InRange(last.Y, 395, 405);
        }

        [Fact]
        public void PlanMove_PathIsCurved_NotAStraightLine()
        {
            var steps = HumanInputPlanner.PlanMove(0, 0, 1000, 0, 1920, 1080, Profile(), new Random(7));

            // A straight horizontal move would keep y==0 throughout. A human arc deviates off-axis.
            int maxOffAxis = steps.Max(s => Math.Abs(s.Y));
            Assert.True(maxOffAxis > 3, $"expected a bowed path, max |y| deviation was {maxOffAxis}");
        }

        [Fact]
        public void PlanMove_TimingIsNonUniform()
        {
            var steps = HumanInputPlanner.PlanMove(0, 0, 600, 600, 1920, 1080, Profile(), new Random(3));
            var delays = steps.Select(s => s.DelayMs).Where(d => d > 0).ToList();

            Assert.True(delays.Distinct().Count() > 1, "human step timing must not be a fixed interval");
        }

        [Fact]
        public void PlanMove_IsDeterministicForASeed()
        {
            var a = HumanInputPlanner.PlanMove(10, 10, 500, 300, 1920, 1080, Profile(), new Random(99));
            var b = HumanInputPlanner.PlanMove(10, 10, 500, 300, 1920, 1080, Profile(), new Random(99));
            Assert.Equal(a, b);
        }

        [Fact]
        public void PlanMove_StaysInsideBounds()
        {
            var steps = HumanInputPlanner.PlanMove(1910, 1070, 5, 5, 1920, 1080, Profile(), new Random(5));
            Assert.All(steps, s =>
            {
                Assert.InRange(s.X, 0, 1919);
                Assert.InRange(s.Y, 0, 1079);
            });
        }

        [Fact]
        public void PlanClick_HoldAndSettleAreWithinHumanBounds()
        {
            for (int i = 0; i < 50; i++)
            {
                var plan = HumanInputPlanner.PlanClick(Profile(), new Random(i));
                Assert.InRange(plan.DownHoldMs, 30, 220);
                Assert.InRange(plan.SettleBeforeMs, 0, 400);
                Assert.InRange(plan.MicroDx, -2, 2);
            }
        }

        [Fact]
        public void HumanizeClickPoint_StaysInsideBounds_AndIsRarelyDeadCentre()
        {
            int offCentre = 0;
            for (int i = 0; i < 100; i++)
            {
                var (x, y) = HumanInputPlanner.HumanizeClickPoint(100, 100, 120, 40, Profile(), new Random(i));
                Assert.InRange(x, 100 - 60 + 2, 100 + 60 - 2);
                Assert.InRange(y, 100 - 20 + 2, 100 + 20 - 2);
                if (x != 100 || y != 100) offCentre++;
            }
            Assert.True(offCentre > 80, "clicks should scatter within the control, not hit dead centre");
        }

        [Fact]
        public void HumanizeClickPoint_TinyControl_UsesCentre()
        {
            var (x, y) = HumanInputPlanner.HumanizeClickPoint(50, 50, 3, 3, Profile(), new Random(1));
            Assert.Equal((50, 50), (x, y));
        }

        [Fact]
        public void PlanTyping_ReproducesTheExactText_EvenWithTypos()
        {
            // A high typo rate must still yield the correct net string: every wrong key is followed by
            // a single backspace-correction, then the intended character.
            var noisy = HumanInputProfile.Test(seed: 1, intensity: 1.0);
            const string text = "Hello, human world! 12345";
            var steps = HumanInputPlanner.PlanTyping(text, noisy, new Random(11));

            var sb = new System.Text.StringBuilder();
            foreach (var step in steps)
            {
                if (step.ThenBackspace) continue;      // typo char is erased before the real one
                sb.Append(step.Char);
            }
            Assert.Equal(text, sb.ToString());
        }

        [Fact]
        public void PlanTyping_TimingVaries_AndFirstKeyIsSlower()
        {
            var steps = HumanInputPlanner.PlanTyping("the quick brown fox", Profile(), new Random(2));
            var pre = steps.Where(s => !s.ThenBackspace).Select(s => s.PreDelayMs).ToList();

            Assert.True(pre.Distinct().Count() > 3, "inter-key timing must be non-uniform");
            Assert.True(pre[0] >= pre.Skip(1).Take(5).Average() * 0.9,
                "the first keystroke should carry an orient/reach delay");
        }

        [Fact]
        public void PlanScroll_HasMomentumTiming_NotAFixedGap()
        {
            var steps = HumanInputPlanner.PlanScroll(12, Profile(), new Random(4));
            var gaps = steps.Where(s => !s.Reverse).Select(s => s.DelayMs).ToList();
            Assert.True(gaps.Distinct().Count() > 1, "scroll notches must not share one fixed delay");
            Assert.True(steps.Count >= 12);
        }

        [Fact]
        public void DisabledIntensity_CollapsesTimingTowardRoboticBaseline()
        {
            var human = HumanInputPlanner.PlanTyping("abcdefgh", Profile(intensity: 1.0), new Random(8));
            var robotic = HumanInputPlanner.PlanTyping("abcdefgh", Profile(intensity: 0.0), new Random(8));
            double humanAvg = human.Where(s => !s.ThenBackspace).Average(s => s.PreDelayMs);
            double roboticAvg = robotic.Where(s => !s.ThenBackspace).Average(s => s.PreDelayMs);
            Assert.True(roboticAvg < humanAvg, "zero intensity should tighten timing toward the baseline");
        }
    }

    /// <summary>Persona determinism and the global kill switch / intensity knob.</summary>
    public class HumanInputProfileTests
    {
        [Fact]
        public void ForSeed_IsStableAcrossCalls()
        {
            var a = HumanInputProfile.ForSeed("container-abc");
            var b = HumanInputProfile.ForSeed("container-abc");
            Assert.Equal(a.TypingWpm, b.TypingWpm, 6);
            Assert.Equal(a.MouseSteadiness, b.MouseSteadiness, 6);
            Assert.Equal(a.LiveSeed, b.LiveSeed);
            Assert.Equal(a.Scroll, b.Scroll);
        }

        [Fact]
        public void DifferentSeeds_ProduceDifferentPersonas()
        {
            var a = HumanInputProfile.ForSeed("container-abc");
            var b = HumanInputProfile.ForSeed("container-xyz");
            // At least one salient trait should differ so agents don't cluster on one fingerprint.
            Assert.True(Math.Abs(a.TypingWpm - b.TypingWpm) > 0.001
                        || Math.Abs(a.MouseSteadiness - b.MouseSteadiness) > 0.001
                        || a.LiveSeed != b.LiveSeed);
        }

        [Fact]
        public void ParametersStayWithinHumanRanges()
        {
            for (int i = 0; i < 200; i++)
            {
                var p = HumanInputProfile.ForSeed($"seed-{i}", intensityOverride: 1.0);
                Assert.InRange(p.TypingWpm, 42, 88);
                Assert.InRange(p.TypoRate, 0.006, 0.026);
                Assert.InRange(p.MouseSteadiness, 0.35, 0.85);
            }
        }

        [Fact]
        public void ZeroIntensity_DisablesHumanisation()
        {
            var p = HumanInputProfile.ForSeed("container-abc", intensityOverride: 0.0);
            Assert.False(p.Enabled);
        }
    }
}
