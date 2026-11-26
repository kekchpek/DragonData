using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using NUnit.Framework;

namespace Kekchpek.DragonData.Tests
{
    public class TypeMetadataTests
    {
        [Test]
        public void RoundTripsRawManagedLayouts()
        {
            AssertRoundTrip<int>();
            AssertRoundTrip<bool>();
            AssertRoundTrip<char>();
            AssertRoundTrip<decimal>();
            AssertRoundTrip<Kind>();
            AssertRoundTrip<Generic<Packed>>();
            AssertRoundTrip<FixedBuffer>();
            AssertRoundTrip<ExplicitUnion>();
        }

        [Test]
        public void WritesPackedFieldNamesManagedOffsetsAndNestedTypes()
        {
            using var stream = new MemoryStream();
            using var writer = new BinaryWriter(stream, Encoding.UTF8, true);
            using var reader = new BinaryReader(stream, Encoding.UTF8, true);
            TypeMetadata.For<Packed>().Write(writer);
            stream.Position = 0;

            AssertHeader(reader, typeof(Packed).FullName, 5, 3);
            AssertField(reader, "flag", 0);
            AssertHeader(reader, typeof(bool).FullName, 1, 0);
            AssertField(reader, "letter", 1);
            AssertHeader(reader, typeof(char).FullName, 2, 0);
            AssertField(reader, "mode", 3);
            AssertHeader(reader, typeof(Kind).FullName, 2, 1);
            AssertField(reader, "value__", 0);
            AssertHeader(reader, typeof(ushort).FullName, 2, 0);
            Assert.That(stream.Position, Is.EqualTo(stream.Length));
        }

        [TestCase("renamed", typeof(int), 0)]
        [TestCase("value", typeof(uint), 0)]
        [TestCase("value", typeof(int), 4)]
        public void RejectsRenamedRetypedOrMovedFieldsOfTheSameSize(string name, Type fieldType, int offset)
        {
            var current = TypeMetadata.For<LayoutVersion>();
            var stored = Decode(writer =>
            {
                WriteHeader(writer, current.TypeName, current.Size, 1);
                WriteScalarField(writer, name, fieldType, offset);
            });

            Assert.That(current.Matches(stored), Is.False);
        }

        [TestCase(0, 0)]
        [TestCase(-1, 0)]
        [TestCase(8, -1)]
        [TestCase(8, int.MaxValue)]
        public void RejectsInvalidSizeOrFieldCount(int size, int fieldCount)
        {
            Assert.Throws<InvalidDataException>(() =>
                Decode(writer => WriteHeader(writer, "Shape", size, fieldCount)));
        }

        [TestCase(-1)]
        [TestCase(5)]
        public void RejectsFieldOutsideItsParent(int offset)
        {
            Assert.Throws<InvalidDataException>(() => Decode(writer =>
            {
                WriteHeader(writer, "Shape", 8, 1);
                WriteScalarField(writer, "value", typeof(int), offset);
            }));
        }

        [TestCase("a", "a")]
        [TestCase("b", "a")]
        public void RejectsDuplicateOrUnorderedFieldNames(string first, string second)
        {
            Assert.Throws<InvalidDataException>(() => Decode(writer =>
            {
                WriteHeader(writer, "Shape", 8, 2);
                WriteScalarField(writer, first, typeof(int), 0);
                WriteScalarField(writer, second, typeof(int), 4);
            }));
        }

        [Test]
        public void RejectsInvalidUtf8AndTruncatedMetadata()
        {
            Assert.Throws<InvalidDataException>(() => Decode(writer =>
            {
                writer.Write((byte)1);
                writer.Write(byte.MaxValue);
            }));

            Assert.Throws<EndOfStreamException>(() => Decode(writer => writer.Write("Shape")));
        }

        [Test]
        public void RejectsUnboundedRecursion()
        {
            Assert.Throws<InvalidDataException>(() => Decode(writer =>
            {
                for (var i = 0; i < 100; i++)
                {
                    WriteHeader(writer, "Shape", 4, 1);
                    writer.Write("nested");
                    writer.Write(0);
                }

                WriteHeader(writer, typeof(int).FullName, 4, 0);
            }));
        }

        [Test]
        public void RejectsProcessLocalAddresses()
        {
            var address = Assert.Throws<TypeInitializationException>(() => TypeMetadata.For<IntPtr>());
            Assert.That(address.InnerException, Is.TypeOf<NotSupportedException>());

            var pointer = Assert.Throws<TypeInitializationException>(() => TypeMetadata.For<Pointer>());
            Assert.That(pointer.InnerException, Is.TypeOf<NotSupportedException>());
        }

        private static void AssertRoundTrip<T>() where T : unmanaged
        {
            var current = TypeMetadata.For<T>();
            var stored = Decode(current.Write);
            Assert.That(current, Is.SameAs(TypeMetadata.For<T>()));
            Assert.That(current.TypeName, Is.EqualTo(typeof(T).FullName));
            Assert.That(current.Size, Is.EqualTo(MemoryMarshal.AsBytes(new T[1].AsSpan()).Length));
            Assert.That(current.Matches(stored), Is.True);
        }

        private static TypeMetadata Decode(Action<BinaryWriter> write)
        {
            using var stream = new MemoryStream();
            using var writer = new BinaryWriter(stream, Encoding.UTF8, true);
            using var reader = new BinaryReader(stream, Encoding.UTF8, true);
            write(writer);
            stream.Position = 0;
            return TypeMetadata.Read(reader);
        }

        private static void AssertHeader(BinaryReader reader, string name, int size, int fieldCount)
        {
            Assert.That(reader.ReadString(), Is.EqualTo(name));
            Assert.That(reader.ReadInt32(), Is.EqualTo(size));
            Assert.That(reader.ReadInt32(), Is.EqualTo(fieldCount));
        }

        private static void AssertField(BinaryReader reader, string name, int offset)
        {
            Assert.That(reader.ReadString(), Is.EqualTo(name));
            Assert.That(reader.ReadInt32(), Is.EqualTo(offset));
        }

        private static void WriteHeader(BinaryWriter writer, string name, int size, int fieldCount)
        {
            writer.Write(name);
            writer.Write(size);
            writer.Write(fieldCount);
        }

        private static void WriteScalarField(BinaryWriter writer, string fieldName, Type fieldType, int offset)
        {
            writer.Write(fieldName);
            writer.Write(offset);
            WriteHeader(writer, fieldType.FullName, 4, 0);
        }

        private enum Kind : ushort
        {
            First
        }

        // These fields are read by TypeMetadata through reflection.
        #pragma warning disable CS0169, CS0649
        [StructLayout(LayoutKind.Sequential, Pack = 1)]
        private struct Packed
        {
            private bool flag;
            private char letter;
            private Kind mode;
        }

        private struct Generic<T> where T : unmanaged
        {
            private byte marker;
            private T nested;
        }

        private unsafe struct FixedBuffer
        {
            private fixed char letters[3];
            private fixed bool flags[5];
        }

        [StructLayout(LayoutKind.Explicit, Size = 8)]
        private struct ExplicitUnion
        {
            [FieldOffset(0)] private long whole;
            [FieldOffset(1)] private char part;
        }

        [StructLayout(LayoutKind.Explicit, Size = 8)]
        private struct LayoutVersion
        {
            [FieldOffset(0)] private int value;
        }

        private unsafe struct Pointer
        {
            private int* value;
        }
        #pragma warning restore CS0169, CS0649
    }
}
