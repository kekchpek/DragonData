using System;
using System.Diagnostics;
using System.IO;
using System.Text.RegularExpressions;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;

namespace Kekchpek.DragonData.Tests
{
    public sealed class DragonDataEnvironmentTests
    {
        private string directory;
        private DragonDataEnvironment environment;

        [SetUp]
        public void SetUp()
        {
            directory = Path.Combine(Path.GetTempPath(), $"dragondata-{Guid.NewGuid():N}");
            environment = new DragonDataEnvironment(directory);
        }

        [TearDown]
        public void TearDown()
        {
            if (Directory.Exists(directory))
                Directory.Delete(directory, recursive: true);
        }

        [Test]
        public void RoundTripLoadsTypesBeforeAcquiringHandlesAndSurvivesArrayGrowth()
        {
            var state = new PositionState { Position = new Vector3(3, -7, 2.5f), Active = true, Symbol = 'Ж' };
            environment.GetValue<PositionState>("Player", "state").Set(state);
            environment.GetValue<bool>("Player", "visible").Set(true);
            for (var i = 0; i < 100; i++)
                environment.GetValue<int>("Player", $"предмет-{i}").Set(i * 10);
            var unreadDefault = environment.GetValue<int>("Player", "unread", 22);
            environment.Save("Player");

            var restored = new DragonDataEnvironment(directory);
            restored.Load("Player", "default");
            var savedState = restored.GetValue<PositionState>("Player", "state").Value;
            Assert.That(savedState.Position, Is.EqualTo(state.Position));
            Assert.That(savedState.Active, Is.True);
            Assert.That(savedState.Symbol, Is.EqualTo('Ж'));
            Assert.That(restored.GetValue<bool>("Player", "visible").Value, Is.True);
            for (var i = 0; i < 100; i++)
                Assert.That(restored.GetValue<int>("Player", $"предмет-{i}", -1).Value, Is.EqualTo(i * 10));
            Assert.That(restored.GetValue<int>("Player", "unread", 33).Value, Is.EqualTo(33));
            Assert.That(unreadDefault.Value, Is.EqualTo(22));
        }

        [Test]
        public void SaveAllAndOverridesRespectTheActiveSlotOfEachContainer()
        {
            var value = environment.GetValue<int>("Player", "coins", 10);
            var absentInBackup = environment.GetValue<int>("Player", "local", 3);
            environment.GetValue<int>("Settings", "difficulty").Set(2);
            environment.Save("Player", "backup");
            value.Value = 15;
            Assert.That(absentInBackup.Value, Is.EqualTo(3));
            environment.SaveAll();

            Assert.That(Directory.GetFiles(directory, "*.ddsave"), Has.Length.EqualTo(3));
            Assert.That(File.Exists(Path.Combine(directory, "Player_default.ddsave")), Is.True);
            Assert.That(File.Exists(Path.Combine(directory, "Player_backup.ddsave")), Is.True);
            Assert.That(File.Exists(Path.Combine(directory, "Settings_default.ddsave")), Is.True);

            var callbacks = 0;
            value.Bind(() => callbacks++, callImmediately: false);
            environment.Load("Player", "backup");
            Assert.That(value.Value, Is.EqualTo(10));
            Assert.That(environment.GetValue<int>("Player", "local", 9).Value, Is.EqualTo(9));
            Assert.That(callbacks, Is.Zero, "Loading replaces data without invoking change handlers.");
            value.Set(11);
            Assert.That(callbacks, Is.EqualTo(1), "Existing handles and subscriptions survive loading.");
            environment.Save("Player");

            environment.Load("Player", "default");
            Assert.That(value.Value, Is.EqualTo(15));
            environment.Load("Player", "backup");
            Assert.That(value.Value, Is.EqualTo(11));
            Assert.That(environment.GetValue<int>("Settings", "difficulty").Value, Is.EqualTo(2));
        }

