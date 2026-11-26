using System;
using System.Collections.Generic;
using System.IO;
using NUnit.Framework;

namespace Kekchpek.DragonData.Tests
{
    public sealed class ComputedBindableTests
    {
        private string directory;
        private DragonDataEnvironment environment;

        [SetUp]
        public void SetUp()
        {
            directory = Path.Combine(Path.GetTempPath(), $"dragondata-computed-{Guid.NewGuid():N}");
            environment = new DragonDataEnvironment(directory);
        }

        [TearDown]
        public void TearDown()
        {
            if (Directory.Exists(directory))
                Directory.Delete(directory, recursive: true);
        }

        [Test]
        public void ConvertToReadsTheCurrentSourceOnDemandWithoutSubscribing()
        {
            var source = environment.GetValue("Player", "score", 2);
            Bindable<int> readOnly = source;
            var calculations = 0;
            var upstreamSubscriptions = 0;
            source.OnAnySubscription += () => upstreamSubscriptions++;
            var doubled = source.ConvertTo(value =>
            {
                calculations++;
                return (long)value * 2;
            });
            var positive = readOnly.ConvertTo(value => value > 0);

            source.Set(4);
            Assert.That(calculations, Is.Zero);
            Assert.That(doubled.Value, Is.EqualTo(8L));
            Assert.That(positive.Value, Is.True);

            var calculationsAfterRead = calculations;
            source.Set(-3);
            Assert.That(calculations, Is.EqualTo(calculationsAfterRead));
            Assert.That(doubled.Value, Is.EqualTo(-6L));
            Assert.That(positive.Value, Is.False);
            Assert.That(upstreamSubscriptions, Is.Zero);
        }

        [Test]
        public void ComputedDependencyIsObservedUntilTheLastHandlerIsRemoved()
        {
            var source = environment.GetValue("Player", "score", 2);
            var upstreamSubscriptions = 0;
            var upstreamClears = 0;
            var calculations = 0;
            source.OnAnySubscription += () => upstreamSubscriptions++;
            source.OnSubscriptionsCleared += () => upstreamClears++;
            var doubled = source.ConvertTo(value =>
            {
                calculations++;
                return value * 2;
            });
            var observed = new List<int>();
            var replacements = new List<(int Previous, int Current)>();
            ChangeHandler<int> change = observed.Add;
            Action<int, int> full = (previous, current) => replacements.Add((previous, current));

            doubled.Bind(change);
            doubled.Bind(full);
            Assert.That(upstreamSubscriptions, Is.EqualTo(1));
            source.Set(5);
            doubled.Unbind(change);
            Assert.That(upstreamClears, Is.Zero);
            source.Set(7);
            doubled.Unbind(full);
            Assert.That(upstreamClears, Is.EqualTo(1));

            var calculationsAfterUnbind = calculations;
            source.Set(9);
            Assert.That(calculations, Is.EqualTo(calculationsAfterUnbind));
            Assert.That(observed, Is.EqualTo(new[] { 4, 10 }));
            Assert.That(replacements, Is.EqualTo(new[] { (4, 10), (10, 14) }));
            Assert.That(doubled.Value, Is.EqualTo(18));

            doubled.Bind(change);
            source.Set(10);
            doubled.Unbind(change);
            Assert.That(upstreamSubscriptions, Is.EqualTo(2));
            Assert.That(upstreamClears, Is.EqualTo(2));
            Assert.That(observed, Is.EqualTo(new[] { 4, 10, 18, 20 }));
        }

        [Test]
        public void AggregateCombinesTwoThreeAndFourHeterogeneousSources()
        {
            var first = environment.GetValue("Player", "first", 1);
            var second = environment.GetValue("Player", "second", 2L);
            var third = environment.GetValue("Player", "third", (short)3);
            var fourth = environment.GetValue("Player", "fourth", (byte)4);
            Bindable<int> firstReader = first;
            Bindable<long> secondReader = second;
            var two = Bindable.Aggregate(firstReader, secondReader, (a, b) => a + b);
            var three = Bindable.Aggregate(first, second, third, (a, b, c) => a + b * 10 + c * 100);
            var four = Bindable.Aggregate(first, second, third, fourth, (a, b, c, d) => a + b + c + d);
            var observed = new List<long>();
            ChangeHandler<long> handler = observed.Add;

            four.Bind(handler);
            Assert.That(two.Value, Is.EqualTo(3L));
            Assert.That(three.Value, Is.EqualTo(321L));
            first.Set(5);
            second.Set(6L);
            third.Set(7);
            fourth.Set(8);
            four.Unbind(handler);
            Assert.That(two.Value, Is.EqualTo(11L));
            Assert.That(three.Value, Is.EqualTo(765L));
            Assert.That(four.Value, Is.EqualTo(26L));
            Assert.That(observed, Is.EqualTo(new[] { 10L, 14L, 18L, 22L, 26L }));
        }

