namespace Omnipotent.Services.Projects.Containers
{
    /// <summary>
    /// The pointer/keyboard primitives a desktop action is made of. Two implementations reach the same
    /// X display by different routes: <see cref="VncTransport"/> (RFB events from the host) and
    /// <see cref="DaemonDesktopInput"/> (XTEST inside the container, through klive-computerd). Either can
    /// serve an action when the other is down, so a wedged x11vnc no longer means a blind, handless agent.
    /// </summary>
    public interface IDesktopInput
    {
        int Width { get; }
        int Height { get; }
        int PointerX { get; }
        int PointerY { get; }
        /// <summary>Short name for results and logs ("vnc", "computerd").</summary>
        string Route { get; }

        Task MoveMouseAsync(int x, int y, CancellationToken ct = default);
        /// <summary>Plays a planned pointer path, holding each sample for its delay.</summary>
        Task MovePathAsync(IReadOnlyList<MotionStep> path, CancellationToken ct = default);
        Task MoveMouseRelativeAsync(int dx, int dy, int steps = 1, CancellationToken ct = default);
        Task MouseDownAsync(int x, int y, int button = 1, CancellationToken ct = default);
        Task MouseUpAsync(int x, int y, int button = 1, CancellationToken ct = default);
        Task ClickAsync(int x, int y, int button = 1, int clicks = 1, CancellationToken ct = default);
        Task DragAsync(int fromX, int fromY, int toX, int toY, int button = 1, CancellationToken ct = default);
        Task ScrollAsync(int x, int y, int dy, int dx, CancellationToken ct = default);
        Task TypeTextAsync(string text, int charDelayMs = 18, CancellationToken ct = default);
        Task TypeCharAsync(char c, int holdMs = 45, CancellationToken ct = default);
        Task KeyChordAsync(string chord, int holdMs = 55, int repeats = 1, CancellationToken ct = default);
        Task KeyDownAsync(string key, CancellationToken ct = default);
        Task KeyUpAsync(string key, CancellationToken ct = default);
        Task ReleaseAllAsync(CancellationToken ct = default);
    }

    /// <summary>
    /// Input through klive-computerd's /input route: the events run as XTEST calls inside the container,
    /// so a planned path keeps its timing exactly instead of being re-timed by the host's scheduler and
    /// the Docker port proxy. Used whenever the VNC route is unavailable.
    /// </summary>
    public sealed class DaemonDesktopInput : IDesktopInput
    {
        private readonly ComputerDaemonClient daemon;
        private int pointerX;
        private int pointerY;
        private readonly HashSet<string> heldKeys = new(StringComparer.OrdinalIgnoreCase);
        private readonly HashSet<int> heldButtons = new();

        public DaemonDesktopInput(ComputerDaemonClient daemon, int width, int height, int pointerX = 0, int pointerY = 0)
        {
            this.daemon = daemon;
            Width = Math.Max(1, width);
            Height = Math.Max(1, height);
            this.pointerX = pointerX;
            this.pointerY = pointerY;
        }

        public int Width { get; }
        public int Height { get; }
        public int PointerX => pointerX;
        public int PointerY => pointerY;
        public string Route => "computerd";

        private int ClampX(int x) => Math.Clamp(x, 0, Width - 1);
        private int ClampY(int y) => Math.Clamp(y, 0, Height - 1);

        private async Task PlayAsync(List<DesktopInputEvent> events, CancellationToken ct)
        {
            var outcome = await daemon.InputAsync(events, ct)
                ?? throw new InvalidOperationException("The desktop's control daemon could not take input (unreachable, outdated or not yet authorised).");
            if (!outcome.Ok)
                throw new InvalidOperationException("Desktop input failed inside the container: " + (outcome.Error ?? "unknown error"));
            if (outcome.Pointer is { } pointer)
            {
                pointerX = pointer.X;
                pointerY = pointer.Y;
            }
        }

        public Task MoveMouseAsync(int x, int y, CancellationToken ct = default)
        {
            pointerX = ClampX(x);
            pointerY = ClampY(y);
            return PlayAsync(new() { DesktopInputEvent.Move(pointerX, pointerY) }, ct);
        }

        public Task MovePathAsync(IReadOnlyList<MotionStep> path, CancellationToken ct = default)
        {
            var events = new List<DesktopInputEvent>(path.Count * 2);
            foreach (var step in path)
            {
                events.Add(DesktopInputEvent.Move(ClampX(step.X), ClampY(step.Y)));
                if (step.DelayMs > 0) events.Add(DesktopInputEvent.Sleep(step.DelayMs));
            }
            if (path.Count > 0)
            {
                pointerX = ClampX(path[^1].X);
                pointerY = ClampY(path[^1].Y);
            }
            return PlayAsync(events, ct);
        }