        [Test]
        public void LoadingMissingSlotLeavesDataAndActiveSlotIntact()
        {
            var value = environment.GetValue<int>("Player", "coins", 42);
            environment.Load("Player", "missing");
            Assert.That(value.Value, Is.EqualTo(42));
            environment.Save("Player");
            Assert.That(File.Exists(Path.Combine(directory, "Player_default.ddsave")), Is.True);
            Assert.That(File.Exists(Path.Combine(directory, "Player_missing.ddsave")), Is.False);
        }

        [Test]
        public void ChecksumFailureRollsBackAndKeepsThePreviousSlot()
        {
            var value = environment.GetValue<int>("Player", "coins");
            value.Value = 11;
            environment.Save("Player", "damaged");
            var path = Path.Combine(directory, "Player_damaged.ddsave");
            var bytes = File.ReadAllBytes(path);
            bytes[bytes.Length - 1] ^= 1;
            File.WriteAllBytes(path, bytes);
            value.Value = 27;

            LogAssert.Expect(LogType.Error, new Regex("DragonData could not load 'Player/damaged'.*checksum"));
            environment.Load("Player", "damaged");

            Assert.That(value.Value, Is.EqualTo(27));
            environment.Save("Player");
            Assert.That(File.ReadAllBytes(path), Is.EqualTo(bytes));
            Assert.That(File.Exists(Path.Combine(directory, "Player_default.ddsave")), Is.True);
        }

        [Test]
        public void MetadataMismatchRollsBackEvenAfterAValidTypeWasRead()
        {
            var unlocked = environment.GetValue<bool>("Player", "unlocked");
            var score = environment.GetValue<int>("Player", "score", 27);
            _ = score.Value;
            var valid = new Subcontainer<bool>();
            var previous = false;
            var next = true;
            valid.Set("unlocked", in previous, in next, force: true);

            new SaveStorage(directory).Save("Player", "mismatch", stream =>
            {
                using var writer = OpenContainerBody(stream);
                valid.Metadata.Write(writer);
                valid.Save(stream);
                writer.Write(typeof(int).FullName);
                writer.Write(sizeof(long));
                writer.Write(0);
            });

            LogAssert.Expect(LogType.Error, new Regex("DragonData could not load 'Player/mismatch'.*Saved metadata"));
            environment.Load("Player", "mismatch");

            Assert.That(unlocked.Value, Is.False);
            Assert.That(score.Value, Is.EqualTo(27));
        }

        [Test]
        public void InvalidKeyIndexCannotChangeExistingData()
        {
            var score = environment.GetValue<int>("Player", "score", 5);
            new SaveStorage(directory).Save("Player", "invalid", stream =>
            {
                using var writer = OpenContainerBody(stream);
                TypeMetadata.For<int>().Write(writer);
                writer.Write(2);
                writer.Write("score");
                writer.Write(0);
                writer.Write("other");
                writer.Write(0);
                writer.Write(1);
                writer.Write(2);
            });

            LogAssert.Expect(LogType.Error, new Regex("DragonData could not load 'Player/invalid'.*value mapping"));
            environment.Load("Player", "invalid");

            Assert.That(score.Value, Is.EqualTo(5));
        }

        [Test]
        public void ConverterMigratesAllLegacyValuesAndMergesDisjointCurrentKeys()
        {
            environment.GetValue<LegacyScore>("Player", "left").Set(new LegacyScore { Amount = 2 });
            environment.GetValue<LegacyScore>("Player", "right").Set(new LegacyScore { Amount = 5 });
            environment.GetValue<long>("Player", "current").Set(90L);
            environment.Save("Player");

            var migrated = new DragonDataEnvironment(directory);
            var priorHandle = migrated.GetValue<long>("Player", "left", -1L);
            var legacyHandle = migrated.GetValue<LegacyScore>("Player", "left");
            migrated.SetConverter<LegacyScore, long>(old => old.Amount * 100L);
            migrated.Load("Player", "default");

            Assert.That(priorHandle.Value, Is.EqualTo(200L));
            Assert.That(migrated.GetValue<long>("Player", "right").Value, Is.EqualTo(500L));
            Assert.That(migrated.GetValue<long>("Player", "current").Value, Is.EqualTo(90L));
            migrated.Save("Player", "migrated");
            Assert.That(legacyHandle.Value.Amount, Is.Zero);

            var restored = new DragonDataEnvironment(directory);
            restored.Load("Player", "migrated");
            Assert.That(restored.GetValue<long>("Player", "left").Value, Is.EqualTo(200L));
            restored.Load("Player", "default");
            Assert.That(restored.GetValue<LegacyScore>("Player", "left").Value.Amount, Is.EqualTo(2));
        }

