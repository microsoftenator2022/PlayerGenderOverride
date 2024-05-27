using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;

using HarmonyLib;

using Kingmaker;
using Kingmaker.Blueprints.Base;
using Kingmaker.Blueprints.JsonSystem;
using Kingmaker.Designers.EventConditionActionSystem.Conditions;
using Kingmaker.ElementsSystem.ContextData;
using Kingmaker.EntitySystem;
using Kingmaker.EntitySystem.Persistence;
using Kingmaker.Localization;
using Kingmaker.Localization.Enums;
using Kingmaker.PubSubSystem.Core;
using Kingmaker.Settings;
using Kingmaker.TextTools;
using Kingmaker.TextTools.Core;
using Kingmaker.UnitLogic.Parts;
using Kingmaker.Utility.DotNetExtensions;
using Kingmaker.Utility.Serialization;
using Kingmaker.Visual.Sound;

using Newtonsoft.Json;

using UnityEngine;

using UnityModManagerNet;

namespace PlayerGenderOverride;

static class Main
{
    [HarmonyPatch]
    internal static class Settings
    {
        const string SettingKey = $"{nameof(PlayerGenderOverride)}";
        const string CustomKey = $"{SettingKey}.Custom";
        const string CustomStringsKey = $"{CustomKey}.Strings";

        internal static Gender? Gender
        {
            set
            {
                if (!InGame) return;

                if (value is null)
                    SettingsController.Instance.InSaveSettingsProvider.RemoveKey(SettingKey);
                else
                    SettingsController.Instance.InSaveSettingsProvider.SetValue(SettingKey, value);
            }

            get
            {
                if (!InGame) return null;

                if (!SettingsController.Instance.InSaveSettingsProvider.HasKey(SettingKey)) return null;

                return SettingsController.Instance.InSaveSettingsProvider.GetValue<Gender>(SettingKey);
            }
        }

        internal static bool UseCustom
        {
            get =>
                SettingsController.Instance.InSaveSettingsProvider.HasKey(CustomKey) &&
                (SettingsController.Instance.InSaveSettingsProvider.GetValue<bool?>(CustomKey) ?? false);
            set
            {
                if (value)
                    SettingsController.Instance.InSaveSettingsProvider.SetValue(CustomKey, true);
                else if (SettingsController.Instance.InSaveSettingsProvider.HasKey(CustomKey))
                    SettingsController.Instance.InSaveSettingsProvider.RemoveKey(CustomKey);
            }
        }

        static Dictionary<string, string>? customStrings = null;

        [HarmonyPatch(typeof(ThreadedGameLoader), nameof(ThreadedGameLoader.DeserializeInGameSettings))]
        [HarmonyPrefix]
        static void ResetCustomStrings()
        {
#if DEBUG
            Main.Logger.Log("Reset custom strings");
#endif
            customStrings = null;
        }

        internal static Dictionary<string, string> CustomStrings
        {
            get
            {
                if (SettingsController.Instance.InSaveSettingsProvider.HasKey(CustomStringsKey) &&
                    customStrings is null &&
                    SettingsController.Instance.InSaveSettingsProvider.GetValue<string>(CustomStringsKey) is string json)
                {
#if DEBUG
                    Main.Logger.Log($"Load custom strings: {json}");
#endif

                    customStrings = Json.Serializer.DeserializeObject<Dictionary<string, string>>(json) ?? [];
                }

                return customStrings ??= [];
            }

            set
            {
                if (value is null || value.Empty())
                {
                    customStrings = [];

                    if (SettingsController.Instance.InSaveSettingsProvider.HasKey(CustomStringsKey))
                        SettingsController.Instance.InSaveSettingsProvider.RemoveKey(CustomStringsKey);

                    return;
                }

                customStrings = value;

                var json = Json.Serializer.SerializeObject(customStrings);

#if DEBUG
                Main.Logger.Log($"Save custom strings: {json}");
#endif

                SettingsController.Instance.InSaveSettingsProvider.SetValue(CustomStringsKey, json);
            }
        }
    }

    private static Harmony? harmonyInstance;
    internal static Harmony HarmonyInstance { get => harmonyInstance!; set => harmonyInstance = value; }
    
    private static UnityModManager.ModEntry.ModLogger? log;
    internal static UnityModManager.ModEntry.ModLogger Logger => log!;

    
    static bool InGame =>
        !Game.IsInMainMenu &&
        Game.Instance.SaveManager.CurrentState is not SaveManager.State.Loading &&
        //Game.Instance.UILoadingProgress >= 1.0 &&
        Game.Instance?.Player is not null;

    static (Locale, string[]) customKeys = ((Locale)(-1), []);
    static (int, int) progress = (0, 0);

    static Task<string[]>? loadKeysTask;

