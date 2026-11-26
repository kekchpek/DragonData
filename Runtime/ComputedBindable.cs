using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.ExceptionServices;

namespace Kekchpek.DragonData
{
    /// <summary>Creates read-only values computed from existing DragonData entries.</summary>
    public static class Bindable
    {
        public static Bindable<TResult> Aggregate<T1, T2, TResult>(
            Bindable<T1> first, Bindable<T2> second, Func<T1, T2, TResult> aggregator)
            where T1 : unmanaged where T2 : unmanaged where TResult : unmanaged
        {
            if (aggregator == null)
                throw new ArgumentNullException(nameof(aggregator));

            return FunctionComputedSource<TResult>.Create(() => aggregator(first.Value, second.Value),
                ComputedDependency.From(first), ComputedDependency.From(second));
        }

        public static Bindable<TResult> Aggregate<T1, T2, TResult>(
            Mutable<T1> first, Mutable<T2> second, Func<T1, T2, TResult> aggregator)
            where T1 : unmanaged where T2 : unmanaged where TResult : unmanaged
            => Aggregate((Bindable<T1>)first, (Bindable<T2>)second, aggregator);

        public static Bindable<TResult> Aggregate<T1, T2, TResult>(
            Mutable<T1> first, Bindable<T2> second, Func<T1, T2, TResult> aggregator)
            where T1 : unmanaged where T2 : unmanaged where TResult : unmanaged
            => Aggregate((Bindable<T1>)first, second, aggregator);

        public static Bindable<TResult> Aggregate<T1, T2, TResult>(
            Bindable<T1> first, Mutable<T2> second, Func<T1, T2, TResult> aggregator)
            where T1 : unmanaged where T2 : unmanaged where TResult : unmanaged
            => Aggregate(first, (Bindable<T2>)second, aggregator);

        public static Bindable<TResult> Aggregate<T1, T2, T3, TResult>(
            Bindable<T1> first, Bindable<T2> second, Bindable<T3> third, Func<T1, T2, T3, TResult> aggregator)
            where T1 : unmanaged where T2 : unmanaged where T3 : unmanaged where TResult : unmanaged
        {
            if (aggregator == null)
                throw new ArgumentNullException(nameof(aggregator));

            return FunctionComputedSource<TResult>.Create(() => aggregator(first.Value, second.Value, third.Value),
                ComputedDependency.From(first), ComputedDependency.From(second), ComputedDependency.From(third));
        }

        public static Bindable<TResult> Aggregate<T1, T2, T3, TResult>(
            Mutable<T1> first, Mutable<T2> second, Mutable<T3> third, Func<T1, T2, T3, TResult> aggregator)
            where T1 : unmanaged where T2 : unmanaged where T3 : unmanaged where TResult : unmanaged
            => Aggregate((Bindable<T1>)first, (Bindable<T2>)second, (Bindable<T3>)third, aggregator);

        public static Bindable<TResult> Aggregate<T1, T2, T3, T4, TResult>(Bindable<T1> first, Bindable<T2> second,
            Bindable<T3> third, Bindable<T4> fourth, Func<T1, T2, T3, T4, TResult> aggregator)
            where T1 : unmanaged where T2 : unmanaged where T3 : unmanaged where T4 : unmanaged where TResult : unmanaged
        {
            if (aggregator == null)
                throw new ArgumentNullException(nameof(aggregator));

            return FunctionComputedSource<TResult>.Create(
                () => aggregator(first.Value, second.Value, third.Value, fourth.Value), ComputedDependency.From(first),
                ComputedDependency.From(second), ComputedDependency.From(third), ComputedDependency.From(fourth));
        }

        public static Bindable<TResult> Aggregate<T1, T2, T3, T4, TResult>(Mutable<T1> first, Mutable<T2> second,
            Mutable<T3> third, Mutable<T4> fourth, Func<T1, T2, T3, T4, TResult> aggregator)
            where T1 : unmanaged where T2 : unmanaged where T3 : unmanaged where T4 : unmanaged where TResult : unmanaged
            => Aggregate((Bindable<T1>)first, (Bindable<T2>)second, (Bindable<T3>)third, (Bindable<T4>)fourth, aggregator);

