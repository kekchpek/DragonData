using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using UnityEngine.Scripting;

namespace Kekchpek.DragonData
{
    internal abstract class Subcontainer
    {
        internal static readonly Encoding TextEncoding = new UTF8Encoding(false, true);
        internal abstract Type DataType { get; }
        internal abstract TypeMetadata Metadata { get; }
        internal abstract int Count { get; }
        internal abstract void Read(BinaryReader reader);
        internal abstract void Write(BinaryWriter writer);
        internal abstract void ReplaceData(Subcontainer replacement);
        internal abstract void MergeFrom(Subcontainer incoming);
        internal abstract bool DependsOn(string name, Subcontainer target, string targetName);
        internal abstract void ConnectComputed(string name, Action handler);
        internal abstract void DisconnectComputed(string name, Action handler);

        internal void Save(Stream stream)
        {
            using var writer = new BinaryWriter(stream, TextEncoding, leaveOpen: true);
            Write(writer);
        }
    }

    internal sealed class Subcontainer<T> : Subcontainer where T : unmanaged
    {
        private static readonly EqualityComparer<T> ValueComparer = EqualityComparer<T>.Default;

        private readonly ComputedBinding computed;
        private Dictionary<string, int> indexes;
        private T[] values = Array.Empty<T>();
        private Dictionary<string, HandlersCollection<Action>> blindHandlers;
        private Dictionary<string, HandlersCollection<ChangeHandler<T>>> changeHandlers;
        private Dictionary<string, HandlersCollection<Action<T, T>>> fullHandlers;
        private Dictionary<string, HandlersCollection<RefChangeHandler<T>>> refHandlers;
        private Dictionary<string, Action> subscriptionObservers;
        private Dictionary<string, Action> clearedObservers;
        private Dictionary<string, ProxyBinding> proxies;

        [Preserve]
        public Subcontainer() { }

        internal Subcontainer(ComputedSource<T> source)
        {
            computed = new ComputedBinding(this, source);
        }

        internal override Type DataType => typeof(T);
        internal override TypeMetadata Metadata => TypeMetadata.For<T>();
        internal override int Count => indexes?.Count ?? 0;

        internal T Get(string name, in T defaultValue)
        {
            if (computed != null)
                return computed.Source.Evaluate();
            var index = GetIndex(name, in defaultValue);
            return values[index];
        }

        internal void Set(string name, in T defaultValue, in T value, bool force)
        {
            StopProxying(name);
            SetCore(name, in defaultValue, in value, force);
        }

        internal void Bind(string name, in T defaultValue, Action handler, bool callImmediately)
        {
            AddHandler(ref blindHandlers, name, handler);
            if (callImmediately)
                handler();
            NotifyObserver(subscriptionObservers, name);
        }

        internal void Bind(string name, in T defaultValue, ChangeHandler<T> handler, bool callImmediately)
            => BindChange(name, in defaultValue, handler, callImmediately, identity: null);

        internal void BindAction(string name, in T defaultValue, Action<T> handler, bool callImmediately)
        {
            if (handler == null)
                throw new ArgumentNullException(nameof(handler));
            BindChange(name, in defaultValue, handler.Invoke, callImmediately, handler);
        }

        private void BindChange(string name, in T defaultValue, ChangeHandler<T> handler, bool callImmediately,
            Delegate identity)
        {
            if (handler == null)
                throw new ArgumentNullException(nameof(handler));
            if (callImmediately)
                handler(Get(name, in defaultValue));
            AddHandler(ref changeHandlers, name, handler, identity);
            NotifyObserver(subscriptionObservers, name);
        }

        internal void Bind(string name, in T defaultValue, Action<T, T> handler)
        {
            AddHandler(ref fullHandlers, name, handler);
            NotifyObserver(subscriptionObservers, name);
        }

