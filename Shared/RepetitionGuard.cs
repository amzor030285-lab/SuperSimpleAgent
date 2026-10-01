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
///
/// Repetition alone is not evidence of a loop. A generated table repeats its row
/// shape, and valid code repeats its boilerplate, so both are held out of the
/// comparison (see <see cref="Append"/>), and a repeat has to persist across
/// several checks before the response is cut (see
/// <see cref="ConsecutiveHitsRequired"/>). Set repetitionguard=0 in the INI to
/// leave the guard off; that is the default.
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

    /// <summary>Characters compared by the anchor prefilter.</summary>
    private const int AnchorChars = 3;

    /// <summary>
    /// Number of back-to-back copies of one block required before the response is
    /// aborted. This is the threshold that decides how fast a runaway is cut: four
    /// copies of a 80-character block is roughly 320 characters, so a model that
    /// loops from its very first tokens is stopped almost at once, instead of after
    /// thousands of characters the reader has already scrolled past.
    ///
    /// Two knobs keep a low count honest. <see cref="MinRepeatedRunChars"/> demands
    /// real mass behind the count, so a four-character block repeated four times
    /// (16 characters, and it happens by accident in ordinary prose) is ignored, and
    /// <see cref="ConsecutiveHitsRequired"/> demands that the repeat keep going, so
    /// a model that quotes the same passage a bounded number of times is left alone.
    /// </summary>
    public const int MinRepeatedRepeats = 4;

    /// <summary>
    /// Minimum total length of the repeated run, in characters. Four copies of a
    /// short block are cheap to produce by accident; this floor keeps the count
    /// meaningful by requiring the run to be worth the reader's time.
    /// </summary>
    public const int MinRepeatedRunChars = 200;

    /// <summary>
    /// Checks in a row that must each find a confirmed repeat before the response
    /// is aborted. A bounded repetition earns one hit and then stops earning them;
    /// a runaway earns one every <see cref="CheckIntervalChars"/> characters
    /// forever. Gating on that difference is what lets the repeat count stay low.
    /// </summary>
    public const int ConsecutiveHitsRequired = 3;

    /// <summary>How often the window is tested, in appended characters.</summary>
    private const int CheckIntervalChars = 128;

    /// <summary>Cap on full verifications per check, bounding worst-case work.</summary>
    private const int MaxPeriodsVerifiedPerCheck = 16;

    /// <summary>Cap on how many copies one run may walk back over.</summary>
    private const int MaxRepeatedCopies = 512;

    /// <summary>
    /// The text the check actually sees: the response with line endings and
    /// horizontal whitespace collapsed, fenced code and markdown table rows removed,
    /// and backtick runs dropped. Normalizing on the way in is what makes a repeat
    /// survive a chunk boundary landing in the middle of a line, or a stream that
    /// mixes \r\n and \n. Holding code and table rows out is what keeps valid output
    /// from looking repetitive: a 30-times repeated code loop and a table of 100
    /// identical rows are both literally periodic, and neither is a runaway.
    /// </summary>
    private readonly StringBuilder _window = new StringBuilder(WindowChars * 2);

    /// <summary>Leading blanks of a line whose first meaningful character is pending.</summary>
    private readonly StringBuilder _pendingIndent = new StringBuilder(16);

    private int _sinceCheck;
    private int _hits;

    private bool _inFence;
    private bool _linePending = true;
    private bool _suppress;
    private bool _pendingCr;
    private bool _pendingSpace;
    private int _pendingTicks;

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

        for (int i = 0; i < text.Length; i++)
            Consume(text[i]);

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

    /// <summary>Classifies one incoming character and keeps it only if it counts.</summary>
    private void Consume(char c)
    {
        // A fence marker may straddle two chunks, so a run of one or two backticks is
        // held back until the character after it settles it.
        if (_pendingTicks > 0)
        {
            if (c == '`')
            {
                if (++_pendingTicks == AnchorChars)
                {
                    _inFence = !_inFence;
                    _pendingTicks = 0;
                }
                return;
            }

            _pendingTicks = 0;
        }

        if (c == '`')
        {
            _pendingTicks = 1;
            return;
        }

        if (c == '\n')
        {
            _linePending = true;
            _suppress = false;
            _pendingIndent.Length = 0;
            _pendingSpace = false;
            if (!_inFence)
                Keep('\n');
            return;
        }

        if (_inFence)
            return;

        if (_linePending)
        {
            if (c == ' ' || c == '\t')
            {
                _pendingIndent.Append(c);
                return;
            }

            _linePending = false;
            _suppress = c == '|';
            if (_suppress)
                return;

            for (int i = 0; i < _pendingIndent.Length; i++)
                Keep(_pendingIndent[i]);

            _pendingIndent.Length = 0;
        }

        if (_suppress)
            return;

        Keep(c);
    }

    /// <summary>
    /// Adds one character to the comparison window, collapsed into its canonical
    /// form. Line endings are one newline and horizontal whitespace is one space,
    /// so a repeat is recognized across a chunk boundary or a mixed-ending stream.
    /// </summary>
    private void Keep(char c)
    {
        if (c == '\r')
        {
            // A \r\n pair must not become two newlines, or every repeat count doubles.
            _pendingCr = true;
            return;
        }

        if (_pendingCr)
        {
            _pendingCr = false;
            AppendWindow('\n');
            if (c == '\n')
                return;
        }

        if (c == '\n')
        {
            AppendWindow('\n');
            return;
        }

        if (c == ' ' || c == '\t')
        {
            if (_pendingSpace)
                return;

            _pendingSpace = true;
            AppendWindow(' ');
            return;
        }

        _pendingSpace = false;
        AppendWindow(c);
    }

    /// <summary>Appends to the tail, dropping whatever falls out of the window.</summary>
    private void AppendWindow(char c)
    {
        _window.Append(c);

        if (_window.Length > WindowChars * 2)
            _window.Remove(0, _window.Length - WindowChars);
    }

    private void Inspect()
    {
        int end = _window.Length;
        int start = end > WindowChars ? end - WindowChars : 0;

        // A period longer than the window could not be confirmed anyway, and a
        // shorter one cannot hold MinRepeatedRepeats copies inside it.
        int maxPeriod = (end - start) / MinRepeatedRepeats;
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
            if (!AnchorMatches(end, period))
                continue;

            // Only the smallest true period is ever needed, and candidates are
            // tried smallest first, so this cap cannot hide a loop. It bounds the
            // one case that is not self-limiting: a long answer that repeats a
            // large block several times, where many multiples are all genuine.
            if (verified++ >= MaxPeriodsVerifiedPerCheck)
                break;

            int copies = CopiesBackwards(start, end, period);

            if (copies >= MinRepeatedRepeats && copies * period >= MinRepeatedRunChars)
            {
                if (++_hits >= ConsecutiveHitsRequired)
                {
                    Tripped = true;
                    Reason = "the model repeated the same " + period + "-character block "
                        + copies + " times in a row (degenerate loop)";
                }

                return;
            }
        }

        _hits = 0;
    }

    /// <summary>Checks three characters at the end against the same period before it.</summary>
    private bool AnchorMatches(int end, int period)
    {
        for (int i = 1; i <= AnchorChars; i++)
        {
            if (_window[end - i] != _window[end - i - period])
                return false;
        }

        return true;
    }

    /// <summary>
    /// Counts how many copies of the final <paramref name="period"/>-character block
    /// sit back to back before the end of the window, stopping at the first copy that
    /// differs. Every copy is compared against the final one rather than against its
    /// neighbour, so a single near-identical copy does not cut the count short.
    /// </summary>
    private int CopiesBackwards(int start, int end, int period)
    {
        // The final block always matches itself.
        int copies = 1;
        int wanted = MinRepeatedRunChars / period;

        if (wanted < MinRepeatedRepeats)
            wanted = MinRepeatedRepeats;

        while (copies < wanted && copies < MaxRepeatedCopies
            && end - (copies + 2) * period >= start)
        {
            if (!CopyMatches(end, copies + 1, period))
                break;

            copies++;
        }

        return copies;
    }

    /// <summary>Compares one copy of the block against the final one.</summary>
    private bool CopyMatches(int end, int copy, int period)
    {
        int last = end - period;
        int other = end - (copy + 1) * period;

        for (int i = 0; i < period; i++)
        {
            if (_window[last + i] != _window[other + i])
                return false;
        }

        return true;
    }
}