        [Test]
        public void CollectionAggregatePreservesInputOrderAndAllowsEmptySources()
        {
            var first = environment.GetValue("Player", "first", 1);
            var second = environment.GetValue("Player", "second", 2);
            var readers = new[] { (Bindable<int>)first, (Bindable<int>)second, (Bindable<int>)first };
            var readOnlyAggregate = Bindable.Aggregate(readers, values => values[0] * 100 + values[1] * 10 + values[2]);
            var mutableAggregate = Bindable.Aggregate(new[] { second, first }, values => values[0] - values[1]);
            var empty = Bindable.Aggregate(Array.Empty<Bindable<int>>(), values => values.Length);
            var observed = new List<int>();
            ChangeHandler<int> handler = observed.Add;

            readOnlyAggregate.Bind(handler);
            Assert.That(mutableAggregate.Value, Is.EqualTo(1));
            Assert.That(empty.Value, Is.Zero);
            first.Set(3);
            readOnlyAggregate.Unbind(handler);
            Assert.That(mutableAggregate.Value, Is.EqualTo(-1));
            Assert.That(readOnlyAggregate.Value, Is.EqualTo(323));
            Assert.That(observed, Is.EqualTo(new[] { 121, 323, 323 }));
        }

        [Test]
        public void AggregateWithAcceptsMutableAndReadOnlyHandles()
        {
            var first = environment.GetValue("Player", "first", 2);
            var second = environment.GetValue("Player", "second", 3L);
            Bindable<int> readFirst = first;
            Bindable<long> readSecond = second;
            var mutablePair = first.AggregateWith(second, (a, b) => a * b);
            var readOnlyPair = readFirst.AggregateWith(readSecond, (a, b) => a + b);
            var readOnlyAndMutable = readFirst.AggregateWith(second, (a, b) => a - b);
            var mutableAndReadOnly = first.AggregateWith(readSecond, (a, b) => a * 10 + b);

            second.Set(7L);
            Assert.That(mutablePair.Value, Is.EqualTo(14L));
            Assert.That(readOnlyPair.Value, Is.EqualTo(9L));
            Assert.That(readOnlyAndMutable.Value, Is.EqualTo(-5L));
            Assert.That(mutableAndReadOnly.Value, Is.EqualTo(27L));
        }

        [Test]
        public void ConvertedProxyFollowsTheSourceAndAnExplicitWriteDetachesIt()
        {
            var source = environment.GetValue("Player", "score", 2);
            var target = environment.GetValue<long>("Player", "scaled");
            var observed = new List<long>();
            target.Bind(observed.Add, callImmediately: false);

            target.Proxy(source, value => value * 10L);
            source.Set(3);
            target.Set(99L);
            source.Set(4);
            target.Proxy((Bindable<int>)source, value => value * 100L);
            source.Set(5);
            target.StopProxying();
            source.Set(6);

            Assert.That(target.Value, Is.EqualTo(500L));
            Assert.That(observed, Is.EqualTo(new[] { 20L, 30L, 99L, 400L, 500L }));
        }

        [Test]
        public void ProxyRejectsCyclesThroughComputedSourcesWithoutDetachingItsPreviousSource()
        {
            var stable = environment.GetValue("Player", "stable", 3);
            var target = environment.GetValue<int>("Player", "target");
            var side = environment.GetValue("Player", "side", 4);
            target.Proxy(stable);
            var direct = target.ConvertTo(value => value * 10);
            var projected = target.ConvertTo(value => value * 100L);
            var nested = projected.AggregateWith(side, (a, b) => a + b);

            Assert.Throws<InvalidOperationException>(() => target.Proxy(direct));
            Assert.Throws<InvalidOperationException>(() => target.Proxy(nested, value => (int)value));
            stable.Set(9);
            Assert.That(target.Value, Is.EqualTo(9));
        }

