# Chess-Coding-Adventure
Version 2.0 of the Coding Adventure Bot. Good at beating up humans (~2600 on [lichess](https://lichess.org/@/CodingAdventureBot/playing)), but still has a very long way to go against its fellow machines (Stockfish crushes it even with rook-odds!)

You can find some videos about the bot's creation process here: [V1](https://www.youtube.com/watch?v=U4ogK0MIzqk) and [V2](https://youtu.be/_vqlIPDR2TU)

---

## Architecture Overview

This is **Sebastian Lague's Chess V2 UCI engine** — a .NET console app that speaks the UCI (Universal Chess Interface) protocol. It's structured in clean layers:

| Layer | Files | Role |
|---|---|---|
| **Entry** | Program.cs, EngineUCI.cs | UCI protocol loop, command parsing |
| **Bot** | Bot.cs | Orchestrates search, time management, opening book |
| **Search** | Searcher.cs, TranspositionTable.cs, MoveOrdering.cs, RepetitionTable.cs | Alpha-beta with iterative deepening |
| **Evaluation** | Evaluation.cs, PieceSquareTable.cs, PrecomputedEvaluationData.cs | Material + PST + pawn structure + king safety |
| **Move Generation** | MoveGenerator.cs, Magic.cs, BitBoardUtility.cs, Bits.cs | Legal movegen with magic bitboards |
| **Board** | Board.cs, Move.cs, Piece.cs, Zobrist.cs, etc. | Board representation, make/unmake, Zobrist hashing |
| **Helpers** | FenUtility.cs, MoveUtility.cs, BoardHelper.cs, PGNCreator.cs | FEN parsing, UCI move converters, SAN notation |

---

## Key Implementation Details

### Board Representation (Board.cs)
- **Hybrid**: 64-element `int[]` mailbox *plus* bitboards per piece type/colour.
- 16-bit compact `Move` struct (6-bit start, 6-bit target, 4-bit flag) — very cache-friendly.
- Incremental **Zobrist hashing** (XOR-based, seeded with constant `29426028`).
- Full **make/unmake** cycle including en passant, castling, promotion, and null move.
- `GameState` is a readonly struct on a stack for instant rollback.

### Move Generation (MoveGenerator.cs)
- **Legal move generation** (not pseudo-legal + legality check) — pins and checks are computed up front via `CalculateAttackData()`.
- **Magic bitboards** for slider attacks (rooks/bishops/queens) with precomputed attack tables.
- Separate pin ray mask and check ray mask bitboards constrain move targets.
- En passant has a special discovered-check test (`InCheckAfterEnPassant`).
- Promotion mode setting: `All`, `QueenOnly`, or `QueenAndKnight` (search uses `QueenAndKnight` to reduce branching).
- Moves written into `Span<Move>` (stack-allocated) for zero GC pressure.

### Search (Searcher.cs)
- **Iterative deepening** up to depth 256 with cancellation support.
- **Alpha-beta negamax** with:
  - **Transposition table** (64MB, Zobrist-keyed, stores exact/upper/lower bounds with mate score correction).
  - **Move ordering**: hash move > winning captures (MVV-LVA) > promotions > killer moves > history heuristic > losing captures.
  - **Late Move Reductions (LMR)**: reduces depth by 1 for non-capture moves at index ≥ 3 when depth ≥ 3.
  - **Search extensions**: +1 ply for check extensions and pawn-to-7th-rank, capped at 16 total extensions.
  - **Quiescence search** to avoid the horizon effect (evaluates only captures until position is quiet).
  - **Mate distance pruning** to short-circuit when a forced mate is already found.
  - **Repetition detection** via `RepetitionTable` with reset boundaries at pawn moves/captures.
  - **50-move rule** detection (returns draw score at 100 half-moves).
- Runs on a **dedicated long-running thread** with `AutoResetEvent` signaling.
- Think time: `Task.Delay` with `CancellationTokenSource` for timed stops.

### Evaluation (Evaluation.cs)
- **Material**: P=100, N=300, B=320, R=500, Q=900.
- **Piece-square tables** with interpolation between midgame and endgame PSTs for pawns and king.
- **Pawn structure**: passed pawn bonuses, isolated pawn penalties.
- **King safety**: pawn shield scoring (quadratic penalty), uncastled king penalty, open/semi-open file penalties near king.
- **Mop-up evaluation**: in winning endgames, drives opponent king to corners using orthogonal + centre-manhattan distances.
- **Endgame transition** (`endgameT` 0→1) computed from remaining piece weight, blends eval components smoothly.

### Opening Book (OpeningBook.cs)
- Loaded from embedded resource `Book.txt`.
- FEN → weighted book moves dictionary; selection uses `weightPow = 0.5` (sqrt-weighted random).
- Book moves limited to first 16 ply (`maxBookPly`).

---

## Notable Observations

**Strengths:**
1. Clean, well-structured code with good separation of concerns.
2. Legal movegen (no illegal moves generated) — correctness by construction.
3. Efficient memory: `Span<Move>` stack allocation, compact 16-bit moves, bitboard ops.
4. Magic bitboards give fast slider attack lookups.
5. Solid search framework with TT, LMR, extensions, killer/history heuristics.

**Weaknesses / Possible Improvements:**
1. **No null-move pruning** in search — this is a major standard optimization that's absent. The board supports `MakeNullMove`/`UnmakeNullMove` but the searcher never calls them.
2. **No aspiration windows** in iterative deepening — each iteration searches the full $[-\infty, +\infty]$ window.
3. **No principal variation search (PVS)** — all moves search the full window, missing the common optimization of searching with a zero-width window first.
4. **LMR is simplistic** — only reduces by 1 ply, no re-search with full depth after LMR failure beyond `eval > alpha`.
5. **No futility pruning, delta pruning, or SEE** (Static Exchange Evaluation) — quiescence tries all captures regardless of whether they're clearly losing.
6. **RepetitionTable is fixed at 256 entries** — could overflow in very long games (the `Push` silently stops writing past bounds but `count` keeps incrementing, which would make `Contains` read stale data).
7. **`TryPop` in RepetitionTable** doesn't match push/pop pairing very strictly — it uses `Math.Max(0, count-1)` which could mask bugs.
8. **No `ucinewgame` TT clear** — `NotifyNewGame()` clears the TT and killers but not history, which could leave stale ordering data.
9. **Evaluation lacks** bishop pair bonus, mobility, rook on open file, connected rooks, outpost detection.
10. **Thread safety**: the search thread and timer/cancellation interact via shared mutable state (`searchCancelled`, `currentSearchID`) without `volatile` or memory barriers — may be unreliable on relaxed memory architectures (though x86 is relatively forgiving).
11. **`PGNCreator`** has a precedence bug at PGNCreator.cs: `result is not GameResult.NotStarted or GameResult.InProgress` parses as `(result is not GameResult.NotStarted) or GameResult.InProgress` which is always true due to the `or` pattern — should likely be `result is not (GameResult.NotStarted or GameResult.InProgress)`.
12. **No UCI `info` output** — the engine doesn't report `info depth ... score ... pv ...` during search, which GUIs expect for live analysis display.

**Estimated Strength**: Likely ~1800–2200 Elo depending on time control. The legal movegen and magic bitboards are solid, but the lack of null-move pruning, PVS, aspiration windows, and deeper eval features limits it compared to modern engines.