        internal void Bind(string name, in T defaultValue, RefChangeHandler<T> handler, bool callImmediately)
        {
            if (handler == null)
                throw new ArgumentNullException(nameof(handler));
            if (callImmediately)
            {
                var value = Get(name, in defaultValue);
                handler(in value);
            }
            AddHandler(ref refHandlers, name, handler);
            NotifyObserver(subscriptionObservers, name);
        }

        internal void Unbind(string name, Action handler) => RemoveHandler(blindHandlers, name, handler);
        internal void Unbind(string name, ChangeHandler<T> handler) => RemoveHandler(changeHandlers, name, handler);
        internal void Unbind(string name, Action<T, T> handler) => RemoveHandler(fullHandlers, name, handler);
        internal void Unbind(string name, RefChangeHandler<T> handler) => RemoveHandler(refHandlers, name, handler);
        internal void UnbindAction(string name, Action<T> handler) => RemoveHandler(changeHandlers, name, handler);

        internal override void ConnectComputed(string name, Action handler)
            => Bind(name, default, handler, callImmediately: false);

        internal override void DisconnectComputed(string name, Action handler) => Unbind(name, handler);

        internal void AddSubscriptionObserver(string name, Action handler, bool cleared)
        {
            if (handler == null)
                return;
            var observers = cleared
                ? clearedObservers ??= new Dictionary<string, Action>(StringComparer.Ordinal)
                : subscriptionObservers ??= new Dictionary<string, Action>(StringComparer.Ordinal);
            observers.TryGetValue(name, out var previous);
            observers[name] = previous + handler;
        }

        internal void RemoveSubscriptionObserver(string name, Action handler, bool cleared)
        {
            var observers = cleared ? clearedObservers : subscriptionObservers;
            if (observers == null || !observers.TryGetValue(name, out var previous))
                return;
            var remaining = previous - handler;
            if (remaining == null)
                observers.Remove(name);
            else
                observers[name] = remaining;
        }

        internal void Proxy(string name, in T defaultValue, Bindable<T> source)
        {
            ValidateSource(name, source);
            if (proxies != null && proxies.ContainsKey(name))
            {
                StopProxying(name);
                ValidateSource(name, source);
            }

            proxies ??= new Dictionary<string, ProxyBinding>(StringComparer.Ordinal);
            var binding = new ProxyBinding(this, name, in defaultValue, source);
            proxies.Add(name, binding);
            try
            {
                source.Bind(binding.Handler, callImmediately: false);
                binding.Apply(source.Value);
            }
            catch
            {
                if (proxies.TryGetValue(name, out var active) && ReferenceEquals(active, binding))
                    StopProxying(name);
                throw;
            }
        }

        internal override bool DependsOn(string name, Subcontainer target, string targetName)
        {
            if (ReferenceEquals(this, target) && name == targetName)
                return true;
            if (computed != null)
                return computed.Source.DependsOn(target, targetName);
            return proxies != null && proxies.TryGetValue(name, out var proxy) &&
                proxy.Source.Subcontainer.DependsOn(proxy.Source.Name, target, targetName);
        }

        private void ValidateSource(string name, Bindable<T> source)
        {
            if (source.Subcontainer.DependsOn(source.Name, this, name))
                throw new InvalidOperationException("A DragonData value cannot proxy itself or form a proxy cycle.");
        }

        internal void StopProxying(string name)
        {
            if (proxies == null || !proxies.TryGetValue(name, out var proxy))
                return;
            proxies.Remove(name);
            proxy.Source.Unbind(proxy.Handler);
        }

        internal override void Write(BinaryWriter writer)
        {
            var count = Count;
            writer.Write(count);
            if (count == 0)
                return;
            foreach (var pair in indexes)
            {
                writer.Write(pair.Key);
                writer.Write(pair.Value);
            }
            writer.BaseStream.Write(MemoryMarshal.AsBytes(values.AsSpan(0, count)));
        }

