using System;
using System.IO;
using System.Reflection;

using Jotunn.Entities;
using Jotunn.Managers;

namespace DvergrWorkers;

/// <summary>
/// Loads every embedded Translations/&lt;Language&gt;.json into Jotunn. Missing languages and keys fall back to English.
/// </summary>
internal static class Translations
{
    private const string ResourcePrefix = "DvergrWorkers.Translations.";
    private const string ResourceSuffix = ".json";

    public static void Load()
    {
        CustomLocalization localization = LocalizationManager.Instance.GetLocalization();
        Assembly assembly = Assembly.GetExecutingAssembly();
        foreach (string resource in assembly.GetManifestResourceNames())
        {
            if (!resource.StartsWith(ResourcePrefix, StringComparison.Ordinal)
                || !resource.EndsWith(ResourceSuffix, StringComparison.Ordinal))
            {
                continue;
            }

            string language = resource.Substring(
                ResourcePrefix.Length,
                resource.Length - ResourcePrefix.Length - ResourceSuffix.Length);
            using StreamReader reader = new(assembly.GetManifestResourceStream(resource));
            localization.AddJsonFile(language, reader.ReadToEnd());
        }
    }
}
