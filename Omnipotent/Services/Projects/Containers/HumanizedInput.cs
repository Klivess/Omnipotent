namespace Omnipotent.Services.Projects.Containers
{
    /// <summary>
    /// Turns each high-level desktop action into the human-shaped sequence of RFB events planned by
    /// <see cref="HumanInputPlanner"/> and replays it on the <see cref="VncTransport"/>. One instance
    /// lives per desktop (keyed by its pooled transport) so its pseudo-random stream advances
    /// continuously across every action — the same "person" moving and typing all session, never the
    /// same curve or gap twice. When the persona is disabled (kill switch / zero intensity) every
    /// method falls straight through to the raw transport, so behaviour is always inspectable.
    ///
    /// Planning is done under a lock because <see cref="Random"/> is not thread-safe; the actual
    /// awaited I/O happens outside the lock. In practice desktop actions are already serialised by
    /// the adapter's action gate, so the lock is only defensive.
    /// </summary>
    public sealed class HumanizedInput
    {
        private readonly VncTransport transport;
        private readonly HumanInputProfile profile;
        private readonly Random rng;
        private readonly object rngLock = new();

        public HumanizedInput(VncTransport transport, HumanInputProfile profile)
        {
            this.transport = transport;
            this.profile = profile;
            rng = new Random(profile.LiveSeed);
        }

        public HumanInputProfile Profile => profile;
        public bool Enabled => profile.Enabled;

        private T Plan<T>(Func<Random, T> plan) { lock (rngLock) return plan(rng); }

        /// <summary>Nudges a click point off dead-centre within a control's bounds using this
        /// persona's continuous stream. Returns the centre unchanged when humanisation is disabled.</summary>
        public (int X, int Y) HumanizeClickPoint(int centreX, int centreY, int boundsWidth, int boundsHeight) =>
            Plan(r => HumanInputPlanner.HumanizeClickPoint(centreX, centreY, boundsWidth, boundsHeight, profile, r));

        // ── movement ─────────────────────────────────────────────────────────────────────────────

        public async Task MoveAsync(int x, int y, CancellationToken ct)
        {
            if (!Enabled) { await transport.MoveMouseAsync(x, y, ct); return; }
            var path = Plan(r => HumanInputPlanner.PlanMove(transport.PointerX, transport.PointerY,
                x, y, transport.Width, transport.Height, profile, r));
            await ReplayPathAsync(path, ct);
        }

        public async Task MoveRelativeAsync(int dx, int dy, CancellationToken ct)
        {
            int toX = Math.Clamp(transport.PointerX + dx, 0, Math.Max(0, transport.Width - 1));
            int toY = Math.Clamp(transport.PointerY + dy, 0, Math.Max(0, transport.Height - 1));
            await MoveAsync(toX, toY, ct);
        }

        private async Task ReplayPathAsync(IReadOnlyList<MotionStep> path, CancellationToken ct)
        {
            foreach (var step in path)
            {
                await transport.MoveMouseAsync(step.X, step.Y, ct);
                if (step.DelayMs > 0) await Task.Delay(step.DelayMs, ct);
            }
        }

        // ── clicks ───────────────────────────────────────────────────────────────────────────────

        public async Task ClickAsync(int x, int y, int button, int clicks, CancellationToken ct)
        {
            if (!Enabled) { await transport.ClickAsync(x, y, button, clicks, ct); return; }
            clicks = Math.Clamp(clicks, 1, 3);
            await MoveAsync(x, y, ct);
            for (int i = 0; i < clicks; i++)
            {
                var plan = Plan(r => HumanInputPlanner.PlanClick(profile, r));
                if (plan.SettleBeforeMs > 0 && i == 0) await Task.Delay(plan.SettleBeforeMs, ct);

                await transport.MouseDownAsync(x, y, button, ct);
                if (plan.MicroDx != 0 || plan.MicroDy != 0)
                {
                    int mx = Math.Clamp(x + plan.MicroDx, 0, Math.Max(0, transport.Width - 1));
                    int my = Math.Clamp(y + plan.MicroDy, 0, Math.Max(0, transport.Height - 1));
                    await transport.MoveMouseAsync(mx, my, ct); // 1–2px shift while pressed, as a hand does
                    if (plan.DownHoldMs > 0) await Task.Delay(plan.DownHoldMs, ct);
                    await transport.MouseUpAsync(mx, my, button, ct);
                }
                else
                {
                    if (plan.DownHoldMs > 0) await Task.Delay(plan.DownHoldMs, ct);
                    await transport.MouseUpAsync(x, y, button, ct);
                }
                if (i + 1 < clicks) await Task.Delay(plan.GapAfterMs, ct);
            }
        }

        // ── drag ─────────────────────────────────────────────────────────────────────────────────

        public async Task DragAsync(int fromX, int fromY, int toX, int toY, int button, CancellationToken ct)
        {
            if (!Enabled) { await transport.DragAsync(fromX, fromY, toX, toY, button, ct); return; }
            await MoveAsync(fromX, fromY, ct);
            await Task.Delay(Plan(r => HumanInputPlanner.PlanClick(profile, r).SettleBeforeMs), ct); // grab dwell
            await transport.MouseDownAsync(fromX, fromY, button, ct);
            var path = Plan(r => HumanInputPlanner.PlanMove(fromX, fromY, toX, toY,
                transport.Width, transport.Height, profile, r));
            await ReplayPathAsync(path, ct);         // curved, variable-velocity drag (button held)
            await Task.Delay(40 + Plan(r => r.Next(0, 140)), ct); // settle before drop
            await transport.MouseUpAsync(toX, toY, button, ct);
        }

        // ── scroll ───────────────────────────────────────────────────────────────────────────────

        /// <summary>Momentum scroll. <paramref name="dy"/>/<paramref name="dx"/> follow the transport
        /// convention: dy&gt;0 up, dy&lt;0 down, dx&lt;0 left, dx&gt;0 right.</summary>
        public async Task ScrollAsync(int x, int y, int dy, int dx, CancellationToken ct)
        {
            if (!Enabled) { await transport.ScrollAsync(x, y, dy, dx, ct); return; }
            if (dy != 0) await ReplayScrollAsync(x, y, Math.Abs(dy), vertical: true, up: dy > 0, ct);
            if (dx != 0) await ReplayScrollAsync(x, y, Math.Abs(dx), vertical: false, up: dx > 0, ct);
        }

        private async Task ReplayScrollAsync(int x, int y, int notches, bool vertical, bool up, CancellationToken ct)
        {
            var plan = Plan(r => HumanInputPlanner.PlanScroll(notches, profile, r));
            foreach (var step in plan)
            {
                bool goUp = step.Reverse ? !up : up;
                int v = vertical ? (goUp ? 1 : -1) : 0;
                int h = vertical ? 0 : (goUp ? 1 : -1);
                await transport.ScrollAsync(x, y, v, h, ct);
                if (step.DelayMs > 0) await Task.Delay(step.DelayMs, ct);
            }
        }

        // ── typing ───────────────────────────────────────────────────────────────────────────────

        public async Task TypeTextAsync(string text, CancellationToken ct)
        {
            if (string.IsNullOrEmpty(text)) return;
            if (!Enabled) { await transport.TypeTextAsync(text, 18, ct); return; }
            var plan = Plan(r => HumanInputPlanner.PlanTyping(text, profile, r));
            foreach (var step in plan)
            {
                if (step.PreDelayMs > 0) await Task.Delay(step.PreDelayMs, ct);
                await transport.TypeCharAsync(step.Char, step.HoldMs, ct);
                if (step.ThenBackspace)
                {
                    await Task.Delay(70 + Plan(r => r.Next(0, 160)), ct); // notice the mistake
                    await transport.KeyChordAsync("backspace", ct: ct);
                }
            }
        }
    }
}
