using System;
using System.Linq;

namespace Chess.Core
{
	public class RepetitionTable
	{
		ulong[] hashes;
		int[] startIndices;
		int count;

		public RepetitionTable()
		{
			hashes = new ulong[1024];
			startIndices = new int[hashes.Length + 1];
		}

		public void Init(Board board)
		{
			ulong[] initialHashes = board.RepetitionPositionHistory.Reverse().ToArray();
			count = initialHashes.Length;

			for (int i = 0; i < initialHashes.Length; i++)
			{
				hashes[i] = initialHashes[i];
				startIndices[i] = 0;
			}
			startIndices[count] = 0;
		}


		public void Push(ulong hash, bool reset)
		{
			if (count >= hashes.Length)
			{
				int newSize = hashes.Length * 2;
				Array.Resize(ref hashes, newSize);
				Array.Resize(ref startIndices, newSize + 1);
			}
			hashes[count] = hash;
			startIndices[count + 1] = reset ? count : startIndices[count];
			count++;
		}

		public void TryPop()
		{
			count = Math.Max(0, count - 1);
		}

		public bool Contains(ulong h)
		{
			int s = startIndices[count];
			// up to count-1 so that curr position is not counted
			for (int i = s; i < count - 1; i++)
			{
				if (hashes[i] == h)
				{
					return true;
				}
			}
			return false;
		}
	}
}