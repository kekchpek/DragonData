using System;
using System.Collections.Generic;

namespace Kekchpek.DragonData
{
    internal sealed class HandlersCollection<T> where T : Delegate
    {
        private struct Slot
        {
            internal T Handler;
            internal int Previous;
            internal int Next;
            internal int PreviousMatch;
        }

        private readonly Dictionary<Delegate, int> _latest = new Dictionary<Delegate, int>();
        private Slot[] _slots = Array.Empty<Slot>();

        // Slot numbers start at one, so zero can terminate both kinds of lists.
        private int _first;
        private int _last;
        private int _used;
        private int _firstFree;
        private int _firstRemoved;
        private int _enumerationDepth;

        internal bool IsEmpty => _latest.Count == 0;

        internal void Add(T handler, Delegate identity = null)
        {
            if (handler == null)
                throw new ArgumentNullException(nameof(handler));

            identity ??= handler;
            _latest.TryGetValue(identity, out var previousMatch);
            var index = RentSlot();
            _latest[identity] = index;
            _slots[index - 1] = new Slot
            {
                Handler = handler,
                Previous = _last,
                PreviousMatch = previousMatch
            };

            if (_last != 0)
                _slots[_last - 1].Next = index;
            else
                _first = index;
            _last = index;
        }

        internal bool Remove(Delegate handler)
        {
            if (handler == null || !_latest.TryGetValue(handler, out var index))
                return false;

            ref var slot = ref _slots[index - 1];
            if (slot.PreviousMatch != 0)
                _latest[handler] = slot.PreviousMatch;
            else
                _latest.Remove(handler);

            if (_enumerationDepth == 0)
                ReturnSlot(index);
            else
            {
                // Preserve links and the dispatch endpoint until the last traversal exits.
                slot.Handler = null;
                slot.PreviousMatch = _firstRemoved;
                _firstRemoved = index;
            }

            return true;
        }

        public Enumerator GetEnumerator() => new Enumerator(this);

        private int RentSlot()
        {
            if (_firstFree != 0)
            {
                var index = _firstFree;
                _firstFree = _slots[index - 1].Next;
                return index;
            }

            if (_used == _slots.Length)
                Array.Resize(ref _slots, _used == 0 ? 4 : checked(_used * 2));
            return ++_used;
        }

        private void ReturnSlot(int index)
        {
            ref var slot = ref _slots[index - 1];
            if (slot.Previous != 0)
                _slots[slot.Previous - 1].Next = slot.Next;
            else
                _first = slot.Next;

            if (slot.Next != 0)
                _slots[slot.Next - 1].Previous = slot.Previous;
            else
                _last = slot.Previous;

            slot = new Slot { Next = _firstFree };
            _firstFree = index;
        }

        private void EndEnumeration()
        {
            if (--_enumerationDepth != 0)
                return;

            while (_firstRemoved != 0)
            {
                var index = _firstRemoved;
                _firstRemoved = _slots[index - 1].PreviousMatch;
                ReturnSlot(index);
            }
        }

        internal struct Enumerator : IDisposable
        {
            private HandlersCollection<T> _owner;
            private readonly int _last;
            private int _next;

            internal Enumerator(HandlersCollection<T> owner)
            {
                _owner = owner;
                _last = owner._last;
                _next = owner._first;
                Current = null;
                owner._enumerationDepth++;
            }

            public T Current { get; private set; }

            public bool MoveNext()
            {
                while (_next != 0)
                {
                    var index = _next;
                    ref var slot = ref _owner._slots[index - 1];
                    _next = index == _last ? 0 : slot.Next;
                    Current = slot.Handler;
                    if (Current != null)
                        return true;
                }

                Current = null;
                return false;
            }

            public void Dispose()
            {
                _owner?.EndEnumeration();
                _owner = null;
                _next = 0;
                Current = null;
            }
        }
    }
}
