using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text;
using BepInEx;
using BepInEx.Bootstrap;
using BepInEx.Configuration;
using HarmonyLib;
using UnityEngine;
using YamlDotNet.Serialization;

// Adapted from AzumattDev/LocalizationManager, MIT-0.
namespace LocalizationManager;

public class Localizer
{
    private static readonly Dictionary<string, Dictionary<string, Func<string>>> PlaceholderProcessors = new();
    private static readonly Dictionary<string, Dictionary<string, string>> LoadedTexts = new();
    private static readonly ConditionalWeakTable<Localization, LanguageState> LocalizationLanguages = new();
    private static readonly List<WeakReference<Localization>> LocalizationObjects = [];
    private static readonly List<string> FileExtensions = [".json", ".yml"];
    private static BaseUnityPlugin? _plugin;

    public static event Action? OnLocalizationComplete;

    private static BaseUnityPlugin Plugin
    {
        get
        {
            if (_plugin is not null)
            {
                return _plugin;
            }

            IEnumerable<TypeInfo> types;
            try
            {
                types = Assembly.GetExecutingAssembly().DefinedTypes.ToList();
            }
            catch (ReflectionTypeLoadException e)
            {
                types = e.Types.Where(t => t != null).Select(t => t!.GetTypeInfo());
            }

            TypeInfo pluginType = types.First(t => t.IsClass && typeof(BaseUnityPlugin).IsAssignableFrom(t));
            _plugin = (BaseUnityPlugin)Chainloader.ManagerObject.GetComponent(pluginType);
            return _plugin;
        }
    }

    public static void Load()
    {
        _ = Plugin;
    }

    public static void AddPlaceholder<T>(
        string key,
        string placeholder,
        ConfigEntry<T> config,
        Func<T, string>? convertConfigValue = null) where T : notnull
    {
        convertConfigValue ??= value => value.ToString();
        if (!PlaceholderProcessors.ContainsKey(key))
        {
            PlaceholderProcessors[key] = new Dictionary<string, Func<string>>();
        }

        void UpdatePlaceholder()
        {
            PlaceholderProcessors[key][placeholder] = () => convertConfigValue(config.Value);
            if (Localization.instance != null)
            {
                UpdatePlaceholderText(Localization.instance, key);
            }
        }

        config.SettingChanged += (_, _) => UpdatePlaceholder();
        if (Localization.instance != null && LoadedTexts.ContainsKey(Localization.instance.GetSelectedLanguage()))
        {
            UpdatePlaceholder();
        }
    }

    public static void AddText(string key, string text)
    {
        List<WeakReference<Localization>> remove = [];
        foreach (WeakReference<Localization> reference in LocalizationObjects)
        {
            if (!reference.TryGetTarget(out Localization localization))
            {
                remove.Add(reference);
                continue;
            }

            if (!LocalizationLanguages.TryGetValue(localization, out LanguageState state) ||
                !LoadedTexts.TryGetValue(state.Language, out Dictionary<string, string> texts) ||
                localization.m_translations.ContainsKey(key))
            {
                continue;
            }

            texts[key] = text;
            localization.AddWord(key, text);
        }

        foreach (WeakReference<Localization> reference in remove)
        {
            LocalizationObjects.Remove(reference);
        }
    }

    public static void LoadLocalizationLater()
    {
        if (Localization.instance != null)
        {
            LoadLocalization(Localization.instance, Localization.instance.GetSelectedLanguage());
        }
    }

    public static void SafeCallLocalizeComplete()
    {
        OnLocalizationComplete?.Invoke();
    }

    private static void LoadLocalization(Localization __instance, string language)
    {
        if (!LocalizationLanguages.Remove(__instance))
        {
            LocalizationObjects.Add(new WeakReference<Localization>(__instance));
        }

        LocalizationLanguages.Add(__instance, new LanguageState(language));

        Dictionary<string, string> localizationFiles = FindExternalLocalizationFiles();
        if (LoadTranslationFromAssembly("English") is not { } englishAssemblyData)
        {
            throw new Exception($"Found no English localizations in mod {Plugin.Info.Metadata.Name}. Expected an embedded resource translations/English.json or translations/English.yml.");
        }

        Dictionary<string, string>? localizationTexts = Deserialize(Encoding.UTF8.GetString(englishAssemblyData));
        if (localizationTexts is null)
        {
            throw new Exception($"Localization for mod {Plugin.Info.Metadata.Name} failed: Localization file was empty.");
        }

        string? localizationData = null;
        if (language != "English")
        {
            if (localizationFiles.TryGetValue(language, out string localizationFile))
            {
                localizationData = File.ReadAllText(localizationFile);
            }
            else if (LoadTranslationFromAssembly(language) is { } languageAssemblyData)
            {
                localizationData = Encoding.UTF8.GetString(languageAssemblyData);
            }
        }

        if (localizationData is null && localizationFiles.TryGetValue("English", out string englishLocalizationFile))
        {
            localizationData = File.ReadAllText(englishLocalizationFile);
        }

        if (localizationData is not null)
        {
            foreach (KeyValuePair<string, string> entry in Deserialize(localizationData) ?? new Dictionary<string, string>())
            {
                localizationTexts[entry.Key] = entry.Value;
            }
        }

        LoadedTexts[language] = localizationTexts;
        foreach (string key in localizationTexts.Keys)
        {
            UpdatePlaceholderText(__instance, key);
        }
    }

