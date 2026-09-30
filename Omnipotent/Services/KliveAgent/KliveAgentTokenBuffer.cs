using System.Diagnostics;
using System.Text;

namespace Omnipotent.Services.KliveAgent;

/// <summary>Emits the first token immediately, then coalesces updates. The inference reader must
/// not rebuild and copy a complete run snapshot for every tiny provider delta.</summary>
internal sealed class KliveAgentTokenBuffer
{
    private readonly StringBuilder _text = new();
    private readonly Action<string> _emit;
    private readonly Func<long> _nowMs;
    private readonly int _intervalMs;
    private long _lastEmitMs;
    private int _emittedLength;

    internal KliveAgentTokenBuffer(Action<string> emit, int intervalMs = 50, Func<long>? nowMs = null)
    {
        _emit = emit;
        _intervalMs = intervalMs;
        var clock = Stopwatch.StartNew();
        _nowMs = nowMs ?? (() => clock.ElapsedMilliseconds);
    }

    internal void Append(string token)
    {
        if (string.IsNullOrEmpty(token)) return;
        _text.Append(token);
        if (_emittedLength == 0 || _nowMs() - _lastEmitMs >= _intervalMs) Flush();
    }

    internal void Flush()
    {
        if (_emittedLength == _text.Length) return;
        _lastEmitMs = _nowMs();
        _emittedLength = _text.Length;
        _emit(_text.ToString());
    }
}
