using System;
using System.Buffers;
using System.IO;
using System.Reflection;
using System.Text;
using Unity.Collections.LowLevel.Unsafe;

namespace Kekchpek.DragonData
{
    internal sealed class TypeMetadata
    {
        private const int MaxDepth = 64;
        private const int MaxNodes = 65_536;
        private const int MaxNameBytes = 16_384;
        private const BindingFlags InstanceFields =
            BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;

        private static readonly Encoding Utf8 = new UTF8Encoding(false, true);

        private readonly Field[] fields;

        private TypeMetadata(string typeName, int size, Field[] fields)
        {
            TypeName = typeName;
            Size = size;
            this.fields = fields;
        }

        internal string TypeName { get; }
        internal int Size { get; }

        internal static TypeMetadata For<T>() where T : unmanaged => Cache<T>.Metadata;

        internal void Write(BinaryWriter writer)
        {
            writer.Write(TypeName);
            writer.Write(Size);
            writer.Write(fields.Length);

            foreach (var field in fields)
            {
                writer.Write(field.Name);
                writer.Write(field.Offset);
                field.Metadata.Write(writer);
            }
        }

        internal static TypeMetadata Read(BinaryReader reader)
        {
            var remainingNodes = MaxNodes;
            var nameBuffer = ArrayPool<byte>.Shared.Rent(MaxNameBytes);
            try
            {
                return Read(reader, nameBuffer, 0, ref remainingNodes);
            }
            catch (DecoderFallbackException exception)
            {
                throw new InvalidDataException("Invalid UTF-8 in DragonData type metadata.", exception);
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(nameBuffer);
            }
        }

        internal bool Matches(TypeMetadata other)
        {
            if (ReferenceEquals(this, other))
                return true;

            if (other == null || TypeName != other.TypeName || Size != other.Size || fields.Length != other.fields.Length)
                return false;

            for (var i = 0; i < fields.Length; i++)
            {
                var field = fields[i];
                var otherField = other.fields[i];
                if (field.Name != otherField.Name || field.Offset != otherField.Offset ||
                    !field.Metadata.Matches(otherField.Metadata))
                    return false;
            }

            return true;
        }

        private static TypeMetadata Describe(Type type, int size, int depth, ref int remainingNodes)
        {
            if (depth >= MaxDepth || remainingNodes-- <= 0)
                throw new NotSupportedException("DragonData type metadata is too complex.");

            if (!type.IsValueType || type == typeof(IntPtr) || type == typeof(UIntPtr))
                throw new NotSupportedException($"DragonData cannot persist the field type '{type}'.");

            var typeName = type.FullName;
            ValidateName(typeName);
            if (size <= 0)
                throw new NotSupportedException($"DragonData could not determine the in-memory size of '{typeName}'.");

            // Primitive value types expose implementation fields such as Int32.m_value that refer to themselves.
            if (type.IsPrimitive)
                return new TypeMetadata(typeName, size, Array.Empty<Field>());

            var reflectedFields = type.GetFields(InstanceFields);
            Array.Sort(reflectedFields, (left, right) => string.CompareOrdinal(left.Name, right.Name));
            if (reflectedFields.Length > remainingNodes)
                throw new NotSupportedException("DragonData type metadata is too complex.");

            var fields = reflectedFields.Length == 0 ? Array.Empty<Field>() : new Field[reflectedFields.Length];
            for (var i = 0; i < fields.Length; i++)
            {
                var field = reflectedFields[i];
                ValidateName(field.Name);
                var fieldType = field.FieldType;
                if (fieldType.IsPointer)
                    throw new NotSupportedException($"DragonData cannot persist pointer field '{typeName}.{field.Name}'.");

                var metadata = Describe(fieldType, UnsafeUtility.SizeOf(fieldType), depth + 1, ref remainingNodes);
                var offset = UnsafeUtility.GetFieldOffset(field);
                if (offset < 0 || offset > size - metadata.Size)
                    throw new NotSupportedException($"DragonData could not determine the in-memory layout of '{typeName}.{field.Name}'.");

                fields[i] = new Field(field.Name, offset, metadata);
            }

            return new TypeMetadata(typeName, size, fields);
        }

        private static TypeMetadata Read(BinaryReader reader, byte[] nameBuffer, int depth, ref int remainingNodes)
        {
            if (depth >= MaxDepth || remainingNodes-- <= 0)
                throw new InvalidDataException("DragonData type metadata is too complex.");

            var typeName = ReadName(reader, nameBuffer);
            var size = reader.ReadInt32();
            var fieldCount = reader.ReadInt32();
            if (size <= 0 || fieldCount < 0 || fieldCount > remainingNodes)
                throw new InvalidDataException($"Invalid DragonData metadata for '{typeName}'.");

            var fields = fieldCount == 0 ? Array.Empty<Field>() : new Field[fieldCount];
            for (var i = 0; i < fields.Length; i++)
            {
                var fieldName = ReadName(reader, nameBuffer);
                var offset = reader.ReadInt32();
                var metadata = Read(reader, nameBuffer, depth + 1, ref remainingNodes);
                if (offset < 0 || offset > size - metadata.Size ||
                    (i > 0 && string.CompareOrdinal(fields[i - 1].Name, fieldName) >= 0))
                    throw new InvalidDataException($"Invalid DragonData metadata for '{typeName}.{fieldName}'.");

                fields[i] = new Field(fieldName, offset, metadata);
            }

            return new TypeMetadata(typeName, size, fields);
        }

        private static void ValidateName(string name)
        {
            if (string.IsNullOrEmpty(name) || Utf8.GetByteCount(name) > MaxNameBytes)
                throw new NotSupportedException("DragonData type metadata contains an invalid name.");
        }

        private static string ReadName(BinaryReader reader, byte[] buffer)
        {
            // BinaryWriter.Write(string) stores the UTF-8 byte count as a seven-bit encoded integer.
            var length = 0;
            for (var shift = 0; shift <= 14; shift += 7)
            {
                var part = reader.ReadByte();
                length |= (part & 0x7f) << shift;
                if ((part & 0x80) != 0)
                    continue;

                if (length <= 0 || length > MaxNameBytes)
                    break;

                var position = 0;
                while (position < length)
                {
                    var count = reader.Read(buffer, position, length - position);
                    if (count == 0)
                        throw new EndOfStreamException();
                    position += count;
                }

                return Utf8.GetString(buffer, 0, length);
            }

            throw new InvalidDataException("Invalid name length in DragonData type metadata.");
        }

        private readonly struct Field
        {
            internal readonly string Name;
            internal readonly int Offset;
            internal readonly TypeMetadata Metadata;

            internal Field(string name, int offset, TypeMetadata metadata)
            {
                Name = name;
                Offset = offset;
                Metadata = metadata;
            }
        }

        private static class Cache<T> where T : unmanaged
        {
            internal static readonly TypeMetadata Metadata = Create();

            private static TypeMetadata Create()
            {
                var remainingNodes = MaxNodes;
                return Describe(typeof(T), UnsafeUtility.SizeOf<T>(), 0, ref remainingNodes);
            }
        }
    }
}
