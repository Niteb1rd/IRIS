using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using Iris.Core.Config;

namespace Iris.App.Services;

internal static class NativeLibraries
{
    public static IReadOnlyList<string> Loaded { get; private set; } = Array.Empty<string>();

    public static void LoadFrom(string dataFolder)
    {
        var loaded = new List<string>();
        foreach (var name in DataPaths.NativeLibraryNames)
        {
            var path = Path.Combine(dataFolder, name);
            if (!File.Exists(path))
                continue;
            try
            {
                NativeLibrary.Load(path);
            }
            catch (Exception ex) when (ex is DllNotFoundException or BadImageFormatException)
            {
                throw new InvalidOperationException($"{name} in the Data folder could not be loaded ({ex.Message}). Copy the Data folder from the IRIS download again.", ex);
            }
            loaded.Add(name);
        }
        Loaded = loaded;
    }
}
