using System;
using System.IO;
using System.Security.Cryptography;
using NUnit.Framework;
using UnityEngine;

namespace Kekchpek.DragonData.Tests
{
    public sealed class SaveStorageTests
    {
        private string _directory;
        private SaveStorage _storage;

        [SetUp]
        public void SetUp()
        {
            _directory = Path.Combine(Path.GetTempPath(), $"DragonData-{Guid.NewGuid():N}");
            _storage = new SaveStorage(_directory);
        }

        [TearDown]
        public void TearDown()
        {
            if (Directory.Exists(_directory))
            {
                Directory.Delete(_directory, true);
            }
        }

        [Test]
        public void UsesUnityPersistentGameSavesDirectory()
        {
            Assert.That(SaveStorage.DefaultDirectory, Is.EqualTo(Path.Combine(Application.persistentDataPath, "GameSaves")));
        }

        [Test]
        public void MissingSaveDoesNotInvokeReaderOrCreateDirectory()
        {
            Assert.That(_storage.Load("Inventory", "default", _ => Assert.Fail("Unexpected payload.")), Is.False);
            Assert.That(Directory.Exists(_directory), Is.False);
            Directory.CreateDirectory(_directory);
            Assert.That(_storage.Load("Inventory", "default", _ => Assert.Fail("Unexpected payload.")), Is.False);
        }

        [Test]
        public void FileHasTrailingSha256AndExposesOnlyThePayload()
        {
            var expected = new byte[213011];
            new System.Random(814).NextBytes(expected);
            _storage.Save("Inventory", "slot_1", stream =>
            {
                Assert.That(stream.Position, Is.Zero);
                stream.WriteByte(expected[0]);
                stream.Write(expected, 1, 37);
                stream.Write(expected.AsSpan(38));
                Assert.That(stream.Length, Is.EqualTo(expected.Length));
            });

            byte[] file = File.ReadAllBytes(Path.Combine(_directory, "Inventory_slot_1.ddsave"));
            using var hash = SHA256.Create();
            int checksumStart = file.Length - 32;
            Assert.That(file.AsSpan(checksumStart).ToArray(), Is.EqualTo(hash.ComputeHash(file, 0, checksumStart)));

            Assert.That(_storage.Load("Inventory", "slot_1", stream =>
            {
                Assert.That(stream.CanSeek, Is.True);
                Assert.That(stream.Position, Is.Zero);
                Assert.That(stream.Length, Is.EqualTo(expected.Length));
                var actual = new byte[expected.Length];
                int offset = 0;
                while (offset < actual.Length)
                {
                    int count = stream.Read(actual.AsSpan(offset));
                    Assert.That(count, Is.Positive);
                    offset += count;
                }

                Assert.That(actual, Is.EqualTo(expected));
                Assert.That(stream.Read(actual, 0, 1), Is.Zero);
                Assert.That(stream.ReadByte(), Is.EqualTo(-1));
                Assert.That(stream.Seek(-1, SeekOrigin.End), Is.EqualTo(expected.Length - 1));
                Assert.That(stream.ReadByte(), Is.EqualTo(expected[expected.Length - 1]));
            }), Is.True);
        }

        [Test]
        public void FailedWritePreservesPreviousSaveAndRemovesTemporaryFile()
        {
            _storage.Save("Inventory", "default", stream => stream.WriteByte(7));
            Assert.Throws<IOException>(() => _storage.Save("Inventory", "default", stream =>
            {
                stream.WriteByte(27);
                stream.Flush();
                throw new IOException("Write failed.");
            }));

            AssertPayload("Inventory", "default", 7);
            Assert.That(Directory.GetFiles(_directory), Is.EqualTo(new[] { Path.Combine(_directory, "Inventory_default.ddsave") }));
        }

        [Test]
        public void ValidSaveCannotBeOverwrittenByCollidingNames()
        {
            _storage.Save("Player_Inventory", "slot", stream => stream.WriteByte(16));
            Assert.Throws<InvalidDataException>(() => _storage.Load("Player", "Inventory_slot", _ => Assert.Fail("Unexpected payload.")));
            Assert.Throws<IOException>(() => _storage.Save("Player", "Inventory_slot", stream => stream.WriteByte(77)));
            AssertPayload("Player_Inventory", "slot", 16);
        }

        [Test]
        public void CorruptSaveIsRejectedAndCanBeReplaced()
        {
            _storage.Save("Player_Inventory", "slot", stream => stream.WriteByte(16));
            string path = Path.Combine(_directory, "Player_Inventory_slot.ddsave");
            byte[] corruptFile = File.ReadAllBytes(path);
            corruptFile[corruptFile.Length - 33] ^= 1;
            File.WriteAllBytes(path, corruptFile);

            Assert.Throws<InvalidDataException>(() => _storage.Load("Player_Inventory", "slot", _ => Assert.Fail("Unexpected payload.")));
            _storage.Save("Player", "Inventory_slot", stream => stream.WriteByte(17));
            AssertPayload("Player", "Inventory_slot", 17);
            _storage.Save("Player", "Inventory_slot", stream => stream.WriteByte(19));
            AssertPayload("Player", "Inventory_slot", 19);
            Assert.That(Directory.GetFiles(_directory), Is.EqualTo(new[] { path }));
        }

        [TestCase("")]
        [TestCase("  ")]
        [TestCase("../escape")]
        [TestCase("C:escape")]
        [TestCase(@"other\save")]
        [TestCase("bad?name")]
        [TestCase("control\ncharacter")]
        public void NamesMustBePortableFilenameParts(string invalid)
        {
            Assert.Throws<ArgumentException>(() => _storage.Save(invalid, "default", _ => { }));
            Assert.Throws<ArgumentException>(() => _storage.Save("Inventory", invalid, _ => { }));
            Assert.That(Directory.Exists(_directory), Is.False);
        }

        private void AssertPayload(string containerName, string saveName, int expected)
        {
            Assert.That(_storage.Load(containerName, saveName, stream =>
            {
                Assert.That(stream.ReadByte(), Is.EqualTo(expected));
                Assert.That(stream.ReadByte(), Is.EqualTo(-1));
            }), Is.True);
        }
    }
}
