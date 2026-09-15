using System.Diagnostics;
using System.Globalization;
using Iris.Agent;
using Iris.Ai;

namespace Iris.CodingAgent.Modes.Interactive.Components;

/// <summary>
/// Iris: output token rate of the latest assistant response, shown at the start of the footer stats.
/// Measured from the first streamed delta, so prompt processing and model loading are excluded. While streaming,
/// each text/thinking/tool-call delta counts as one token (exact for llama.cpp); once the response ends, the
/// provider-reported output token count replaces the estimate.
/// </summary>
public sealed class TokenRateMeter
{
    private long _firstDeltaAt;
    private int _deltaCount;
    private double? _rate;

    public double? Rate => _rate;

    public void Reset()
    {
        _firstDeltaAt = 0;
        _deltaCount = 0;
        _rate = null;
    }

    /// <summary>Feed agent events; returns true when the displayed rate may have changed.</summary>
    public bool Handle(AgentEvent evt)
    {
        switch (evt)
        {
            case MessageStartEvent { Message: AssistantMessage }:
                _firstDeltaAt = 0;
                _deltaCount = 0;
                return false;

            case MessageUpdateEvent { Message: AssistantMessage, AssistantMessageEvent: TextDeltaEvent or ThinkingDeltaEvent or ToolCallDeltaEvent }:
            {
                var now = Stopwatch.GetTimestamp();
                if (_deltaCount++ == 0)
                {
                    _firstDeltaAt = now;
                    return false;
                }
                var seconds = Stopwatch.GetElapsedTime(_firstDeltaAt, now).TotalSeconds;
                // Too few samples right after the first delta give wild rates.
                if (seconds < 0.25) return false;
                _rate = (_deltaCount - 1) / seconds;
                return true;
            }

            case MessageEndEvent { Message: AssistantMessage message } when _deltaCount > 0:
            {
                var seconds = Stopwatch.GetElapsedTime(_firstDeltaAt).TotalSeconds;
                var generated = message.Usage.Output > 1 ? message.Usage.Output - 1 : _deltaCount - 1;
                if (seconds > 0 && generated > 0) _rate = generated / seconds;
                _deltaCount = 0;
                return true;
            }

            default:
                return false;
        }
    }

    public static string Format(double rate) =>
        (rate >= 10 ? Math.Round(rate, MidpointRounding.AwayFromZero).ToString(CultureInfo.InvariantCulture) : rate.ToString("0.0", CultureInfo.InvariantCulture)) + "tok/s";
}
