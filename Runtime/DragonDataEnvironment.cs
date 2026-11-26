using System;
using System.Collections.Generic;
using System.IO;
using Unity.Collections.LowLevel.Unsafe;
using UnityEngine;

namespace Kekchpek.DragonData
{
    /// <summary>Owns a set of DragonData containers and their save files.</summary>
    public sealed class DragonDataEnvironment
    {
        private readonly Dictionary<string, Container> containers = new Dictionary<string, Container>(StringComparer.Ordinal);
        private readonly Dictionary<string, Func<Subcontainer>> factories = new Dictionary<string, Func<Subcontainer>>(StringComparer.Ordinal);
        private readonly Dictionary<Type, Func<Subcontainer, Subcontainer>> converters = new Dictionary<Type, Func<Subcontainer, Subcontainer>>();
        private readonly SaveStorage storage;

        public DragonDataEnvironment() : this(SaveStorage.DefaultDirectory) { }

        public DragonDataEnvironment(string saveDirectory)
        {
            storage = new SaveStorage(saveDirectory);
        }

        public Mutable<T> GetValue<T>(string containerName, string dataName) where T : unmanaged
            => GetValue<T>(containerName, dataName, default);

        public Mutable<T> GetValue<T>(string containerName, string dataName, T defaultValue) where T : unmanaged
        {
            if (dataName == null)
                throw new ArgumentNullException(nameof(dataName));
            if (containerName == null)
                throw new ArgumentNullException(nameof(containerName));
            if (!containers.TryGetValue(containerName, out var container))
            {
                SaveStorage.ValidateName(containerName, nameof(containerName));
                container = new Container();
                containers.Add(containerName, container);
            }
            RegisterFactory<T>();
            return new Mutable<T>(container.GetSubcontainer<T>(), dataName, in defaultValue);
        }

        public void SetConverter<T1, T2>(Func<T1, T2> converter) where T1 : unmanaged where T2 : unmanaged
        {
            if (converter == null)
                throw new ArgumentNullException(nameof(converter));
            // Both concrete subcontainers are rooted when a migration is registered.
            RegisterFactory<T1>();
            RegisterFactory<T2>();
            converters[typeof(T1)] = source => ((Subcontainer<T1>)source).Convert(converter);
        }

        public void Load(string containerName, string saveName)
        {
            SaveStorage.ValidateName(containerName, nameof(containerName));
            SaveStorage.ValidateName(saveName, nameof(saveName));
            var existing = containers.TryGetValue(containerName, out var container);
            container ??= new Container();
            try
            {
                if (storage.Load(containerName, saveName, stream => container.Load(stream, saveName, this)) && !existing)
                    containers.Add(containerName, container);
            }
            catch (Exception error)
            {
                Debug.LogError($"DragonData could not load '{containerName}/{saveName}': {error.Message}");
            }
        }

        public void Save(string containerName)
        {
            var container = FindContainer(containerName);
            storage.Save(containerName, container.SaveName, container.Save);
        }

        public void Save(string containerName, string overrideSaveName)
        {
            var container = FindContainer(containerName);
            storage.Save(containerName, overrideSaveName, container.Save);
        }

        public void SaveAll()
        {
            foreach (var pair in containers)
                storage.Save(pair.Key, pair.Value.SaveName, pair.Value.Save);
        }

        internal Subcontainer CreateSubcontainer(TypeMetadata metadata)
        {
            Subcontainer result;
            if (factories.TryGetValue(metadata.TypeName, out var factory))
            {
                result = factory();
            }
            else
            {
                var type = ResolveType(metadata.TypeName);
                if (type == null || !type.IsValueType || !UnsafeUtility.IsUnmanaged(type))
                    throw new InvalidDataException($"DragonData type '{metadata.TypeName}' is unavailable.");
                result = (Subcontainer)Activator.CreateInstance(typeof(Subcontainer<>).MakeGenericType(type));
            }
            if (!result.Metadata.Matches(metadata))
                throw new InvalidDataException($"Saved metadata does not match DragonData type '{metadata.TypeName}'.");
            return result;
        }

        internal Subcontainer Convert(Subcontainer source)
        {
            if (converters.TryGetValue(source.DataType, out var converter))
                return converter(source);
            return source;
        }

        private void RegisterFactory<T>() where T : unmanaged
        {
            var name = Factory<T>.Name;
            if (factories.TryGetValue(name, out var factory))
            {
                if (factory != Factory<T>.Create)
                    throw new InvalidOperationException($"Multiple DragonData types have the full name '{name}'.");
            }
            else
            {
                factories.Add(name, Factory<T>.Create);
            }
        }

        private Container FindContainer(string name)
        {
            SaveStorage.ValidateName(name, nameof(name));
            if (!containers.TryGetValue(name, out var container))
                throw new KeyNotFoundException($"DragonData container '{name}' does not exist.");
            return container;
        }

        private static Type ResolveType(string name)
        {
            var result = Type.GetType(name, throwOnError: false);
            foreach (var assembly in AppDomain.CurrentDomain.GetAssemblies())
            {
                var candidate = assembly.GetType(name, throwOnError: false);
                if (candidate == null)
                    continue;
                if (result != null && result != candidate)
                    throw new InvalidDataException($"Multiple DragonData types have the full name '{name}'.");
                result = candidate;
            }
            return result;
        }

        private static class Factory<T> where T : unmanaged
        {
            internal static readonly string Name = typeof(T).FullName;
            internal static readonly Func<Subcontainer> Create = () => new Subcontainer<T>();
        }
    }
}
