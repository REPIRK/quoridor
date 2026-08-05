namespace Quoridor.Ui.Game;

/// <summary>
/// What the shell around these components can actually do, said once by whoever builds
/// it. The board and the panels are the same code in a browser tab and in a native app;
/// the difference between the two is not a difference in the game, it is a difference in
/// how many threads there are to run the search on — so it is the host that answers for
/// it, and nothing in the UI has to know which host it is running in.
///
/// One question is behind all of it. Under WebAssembly there is a single thread and the
/// search is on it, so the engine's budget is also how long the page stops answering: a
/// three-second search is a three-second freeze, with no repaint, no scrolling and no way
/// to press anything. Behind a native shell the search gets a real thread of its own and
/// costs the screen nothing at all.
/// </summary>
public sealed record HostProfile(int[] MoveTimes, int DefaultMoveTime, bool MayPonder, string LimitNote)
{
    /// <summary>
    /// A single-threaded host: a browser tab. 0.6 s is the longest search that still reads
    /// as the opponent answering rather than as the page having died, and it is what this
    /// build has run at since it existed. The default is the same number for the same
    /// reason — here it is a ceiling and not a preference.
    /// </summary>
    public static readonly HostProfile SingleThreaded = new(
        MoveTimes: new[] { 400, 600 },
        DefaultMoveTime: 600,
        MayPonder: false,
        LimitNote: "In a browser the search runs on the only thread there is, so a longer budget " +
            "is also how long the page stops answering. The desktop and phone builds offer more.");

    /// <summary>
    /// A host with a thread pool behind the view: the phone.
    ///
    /// One second, which is neither of the numbers this game already had. The browser's 0.6 s
    /// is a ceiling forced by having one thread and was never a judgement about how long an
    /// opponent should think; the desktop's 1.2 s was chosen for a machine that is plugged
    /// in, sitting still, and not in anyone's hand. A phone is none of those, so it gets its
    /// own number, and here is what it is made of.
    ///
    /// What a longer budget buys was measured rather than assumed — the full search over 22
    /// positions drawn from six games, standard and rolled, at each budget on the ladder:
    ///
    ///     0.4 s → depth  9.14      0.9 s → depth 10.00      1.8 s → depth 10.86
    ///     0.6 s → depth  9.77      1.2 s → depth 10.59      3.0 s → depth 11.32
    ///
    /// which is about nine tenths of a ply per doubling of the budget, all the way up. There
    /// is no cliff to stay above and no plateau to stop at; every extra ply simply costs
    /// twice what the last one did. So the number is not decided by the search at all — it
    /// is decided by what the doubling costs on this machine, and on a phone a doubling
    /// costs three things the desktop does not pay:
    ///
    /// The wait is felt. This is the only build where the player is holding the thing and
    /// has nothing else on the screen to look at. A second is about where a reply stops
    /// reading as an answer and starts reading as a wait, and the phone should sit under it.
    ///
    /// The core is one core, at full tilt, in a hand. Forty plies of a game at one second is
    /// something like twenty seconds of a phone's fastest core; at three it is a minute, and
    /// on a phone that is heat and battery rather than a fan nobody notices.
    ///
    /// And the budget is not what it costs. Measured against the wall, the search returns in
    /// roughly three quarters of its budget — it will not begin an iteration it does not
    /// think it can finish — so a second of budget is about seven tenths of a second of
    /// waiting on the machine these numbers came off.
    ///
    /// The ladder still reaches 3 s, which is the desktop's own longest: a phone on a charger
    /// with a player who wants the harder opponent can have it. This is the number the app
    /// opens on, not the most it will do.
    ///
    /// What none of the above is: a measurement on a phone. The depths are this desktop's,
    /// and an arm64 core will reach fewer of them per second. The shape of the curve is the
    /// search's own and travels; where a particular phone sits on it does not.
    /// </summary>
    public static readonly HostProfile Native = new(
        MoveTimes: new[] { 600, 1000, 2000, 3000 },
        DefaultMoveTime: 1000,
        MayPonder: true,
        LimitNote: "");

    /// <summary>
    /// Which host these components are running in. Defaults to the cautious answer: a host
    /// that has not said is treated as one that cannot spare a thread, so forgetting to
    /// declare it costs some depth rather than freezing the screen.
    /// </summary>
    public static HostProfile Current { get; set; } = SingleThreaded;

    /// <summary>
    /// Where an invite handed out by this host should send the other player. Empty means
    /// the page's own address, which is what a browser tab wants: the address it is being
    /// read at is by construction an address that works.
    ///
    /// An app has no such address. Under a native shell the page is served from inside the
    /// process, so the link the game would otherwise build points at a host that exists
    /// only on that one device — the invite would look ordinary and be unopenable. A shell
    /// in that position says here where the game can be played instead, and the code
    /// beside the link keeps working either way.
    ///
    /// Not part of the record's own state above, because unlike the other three it is not
    /// a consequence of how many threads the host has.
    /// </summary>
    public string InviteBase { get; init; } = string.Empty;

    /// <summary>
    /// Whether the list stops short of what another build would offer, which is the only
    /// case worth explaining: a full list needs no note under it. Asked of the note rather
    /// than of the numbers, because a note exists exactly when there is a limit to explain
    /// and only the host that has one knows what to say about it.
    /// </summary>
    public bool IsLimited => LimitNote.Length > 0;

    /// <summary>
    /// A remembered budget brought inside what this host allows. A phone preference read
    /// back in a browser — the settings travel by link, and one build's number can reach
    /// the other — must not be honoured there, and a value no button carries would leave
    /// the row with nothing lit.
    /// </summary>
    public int Allowed(int milliseconds)
    {
        int[] offered = MoveTimes;
        int best = offered[0];

        foreach (int option in offered)
        {
            if (option <= milliseconds && option > best) best = option;
        }

        return best;
    }

    /// <summary>A budget as the picker writes it: seconds, and no more digits than it needs.</summary>
    public static string Label(int milliseconds) =>
        milliseconds % 1000 == 0 ? $"{milliseconds / 1000} s" : $"{milliseconds / 1000.0:0.0} s";
}
