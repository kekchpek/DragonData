using System;

namespace Kekchpek.DragonData
{
    /// <summary>A read-write handle to a DragonData entry and its change notifications.</summary>
    public readonly struct Mutable<T> where T : unmanaged
    {
        private readonly Subcontainer<T> _subcontainer;
        private readonly string _dataName;
        private readonly T _defaultValue;

        internal Mutable(Subcontainer<T> subcontainer, string dataName, in T defaultValue)
        {
            _subcontainer = subcontainer;
            _dataName = dataName;
            _defaultValue = defaultValue;
        }

        private Subcontainer<T> Subcontainer => _subcontainer ?? throw new InvalidOperationException(
            "Get a DragonData handle through GetValue before using it.");

        /// <summary>Runs after a change handler is bound to this entry.</summary>
        public event Action OnAnySubscription
        {
            add => Subcontainer.AddSubscriptionObserver(_dataName, value, cleared: false);
            remove => Subcontainer.RemoveSubscriptionObserver(_dataName, value, cleared: false);
        }

        /// <summary>Runs after the last change handler is removed from this entry.</summary>
        public event Action OnSubscriptionsCleared
        {
            add => Subcontainer.AddSubscriptionObserver(_dataName, value, cleared: true);
            remove => Subcontainer.RemoveSubscriptionObserver(_dataName, value, cleared: true);
        }

        public T Value
        {
            get => Subcontainer.Get(_dataName, in _defaultValue);
            set => Subcontainer.Set(_dataName, in _defaultValue, in value, force: false);
        }

        public static implicit operator Bindable<T>(Mutable<T> mutable)
            => new Bindable<T>(mutable._subcontainer, mutable._dataName, in mutable._defaultValue);

        /// <summary>Sets the value and notifies handlers if it changes.</summary>
        public void Set(T value) => Subcontainer.Set(_dataName, in _defaultValue, in value, force: false);

        /// <summary>Sets the value and notifies handlers even if it compares equal.</summary>
        public void ForceSet(T value) => Subcontainer.Set(_dataName, in _defaultValue, in value, force: true);

        /// <summary>Copies the source's current value and follows subsequent changes.</summary>
        public void Proxy(Bindable<T> source) => Subcontainer.Proxy(_dataName, in _defaultValue, source);

        /// <summary>Stops following a proxied source and keeps the most recent value.</summary>
        public void StopProxying() => Subcontainer.StopProxying(_dataName);

        public void Bind(Action handler, bool callImmediately = true)
            => Subcontainer.Bind(_dataName, in _defaultValue, handler, callImmediately);

        public void Bind(ChangeHandler<T> handler, bool callImmediately = true)
            => Subcontainer.Bind(_dataName, in _defaultValue, handler, callImmediately);

        public void Bind(Action<T, T> handler) => Subcontainer.Bind(_dataName, in _defaultValue, handler);

        public void Bind(RefChangeHandler<T> handler, bool callImmediately = true)
            => Subcontainer.Bind(_dataName, in _defaultValue, handler, callImmediately);

        public void Unbind(Action handler) => Subcontainer.Unbind(_dataName, handler);

        public void Unbind(ChangeHandler<T> handler) => Subcontainer.Unbind(_dataName, handler);

        public void Unbind(Action<T, T> handler) => Subcontainer.Unbind(_dataName, handler);

        public void Unbind(RefChangeHandler<T> handler) => Subcontainer.Unbind(_dataName, handler);

        internal void BindAction(Action<T> handler, bool callImmediately)
            => Subcontainer.BindAction(_dataName, in _defaultValue, handler, callImmediately);

        internal void UnbindAction(Action<T> handler) => Subcontainer.UnbindAction(_dataName, handler);
    }
}