        internal override void Read(BinaryReader reader)
        {
            var count = reader.ReadInt32();
            var stream = reader.BaseStream;
            var dataBytes = (long)count * Metadata.Size;
            if (count < 0 || dataBytes > int.MaxValue || dataBytes + count * 5L > stream.Length - stream.Position)
                throw new InvalidDataException("Invalid DragonData value count.");
            if (count == 0)
                return;

            var incomingIndexes = new Dictionary<string, int>(count, StringComparer.Ordinal);
            var seenIndexes = new bool[count];
            var mapEnd = stream.Length - dataBytes;
            for (var index = 0; index < count; index++)
            {
                var key = reader.ReadString();
                var valueIndex = reader.ReadInt32();
                if (stream.Position > mapEnd || (uint)valueIndex >= (uint)count ||
                    seenIndexes[valueIndex] || !incomingIndexes.TryAdd(key, valueIndex))
                    throw new InvalidDataException("Invalid DragonData value mapping.");
                seenIndexes[valueIndex] = true;
            }

            var incomingValues = new T[count];
            var bytes = MemoryMarshal.AsBytes(incomingValues.AsSpan());
            while (!bytes.IsEmpty)
            {
                var read = stream.Read(bytes);
                if (read == 0)
                    throw new EndOfStreamException("Incomplete DragonData value array.");
                bytes = bytes.Slice(read);
            }
            indexes = incomingIndexes;
            values = incomingValues;
        }

        internal override void ReplaceData(Subcontainer replacement)
        {
            var typed = (Subcontainer<T>)replacement;
            indexes = typed?.indexes;
            values = typed?.values ?? Array.Empty<T>();
        }

        internal override void MergeFrom(Subcontainer incoming)
        {
            var typed = (Subcontainer<T>)incoming;
            if (typed.indexes == null)
                return;
            foreach (var pair in typed.indexes)
            {
                if (indexes != null && indexes.ContainsKey(pair.Key))
                    throw new InvalidDataException($"Multiple DragonData values map to '{typeof(T).FullName}/{pair.Key}'.");
                GetIndex(pair.Key, in typed.values[pair.Value]);
            }
        }

        internal Subcontainer<TOut> Convert<TOut>(Func<T, TOut> converter) where TOut : unmanaged
        {
            var converted = new Subcontainer<TOut>();
            if (indexes == null)
                return converted;
            var result = new TOut[Count];
            for (var index = 0; index < result.Length; index++)
                result[index] = converter(values[index]);
            // The isolated source is discarded after migration. Its validated mapping belongs to the result.
            converted.indexes = indexes;
            converted.values = result;
            return converted;
        }

        private int GetIndex(string name, in T defaultValue)
        {
            indexes ??= new Dictionary<string, int>(StringComparer.Ordinal);
            if (indexes.TryGetValue(name, out var index))
                return index;
            index = indexes.Count;
            if (index == values.Length)
                Array.Resize(ref values, index == 0 ? 4 : checked(index * 2));
            indexes.Add(name, index);
            values[index] = defaultValue;
            return index;
        }

        private void SetCore(string name, in T defaultValue, in T value, bool force)
        {
            var index = GetIndex(name, in defaultValue);
            var previous = values[index];
            if (!force && ValueComparer.Equals(previous, value))
                return;
            values[index] = value;
            NotifyChange(name, in previous, in value);
        }

        private void NotifyChange(string name, in T previous, in T value)
        {
            if (blindHandlers != null && blindHandlers.TryGetValue(name, out var blind))
                foreach (var handler in blind)
                    handler();
            if (fullHandlers != null && fullHandlers.TryGetValue(name, out var full))
                foreach (var handler in full)
                    handler(previous, value);
            if (changeHandlers != null && changeHandlers.TryGetValue(name, out var change))
                foreach (var handler in change)
                    handler(value);
            if (refHandlers != null && refHandlers.TryGetValue(name, out var byRef))
                foreach (var handler in byRef)
                    handler(in value);
        }

