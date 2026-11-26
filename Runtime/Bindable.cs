using System;

namespace Kekchpek.DragonData
{
    /// <summary>Receives the current value of a DragonData entry.</summary>
    public delegate void ChangeHandler<T>(T value) where T : unmanaged;

    /// <summary>Receives the current value by readonly reference during a notification.</summary>
    public delegate void RefChangeHandler<T>(in T value) where T : unmanaged;

    /// <summary>A read-only handle to a DragonData entry and its change notifications.</summary>
    public readonly struct Bindable<T> where T : unmanaged
    {
        private readonly Subcontainer<T> _subcontainer;
        private readonly string _dataName;
        private readonly T _defaultValue;

        internal Bindable(Subcontainer<T> subcontainer, string dataName, in T defaultValue)
        {
            _subcontainer = subcontainer;
            _dataName = dataName;
            _defaultValue = defaultValue;
        }

        internal Subcontainer<T> Subcontainer => _subcontainer ?? throw new InvalidOperationException(
            "Get a DragonData handle through GetValue before using it.");

        internal string Name => _dataName;

        public T Value => Subcontainer.Get(_dataName, in _defaultValue);

        public void Bind(Action handler, bool callImmediately = true)
            => Subcontainer.Bind(_dataName, in _defaultValue, handler, callImmediately);

        public void Bind(ChangeHandler<T> handler, bool callImmediately = true)
            => Subcontainer.Bind(_dataName, in _defaultValue, handler, callImmediately);

        public void Bind(Action<T, T> handler)
            => Subcontainer.Bind(_dataName, in _defaultValue, handler);

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
