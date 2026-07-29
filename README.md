# Quoridor

A Quoridor game for Windows, and the alpha-beta engine behind it.

**[Play it in your browser →](https://REPIRK.github.io/quoridor/)**
— the same engine, compiled to WebAssembly. No install, no account.

**[Download for Windows →](https://github.com/REPIRK/quoridor/releases/latest/download/Quoridor.exe)**
— one file, nothing to install, no .NET needed. (That link is the app itself. The
"Source code" archives on the releases page are this repository, not the game.)

*Русская версия с подробностями по движку: [README.ru.md](README.ru.md).*

---

## The game

Standard 9×9 Quoridor, ten walls each, full rules — jumping over the facing pawn,
stepping diagonally when a wall is behind it, and the rule that no wall may ever leave
a player without a route.

The desktop build has local play, three engine strengths, a chess clock, a mode where
two engines play each other, review of the game so far, play over your own network, and
a light and a dark theme. The browser build is smaller, but it is the one you can share:
play the engine, play a friend on the same screen, or **send someone a link and play
them online**. It works on a phone, and it keeps premoves and a move list.

Either build lets you choose which side you take — before the game and again on a
rematch. Setting a game up asks one question first:

| | |
| --- | --- |
| **Standard** | nine by nine, ten walls each. Starts without further questions. |
| **Random** | board, walls, holes, pickups and who moves first, all rolled |
| **Custom** | every setting, in dropdowns |

Custom opens up the board itself: **7×7** and **5×5** as well as the usual 9×9, a wall
supply from none to twenty, **holes** — squares taken out of play, scattered at random —
and **pickups**, which sit on squares waiting to be stepped on. A pickup is either a
spare wall or a free move that skips your opponent's turn.

Holes and pickups are always placed in pairs that map onto each other under a half turn
of the board, the same turn that maps one player's half onto the other's. So a random
board is still a fair one: whatever the roll does to your route, it does to theirs.

There is no 11×11. The core is compiled around a nine-wide grid, and a smaller game is
played on a centred square of it — which costs nothing and is why 7×7 and 5×5 are here.
Going the other way would mean making the board size a runtime value: every index,
shift and mask stops being a constant, and 121 wall slots no longer fit the 64-bit word
they are packed into. That is a real cost to the engine, which is the thing this project
is actually about.

| | |
| --- | --- |
| Click a highlighted square | move your pawn |
| Hover the groove between two squares | preview a wall — the panel tells you what it costs each side |
| Click | place it |
| `Space` | draw both players' shortest routes |
| `←` `→` | step back through the game |
| `Ctrl+Z` undo · `F2` restart · `Esc` menu · `Ctrl+T` theme · `F11` fullscreen | |

## Running it

```bash
dotnet run --project src/Quoridor.App -c Release      # the Windows app
dotnet run --project src/Quoridor.Web -c Release      # the browser build, on localhost
dotnet run --project tests/Quoridor.Selftest -c Release   # rules and engine checks
dotnet run --project tests/Quoridor.Bench -c Release -- depth|fixed|duel|ablate
```

A single self-contained `Quoridor.exe` that needs no .NET installed:

```powershell
powershell -ExecutionPolicy Bypass -File publish.ps1
```

## How it is put together

```
src/Quoridor.Core     rules and position, no UI
src/Quoridor.Engine   evaluation, heuristic bot, alpha-beta search
src/Quoridor.App      the Windows app (WPF)
src/Quoridor.Web      the browser build (Blazor WebAssembly)
tests/Quoridor.Selftest   correctness, no external dependencies
tests/Quoridor.Bench      engine measurement
```

Dependencies run one way: `App`/`Web` → `Engine` → `Core`. The core has never known
about a UI, which is why the browser build reuses it and the engine **unchanged** —
only the view is written twice.

### The core

A position is a mutable 100-byte struct. The search copies it rather than implementing
undo: cheaper than a pair of cache lines, and it removes a whole class of bugs.

Squares are a bitboard in a `UInt128` — 81 bits, `index = row * 9 + col`. Four
`Blocked*` boards answer "may a pawn step this way" in one AND, with the board edges
baked in, so neither move generation nor path finding ever checks bounds. Finding a
shortest route is a flood fill: one step advances the whole frontier in four shifts and
four ANDs. **About 30 ns per query**, which matters because checking that a wall does
not seal a player in runs it twice for each of ~30 candidate walls at every node.

Those same four boards are what makes the alternative boards nearly free: a square out
of play is sealed on all four sides, and its neighbours are sealed against stepping into
it. After that the rules, the flood fill and the search need no idea that holes exist —
they are already walls as far as the masks are concerned. The one place that does care
is noted below. A smaller game is the same trick applied to the ring around a centred
square, plus a pair of bytes saying which rows the players are aiming for.

Pickups are two more bitboards, cleared as they are taken and folded into the hash. They
cost the standard game nothing measurable — the position is copied per node either way,
and it is still a handful of vector moves.

The engine used to be blind to them: it found one only by searching onto it, so anything
further out was invisible and it walked past prizes a person plans a route around. The
evaluation now counts what the pickups on the board are worth to whoever is nearer, using
plain distance — a true one would want a flood fill from each pawn and double what an
evaluation costs, and the term only has to lean the search the right way. How hard to lean
was settled by duel rather than taste: at equal depth on pickup boards, 25 per cent of a
pickup's worth beat blind 41:23 over 64 games, while 100 per cent lost 11:13. Overpriced,
it stops racing and goes shopping. A standard board pays nothing: the term is skipped
before any arithmetic when there are no pickups.

### The engine

Alpha-beta with a principal variation search: transposition table, killers, history,
late move reductions, aspiration windows, an exact verdict for wall-less races, and
repetition detection.

Two things carry most of the speed. Walls are only considered where they could matter —
beside either player's current route, next to an existing wall, or right in front of the
opponent. And a wall provably cannot change any distance unless it closes an edge between
two squares whose distances differ, which is four array reads instead of a flood fill.
That claim is exact, the search leans on it hard, and it is verified by exhaustive audit
in the self-tests.

The second shortcut is the one holes break. A wall can only cut the board in two if it
joins a chain running from one border to another, so unless two of its three grid points
already touch a wall or a border it provably cannot seal anyone in — and the two flood
fills can be skipped. That argument knows about walls and borders; a square out of play
is neither, and a chain could run to one and cut the board with nothing else attached.
So on those boards the shortcut is simply not taken and the exact check always runs.
Slower, and correct.

Pickups break a different assumption: that the two players alternate. A free move does
not pass the turn, so the child of that move is scored from the same side and must not
be negated — every recursion goes through one helper that checks which it is. The exact
race verdict also stops being exact while pickups are on the board, since one can hand
out a wall in a position that had none, so it is switched off until they are gone.

```
                     depth reached, one thread
opening,      1 s    10 ply    1.16M nodes/s
middlegame,   1 s    10 ply    1.23M nodes/s
middlegame, 0.1 s     8 ply
```

In the browser the same code runs on WebAssembly, which is slower. Ahead-of-time
compilation is what keeps it a real opponent — measured from the opening on a 0.6 s
budget:

```
interpreted    depth 5     3,818 nodes
AOT            depth 8    76,401 nodes
```

Strength, over games from randomised openings:

```
depth 4 : depth 2      21 : 3
depth 6 : depth 4      14 : 9
depth 4 : heuristic    23 : 1
```

### Playing someone online, with no server

The site is on static hosting, so there is no backend to relay moves through and none
to pay for. The two browsers connect straight to each other over a WebRTC data channel:
create a game, send the link, and the game starts when the other person opens it. Moves
travel as their notation — `e2`, `e6h` — carrying a ply number so a duplicate or a
message that overtook another is dropped rather than guessed at. Whoever creates the
game chooses the sides and the board, and says so in the first message across the
link, so neither browser has to assume.

The desktop app plays over a network too, but differently: one copy listens on a port
and the other dials it, with no service in the middle at all. On the same network that
needs nothing set up. The two builds do not talk to each other.

A free public signalling service introduces the two browsers to each other. It is used
once, at the start; after that nothing else is in the path, and a game already running
is unaffected if that service goes down. Two honest limitations: a new game cannot be
started while it is down, and some strict networks block the direct connection outright.
A real relay server would fix both, and would need hosting.

### Things that did not work

Kept here because a measured failure is worth as much as a measured win.

**Threads lose.** Lazy SMP is implemented and the table is lock-free, but eight threads
score 6:13 against one at the same clock. Two explanations were tested and both were
wrong: helper threads duplicating the main search (they were diversified — no change),
and helpers evicting the main thread's deep entries (replacement was reworked — no
change). The engine ships single-threaded.

**History ordering is a wash.** 13:11 over 24 games at equal depth. Kept because it is
sound and nearly free, not because it was shown to help.

**Twelve games prove nothing.** The same depth ladder gave 6:5 on twelve games and 14:9
on twenty-four. Use twenty-four or more before believing a result.

## Publishing this yourself

```bash
git config user.name  "Your Name"
git config user.email "the address on your GitHub account"

git add -A && git commit -m "Quoridor"
gh repo create quoridor --public --source=. --push
```

Then turn on Pages for the repository with **GitHub Actions** as the source. After that:

- `.github/workflows/ci.yml` builds the solution and runs the checks on every push.
- `.github/workflows/pages.yml` publishes the browser build on every push to `main`. It
  installs the WebAssembly AOT toolchain, so expect the job to take a few minutes.

The play link at the top of this file assumes the repository is named `quoridor`; the
Pages workflow picks the name up on its own.

## Licence

MIT. See [LICENSE](LICENSE).
