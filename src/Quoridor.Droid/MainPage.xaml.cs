using Quoridor.Ui.Game;

namespace Quoridor.Droid;

public partial class MainPage : ContentPage
{
    public MainPage()
    {
        InitializeComponent();
    }

    /// <summary>
    /// The system's back gesture, handed to the page.
    ///
    /// It is the one control on this screen the app does not draw, and on a phone it is
    /// the control: a sweep in from the edge, taken by everybody to mean "up one level".
    /// So the page answers it, because the page is the only thing that knows what a level
    /// is here — a card over the board, a position being read back, a game in progress —
    /// and it answers whether it took the press. True means it did, and this window stays
    /// where it is. False means the page had nothing left to unwind, and back at the bottom
    /// of an app means leaving, so this returns false and lets the system do it.
    ///
    /// Which is also how a game in progress is never walked away from by accident: the page
    /// puts up its own card first and answers true, so the press that would have closed the
    /// app closes nothing and asks instead. See Play.OnBackPressed for the ladder.
    /// </summary>
    protected override bool OnBackButtonPressed() => HostShell.BackPressed?.Invoke() ?? false;
}
