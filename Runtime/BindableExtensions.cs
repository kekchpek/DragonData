using System;

namespace Kekchpek.DragonData
{
    /// <summary>Extends DragonData handles with typed callbacks and computed values.</summary>
    public static class BindableExtensions
    {
        public static void Bind<T>(this Bindable<T> value, Action<T> handler, bool callImmediately = true)
            where T : unmanaged => value.BindAction(handler, callImmediately);

        public static void Unbind<T>(this Bindable<T> value, Action<T> handler)
            where T : unmanaged => value.UnbindAction(handler);

        public static void Bind<T>(this Mutable<T> value, Action<T> handler, bool callImmediately = true)
            where T : unmanaged => value.BindAction(handler, callImmediately);

        public static void Unbind<T>(this Mutable<T> value, Action<T> handler)
            where T : unmanaged => value.UnbindAction(handler);

        /// <summary>Creates a read-only value that applies a converter to this source.</summary>
        public static Bindable<TResult> ConvertTo<TResult, TSource>(this Bindable<TSource> source,
            Func<TSource, TResult> converter) where TResult : unmanaged where TSource : unmanaged
        {
            if (converter == null)
                throw new ArgumentNullException(nameof(converter));

            return FunctionComputedSource<TResult>.Create(() => converter(source.Value), ComputedDependency.From(source));
        }

        public static Bindable<TResult> ConvertTo<TResult, TSource>(this Mutable<TSource> source,
            Func<TSource, TResult> converter) where TResult : unmanaged where TSource : unmanaged
            => ((Bindable<TSource>)source).ConvertTo(converter);

        public static Bindable<TResult> AggregateWith<T1, T2, TResult>(this Bindable<T1> first, Bindable<T2> second,
            Func<T1, T2, TResult> aggregator) where T1 : unmanaged where T2 : unmanaged where TResult : unmanaged
            => Bindable.Aggregate(first, second, aggregator);

        public static Bindable<TResult> AggregateWith<T1, T2, TResult>(this Mutable<T1> first, Bindable<T2> second,
            Func<T1, T2, TResult> aggregator) where T1 : unmanaged where T2 : unmanaged where TResult : unmanaged
            => Bindable.Aggregate(first, second, aggregator);

        public static Bindable<TResult> AggregateWith<T1, T2, TResult>(this Bindable<T1> first, Mutable<T2> second,
            Func<T1, T2, TResult> aggregator) where T1 : unmanaged where T2 : unmanaged where TResult : unmanaged
            => Bindable.Aggregate(first, second, aggregator);

        public static Bindable<TResult> AggregateWith<T1, T2, TResult>(this Mutable<T1> first, Mutable<T2> second,
            Func<T1, T2, TResult> aggregator) where T1 : unmanaged where T2 : unmanaged where TResult : unmanaged
            => Bindable.Aggregate(first, second, aggregator);

        /// <summary>Follows a source after converting each value to the destination type.</summary>
        public static void Proxy<T, TSource>(this Mutable<T> target, Bindable<TSource> source,
            Func<TSource, T> converter) where T : unmanaged where TSource : unmanaged
            => target.Proxy(source.ConvertTo(converter));

        public static void Proxy<T, TSource>(this Mutable<T> target, Mutable<TSource> source,
            Func<TSource, T> converter) where T : unmanaged where TSource : unmanaged
            => target.Proxy(((Bindable<TSource>)source).ConvertTo(converter));
    }
}
