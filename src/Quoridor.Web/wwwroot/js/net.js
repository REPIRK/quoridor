// Peer-to-peer link between two browsers.
//
// The site is served from static hosting, so there is no server of ours to relay
// moves through. WebRTC connects the two browsers directly; a signalling server is
// only needed to introduce them, and PeerJS's free public one does that. Once the
// data channel is open nothing else is involved — the moves never touch a third
// party, and if the signalling server goes down, games already in progress carry on.

let peer = null;
let conn = null;
let owner = null;

function notify(kind, payload) {
    if (owner) owner.invokeMethodAsync('OnNetEvent', kind, String(payload ?? ''));
}

function attach(connection) {
    conn = connection;

    connection.on('open', () => notify('connected', connection.peer));
    connection.on('data', data => notify('message', data));
    connection.on('close', () => notify('closed', ''));
    connection.on('error', error => notify('error', error?.message ?? 'connection error'));
}

function createPeer(dotnetRef) {
    owner = dotnetRef;

    if (typeof Peer === 'undefined') {
        notify('error', 'The peer-to-peer library did not load. Check your connection and reload.');
        return null;
    }

    const created = new Peer();

    created.on('error', error => notify('error', error?.message ?? 'network error'));
    created.on('disconnected', () => notify('error', 'Lost the signalling server. The game itself is unaffected.'));

    return created;
}

export function host(dotnetRef) {
    close();

    peer = createPeer(dotnetRef);
    if (!peer) return;

    peer.on('open', id => notify('hosting', id));
    peer.on('connection', attach);
}

export function join(code, dotnetRef) {
    close();

    peer = createPeer(dotnetRef);
    if (!peer) return;

    peer.on('open', () => {
        notify('joining', code);
        attach(peer.connect(code, { reliable: true }));
    });
}

export function send(text) {
    if (conn && conn.open) conn.send(text);
}

export function close() {
    if (conn) { try { conn.close(); } catch { /* already gone */ } conn = null; }
    if (peer) { try { peer.destroy(); } catch { /* already gone */ } peer = null; }
}
