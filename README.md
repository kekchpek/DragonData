# DragonData

DragonData stores application data in named Unity containers and provides typed handles for reading, writing, and observing values. Each entry is identified by its container name, value type, and data name.

## Install

In Unity, open **Window → Package Manager**, select **+ → Install package from git URL**, and enter `https://github.com/kekchpek/DragonData.git`. The package requires Unity 6.2 or later.

## Read, write, and observe

`GetValue<T>` returns a `Mutable<T>`. The first read or write of a missing entry initializes it from the handle's default. The two-argument overload uses `default(T)`. Handles to the same entry share its value, and a mutable handle converts implicitly to a read-only `Bindable<T>`.

```csharp
using Kekchpek.DragonData;
using UnityEngine;

var coins = DragonData.GetValue<int>("Player", "Coins", 10);
DragonData.Load("Player", "default");

Bindable<int> display = coins;
ChangeHandler<int> render = count => Debug.Log(count);
display.Bind(render);

coins.Value += 5;
DragonData.Save("Player");

display.Unbind(render);
```

Use `Bind` and `Unbind` with an `Action`, a `ChangeHandler<T>` or `Action<T>`, an `Action<T, T>` receiving the previous and current values, or a `RefChangeHandler<T>` receiving the current value as `in T`. The previous/current overload starts with the next change. The other overloads call immediately by default and accept `callImmediately: false`. Keep the handler so you can remove it during teardown. `OnAnySubscription` and `OnSubscriptionsCleared` on a mutable handle observe subscription changes.

`Value` and `Set` notify when the value compares unequal. Use `ForceSet` to publish even when the value compares equal. `Proxy(source)` copies and follows a source until an explicit assignment or `StopProxying()`.

## Computed values

Use `ConvertTo` to project a value, `AggregateWith` to combine two values, or `Bindable.Aggregate` to combine two to four typed values or a collection of values of the same type. The result is a read-only handle backed by separate in-memory storage. Reads evaluate against the current sources. Source subscriptions last as long as the result has listeners. A computed change reports its last observed result and the newly evaluated result. Use side-effect-free projection and aggregation functions.

```csharp
var gold = DragonData.GetValue<int>("Player", "Gold");
var gems = DragonData.GetValue<int>("Player", "Gems");

Bindable<bool> canBuy = gold.ConvertTo(amount => amount >= 25);
Bindable<int> currencyTotal = Bindable.Aggregate(gold, gems, (g, s) => g + s);
```

When mixing mutable and read-only inputs in a three- or four-source call, pass the inputs as `Bindable<T>` so C# can infer every type. To copy a computed result into a stored entry, call `destination.Proxy(computed)` or assign its value.

## Save slots

A new container uses the `default` slot. `Save(containerName)` writes that container to its active slot, `Save(containerName, overrideSaveName)` exports a copy under another name, and `SaveAll()` writes each container to its active slot. The default directory is `Application.persistentDataPath/GameSaves`, with files named `ContainerName_SaveName.ddsave`. Use `new DragonDataEnvironment(saveDirectory)` for an isolated set of containers and a custom save directory.

`Load(containerName, saveName)` replaces the container contents and selects the slot after a successful read. Existing handles remain usable. Refresh any views after loading, since change handlers run on writes and proxy updates. Missing files are treated as a no-op. Checksum, layout, or migration failures log an error and restore the previous data and active slot. Save errors propagate to the caller.

Each file ends with a SHA-256 checksum. DragonData writes and verifies a temporary file in the same directory before replacing the destination.

## Migrate saved types

Keep the legacy struct and its layout available, define a new type, and register a converter before loading. DragonData validates the saved layout against the legacy type, then applies one registered conversion per source type, keeping each data name. Distinct keys that migrate to the same type merge into its subcontainer. Duplicate destination keys fail the load.

```csharp
DragonData.SetConverter<LegacyProgress, Progress>(old => new Progress
{
    Level = old.Level,
    Experience = 0
});
DragonData.Load("Player", "default");
```

## Runtime constraints

- Values must be unmanaged. Persistence requires matching field names, in-memory sizes and offsets, and platform byte order. Process-local pointers and handles represented by `IntPtr` or `UIntPtr` are excluded from saves.
- Use each environment on one thread. Save and load are synchronous. On AOT builds, obtain handles for persisted types or register their converters before loading so the necessary typed subcontainers are available.
- Reuse handles on frequently executed paths. For custom structs, implement `IEquatable<T>` to avoid boxing during ordinary equality checks. Fixed-arity computed values evaluate without a temporary values array. The collection overload of `Aggregate` passes a fresh array to its function on each evaluation.

## Run tests

Open the development project and run **EditMode** tests in Unity Test Runner. A separate Unity project needs Unity Test Framework and the package name `com.kekchpek.dragon-data` in the `testables` array of its `Packages/manifest.json` to run tests from an installed Git package.
