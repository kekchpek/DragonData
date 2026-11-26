using System;

namespace Kekchpek.DragonData
{
    /// <summary>Accesses the application's shared DragonData environment.</summary>
    public static class DragonData
    {
        private static DragonDataEnvironment Environment => Shared.Instance;

        private static class Shared
        {
            internal static readonly DragonDataEnvironment Instance = new DragonDataEnvironment();

            static Shared() { }
        }

        public static Mutable<T> GetValue<T>(string containerName, string dataName) where T : unmanaged
            => Environment.GetValue<T>(containerName, dataName);

        public static Mutable<T> GetValue<T>(string containerName, string dataName, T defaultValue) where T : unmanaged
            => Environment.GetValue(containerName, dataName, defaultValue);

        public static void SetConverter<T1, T2>(Func<T1, T2> converter) where T1 : unmanaged where T2 : unmanaged
            => Environment.SetConverter(converter);

        public static void Load(string containerName, string saveName) => Environment.Load(containerName, saveName);

        public static void Save(string containerName) => Environment.Save(containerName);

        public static void Save(string containerName, string overrideSaveName)
            => Environment.Save(containerName, overrideSaveName);

        public static void SaveAll() => Environment.SaveAll();
    }
}
