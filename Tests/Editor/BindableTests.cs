using System;
using System.Collections.Generic;
using System.IO;
using NUnit.Framework;

namespace Kekchpek.DragonData.Tests
{
    public class BindableTests
    {
        [Test]
        public void FirstAccessSetsTheDefaultAndAllCopiesShareTheEntry()
        {
            var data = new DragonDataEnvironment(Path.GetTempPath());
            var first = data.GetValue("game", "score", 3);
            var second = data.GetValue("game", "score", 7);
            Bindable<int> reader = first;

            Assert.That(second.Value, Is.EqualTo(7));
            Assert.That(reader.Value, Is.EqualTo(7));
            data.GetValue<int>("game", "score").Set(10);
            Assert.That(first.Value, Is.EqualTo(10));
            Assert.That(reader.Value, Is.EqualTo(10));
            Assert.That(data.GetValue("game", "score", 2.5f).Value, Is.EqualTo(2.5f));
            Assert.That(data.GetValue("other", "score", 23).Value, Is.EqualTo(23));
            Assert.That(data.GetValue<int>("game", "visits").Value, Is.Zero);
        }

        [Test]
        public void DefaultHandlesReportThatTheyNeedAnEntry()
        {
            var mutable = default(Mutable<int>);
            Bindable<int> bindable = mutable;

            Assert.That(Assert.Throws<InvalidOperationException>(() => mutable.Set(8)).Message,
                Does.Contain("GetValue"));
            Assert.That(Assert.Throws<InvalidOperationException>(() => _ = bindable.Value).Message,
                Does.Contain("GetValue"));
        }

        [Test]
        public void AllSubscriptionShapesReceiveChangesAndCanBeRemovedThroughAnotherHandle()
        {
            var data = new DragonDataEnvironment(Path.GetTempPath());
            var mutable = data.GetValue("game", "score", 3);
            Bindable<int> readOnly = mutable;
            var notifications = new List<string>();
            var additions = 0;
            var cleared = 0;
            Action onBound = () => additions++;
            Action onCleared = () => cleared++;
            Action blind = () => notifications.Add("blind");
            Action<int, int> full = (previous, current) => notifications.Add($"{previous}->{current}");
            ChangeHandler<int> change = value => notifications.Add($"value={value}");
            RefChangeHandler<int> byRef = (in int value) => notifications.Add($"ref={value}");

            mutable.OnAnySubscription += onBound;
            mutable.OnSubscriptionsCleared += onCleared;
            readOnly.Bind(blind, callImmediately: false);
            mutable.Bind(full);
            readOnly.Bind(change);
            mutable.Bind(byRef);
            Assert.That(additions, Is.EqualTo(4));
            Assert.That(notifications, Is.EqualTo(new[] { "value=3", "ref=3" }));

            notifications.Clear();
            mutable.Value = 9;
            mutable.Value = 9;
            Assert.That(notifications, Is.EqualTo(new[] { "blind", "3->9", "value=9", "ref=9" }));

            mutable.Unbind(blind);
            readOnly.Unbind(full);
            mutable.Unbind(change);
            Assert.That(cleared, Is.Zero);
            readOnly.Unbind(byRef);
            readOnly.Unbind(byRef);
            Assert.That(cleared, Is.EqualTo(1));

            notifications.Clear();
            mutable.Value = 11;
            Assert.That(notifications, Is.Empty);
            mutable.OnAnySubscription -= onBound;
            mutable.OnSubscriptionsCleared -= onCleared;
            mutable.Bind(blind);
            Assert.That(notifications, Is.EqualTo(new[] { "blind" }));
            mutable.Unbind(blind);
            Assert.That(additions, Is.EqualTo(4));
            Assert.That(cleared, Is.EqualTo(1));
        }

