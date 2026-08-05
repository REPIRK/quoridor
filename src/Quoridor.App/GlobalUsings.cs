// The session, the clock, the preferences and the network peer moved out of this project
// into Quoridor.Session, so that a phone can have them too. They are used all over the
// app and by name — GameSession, TimeControl, Settings, NetPeer — so rather than adding
// the same using to a dozen files, it is stated once here.
global using Quoridor.Session;
