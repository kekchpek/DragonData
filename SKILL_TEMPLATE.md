---
name: dragondata
description: Подключение DragonData, работа с Bindable и Mutable, вычисляемыми значениями, сохранениями и миграциями данных в Unity-проектах.
---

# DragonData

## Подключение к Unity

Найдите пакет `com.kekchpek.dragon-data` в `Packages/manifest.json` или `Packages/com.kekchpek.dragon-data`. Для Git-зависимости точный коммит указан в `Packages/packages-lock.json`. Сверяйте примеры с README и публичным API установленного пакета. Для встроенного пакета его файлы лежат в `Packages/com.kekchpek.dragon-data`, для Git-зависимости — в каталоге пакета в `Library/PackageCache`.

Для новой установки откройте **Window → Package Manager**, выберите **+ → Install package from git URL** и укажите `https://github.com/kekchpek/DragonData.git`. При ручном редактировании добавьте запись в существующий объект `dependencies` файла `Packages/manifest.json`.

```text
"com.kekchpek.dragon-data": "https://github.com/kekchpek/DragonData.git"
```

## Данные и подписки

- Импортируйте `Kekchpek.DragonData`. Получайте `Mutable<T>` через `DragonData.GetValue<T>(containerName, dataName, defaultValue)`. Первая операция чтения или записи инициализирует отсутствующую запись. Без явного значения используется `default(T)`. Тип должен быть `unmanaged`.
- Используйте `Value` или `Set(value)` для записи. `ForceSet(value)` уведомляет даже при равенстве значений. Передавайте `Bindable<T>` коду, которому нужны чтение и подписки. Преобразование из `Mutable<T>` неявное, оба дескриптора обращаются к одной записи.
- Подписывайтесь через `Bind` и снимайте подписку тем же обработчиком через `Unbind`. Поддерживаются `Action`, `ChangeHandler<T>` и `Action<T>`, `Action<T, T>` для пары «предыдущее, новое» и `RefChangeHandler<T>` с параметром `in T`. Используйте `callImmediately: false`, чтобы пропустить начальный вызов. Обработчик пары вызывается только при уведомлениях.
- Для вычисляемых значений применяйте `ConvertTo`, `AggregateWith` и `Bindable.Aggregate`. Они возвращают `Bindable<T>` с результатом вычисления в памяти. Источники наблюдаются, пока у результата есть подписчики. Для записи в именованный контейнер явно присвойте результат или используйте `Mutable<T>.Proxy(source)`.

```csharp
using Kekchpek.DragonData;

var coins = DragonData.GetValue<int>("Player", "Coins", 10);
DragonData.Load("Player", "default");
Bindable<bool> canBuy = coins.ConvertTo(count => count >= 25);
coins.Set(coins.Value + 5);
DragonData.Save("Player");
```

## Сохранения и миграции

- `Load(containerName, saveName)` синхронно заменяет данные и выбирает активный слот при успешной загрузке. После вызова перечитайте данные для обновления интерфейса. Ошибка формата, контрольной суммы или миграции логируется с откатом состояния контейнера.
- `Save(containerName)` сохраняет активный слот, `Save(containerName, overrideSaveName)` создаёт отдельную копию, `SaveAll()` сохраняет все контейнеры. Начальный слот — `default`. Файлы `ContainerName_SaveName.ddsave` лежат в `Application.persistentDataPath/GameSaves`. Для отдельного набора контейнеров используйте `new DragonDataEnvironment(saveDirectory)`.
- Регистрируйте `SetConverter<Legacy, Current>(converter)` до загрузки. Сохраняйте исходное полное имя и раскладку типа `Legacy`, чтобы DragonData смог проверить и прочитать прежние данные. Используйте отдельный тип `Current` для новой схемы. Один загрузочный проход применяет один конвертер к каждому исходному типу. Конфликт ключей после миграции вызывает откат.
- Для AOT-сборок до загрузки получите дескрипторы сохраняемых типов или зарегистрируйте их конвертеры. Используйте окружение с одного потока. Для часто записываемых пользовательских структур реализуйте `IEquatable<T>` и переиспользуйте полученные дескрипторы.
