// Small things the page needs from the browser that are not worth a library.

/// Copies text to the clipboard. The modern API is a method on navigator.clipboard and
/// has to be called as one — reaching it through an interop path that loses `this`
/// throws "Illegal invocation" — and it also needs a secure context, which a plain-http
/// test server is not. The old selection trick stands behind it for both cases.
export async function copy(text) {
    if (!text) return false;

    if (navigator.clipboard && window.isSecureContext) {
        try {
            await navigator.clipboard.writeText(text);
            return true;
        } catch (error) {
            // Denied or unavailable; fall through to the older way.
        }
    }

    const field = document.createElement('textarea');

    field.value = text;
    field.setAttribute('readonly', '');
    field.style.position = 'fixed';
    field.style.top = '0';
    field.style.opacity = '0';

    document.body.appendChild(field);

    try {
        field.select();
        field.setSelectionRange(0, text.length);
        return document.execCommand('copy');
    } catch (error) {
        return false;
    } finally {
        field.remove();
    }
}

/// Whether the device has no hover — a touch screen, where one stray tap would move a
/// pawn and the confirm step earns its keep.
export function isCoarsePointer() {
    return window.matchMedia('(hover: none)').matches;
}

/// Keeps the move list on the move that matters, which is decided by whether the game is
/// being reviewed: stepping back names the row to show, and being live means the newest.
///
/// No "unless they have scrolled away" clause, deliberately. Reading the game so far is
/// what review is for, and clicking a row enters it — so the case that clause would
/// protect is already handled by the branch above it, and a rule that measures how far
/// the list has drifted can strand itself out of reach with no way back.
///
/// The row is brought in by moving the list itself and nothing else. scrollIntoView does
/// the same job in one line, but it scrolls every scrollable ancestor as well, so on a
/// phone — where the panel sits under the board and the page is the thing that scrolls —
/// pressing an arrow key used to drag the board off the top of the screen. Nudging
/// list.scrollTop cannot reach past the list.
export function followMoves(reviewing) {
    const list = document.querySelector('.moves');
    if (!list || list.scrollHeight <= list.clientHeight) return;

    if (!reviewing) {
        list.scrollTo({ top: list.scrollHeight });
        return;
    }

    const row = list.querySelector('li.here');
    if (!row) return;

    const seen = list.getBoundingClientRect();
    const here = row.getBoundingClientRect();

    // Already in view is the common case — stepping through a game moves one row at a
    // time — and the cheapest thing to do about it is nothing.
    if (here.top < seen.top) list.scrollTop -= seen.top - here.top;
    else if (here.bottom > seen.bottom) list.scrollTop += here.bottom - seen.bottom;
}

// ============================================================== the settings ==

// Where the panel's choices live between visits. One key holding the whole set rather
// than one key each: they are written together and read together, and a single string
// cannot come back half from one visit and half from another.
const settingsKey = 'quoridor.settings';

/// The stored settings, exactly as they were written, or an empty string when there is
/// nothing stored yet. Storage is not always there to be had — a browser told to refuse
/// it throws on the very first read rather than answering null — and a remembered volume
/// is never worth a page that will not start.
export function readSettings() {
    try {
        return localStorage.getItem(settingsKey) ?? '';
    } catch (error) {
        return '';
    }
}

/// Stores the settings. Failure is silent on purpose: the choices still hold for as long
/// as this tab is open, which is the whole of what the player asked for just now.
export function writeSettings(text) {
    try {
        localStorage.setItem(settingsKey, text);
    } catch (error) {
        // Refused, or full. Nothing here is worth interrupting a game over.
    }
}

// The keys the game answers to. Listed here as well as in the component because the
// default has to be cancelled in the same tick as the press — Space scrolls the page,
// and waiting for the round trip into .NET to find out whether we wanted it is a tick
// too late. What each key does is decided on the other side; this only claims them.
const claimed = new Set(['ArrowLeft', 'ArrowRight', ' ', '?', 'ctrl+z']);

// The keys the board answers to for itself once something on it holds focus. The board has
// a caret of its own to move and a move of its own to commit, and the page reads four of
// these as "step through the game so far" — so an arrow pressed at the board would move the
// caret and walk the game backwards in the same tick, and Space would play a move and turn
// the reading on. A key pressed at the board is the board's, for the same reason a key
// pressed into a field is the field's.
//
// Escape is not here: the board hands it back deliberately when it has nothing of its own
// to close, and the page then uses it to leave the review. W and R are not here either —
// the page does not bind them, so there is nothing for them to collide with.
const boardKeys = new Set(['ArrowLeft', 'ArrowRight', 'ArrowUp', 'ArrowDown', 'Enter', ' ', 'Home']);

let keyOwner = null;

/// Sends key presses to the component for as long as it is on the page. Listening on the
/// document rather than an element means the shortcuts work without anything focused,
/// which is the state the board is in for a player who has only been clicking.
export function listenForKeys(owner) {
    keyOwner = owner;
    document.addEventListener('keydown', onKey);
}

export function stopListeningForKeys() {
    document.removeEventListener('keydown', onKey);
    keyOwner = null;
}

function onKey(event) {
    if (!keyOwner || event.altKey || event.repeat) return;

    // A key pressed into a field is the field's, not the game's. The invite code box is
    // the one that matters: Ctrl+Z there should undo the typing.
    const focused = document.activeElement;
    if (focused && (focused.isContentEditable ||
        ['INPUT', 'TEXTAREA', 'SELECT'].includes(focused.tagName))) {
        return;
    }

    // The board's own keys, while the board is what the player is on. Cancelled here rather
    // than left alone because ArrowUp, ArrowDown and Home scroll the page and the claimed
    // set above does not cover them — it never had to, since until now nothing on the board
    // could hold focus. Arrows pressed anywhere else still scroll, as they should.
    // Held modifiers are excluded so Ctrl+Z stays the page's undo and the browser keeps its
    // own combinations: the board ignores a modified press too, so claiming one here would
    // only make a key that does nothing at all.
    if (!event.ctrlKey && !event.metaKey &&
        focused?.closest?.('.board') && boardKeys.has(event.key)) {
        event.preventDefault();
        return;
    }

    let name = event.key;

    if (event.ctrlKey || event.metaKey) {
        if (name.length !== 1) return;
        name = 'ctrl+' + name.toLowerCase();
    }

    if (claimed.has(name)) event.preventDefault();

    keyOwner.invokeMethodAsync('OnKey', name);
}