        /// <summary>Computes a value from a fixed list of sources and passes a fresh value array to the aggregator.</summary>
        public static Bindable<TResult> Aggregate<T, TResult>(
            IEnumerable<Bindable<T>> bindableValues, Func<T[], TResult> aggregator)
            where T : unmanaged where TResult : unmanaged
        {
            if (bindableValues == null)
                throw new ArgumentNullException(nameof(bindableValues));
            if (aggregator == null)
                throw new ArgumentNullException(nameof(aggregator));

            var sources = bindableValues.ToArray();
            var dependencies = new ComputedDependency[sources.Length];
            for (var index = 0; index < sources.Length; index++)
                dependencies[index] = ComputedDependency.From(sources[index]);

            return FunctionComputedSource<TResult>.Create(() =>
            {
                var values = new T[sources.Length];
                for (var index = 0; index < sources.Length; index++)
                    values[index] = sources[index].Value;
                return aggregator(values);
            }, dependencies);
        }

        public static Bindable<TResult> Aggregate<T, TResult>(
            IEnumerable<Mutable<T>> bindableValues, Func<T[], TResult> aggregator)
            where T : unmanaged where TResult : unmanaged
        {
            if (bindableValues == null)
                throw new ArgumentNullException(nameof(bindableValues));

            return Aggregate(bindableValues.Select(static value => (Bindable<T>)value), aggregator);
        }
    }

    internal abstract class ComputedSource<T> where T : unmanaged
    {
        internal const string DataName = "value";

        private readonly ComputedDependency[] _dependencies;

        protected ComputedSource(ComputedDependency[] dependencies) => _dependencies = dependencies;

        internal abstract T Evaluate();

        internal void Connect(Action handler)
        {
            foreach (var dependency in _dependencies)
                dependency.Connect(handler);
        }

        internal void Disconnect(Action handler)
        {
            ExceptionDispatchInfo firstError = null;
            foreach (var dependency in _dependencies)
            {
                try
                {
                    dependency.Disconnect(handler);
                }
                catch (Exception error)
                {
                    // A subscription observer may throw after removal. Still detach every dependency.
                    firstError ??= ExceptionDispatchInfo.Capture(error);
                }
            }
            firstError?.Throw();
        }

        internal bool DependsOn(Subcontainer storage, string name)
        {
            foreach (var dependency in _dependencies)
            {
                if (dependency.DependsOn(storage, name))
                    return true;
            }
            return false;
        }
    }

    internal sealed class FunctionComputedSource<T> : ComputedSource<T> where T : unmanaged
    {
        private readonly Func<T> _evaluate;

        private FunctionComputedSource(Func<T> evaluate, ComputedDependency[] dependencies) : base(dependencies)
            => _evaluate = evaluate;

        internal static Bindable<T> Create(Func<T> evaluate, params ComputedDependency[] dependencies)
            => new Bindable<T>(new Subcontainer<T>(new FunctionComputedSource<T>(evaluate, dependencies)), DataName,
                default);

        internal override T Evaluate() => _evaluate();
    }

    internal readonly struct ComputedDependency
    {
        private readonly Subcontainer _storage;
        private readonly string _name;

        private ComputedDependency(Subcontainer storage, string name)
        {
            _storage = storage;
            _name = name;
        }

        internal static ComputedDependency From<T>(Bindable<T> value) where T : unmanaged
            => new ComputedDependency(value.Subcontainer, value.Name);

        internal void Connect(Action handler) => _storage.ConnectComputed(_name, handler);

        internal void Disconnect(Action handler) => _storage.DisconnectComputed(_name, handler);

        internal bool DependsOn(Subcontainer storage, string name) => _storage.DependsOn(_name, storage, name);
    }
}
