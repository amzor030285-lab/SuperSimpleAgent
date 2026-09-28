using System;
using System.Text;

/// <summary>
/// Detects degenerate repetition in a streamed model response: the model emitting
/// the same short block over and over until the answer is worthless. A stall
/// watchdog cannot catch this, because the stream stays busy, and no output cap is
/// sent to the server, so nothing else bounds the response.
///
/// The check runs on a fixed-size window of the tail of the response, so the whole
/// response is never buffered and the added work stays small next to the SSE/JSON
/// parsing and rendering already done per streamed token.
/// </summary>
public sealed class RepetitionGuard
{
    /// <summary>
    /// Tail size kept for comparison. Must stay well above MinRepeatedRunChars,
    /// because a run can only be confirmed over the part of the window it covers.
    /// </summary>
    private const int WindowChars = 16384;

    /// <summary>Shortest repeat length considered; below this it is not a block.</summary>
    private const int MinPeriodChars = 4;

    /// <summary>Longest repeat length considered.</summary>
    private const int MaxPeriodChars = 1536;

    /// <summary>
    /// Length of an unbroken repeat required before the response is aborted.
    ///
    /// This has to sit above the largest repeat a legitimate answer plausibly
    /// contains, which is the awkward part: a generated table of 100 identical rows
    /// is 1600 characters of unbroken repeat, and so is a degenerate loop. No fixed
    /// character count separates those two, so the threshold is set where a real
    /// runaway clears it in well under a second (at 350 chars/s that is ~3 s) while
    /// a table someone actually asked for does not reach it. Set repetitionguard=0
    /// to turn the check off entirely if a response is ever cut short by mistake.
    /// </summary>
    public const int MinRepeatedRunChars = 4000;

    /// <summary>How often the window is tested, in appended characters.</summary>
    private const int CheckIntervalChars = 128;

    /// <summary>Cap on full verifications per check, bounding worst-case work.</summary>
    private const int MaxPeriodsVerifiedPerCheck = 16;

    private readonly StringBuilder _window = new StringBuilder(WindowChars * 2);
    private int _sinceCheck;

    /// <summary>True once a degenerate repeat has been seen; the response should be aborted.</summary>
    public bool Tripped { get; private set; }

    /// <summary>Human-readable reason, for the message shown when the run is aborted.</summary>
    public string Reason { get; private set; }

    public RepetitionGuard()
    {
        Reason = "";
    }

    /// <summary>
    /// Feeds streamed content to the guard. Only the tail is kept, and the window is
    /// inspected once per <see cref="CheckIntervalChars"/> appended characters.
    /// </summary>
    public void Append(string text)
    {
        if (Tripped || string.IsNullOrEmpty(text))
            return;

        _window.Append(text);

        if (_window.Length > WindowChars * 2)
            _window.Remove(0, _window.Length - WindowChars);

        _sinceCheck += text.Length;
        if (_sinceCheck < CheckIntervalChars)
            return;

        _sinceCheck = 0;
        Inspect();
    }

    /// <summary>
    /// Runs the check immediately instead of waiting for the next chunk boundary.
    /// Returns whether the guard is tripped.
    /// </summary>
    public bool CheckNow()
    {
        if (!Tripped)
            Inspect();
        return Tripped;
    }

    private void Inspect()
    {
        int end = _window.Length;
        int start = end > WindowChars ? end - WindowChars : 0;

        // A period longer than the window could not be confirmed anyway, and a
        // shorter one cannot cover MinRepeatedRunChars inside it.
        int maxPeriod = end - start - MinRepeatedRunChars;
        if (maxPeriod < MinPeriodChars)
            return;

        if (maxPeriod > MaxPeriodChars)
            maxPeriod = MaxPeriodChars;

        int verified = 0;

        for (int period = MinPeriodChars; period <= maxPeriod; period++)
        {
            // Prefilter on the last few characters. For a periodic tail every
            // multiple of the period passes, and for text that does not repeat
            // almost nothing does, so the full scan below runs only a handful of
            // times. A wrong period also fails on its first compared character.
            if (!AnchorMatches(_window, end, period))
                continue;

            // Only the smallest true period is ever needed, and candidates are
            // tried smallest first, so this cap cannot hide a loop. It bounds the
            // one case that is not self-limiting: a long answer that repeats a
            // large block several times, where many multiples are all genuine.
            if (verified++ >= MaxPeriodsVerifiedPerCheck)
                break;

            if (RepeatsFrom(_window, start, end, period, MinRepeatedRunChars))
            {
                Tripped = true;
                Reason = "the model repeated the same " + period + "-character block for over "
                    + MinRepeatedRunChars + " characters (degenerate loop)";
                return;
            }
        }
    }

    /// <summary>Checks three characters at the end against the same period before it.</summary>
    private static bool AnchorMatches(StringBuilder window, int end, int period)
    {
        for (int i = 1; i <= 3; i++)
        {
            if (window[end - i] != window[end - i - period])
                return false;
        }

        return true;
    }

    /// <summary>
    /// True when the tail ending at <paramref name="end"/> is one block of
    /// <paramref name="period"/> characters repeated back to back for at least
    /// <paramref name="minRun"/> characters. Walks backwards from the end and stops
    /// at the first mismatch, so one non-repeating character ends the run.
    /// </summary>
    private static bool RepeatsFrom(StringBuilder window, int start, int end, int period, int minRun)
    {
        int matched = 0;

        for (int k = end - 1; k >= start + period; k--)
        {
            if (window[k] != window[k - period])
                break;
            matched++;
        }

        return matched >= minRun;
    }
}