    private static Dictionary<string, string> FindExternalLocalizationFiles()
    {
        Dictionary<string, string> localizationFiles = new();
        string searchRoot = Path.GetDirectoryName(Paths.PluginPath) ?? Paths.PluginPath;
        foreach (string file in Directory.GetFiles(searchRoot, $"{Plugin.Info.Metadata.Name}.*", SearchOption.AllDirectories)
                     .Where(file => FileExtensions.Contains(Path.GetExtension(file))))
        {
            string[] parts = Path.GetFileNameWithoutExtension(file).Split('.');
            if (parts.Length < 2)
            {
                continue;
            }

            string language = parts[1];
            if (localizationFiles.ContainsKey(language))
            {
                Debug.LogWarning($"Duplicate key {language} found for {Plugin.Info.Metadata.Name}. The duplicate file found at {file} will be skipped.");
                continue;
            }

            localizationFiles[language] = file;
        }

        return localizationFiles;
    }

    private static void UpdatePlaceholderText(Localization localization, string key)
    {
        if (!LocalizationLanguages.TryGetValue(localization, out LanguageState state) ||
            !LoadedTexts.TryGetValue(state.Language, out Dictionary<string, string> texts) ||
            !texts.TryGetValue(key, out string text))
        {
            return;
        }

        if (PlaceholderProcessors.TryGetValue(key, out Dictionary<string, Func<string>> textProcessors))
        {
            text = textProcessors.Aggregate(text, (current, entry) => current.Replace("{" + entry.Key + "}", entry.Value()));
        }

        localization.AddWord(key, text);
    }

    private static byte[]? LoadTranslationFromAssembly(string language)
    {
        foreach (string extension in FileExtensions)
        {
            if (ReadEmbeddedFileBytes("translations." + language + extension) is { } data)
            {
                return data;
            }
        }

        return null;
    }

    public static byte[]? ReadEmbeddedFileBytes(string resourceFileName, Assembly? containingAssembly = null)
    {
        using MemoryStream stream = new();
        containingAssembly ??= Assembly.GetExecutingAssembly();
        if (containingAssembly.GetManifestResourceNames()
            .FirstOrDefault(name => name.EndsWith(resourceFileName, StringComparison.Ordinal)) is { } resourceName)
        {
            containingAssembly.GetManifestResourceStream(resourceName)?.CopyTo(stream);
        }

        return stream.Length == 0 ? null : stream.ToArray();
    }

    private static Dictionary<string, string>? Deserialize(string data)
    {
        return new DeserializerBuilder().IgnoreFields().Build().Deserialize<Dictionary<string, string>?>(data);
    }

    static Localizer()
    {
        Harmony harmony = new("org.bepinex.helpers.LocalizationManager");
        harmony.Patch(
            AccessTools.DeclaredMethod(typeof(Localization), nameof(Localization.SetupLanguage)),
            postfix: new HarmonyMethod(AccessTools.DeclaredMethod(typeof(Localizer), nameof(LoadLocalization))));
        harmony.Patch(
            AccessTools.DeclaredMethod(typeof(FejdStartup), nameof(FejdStartup.SetupGui)),
            postfix: new HarmonyMethod(AccessTools.DeclaredMethod(typeof(Localizer), nameof(LoadLocalizationLater))));
        harmony.Patch(
            AccessTools.DeclaredMethod(typeof(FejdStartup), nameof(FejdStartup.Start)),
            postfix: new HarmonyMethod(AccessTools.DeclaredMethod(typeof(Localizer), nameof(SafeCallLocalizeComplete))));
    }

    private sealed class LanguageState(string language)
    {
        internal readonly string Language = language;
    }
}

public static class LocalizationManagerVersion
{
    public const string Version = "1.4.0";
}