        private void AddHandler<THandler>(
            ref Dictionary<string, HandlersCollection<THandler>> handlers, string name, THandler handler,
            Delegate identity = null)
            where THandler : Delegate
        {
            if (handler == null)
                throw new ArgumentNullException(nameof(handler));
            handlers ??= new Dictionary<string, HandlersCollection<THandler>>(StringComparer.Ordinal);
            if (!handlers.TryGetValue(name, out var collection))
            {
                collection = new HandlersCollection<THandler>();
                handlers.Add(name, collection);
            }
            collection.Add(handler, identity);
            if (computed == null)
                return;
            try
            {
                computed.Refresh();
            }
            catch
            {
                if (collection.Remove(identity ?? handler) && collection.IsEmpty &&
                    handlers.TryGetValue(name, out var registered) && ReferenceEquals(registered, collection))
                    handlers.Remove(name);
                throw;
            }
        }

        private void RemoveHandler<THandler>(
            Dictionary<string, HandlersCollection<THandler>> handlers, string name, Delegate handler)
            where THandler : Delegate
        {
            if (handler == null || handlers == null || !handlers.TryGetValue(name, out var collection) ||
                !collection.Remove(handler) || !collection.IsEmpty)
                return;
            handlers.Remove(name);
            if (!HasHandlers(name))
            {
                computed?.Refresh();
                NotifyObserver(clearedObservers, name);
            }
        }

        private bool HasHandlers(string name) => blindHandlers?.ContainsKey(name) == true ||
            fullHandlers?.ContainsKey(name) == true || changeHandlers?.ContainsKey(name) == true ||
            refHandlers?.ContainsKey(name) == true;

        private static void NotifyObserver(Dictionary<string, Action> observers, string name)
        {
            if (observers != null && observers.TryGetValue(name, out var handler))
                handler();
        }

        private sealed class ComputedBinding
        {
            private readonly Subcontainer<T> target;
            private Action onChanged;
            private T previous;
            private bool observing;
            private bool refreshing;

            internal readonly ComputedSource<T> Source;

            internal ComputedBinding(Subcontainer<T> target, ComputedSource<T> source)
            {
                this.target = target;
                Source = source;
            }

            internal void Refresh()
            {
                if (refreshing)
                    return;
                refreshing = true;
                try
                {
                    while (observing != target.HasHandlers(ComputedSource<T>.DataName))
                    {
                        if (observing)
                        {
                            observing = false;
                            Source.Disconnect(onChanged);
                            continue;
                        }

                        previous = Source.Evaluate();
                        onChanged ??= OnSourceChanged;
                        observing = true;
                        try
                        {
                            Source.Connect(onChanged);
                        }
                        catch
                        {
                            observing = false;
                            try
                            {
                                Source.Disconnect(onChanged);
                            }
                            catch (Exception)
                            {
                                // Disconnect has already attempted every dependency. Keep the setup error.
                            }
                            throw;
                        }
                    }
                }
                finally
                {
                    refreshing = false;
                }
            }

            private void OnSourceChanged()
            {
                if (!observing)
                    return;
                var before = previous;
                var after = Source.Evaluate();
                previous = after;
                target.NotifyChange(ComputedSource<T>.DataName, in before, in after);
            }
        }

        private sealed class ProxyBinding
        {
            private readonly Subcontainer<T> target;
            private readonly string name;
            private readonly T defaultValue;
            internal readonly Bindable<T> Source;
            internal readonly ChangeHandler<T> Handler;

            internal ProxyBinding(Subcontainer<T> target, string name, in T defaultValue, Bindable<T> source)
            {
                this.target = target;
                this.name = name;
                this.defaultValue = defaultValue;
                Source = source;
                Handler = Apply;
            }

            internal void Apply(T value)
            {
                if (target.proxies.TryGetValue(name, out var active) && ReferenceEquals(active, this))
                    target.SetCore(name, in defaultValue, in value, force: true);
            }
        }
    }
}
