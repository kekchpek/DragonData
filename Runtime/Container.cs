using System;
using System.Collections.Generic;
using System.IO;

namespace Kekchpek.DragonData
{
    internal sealed class Container
    {
        private const byte FormatVersion = 1;
        private Dictionary<Type, Subcontainer> subcontainers = new Dictionary<Type, Subcontainer>();

        internal string SaveName { get; private set; } = "default";

        internal Subcontainer<T> GetSubcontainer<T>() where T : unmanaged
        {
            var type = typeof(T);
            if (!subcontainers.TryGetValue(type, out var subcontainer))
            {
                subcontainer = new Subcontainer<T>();
                subcontainers.Add(type, subcontainer);
            }
            return (Subcontainer<T>)subcontainer;
        }

        internal void Save(Stream stream)
        {
            using var writer = new BinaryWriter(stream, Subcontainer.TextEncoding, leaveOpen: true);
            writer.Write(FormatVersion);
            writer.Write(BitConverter.IsLittleEndian);
            foreach (var subcontainer in subcontainers.Values)
            {
                if (subcontainer.Count == 0)
                    continue;
                subcontainer.Metadata.Write(writer);
                subcontainer.Write(writer);
            }
        }

        internal void Load(Stream stream, string saveName, DragonDataEnvironment environment)
        {
            using var reader = new BinaryReader(stream, Subcontainer.TextEncoding, leaveOpen: true);
            if (reader.ReadByte() != FormatVersion)
                throw new InvalidDataException("Unsupported DragonData container version.");
            if (reader.ReadByte() != (BitConverter.IsLittleEndian ? 1 : 0))
                throw new InvalidDataException("The save byte order differs from this platform.");

            var loaded = new Dictionary<Type, Subcontainer>();
            var sourceTypes = new HashSet<Type>();
            while (stream.Position < stream.Length)
            {
                var metadata = TypeMetadata.Read(reader);
                var subcontainer = environment.CreateSubcontainer(metadata);
                if (!sourceTypes.Add(subcontainer.DataType))
                    throw new InvalidDataException($"Repeated DragonData type block: '{metadata.TypeName}'.");
                subcontainer.Read(reader);
                var converted = environment.Convert(subcontainer);
                if (!loaded.TryAdd(converted.DataType, converted))
                    loaded[converted.DataType].MergeFrom(converted);
            }

            // Reserve all keys before transferring the validated data to the objects held by existing handles.
            foreach (var type in subcontainers.Keys)
                loaded.TryAdd(type, null);

            foreach (var pair in subcontainers)
            {
                pair.Value.ReplaceData(loaded[pair.Key]);
                loaded[pair.Key] = pair.Value;
            }
            subcontainers = loaded;
            SaveName = saveName;
        }
    }
}
