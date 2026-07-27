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
