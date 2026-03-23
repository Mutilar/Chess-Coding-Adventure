namespace Chess.Core
{
	using System;
	using static System.Math;

	public class Searcher
	{
		// Constants
		const int transpositionTableSizeMB = 64;
		const int maxExtentions = 16;

		const int immediateMateScore = 100000;
		const int positiveInfinity = 9999999;
		const int negativeInfinity = -positiveInfinity;

		public event Action<Move>? OnSearchComplete;

		// State
		public int CurrentDepth;
		public Move BestMoveSoFar => bestMove;
		public int BestEvalSoFar => bestEval;
		bool isPlayingWhite;
		Move bestMoveThisIteration;
		int bestEvalThisIteration;
		Move bestMove;
		int bestEval;
		bool hasSearchedAtLeastOneMove;
		volatile bool searchCancelled;

		// Diagnostics
		public SearchDiagnostics searchDiagnostics;
		int currentIterationDepth;
		System.Diagnostics.Stopwatch searchIterationTimer;
		System.Diagnostics.Stopwatch searchTotalTimer;
		public string debugInfo;

		// References
		readonly TranspositionTable transpositionTable;
		readonly RepetitionTable repetitionTable;
		readonly MoveGenerator moveGenerator;
		readonly MoveOrdering moveOrderer;
		readonly Evaluation evaluation;
		readonly Board board;

		public Searcher(Board board)
		{
			this.board = board;

			evaluation = new Evaluation();
			moveGenerator = new MoveGenerator();
			transpositionTable = new TranspositionTable(board, transpositionTableSizeMB);
			moveOrderer = new MoveOrdering(moveGenerator, transpositionTable);
			repetitionTable = new RepetitionTable();

			moveGenerator.promotionsToGenerate = MoveGenerator.PromotionMode.QueenAndKnight;

			// Run a depth 1 search so that JIT doesn't run during actual search (and mess up timing stats in editor)
			Search(1, 0, negativeInfinity, positiveInfinity);
		}

		public void StartSearch()
		{
			// Initialize search
			bestEvalThisIteration = bestEval = 0;
			bestMoveThisIteration = bestMove = Move.NullMove;

			isPlayingWhite = board.IsWhiteToMove;

			moveOrderer.ClearHistory();
			repetitionTable.Init(board);

			// Initialize debug info
			CurrentDepth = 0;
			debugInfo = "Starting search with FEN " + FenUtility.CurrentFen(board);
			searchCancelled = false;
			searchDiagnostics = new SearchDiagnostics();
			searchIterationTimer = new System.Diagnostics.Stopwatch();
			searchTotalTimer = System.Diagnostics.Stopwatch.StartNew();

			// Search
			RunIterativeDeepeningSearch();


			// Finish up
			// In the unlikely event that the search is cancelled before a best move can be found, take any move
			if (bestMove.IsNull)
			{
				bestMove = moveGenerator.GenerateMoves(board)[0];
			}
			OnSearchComplete?.Invoke(bestMove);
			searchCancelled = false;
		}

		// Run iterative deepening. This means doing a full search with a depth of 1, then with a depth of 2, and so on.
		// This allows the search to be cancelled at any time and still yield a useful result.
		// Thanks to the transposition table and move ordering, this idea is not nearly as terrible as it sounds.
		void RunIterativeDeepeningSearch()
		{
			for (int searchDepth = 1; searchDepth <= 256; searchDepth++)
			{
				hasSearchedAtLeastOneMove = false;
				debugInfo += "\nStarting Iteration: " + searchDepth;
				searchIterationTimer.Restart();
				currentIterationDepth = searchDepth;

				// Aspiration windows: narrow window around previous eval from depth 4+
				if (searchDepth >= 4 && !IsMateScore(bestEval))
				{
					const int aspirationWindow = 30;
					int aspAlpha = bestEval - aspirationWindow;
					int aspBeta = bestEval + aspirationWindow;
					int aspResult = Search(searchDepth, 0, aspAlpha, aspBeta);

					if (!searchCancelled && (aspResult <= aspAlpha || aspResult >= aspBeta))
					{
						bestEvalThisIteration = int.MinValue;
						bestMoveThisIteration = Move.NullMove;
						hasSearchedAtLeastOneMove = false;
						Search(searchDepth, 0, negativeInfinity, positiveInfinity);
					}
				}
				else
				{
					Search(searchDepth, 0, negativeInfinity, positiveInfinity);
				}

				if (searchCancelled)
				{
					if (hasSearchedAtLeastOneMove)
					{
						bestMove = bestMoveThisIteration;
						bestEval = bestEvalThisIteration;
						searchDiagnostics.move = MoveUtility.GetMoveNameUCI(bestMove);
						searchDiagnostics.eval = bestEval;
						searchDiagnostics.moveIsFromPartialSearch = true;
						debugInfo += "\nUsing partial search result: " + MoveUtility.GetMoveNameUCI(bestMove) + " Eval: " + bestEval;
					}

					debugInfo += "\nSearch aborted";
					break;
				}
				else
				{
					CurrentDepth = searchDepth;
					bestMove = bestMoveThisIteration;
					bestEval = bestEvalThisIteration;

					debugInfo += "\nIteration result: " + MoveUtility.GetMoveNameUCI(bestMove) + " Eval: " + bestEval;
					if (IsMateScore(bestEval))
					{
						debugInfo += " Mate in ply: " + NumPlyToMateFromScore(bestEval);
					}

					// UCI info output
					long elapsedMs = searchTotalTimer.ElapsedMilliseconds;
					long nps = elapsedMs > 0 ? (searchDiagnostics.numPositionsEvaluated * 1000L) / elapsedMs : 0;
					string scoreStr = IsMateScore(bestEval)
						? $"score mate {(bestEval > 0 ? "" : "-")}{(NumPlyToMateFromScore(bestEval) + 1) / 2}"
						: $"score cp {bestEval}";
					Console.WriteLine($"info depth {searchDepth} {scoreStr} nodes {searchDiagnostics.numPositionsEvaluated} nps {nps} time {elapsedMs}");

					bestEvalThisIteration = int.MinValue;
					bestMoveThisIteration = Move.NullMove;

					// Update diagnostics
					searchDiagnostics.numCompletedIterations = searchDepth;
					searchDiagnostics.move = MoveUtility.GetMoveNameUCI(bestMove);
					searchDiagnostics.eval = bestEval;
					// Exit search if found a mate within search depth.
					// A mate found outside of search depth (due to extensions) may not be the fastest mate.
					if (IsMateScore(bestEval) && NumPlyToMateFromScore(bestEval) <= searchDepth)
					{
						debugInfo += "\nExitting search due to mate found within search depth";
						break;
					}
				}
			}
		}

		public (Move move, int eval) GetSearchResult()
		{
			return (bestMove, bestEval);
		}

		public void EndSearch()
		{
			searchCancelled = true;
		}


		int Search(int plyRemaining, int plyFromRoot, int alpha, int beta, int numExtensions = 0, Move prevMove = default, bool prevWasCapture = false, bool allowNullMove = true)
		{
			if (searchCancelled)
			{
				return 0;
			}

			if (plyFromRoot > 0)
			{
				// Detect draw by three-fold repetition.
				// (Note: returns a draw score even if this position has only appeared once for sake of simplicity)
				if (board.CurrentGameState.fiftyMoveCounter >= 100 || repetitionTable.Contains(board.CurrentGameState.zobristKey))
				{
					/*
					const int contempt = 50;
					// So long as not in king and pawn ending, prefer a slightly worse position over game ending in a draw
					if (board.totalPieceCountWithoutPawnsAndKings > 0)
					{
						bool isAITurn = board.IsWhiteToMove == aiPlaysWhite;
						return isAITurn ? -contempt : contempt;
					}
					*/
					return 0;
				}

				// Skip this position if a mating sequence has already been found earlier in the search, which would be shorter
				// than any mate we could find from here. This is done by observing that alpha can't possibly be worse
				// (and likewise beta can't  possibly be better) than being mated in the current position.
				alpha = Max(alpha, -immediateMateScore + plyFromRoot);
				beta = Min(beta, immediateMateScore - plyFromRoot);
				if (alpha >= beta)
				{
					return alpha;
				}
			}

			// Try looking up the current position in the transposition table.
			// If the same position has already been searched to at least an equal depth
			// to the search we're doing now,we can just use the recorded evaluation.
			int ttVal = transpositionTable.LookupEvaluation(plyRemaining, plyFromRoot, alpha, beta);
			if (ttVal != TranspositionTable.LookupFailed)
			{
				if (plyFromRoot == 0)
				{
					bestMoveThisIteration = transpositionTable.TryGetStoredMove();
					bestEvalThisIteration = transpositionTable.entries[transpositionTable.Index].value;
				}
				return ttVal;
			}

			if (plyRemaining == 0)
			{
				int evaluation = QuiescenceSearch(alpha, beta);
				return evaluation;
			}

			// Null-move pruning: if giving opponent a free move still results in a beta cutoff,
			// the position is likely so good that we can prune this branch
			if (allowNullMove && plyFromRoot > 0 && plyRemaining >= 3 && !board.IsInCheck() && board.TotalPieceCountWithoutPawnsAndKings > 0)
			{
				board.MakeNullMove();
				int nullEval = -Search(plyRemaining - 1 - 2, plyFromRoot + 1, -beta, -beta + 1, numExtensions, Move.NullMove, true, allowNullMove: false);
				board.UnmakeNullMove();
				if (searchCancelled) return 0;
				if (nullEval >= beta)
				{
					return beta;
				}
			}

			Span<Move> moves = stackalloc Move[256];
			moveGenerator.GenerateMoves(board, ref moves, capturesOnly: false);
			Move prevBestMove = plyFromRoot == 0 ? bestMove : transpositionTable.TryGetStoredMove();
			moveOrderer.OrderMoves(prevBestMove, board, moves, moveGenerator.opponentAttackMap, moveGenerator.opponentPawnAttackMap, false, plyFromRoot);
			// Detect checkmate and stalemate when no legal moves are available
			if (moves.Length == 0)
			{
				if (moveGenerator.InCheck())
				{
					int mateScore = immediateMateScore - plyFromRoot;
					return -mateScore;
				}
				else
				{
					return 0;
				}
			}

			if (plyFromRoot > 0)
			{
				bool wasPawnMove = Piece.PieceType(board.Square[prevMove.TargetSquare]) == Piece.Pawn;
				repetitionTable.Push(board.CurrentGameState.zobristKey, prevWasCapture || wasPawnMove);
			}

			int evaluationBound = TranspositionTable.UpperBound;
			Move bestMoveInThisPosition = Move.NullMove;

			for (int i = 0; i < moves.Length; i++)
			{
				Move move = moves[i];
				int capturedPieceType = Piece.PieceType(board.Square[move.TargetSquare]);
				bool isCapture = capturedPieceType != Piece.None;
				board.MakeMove(moves[i], inSearch: true);

				// Extend the depth of the search in certain interesting cases
				int extension = 0;
				if (numExtensions < maxExtentions)
				{
					int movedPieceType = Piece.PieceType(board.Square[move.TargetSquare]);
					int targetRank = BoardHelper.RankIndex(move.TargetSquare);
					if (board.IsInCheck())
					{
						extension = 1;
					}
					else if (movedPieceType == Piece.Pawn && (targetRank == 1 || targetRank == 6))
					{
						extension = 1;
					}
				}

				bool needsFullSearch = true;
				int eval = 0;

				if (i > 0)
				{
					// Late Move Reductions: reduce depth for quiet moves later in the list
					if (extension == 0 && plyRemaining >= 3 && i >= 3 && !isCapture)
					{
						// Variable reduction based on depth and move index
						int R = 1 + (int)(Log(plyRemaining) * Log(i) / 3.5);
						R = Min(R, plyRemaining - 1);
						eval = -Search(plyRemaining - 1 - R, plyFromRoot + 1, -alpha - 1, -alpha, numExtensions, move, isCapture);
					}
					else
					{
						// PVS: zero-window search for non-PV moves
						eval = -Search(plyRemaining - 1 + extension, plyFromRoot + 1, -alpha - 1, -alpha, numExtensions + extension, move, isCapture);
					}
					// If reduced/zero-window search beat alpha, verify with full-depth full-window search
					needsFullSearch = eval > alpha;
				}

				// Full-depth full-window search (always for first move, re-search for others if needed)
				if (needsFullSearch)
				{
					eval = -Search(plyRemaining - 1 + extension, plyFromRoot + 1, -beta, -alpha, numExtensions + extension, move, isCapture);
				}
				board.UnmakeMove(moves[i], inSearch: true);

				if (searchCancelled)
				{
					return 0;
				}

				// Move was *too* good, opponent will choose a different move earlier on to avoid this position.
				// (Beta-cutoff / Fail high)
				if (eval >= beta)
				{
					// Store evaluation in transposition table. Note that since we're exiting the search early, there may be an
					// even better move we haven't looked at yet, and so the current eval is a lower bound on the actual eval.
					transpositionTable.StoreEvaluation(plyRemaining, plyFromRoot, beta, TranspositionTable.LowerBound, moves[i]);

					// Update killer moves and history heuristic (note: don't include captures as theres are ranked highly anyway)
					if (!isCapture)
					{
						if (plyFromRoot < MoveOrdering.maxKillerMovePly)
						{
							moveOrderer.killerMoves[plyFromRoot].Add(move);
						}
						int historyScore = plyRemaining * plyRemaining;
						moveOrderer.History[board.MoveColourIndex, moves[i].StartSquare, moves[i].TargetSquare] += historyScore;
					}
					if (plyFromRoot > 0)
					{
						repetitionTable.TryPop();
					}

					searchDiagnostics.numCutOffs++;
					return beta;
				}

				// Found a new best move in this position
				if (eval > alpha)
				{
					evaluationBound = TranspositionTable.Exact;
					bestMoveInThisPosition = moves[i];

					alpha = eval;
					if (plyFromRoot == 0)
					{
						bestMoveThisIteration = moves[i];
						bestEvalThisIteration = eval;
						hasSearchedAtLeastOneMove = true;
					}
				}
			}

			if (plyFromRoot > 0)
			{
				repetitionTable.TryPop();
			}

			transpositionTable.StoreEvaluation(plyRemaining, plyFromRoot, alpha, evaluationBound, bestMoveInThisPosition);

			return alpha;

		}

		// Search capture moves until a 'quiet' position is reached.
		int QuiescenceSearch(int alpha, int beta)
		{
			if (searchCancelled)
			{
				return 0;
			}
			// Stand-pat: evaluate the position without making any capture.
			int standPat = evaluation.Evaluate(board);
			searchDiagnostics.numPositionsEvaluated++;
			if (standPat >= beta)
			{
				searchDiagnostics.numCutOffs++;
				return beta;
			}
			if (standPat > alpha)
			{
				alpha = standPat;
			}

			Span<Move> moves = stackalloc Move[128];
			moveGenerator.GenerateMoves(board, ref moves, capturesOnly: true);
			moveOrderer.OrderMoves(Move.NullMove, board, moves, moveGenerator.opponentAttackMap, moveGenerator.opponentPawnAttackMap, true, 0);
			for (int i = 0; i < moves.Length; i++)
			{
				// Delta pruning: skip captures that can't possibly raise alpha
				int capPieceType = moves[i].MoveFlag == Move.EnPassantCaptureFlag
					? Piece.Pawn
					: Piece.PieceType(board.Square[moves[i].TargetSquare]);
				if (standPat + GetStaticPieceValue(capPieceType) + 200 < alpha)
				{
					continue;
				}

				board.MakeMove(moves[i], true);
				int eval = -QuiescenceSearch(-beta, -alpha);
				board.UnmakeMove(moves[i], true);

				if (eval >= beta)
				{
					searchDiagnostics.numCutOffs++;
					return beta;
				}
				if (eval > alpha)
				{
					alpha = eval;
				}
			}

			return alpha;
		}


		static int GetStaticPieceValue(int pieceType) => pieceType switch
		{
			Piece.Pawn => Evaluation.PawnValue,
			Piece.Knight => Evaluation.KnightValue,
			Piece.Bishop => Evaluation.BishopValue,
			Piece.Rook => Evaluation.RookValue,
			Piece.Queen => Evaluation.QueenValue,
			_ => 0
		};

		public static bool IsMateScore(int score)
		{
			if (score == int.MinValue)
			{
				return false;
			}
			const int maxMateDepth = 1000;
			return Abs(score) > immediateMateScore - maxMateDepth;
		}

		public static int NumPlyToMateFromScore(int score)
		{
			return immediateMateScore - Abs(score);

		}

		public string AnnounceMate()
		{
			if (IsMateScore(bestEvalThisIteration))
			{
				int numPlyToMate = NumPlyToMateFromScore(bestEvalThisIteration);
				int numMovesToMate = (int)Ceiling(numPlyToMate / 2f);

				string sideWithMate = (bestEvalThisIteration * ((board.IsWhiteToMove) ? 1 : -1) < 0) ? "Black" : "White";

				return $"{sideWithMate} can mate in {numMovesToMate} move{((numMovesToMate > 1) ? "s" : "")}";
			}
			return "No mate found";
		}

		/// <summary>
		/// Run a depth-limited search (blocks until complete). No time limit.
		/// Returns (bestMove, eval) for the current position.
		/// </summary>
		public (Move move, int eval) SearchToDepth(int maxDepth)
		{
			bestEvalThisIteration = bestEval = 0;
			bestMoveThisIteration = bestMove = Move.NullMove;

			isPlayingWhite = board.IsWhiteToMove;
			moveOrderer.ClearHistory();
			repetitionTable.Init(board);

			CurrentDepth = 0;
			searchCancelled = false;
			searchDiagnostics = new SearchDiagnostics();
			searchIterationTimer = new System.Diagnostics.Stopwatch();
			searchTotalTimer = System.Diagnostics.Stopwatch.StartNew();

			maxDepth = Max(1, Min(maxDepth, 256));

			for (int searchDepth = 1; searchDepth <= maxDepth; searchDepth++)
			{
				bestEvalThisIteration = int.MinValue;
				bestMoveThisIteration = Move.NullMove;
				hasSearchedAtLeastOneMove = false;
				currentIterationDepth = searchDepth;

				if (searchDepth >= 4 && !IsMateScore(bestEval))
				{
					const int aspirationWindow = 30;
					int aspAlpha = bestEval - aspirationWindow;
					int aspBeta = bestEval + aspirationWindow;
					int aspResult = Search(searchDepth, 0, aspAlpha, aspBeta);

					if (aspResult <= aspAlpha || aspResult >= aspBeta)
					{
						bestEvalThisIteration = int.MinValue;
						bestMoveThisIteration = Move.NullMove;
						hasSearchedAtLeastOneMove = false;
						Search(searchDepth, 0, negativeInfinity, positiveInfinity);
					}
				}
				else
				{
					Search(searchDepth, 0, negativeInfinity, positiveInfinity);
				}

				if (hasSearchedAtLeastOneMove)
				{
					bestMove = bestMoveThisIteration;
					bestEval = bestEvalThisIteration;
				}
				CurrentDepth = searchDepth;

				if (IsMateScore(bestEval) && NumPlyToMateFromScore(bestEval) <= searchDepth)
					break;
			}

			if (bestMove.IsNull)
			{
				var moves = moveGenerator.GenerateMoves(board);
				if (moves.Length > 0) bestMove = moves[0];
			}

			return (bestMove, bestEval);
		}

		/// <summary>
		/// Run static evaluation of the current position.
		/// Returns centipawn score from the perspective of the side to move.
		/// </summary>
		public int StaticEval()
		{
			return evaluation.Evaluate(board);
		}

		public void ClearForNewPosition()
		{
			transpositionTable.Clear();
			moveOrderer.ClearKillers();
		}

		public TranspositionTable GetTranspositionTable() => transpositionTable;

		[Serializable]
		public struct SearchDiagnostics
		{
			public int numCompletedIterations;
			public int numPositionsEvaluated;
			public ulong numCutOffs;

			public string moveVal;
			public string move;
			public int eval;
			public bool moveIsFromPartialSearch;
			public int NumQChecks;
			public int numQMates;

			public bool isBook;

			public int maxExtentionReachedInSearch;
		}

	}
}