// Copyright (c) Microsoft Corporation.
// The Microsoft Corporation licenses this file to you under the MIT license.
// See NOTICE.md for the license text.

using System.Buffers;
using System.Globalization;
using System.Text;

namespace JPSoftworks.Regedit.Models.Tools;

// Adapted from the PowerToys Command Palette fuzzy matcher.
internal sealed class FuzzyStringMatcher
{
    private const int MaxScoreCells = 64 * 1024;
    private static readonly Lazy<char[]> FoldedCharacters = new(static () => new char[char.MaxValue + 1]);
    private readonly string _query;
    private readonly string _foldedQuery;
    private readonly bool _foldSlashes;
    private readonly bool _foldDiacritics;

    internal FuzzyStringMatcher(string query, bool foldSlashes = true, bool foldDiacritics = true)
    {
        this._query = query;
        this._foldSlashes = foldSlashes;
        this._foldDiacritics = foldDiacritics;
        this._foldedQuery = string.Create(query.Length, (Query: query, FoldSlashes: foldSlashes, FoldDiacritics: foldDiacritics),
            static (destination, state) => Fold(state.Query, destination, state.FoldSlashes, state.FoldDiacritics));
    }

    internal (int Score, int[] Positions) Match(string target)
    {
        var queryLength = this._query.Length;
        var targetLength = target.Length;
        var cellCount = (long)queryLength * targetLength;
        if (queryLength == 0 || queryLength > targetLength)
        {
            return (0, []);
        }

        using var targetBuffer = new ScratchBuffer<char>(targetLength, stackalloc char[256]);
        var folded = targetBuffer.Span;
        Fold(target, folded, this._foldSlashes, this._foldDiacritics);
        if (!this.CanMatch(folded))
        {
            return (0, []);
        }

        if (cellCount > MaxScoreCells)
        {
            // A long pasted name can make scoring every alignment too expensive for the UI thread.
            return this.ScoreLongMatch(target, folded);
        }

        // Keep the previous score and consecutive run length for each column, plus one traceback bit per cell.
        var traceLength = (int)((cellCount + 63) / 64);
        using var traceBuffer = new ScratchBuffer<ulong>(traceLength, stackalloc ulong[64]);
        using var rowBuffer = new ScratchBuffer<int>(targetLength * 2, stackalloc int[128]);
        var trace = traceBuffer.Span;
        var scores = rowBuffer.Span[..targetLength];
        var runs = rowBuffer.Span[targetLength..];
        trace.Clear();
        rowBuffer.Span.Clear();

        for (var qi = 0; qi < queryLength; qi++)
        {
            var leftScore = 0;
            var diagonalScore = 0;
            var diagonalRun = 0;
            var rowStart = qi * targetLength;

            for (var ti = 0; ti < targetLength; ti++)
            {
                var aboveScore = scores[ti];
                var aboveRun = runs[ti];
                var score = (diagonalScore != 0 || qi == 0) && this._foldedQuery[qi] == folded[ti]
                    ? ScoreCharacter(this._query[qi], target, ti, diagonalRun)
                    : 0;

                if (score != 0 && diagonalScore + score >= leftScore)
                {
                    scores[ti] = diagonalScore + score;
                    runs[ti] = diagonalRun + 1;
                    var cell = rowStart + ti;
                    trace[cell / 64] |= 1UL << (cell % 64);
                }
                else
                {
                    scores[ti] = leftScore;
                    runs[ti] = 0;
                }

                leftScore = scores[ti];
                diagonalScore = aboveScore;
                diagonalRun = aboveRun;
            }

            if (leftScore == 0)
            {
                return (0, []);
            }
        }

        var positions = new int[queryLength];
        var queryIndex = queryLength - 1;
        for (var ti = targetLength - 1; ti >= 0; ti--)
        {
            var cell = queryIndex * targetLength + ti;
            if ((trace[cell / 64] & (1UL << (cell % 64))) == 0)
            {
                continue;
            }

            positions[queryIndex] = ti;
            if (queryIndex == 0)
            {
                return (scores[^1], positions);
            }

            queryIndex--;
        }

        return (0, []);
    }

