using Quoridor.Core;

namespace Quoridor.App.Views;

/// <summary>
/// The one thing the screen needs from the board in order for the game to be played from
/// the keyboard: somewhere to put the caret.
///
/// Everything else about keyboard play — which key means what, where the caret may stand,
/// whether what it stands on is legal, and what a wall there would cost — is a question
/// about the position rather than about the drawing, and is answered in
/// <see cref="GameView"/> from the same public rules the board itself reads. What is left
/// over is the one thing only the board can answer, because only the board knows where a
/// square is: draw the caret.
///
/// It is stated as an interface, and as a small one, because the control that draws the
/// board is maintained apart from the screens around it. This is the whole of the contract
/// between them.
/// </summary>
public interface IBoardAim
{
    /// <summary>
    /// Puts the caret on <paramref name="at"/>, or takes it off the board when that is
    /// null. The move is in rules coordinates, the same ones every other call into the
    /// board uses, so a turned board stays the board's own business.
    ///
    /// <paramref name="playable"/> is whether pressing now would be accepted. A caret
    /// resting somewhere it cannot go should say so before the key is pressed, rather
    /// than by the press doing nothing.
    /// </summary>
    void Aim(Move? at, bool playable);
}
