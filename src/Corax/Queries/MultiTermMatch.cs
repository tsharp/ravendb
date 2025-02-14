using System;
using System.Buffers;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Corax.Mappings;
using Corax.Utils;
using Sparrow.Server;
using Sparrow.Server.Utils;

namespace Corax.Queries
{
    [DebuggerDisplay("{DebugView,nq}")]
    public unsafe struct MultiTermMatch<TTermProvider> : IQueryMatch
        where TTermProvider : ITermProvider
    {

        private const int InitialFrequencyHolders = 64;
        
        private readonly bool _isBoosting;
        private long _totalResults;
        private long _current;
        private long _currentIdx;
        private QueryCountConfidence _confidence;

        private Bm25Relevance[] _frequenciesHolder;
        private int _currentFreqIdx;
        private int FrequenciesHolderSize => _frequenciesHolder?.Length ?? 0;
       
        
        private TTermProvider _inner;
        private TermMatch _currentTerm;        
        private readonly ByteStringContext _context;

        public bool IsBoosting => _isBoosting;
        public long Count => _totalResults;
        public long Current => _currentIdx <= QueryMatch.Start ? _currentIdx : _current;

        public QueryCountConfidence Confidence => _confidence;

        public MultiTermMatch(in FieldMetadata field, ByteStringContext context, TTermProvider inner, long totalResults = 0, QueryCountConfidence confidence = QueryCountConfidence.Low)
        {
            _inner = inner;
            _context = context;
            _current = QueryMatch.Start;
            _currentIdx = QueryMatch.Start;
            _totalResults = totalResults;
            _confidence = confidence;
            var result = _inner.Next(out _currentTerm);
            if (result == false)
                _current = QueryMatch.Invalid;
            
            _isBoosting = field.HasBoost;
            if (_isBoosting)
            {
                var pool = Bm25Relevance.RelevancePool ??= ArrayPool<Bm25Relevance>.Create();
                _frequenciesHolder = pool.Rent(InitialFrequencyHolders);
            }
        }

#if !DEBUG
        [SkipLocalsInit]
#endif
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public int Fill(Span<long> buffer)
        {
            if (_current == QueryMatch.Invalid)
                return 0;

            int count = 0;
            var bufferState = buffer;
            bool requiresSort = false;
            while (bufferState.Length > 0)
            {
                var read = _currentTerm.Fill(bufferState);
                
                if (read == 0)
                {
                    AddTermToBm25();
                    if (_inner.Next(out _currentTerm) == false)
                    {
                        _current = QueryMatch.Invalid;
                        goto End;
                    }
                    // We can prove that we need sorting and deduplication in the end. 
                    requiresSort |= count != buffer.Length;
                }

                count += read;
                bufferState = bufferState.Slice(read);
            }

            _current = count != 0 ? buffer[count - 1] : QueryMatch.Invalid;

        End:
            if (requiresSort && count > 1)
            {
                count = Sorting.SortAndRemoveDuplicates(buffer[0..count]);                         
            }

            return count;
        }

        private void UnlikelyGrowBufferOfTermMatches()
        {
            var pool = Bm25Relevance.RelevancePool;
            var handler = pool.Rent(2 * FrequenciesHolderSize);
            _frequenciesHolder.CopyTo(handler.AsSpan(0, _currentFreqIdx));
            
            pool.Return(_frequenciesHolder);
            _frequenciesHolder = handler;
        }
        
#if !DEBUG
        [SkipLocalsInit]
#endif
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public int AndWith(Span<long> buffer, int matches)
        {
            // We should consider the policy where it makes sense to actually implement something different to avoid
            // the N^2 check here. While could be interesting to do it now, we still dont know what are the conditions
            // that would trigger such optimization or if they are even necessary. The reason why is that buffer size
            // may offset some of the requirements for such a scan operation. Interesting approaches to consider include
            // evaluating directly, construct temporary data structures like bloom filters on subsequent iterations when
            // the statistics guarantee those approaches, etc. Currently we apply memoization but without any limit to 
            // size of the result and it's subsequent usage of memory. 

            using var _ = _context.Allocate(3 * sizeof(long) * buffer.Length, out var bufferHolder);
            var longBuffer = MemoryMarshal.Cast<byte, long>(bufferHolder.ToSpan());

            // PERF: We want to avoid to share cache lines, that's why the third array will move toward the end of the array. 
            Span<long> results = longBuffer.Slice(0, buffer.Length);
            Span<long> tmp = longBuffer.Slice(buffer.Length, buffer.Length);
            Span<long> tmp2 = longBuffer.Slice(2 * buffer.Length, buffer.Length);

            _inner.Reset();
            ResetBm25Buffer();
            
            var actualMatches = buffer.Slice(0, matches);

            bool hasData = _inner.Next(out _currentTerm);
            AddTermToBm25();

            long totalRead = _currentTerm.Count;

            int totalSize = 0;
            while (totalSize < buffer.Length && hasData)
            {
                actualMatches.CopyTo(tmp);
                var read = _currentTerm.AndWith(tmp, matches);
                if (read != 0)
                {
                    results[0..totalSize].CopyTo(tmp2);
                    totalSize = MergeHelper.Or(results, tmp2[0..totalSize], tmp[0..read]);
                    totalRead += _currentTerm.Count;
                }
                hasData = _inner.Next(out _currentTerm);
                AddTermToBm25();
            }

            // We will check if we can make a better decision next time. 
            if (!hasData)
            {
                _totalResults = totalRead;
                _confidence = QueryCountConfidence.High;
            }
            
            results[0..totalSize].CopyTo(buffer);

            return totalSize;
        }

        private void ResetBm25Buffer()
        {
            for (int idX = 0; idX < _currentFreqIdx; ++idX)
            {
                _frequenciesHolder[idX].Dispose();
                _frequenciesHolder[idX] = null;
            }
            _currentFreqIdx = 0;
        }

        private void AddTermToBm25()
        {
            if (_isBoosting && _currentTerm.Count != 0)
            {
                if (_currentFreqIdx >= FrequenciesHolderSize)
                    UnlikelyGrowBufferOfTermMatches();
                _frequenciesHolder[_currentFreqIdx] = _currentTerm._bm25Relevance;
                _currentFreqIdx += 1;
            }
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void Score(Span<long> matches, Span<float> scores, float boostFactor)
        {
            if (_isBoosting == false)
                return;
            
            //We've to gather all data from already seen TermMatches to get proper ranking :)
            for (int idX = 0; idX < _currentFreqIdx; ++idX)
            {
                ref var currentRelevance = ref _frequenciesHolder[idX];
                currentRelevance.Score(matches, scores, boostFactor);
                currentRelevance.Dispose();
                currentRelevance = null;
            }
            
            Bm25Relevance.RelevancePool.Return(_frequenciesHolder);
            _frequenciesHolder = null;
            _currentFreqIdx = 0;
        }

        public QueryInspectionNode Inspect()
        {
            return new QueryInspectionNode(nameof(MultiTermMatch<TTermProvider>),
                children: new List<QueryInspectionNode> { _inner.Inspect() },
                parameters: new Dictionary<string, string>()
                {
                    { nameof(IsBoosting), IsBoosting.ToString() },
                    { nameof(Count), $"{Count} [{Confidence}]" }
                });
        }

        public string DebugView => Inspect().ToString();
    }
}