        [Test]
        public void DuplicateRemovalLeavesTheOlderRegistrationInOrder()
        {
            var data = new DragonDataEnvironment(Path.GetTempPath());
            var entry = data.GetValue<int>("game", "score");
            var order = new List<string>();
            ChangeHandler<int> duplicate = _ => order.Add("duplicate");
            ChangeHandler<int> middle = _ => order.Add("middle");
            ChangeHandler<int> tail = _ => order.Add("tail");

            entry.Bind(duplicate, callImmediately: false);
            entry.Bind(middle, callImmediately: false);
            entry.Bind(duplicate, callImmediately: false);
            entry.Bind(tail, callImmediately: false);
            entry.Unbind(duplicate);
            entry.Value = 1;
            Assert.That(order, Is.EqualTo(new[] { "duplicate", "middle", "tail" }));

            order.Clear();
            entry.Unbind(duplicate);
            entry.Value = 2;
            Assert.That(order, Is.EqualTo(new[] { "middle", "tail" }));
        }

        [Test]
        public void TypedActionCallbacksCanBeRemovedWithAnEqualDelegate()
        {
            var data = new DragonDataEnvironment(Path.GetTempPath());
            var entry = data.GetValue("game", "score", 6);
            Bindable<int> reader = entry;
            var values = new List<int>();
            Action<int> action = new Action<int>(values.Add);
            Action<int> equalAction = new Action<int>(values.Add);

            reader.Bind(action);
            entry.Bind(action, callImmediately: false);
            reader.Unbind(equalAction);
            entry.Set(7);
            entry.Unbind(equalAction);
            entry.Set(8);

            Assert.That(values, Is.EqualTo(new[] { 6, 7 }));
        }

        [Test]
        public void NestedDispatchSeesNewHandlersWhileTheOuterDispatchSkipsThemAndRemovedHandlers()
        {
            var data = new DragonDataEnvironment(Path.GetTempPath());
            var entry = data.GetValue<int>("game", "score");
            var order = new List<string>();
            ChangeHandler<int> removed = value => order.Add($"removed={value}");
            ChangeHandler<int> added = value => order.Add($"added={value}");
            ChangeHandler<int> first = value =>
            {
                order.Add($"first={value}");
                if (value == 1)
                {
                    entry.Unbind(removed);
                    entry.Bind(added, callImmediately: false);
                    entry.Value = 2;
                }
            };

            entry.Bind(first, callImmediately: false);
            entry.Bind(removed, callImmediately: false);
            entry.Value = 1;
            Assert.That(order, Is.EqualTo(new[] { "first=1", "first=2", "added=2" }));
            Assert.That(entry.Value, Is.EqualTo(2));

            order.Clear();
            entry.Bind(removed, callImmediately: false);
            entry.Value = 3;
            Assert.That(order, Is.EqualTo(new[] { "first=3", "added=3", "removed=3" }));
        }

        [Test]
        public void ForceSetNotifiesForAnEqualValueAndExplicitWritesBreakProxying()
        {
            var data = new DragonDataEnvironment(Path.GetTempPath());
            var destination = data.GetValue<int>("game", "destination");
            var source = data.GetValue("game", "source", 7);
            var values = new List<int>();
            destination.Bind(values.Add, callImmediately: false);

            destination.Proxy(source);
            source.Value = 8;
            source.ForceSet(8);
            destination.Value = 8;
            source.Value = 9;
            Assert.That(values, Is.EqualTo(new[] { 7, 8, 8 }));
            Assert.That(destination.Value, Is.EqualTo(8));

            destination.Proxy(source);
            destination.ForceSet(9);
            source.Value = 10;
            destination.Proxy(source);
            destination.StopProxying();
            source.Value = 11;
            Assert.That(values, Is.EqualTo(new[] { 7, 8, 8, 9, 9, 10 }));
            Assert.That(destination.Value, Is.EqualTo(10));
        }

        [Test]
        public void ProxyRejectsSelfAndCyclesWithoutBreakingAWorkingChain()
        {
            var data = new DragonDataEnvironment(Path.GetTempPath());
            var first = data.GetValue<int>("game", "first");
            var second = data.GetValue<int>("game", "second");
            var source = data.GetValue("game", "source", 5);

            first.Proxy(second);
            second.Proxy(source);
            Assert.Throws<InvalidOperationException>(() => first.Proxy(first));
            Assert.Throws<InvalidOperationException>(() => source.Proxy(first));
            source.Value = 7;
            Assert.That(first.Value, Is.EqualTo(7));
            Assert.That(second.Value, Is.EqualTo(7));
        }
    }
}