    static Task<string[]> GetCustomKeys() =>
        loadKeysTask ??= Task.Run(() =>
            {
                var (locale, keys) = customKeys;

                if (locale != LocalizationManager.Instance.CurrentLocale)
                {
                    locale = LocalizationManager.Instance.CurrentLocale;

                    // Match format:
                    // [prefix]{mf|<PcMaleString>|<PcFemaleString>}[suffix]
                    var regex =
#if DEBUG
                        new Regex(@"(?'prefix'\w*)\{mf\|(?'male'\w+)\|(?'female'\w+)\}(?'suffix'\w*)");
#else
                        new Regex(@"\w*\{mf\|\w+\|\w+\}\w*");
#endif

                    var list = new HashSet<string>();

                    var i = 0;
                    var total = LocalizationManager.Instance.CurrentPack.m_Strings.Count;

                    foreach (var s in LocalizationManager.Instance.CurrentPack.m_Strings.Values)
                    {
                        progress = (i, total);
                        foreach (var m in regex.Matches(s.Text).Cast<Match>().Where(m => m.Success))
                        {
#if DEBUG
                            if (!list.Contains(m.Value))
                            {
                                using var handle = ContextData<PooledStringBuilder>.Request();

                                var sb = handle.Builder;
                                sb.Clear();

                                sb.AppendLine($"New key string \"{m.Value}\"");

                                sb.AppendLine($"Prefix: \"{m.Groups["prefix"].Value}\"");
                                sb.AppendLine($"Suffix: \"{m.Groups["suffix"].Value}\"");
                                sb.AppendLine($"Male string: \"{m.Groups["male"].Value}\"");
                                sb.AppendLine($"Female string: \"{m.Groups["female"].Value}\"");

                                Main.Logger.Log(sb.ToString());

                                sb.Clear();
                            }
#endif

                            list.Add(m.Value);
                        }

                        i++;
                    }
                    keys = list.OrderBy(s => s).ToArray();
                    customKeys = (locale, keys);
                }
                
                return keys;
            });

    static bool Load(UnityModManager.ModEntry modEntry)
    {
        log = modEntry.Logger;

        modEntry.OnGUI = OnGUI;
        HarmonyInstance = new Harmony(modEntry.Info.Id);
        HarmonyInstance.PatchAll(Assembly.GetExecutingAssembly());
        return true;
    }

    static string ExportPath =>
        Path.Combine(
            Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location),
            $"{nameof(PlayerGenderOverride)}.custom.{LocalizationManager.Instance.CurrentLocale}.json");

    static bool Export(Dictionary<string, string> entries)
    {
        File.WriteAllText(ExportPath, JsonConvert.SerializeObject(entries, Formatting.Indented));

        return true;
    }

    static Dictionary<string, string>? Import()
    {
        if (File.Exists(ExportPath))
            return JsonConvert.DeserializeObject<Dictionary<string, string>>(File.ReadAllText(ExportPath))!;

        else return null;
    }

    static void OnGUI(UnityModManager.ModEntry modEntry)
    {
        Dictionary<string, string> customMap = [];

        GUILayout.BeginVertical();
        {
            GUI.enabled = InGame;
            GUILayout.BeginHorizontal();
            {
                GUILayout.Label("Force gender:");

                int selected = Settings.Gender is null ? 0 : ((int)Settings.Gender + 1);

                selected = GUILayout.SelectionGrid(selected, ["Disabled", "Male", "Female"], 3, "toggle");

                Settings.Gender = selected switch
                {
                    1 => Gender.Male,
                    2 => Gender.Female,
                    _ => null
                };

                GUILayout.FlexibleSpace();
            }
            GUILayout.EndHorizontal();
            
            if (Settings.Gender == null)
            {
                Settings.UseCustom = false;
                GUI.enabled = false;
            }

            GUILayout.BeginHorizontal();
            {
                Settings.UseCustom = GUILayout.Toggle(Settings.UseCustom, "Custom text");

                GUI.enabled = Settings.UseCustom;
                if (GUILayout.Button("Export"))
                {
                    Export(customMap);
                }
                
                GUI.enabled = Settings.UseCustom && File.Exists(ExportPath);
                if (GUILayout.Button("Import"))
                {
                    customMap = Import() ?? customMap;
                }
                GUI.enabled = Settings.UseCustom;

                GUILayout.FlexibleSpace();
            }
            GUILayout.EndHorizontal();

            if (Settings.UseCustom)
            {
                static Dictionary<string, string>? getCustomMap()
                {
                    var value = Settings.CustomStrings;
                    return value.Empty() ? null : value;
                }

                customMap = getCustomMap() ?? customMap;
                var getKeysTask = GetCustomKeys();
                
                var (i, total) = progress;

                if (!getKeysTask.IsCompleted)
                {
                    GUILayout.Label($"Parsing strings"
#if DEBUG
                        + $" {i + 1}/{total}"
#endif
                        );
                }

                foreach (var key in getKeysTask.IsCompleted ? getKeysTask.Result : [])
                {
                    GUILayout.BeginHorizontal();
                    {
                        GUILayout.Space(24);

                        customMap.TryGetValue(key, out var value);
                        
                        var en = value != null;
                        
                        en = GUILayout.Toggle(en, $"{key}", GUILayout.Width(200));
                        
                        if (!en)
                        {
                            value = null;
                            GUI.enabled = false;
                        }
                        var customText = "";
                        
                        customText = GUILayout.TextField(value ?? "", 80, GUILayout.Width(200));
                        
                        value = en ? customText : null;

                        if (value is null) customMap.Remove(key);
                        else customMap[key] = customText;

                        GUI.enabled = true;

                        GUILayout.Label(TextTemplateEngine.Instance.Process(en ? customText : key));

                        GUILayout.FlexibleSpace();
                    }
                    GUILayout.EndHorizontal();
                }

                //SettingsController.Instance.InSaveSettingsProvider.SetValue(CustomStringsKey, customMap);
                Settings.CustomStrings = customMap;
            }
            else
            {
                //SettingsController.Instance.InSaveSettingsProvider.RemoveKey(CustomStringsKey);
                Settings.CustomStrings = [];
            }
        }
        GUILayout.EndVertical();
        GUI.enabled = true;
    }
}