        [Test]
        public void ReadingAndObservingComputedValuesDoesNotChangeTheSavePayload()
        {
            var source = environment.GetValue<int>("Player", "score");
            var other = environment.GetValue<int>("Player", "bonus");
            source.Set(3);
            other.Set(7);
            environment.Save("Player");
            var path = Path.Combine(directory, "Player_default.ddsave");
            var before = File.ReadAllBytes(path);

            var converted = source.ConvertTo(value => value * 2L);
            var computed = converted.AggregateWith(other, (a, b) => a + b);
            var observed = 0L;
            ChangeHandler<long> handler = value => observed = value;
            computed.Bind(handler);
            Assert.That(observed, Is.EqualTo(13L));
            Assert.That(computed.Value, Is.EqualTo(13L));
            environment.Save("Player");
            computed.Unbind(handler);

            Assert.That(File.ReadAllBytes(path), Is.EqualTo(before));
            var restored = new DragonDataEnvironment(directory);
            restored.Load("Player", "default");
            Assert.That(restored.GetValue<int>("Player", "score").Value, Is.EqualTo(3));
            Assert.That(restored.GetValue<int>("Player", "bonus").Value, Is.EqualTo(7));
        }

        [Test]
        public void ComputedArgumentsRequireASelectorAndSourceCollection()
        {
            var source = environment.GetValue("Player", "score", 2);
            Func<int, int> missingSelector = null;
            IEnumerable<Bindable<int>> missingInputs = null;

            Assert.Throws<ArgumentNullException>(() => source.ConvertTo(missingSelector));
            Assert.Throws<ArgumentNullException>(() => Bindable.Aggregate(missingInputs, values => values.Length));
        }

        [Test]
        public void FailedConnectionDetachesSourcesAndDoesNotLeaveAHandlerRegistered()
        {
            var first = environment.GetValue("Player", "first", 1);
            var second = environment.GetValue("Player", "second", 2);
            var aggregate = Bindable.Aggregate(first, second, (a, b) => a + b);
            var detachments = 0;
            first.OnSubscriptionsCleared += () => detachments++;
            Action failConnection = () => throw new InvalidOperationException("Connection failed.");
            Action failDisconnection = () => throw new InvalidOperationException("Disconnection failed.");
            second.OnAnySubscription += failConnection;
            second.OnSubscriptionsCleared += failDisconnection;
            var observations = new List<int>();
            ChangeHandler<int> handler = observations.Add;

            var error = Assert.Throws<InvalidOperationException>(() => aggregate.Bind(handler, callImmediately: false));
            Assert.That(error.Message, Is.EqualTo("Connection failed."));
            Assert.That(detachments, Is.EqualTo(1));
            first.Set(4);
            Assert.That(observations, Is.Empty);

            second.OnAnySubscription -= failConnection;
            second.OnSubscriptionsCleared -= failDisconnection;
            aggregate.Bind(handler, callImmediately: false);
            first.Set(6);
            aggregate.Unbind(handler);
            Assert.That(observations, Is.EqualTo(new[] { 8 }));
            Assert.That(detachments, Is.EqualTo(2));
        }

        [Test]
        public void FixedArityComputationAllocatesNoManagedMemoryOnTheWarmedPath()
        {
            var first = environment.GetValue("Player", "first", 1);
            var second = environment.GetValue("Player", "second", 2);
            var aggregate = Bindable.Aggregate(first, second, (a, b) => a + b);
            var observed = 0;
            ChangeHandler<int> handler = value => observed = value;
            aggregate.Bind(handler);
            first.Set(-1);
            _ = aggregate.Value;

            const int operations = 100_000;
            var timer = new System.Diagnostics.Stopwatch();
            timer.Start();
            var initialBytes = GC.GetAllocatedBytesForCurrentThread();
            var total = 0L;
            for (var i = 0; i < operations; i++)
            {
                first.Set(i);
                total += aggregate.Value;
            }
            var allocatedBytes = GC.GetAllocatedBytesForCurrentThread() - initialBytes;
            timer.Stop();
            aggregate.Unbind(handler);

            Assert.That(observed, Is.EqualTo(operations + 1));
            Assert.That(total, Is.EqualTo((long)operations * (operations + 3) / 2));
            Assert.That(allocatedBytes, Is.Zero);
            TestContext.WriteLine($"{operations} aggregate updates and reads: {timer.Elapsed.TotalMilliseconds:F3} ms, {allocatedBytes} allocated bytes.");
        }
    }
}