        [Test]
        public void ConflictingMigrationAndThrowingConverterRollBack()
        {
            environment.GetValue<LegacyScore>("Player", "score").Set(new LegacyScore { Amount = 2 });
            environment.GetValue<long>("Player", "score").Set(50L);
            environment.Save("Player");

            var migrated = new DragonDataEnvironment(directory);
            var score = migrated.GetValue<long>("Player", "score", 99L);
            migrated.SetConverter<LegacyScore, long>(old => old.Amount);
            LogAssert.Expect(LogType.Error, new Regex("DragonData could not load 'Player/default'.*Multiple DragonData values"));
            migrated.Load("Player", "default");
            Assert.That(score.Value, Is.EqualTo(99L));

            migrated.SetConverter<LegacyScore, long>(_ => throw new InvalidOperationException("converter failed"));
            LogAssert.Expect(LogType.Error, new Regex("DragonData could not load 'Player/default'.*converter failed"));
            migrated.Load("Player", "default");
            Assert.That(score.Value, Is.EqualTo(99L));
        }

        [Test]
        public void IndependentEnvironmentsAndStaticFacadeShareOnlyTheirOwnValues()
        {
            var name = "test-" + Guid.NewGuid().ToString("N");
            var local = environment.GetValue<int>(name, "score", 1);
            var shared = DragonData.GetValue<int>(name, "score");
            var sharedWithDefault = DragonData.GetValue<int>(name, "score", 2);
            sharedWithDefault.Set(8);

            Assert.That(local.Value, Is.EqualTo(1));
            Assert.That(shared.Value, Is.EqualTo(8));
            Assert.Throws<ArgumentNullException>(() => environment.GetValue<int>(name, null));
            Assert.Throws<ArgumentNullException>(() => environment.SetConverter<int, long>(null));
            Assert.Throws<System.Collections.Generic.KeyNotFoundException>(() => environment.Save("unknown"));
        }

        [TestCase(1_000)]
        [TestCase(100_000)]
        public void WarmedPrimitiveReadsWritesAndNotificationsAllocateNoManagedMemory(int operations)
        {
            var value = environment.GetValue<int>("Player", "score");
            var observed = 0;
            value.Bind((in int next) => observed = next, callImmediately: false);
            value.Set(-1);
            var timer = new Stopwatch();
            _ = GC.GetAllocatedBytesForCurrentThread();
            timer.Start();
            var initialBytes = GC.GetAllocatedBytesForCurrentThread();
            var total = 0L;
            for (var i = 0; i < operations; i++)
            {
                value.Set(i);
                total += value.Value;
            }
            var allocatedBytes = GC.GetAllocatedBytesForCurrentThread() - initialBytes;
            timer.Stop();

            Assert.That(observed, Is.EqualTo(operations - 1));
            Assert.That(total, Is.EqualTo((long)operations * (operations - 1) / 2));
            Assert.That(allocatedBytes, Is.Zero);
            TestContext.WriteLine($"{operations} write/read pairs and notifications: {timer.Elapsed.TotalMilliseconds:F3} ms, {allocatedBytes} allocated bytes.");
        }

        private static BinaryWriter OpenContainerBody(Stream stream)
        {
            var writer = new BinaryWriter(stream, Subcontainer.TextEncoding, leaveOpen: true);
            writer.Write((byte)1);
            writer.Write(BitConverter.IsLittleEndian);
            return writer;
        }

        private struct LegacyScore
        {
            internal int Amount;
        }

        private struct PositionState
        {
            internal Vector3 Position;
            internal bool Active;
            internal char Symbol;
        }
    }
}