    private bool CanMatch(ReadOnlySpan<char> target)
    {
        var queryIndex = 0;
        foreach (var character in target)
        {
            if (this._foldedQuery[queryIndex] == character && ++queryIndex == this._foldedQuery.Length)
            {
                return true;
            }
        }

        return false;
    }

    private (int Score, int[] Positions) ScoreLongMatch(string target, ReadOnlySpan<char> folded)
    {
        var positions = new int[this._query.Length];
        long score = 0;
        // shortcut: Score the first contiguous run or greedy subsequence. Revisit if long names need optimal ranking.
        var next = Math.Max(0, folded.IndexOf(this._foldedQuery.AsSpan()));
        var run = 0;
        for (var i = 0; i < positions.Length; i++)
        {
            var position = next + folded[next..].IndexOf(this._foldedQuery[i]);
            if (position != next)
            {
                run = 0;
            }

            positions[i] = position;
            score += ScoreCharacter(this._query[i], target, position, run++);
            next = position + 1;
        }

        return ((int)Math.Min(score, int.MaxValue), positions);
    }

    private static int ScoreCharacter(char query, string target, int index, int run)
    {
        var character = target[index];
        var score = 1 + run * 5 + (query == character ? 1 : 0);
        if (index == 0)
        {
            return score + 8;
        }

        return score + (target[index - 1] switch
        {
            '/' or '\\' => 5,
            '_' or '-' or '.' or ' ' or '\'' or '"' or ':' => 4,
            _ => run == 0 && char.IsUpper(character) ? 2 : 0
        });
    }

    private static void Fold(ReadOnlySpan<char> source, Span<char> destination, bool foldSlashes, bool foldDiacritics)
    {
        for (var i = 0; i < source.Length; i++)
        {
            var upper = foldSlashes && source[i] == '\\' ? '/' : char.ToUpperInvariant(source[i]);
            destination[i] = foldDiacritics && upper > 0x7f && !char.IsSurrogate(upper) ? FoldDiacritics(upper) : upper;
        }
    }

    private static char FoldDiacritics(char upper)
    {
        var cache = FoldedCharacters.Value;
        var cached = cache[upper];
        if (cached != '\0')
        {
            return cached;
        }

        var letter = FirstNonMark(upper, NormalizationForm.FormD);
        if (letter == '\0' || letter == upper)
        {
            letter = FirstNonMark(upper, NormalizationForm.FormKD);
        }

        var folded = char.ToUpperInvariant(letter == '\0' ? upper : letter);
        cache[upper] = folded;
        return folded;

        static char FirstNonMark(char character, NormalizationForm form)
        {
            foreach (var candidate in character.ToString().Normalize(form))
            {
                if (CharUnicodeInfo.GetUnicodeCategory(candidate) is not
                    (UnicodeCategory.NonSpacingMark or UnicodeCategory.SpacingCombiningMark or UnicodeCategory.EnclosingMark))
                {
                    return candidate;
                }
            }

            return '\0';
        }
    }

    private readonly ref struct ScratchBuffer<T> where T : unmanaged
    {
        private readonly T[]? _rented;

        internal Span<T> Span { get; }

        internal ScratchBuffer(int length, Span<T> stackStorage)
        {
            if (length > stackStorage.Length)
            {
                this._rented = ArrayPool<T>.Shared.Rent(length);
                this.Span = this._rented.AsSpan(0, length);
            }
            else
            {
                this._rented = null;
                this.Span = stackStorage[..length];
            }
        }

        public void Dispose()
        {
            if (this._rented is { } rented)
            {
                ArrayPool<T>.Shared.Return(rented);
            }
        }
    }
}