[HarmonyPatch]
static class GenderPatch
{
    static Gender GenderOverride(Gender pcGender) => Main.Settings.Gender ?? pcGender;

    [HarmonyPatch(typeof(SoundUtility), nameof(SoundUtility.SetGenderFlags))]
    [HarmonyTranspiler]
    static IEnumerable<CodeInstruction> SetGenderFlags_Transpiler(IEnumerable<CodeInstruction> instructions)
    {
        foreach (var i in instructions)
        {
            yield return i;

            if (i.Calls(AccessTools.PropertyGetter(typeof(IAbstractUnitEntity), nameof(IAbstractUnitEntity.Gender))))
            {
                yield return new CodeInstruction(OpCodes.Call, AccessTools.Method(typeof(GenderPatch), nameof(GenderPatch.GenderOverride)));
            }
        }
    }

    static Gender PlayerGenderOverride(PartUnitDescription description, Gender gender)
    {
        if (description.Owner != Game.Instance.Player.MainCharacterEntity) return gender;

        return GenderOverride(gender);
    }

    [HarmonyPatch(typeof(BaseTextTemplateEngine), nameof(BaseTextTemplateEngine.Process))]
    [HarmonyPrefix]
    static void BaseTextTemplateEngine_Process_Patch(ref string text)
    {
        if (!Main.Settings.UseCustom || Main.Settings.CustomStrings.Empty())
            return;

        var customMap = Main.Settings.CustomStrings;

        if (customMap is null)
        {
            Main.Logger.Error("Failed to load custom strings");
            return;
        }

        foreach (var pair in customMap)
        {
            text = text.Replace(pair.Key, pair.Value);
        }
    }

    [HarmonyPatch(typeof(MaleFemaleTemplate), nameof(MaleFemaleTemplate.Generate))]
    [HarmonyTranspiler]
    static IEnumerable<CodeInstruction> MaleFemaleTemplate_Generate_Transpiler(IEnumerable<CodeInstruction> instructions)
    {
        foreach (var i in instructions)
        {
            if (i.Calls(AccessTools.PropertyGetter(typeof(PartUnitDescription), nameof(PartUnitDescription.Gender))))
            {
                yield return new(OpCodes.Dup) { labels = i.labels };
                i.labels = [];
                yield return i;

                yield return new(OpCodes.Call, AccessTools.Method(typeof(GenderPatch), nameof(GenderPatch.PlayerGenderOverride)));
            }
            else yield return i;
        }
    }

    [HarmonyPatch(typeof(PcFemale), nameof(PcFemale.CheckCondition))]
    [HarmonyPostfix]
    static bool IsFemale_Patch(bool __result)
    {
        if (Main.Settings.Gender is null) return __result;

        return Main.Settings.Gender == Gender.Female;
    }

    [HarmonyPatch(typeof(PcMale), nameof(PcMale.CheckCondition))]
    [HarmonyPostfix]
    static bool IsMale_Patch(bool __result)
    {
        if (Main.Settings.Gender is null) return __result;

        return Main.Settings.Gender == Gender.Male;
    }
}

//[HarmonyPatch]
//static class UIPatch
//{
//    [HarmonyPatch(typeof(CharGenAppearancePhaseDetailedView), nameof(CharGenAppearancePhaseDetailedView.Initialize))]
//    static void CharGenAppearancePhaseDetailedView_Patch(CharGenAppearancePhaseDetailedView __instance)
//    {
//        var go = __instance.gameObject;

//        Main.Logger.Log(go.name);

//        var t = go.transform.Find("DeviceParent/Screen_view/ItemView/ServiceWindowStandardScrollView/Viewport/Content");
//        if (t == null)
//            return;

//        var content = t.gameObject;
//    }
//}