        public Task MoveMouseRelativeAsync(int dx, int dy, int steps = 1, CancellationToken ct = default)
        {
            int fromX = pointerX, fromY = pointerY;
            int toX = ClampX(fromX + dx), toY = ClampY(fromY + dy);
            int count = Math.Clamp(steps, 1, 120);
            var path = new List<MotionStep>(count);
            for (int i = 1; i <= count; i++)
                path.Add(new MotionStep(fromX + (toX - fromX) * i / count, fromY + (toY - fromY) * i / count, i < count ? 8 : 0));
            return MovePathAsync(path, ct);
        }

        public async Task MouseDownAsync(int x, int y, int button = 1, CancellationToken ct = default)
        {
            pointerX = ClampX(x);
            pointerY = ClampY(y);
            await PlayAsync(new() { DesktopInputEvent.Move(pointerX, pointerY), DesktopInputEvent.Down(button) }, ct);
            heldButtons.Add(button);
        }

        public async Task MouseUpAsync(int x, int y, int button = 1, CancellationToken ct = default)
        {
            pointerX = ClampX(x);
            pointerY = ClampY(y);
            await PlayAsync(new() { DesktopInputEvent.Move(pointerX, pointerY), DesktopInputEvent.Up(button) }, ct);
            heldButtons.Remove(button);
        }

        public Task ClickAsync(int x, int y, int button = 1, int clicks = 1, CancellationToken ct = default)
        {
            pointerX = ClampX(x);
            pointerY = ClampY(y);
            var events = new List<DesktopInputEvent> { DesktopInputEvent.Move(pointerX, pointerY) };
            for (int i = 0; i < Math.Clamp(clicks, 1, 3); i++)
            {
                if (i > 0) events.Add(DesktopInputEvent.Sleep(90));
                events.Add(DesktopInputEvent.Down(button));
                events.Add(DesktopInputEvent.Sleep(40));
                events.Add(DesktopInputEvent.Up(button));
            }
            return PlayAsync(events, ct);
        }

        public Task DragAsync(int fromX, int fromY, int toX, int toY, int button = 1, CancellationToken ct = default)
        {
            var events = new List<DesktopInputEvent>
            {
                DesktopInputEvent.Move(ClampX(fromX), ClampY(fromY)), DesktopInputEvent.Down(button),
            };
            const int steps = 12;
            for (int i = 1; i <= steps; i++)
            {
                events.Add(DesktopInputEvent.Move(ClampX(fromX + (toX - fromX) * i / steps), ClampY(fromY + (toY - fromY) * i / steps)));
                events.Add(DesktopInputEvent.Sleep(15));
            }
            events.Add(DesktopInputEvent.Up(button));
            pointerX = ClampX(toX);
            pointerY = ClampY(toY);
            return PlayAsync(events, ct);
        }

        public Task ScrollAsync(int x, int y, int dy, int dx, CancellationToken ct = default)
        {
            pointerX = ClampX(x);
            pointerY = ClampY(y);
            return PlayAsync(new() { DesktopInputEvent.Move(pointerX, pointerY), DesktopInputEvent.Scroll(dy, dx) }, ct);
        }

        public Task TypeTextAsync(string text, int charDelayMs = 18, CancellationToken ct = default) =>
            string.IsNullOrEmpty(text) ? Task.CompletedTask
                : PlayAsync(new() { DesktopInputEvent.Type(text, Math.Clamp(charDelayMs, 0, 500)) }, ct);

        public Task TypeCharAsync(char c, int holdMs = 45, CancellationToken ct = default) =>
            PlayAsync(new() { DesktopInputEvent.Type(c.ToString(), Math.Clamp(holdMs, 0, 400)) }, ct);

        public Task KeyChordAsync(string chord, int holdMs = 55, int repeats = 1, CancellationToken ct = default) =>
            PlayAsync(new() { DesktopInputEvent.Key(chord, Math.Clamp(holdMs, 1, 1000), Math.Clamp(repeats, 1, 50)) }, ct);

        public async Task KeyDownAsync(string key, CancellationToken ct = default)
        {
            await PlayAsync(new() { DesktopInputEvent.KeyDown(key) }, ct);
            heldKeys.Add(key);
        }

        public async Task KeyUpAsync(string key, CancellationToken ct = default)
        {
            await PlayAsync(new() { DesktopInputEvent.KeyUp(key) }, ct);
            heldKeys.Remove(key);
        }

        public async Task ReleaseAllAsync(CancellationToken ct = default)
        {
            var events = new List<DesktopInputEvent>();
            foreach (int button in heldButtons) events.Add(DesktopInputEvent.Up(button));
            foreach (string key in new[] { "shift", "ctrl", "alt", "super" }.Concat(heldKeys).Distinct(StringComparer.OrdinalIgnoreCase))
                events.Add(DesktopInputEvent.KeyUp(key));
            heldButtons.Clear();
            heldKeys.Clear();
            try { await PlayAsync(events, ct); } catch { /* releasing is best effort by definition */ }
        }
    }
}
