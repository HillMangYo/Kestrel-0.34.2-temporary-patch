using BepInEx;
using BepInEx.Configuration;
using BepInEx.Logging;
using HarmonyLib;
using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using UnityEngine;

namespace KestrelCompatibility
{
    [BepInPlugin(PluginGuid, PluginName, PluginVersion)]
    [BepInProcess("NuclearOption.exe")]
    [BepInDependency("blueprinter.kestrel", BepInDependency.DependencyFlags.HardDependency)]
    public sealed class Plugin : BaseUnityPlugin
    {
        public const string PluginGuid = "local.nuclearoption.kestrelcompatibility";
        public const string PluginName = "Kestrel Compatibility";
        public const string PluginVersion = "0.1.6";

        internal static ConfigEntry<string> DonorAircraftKey;
        internal static ConfigEntry<bool> ReplaceHud;
        internal static ConfigEntry<bool> ReplaceStatusDisplay;
        internal static ConfigEntry<bool> ReplaceTacScreen;
        internal static ConfigEntry<bool> ReplaceWheelMaterials;
        internal static ManualLogSource PluginLogger;

        private Harmony harmony;

        private void Awake()
        {
            PluginLogger = Logger;

            DonorAircraftKey = Config.Bind(
                "Compatibility",
                "Donor aircraft key",
                "trainer",
                "Internal aircraft key to borrow current interface and wheel assets from. 'trainer' is the T/A-30 Compass.");

            ReplaceHud = Config.Bind(
                "Compatibility",
                "Replace HUD",
                true,
                "Use the donor aircraft's current HUD extras on the Kestrel.");

            ReplaceStatusDisplay = Config.Bind(
                "Compatibility",
                "Replace status display",
                false,
                "Use the donor aircraft's current status display on the Kestrel.");

            ReplaceTacScreen = Config.Bind(
                "Compatibility",
                "Replace tactical screen",
                true,
                "Use the donor aircraft's current cockpit tactical-screen interface on the Kestrel.");

            ReplaceWheelMaterials = Config.Bind(
                "Compatibility",
                "Replace wheel materials",
                true,
                "Repair missing Kestrel tire slots with a current dark rubber material.");

            harmony = new Harmony(PluginGuid);
            harmony.PatchAll(typeof(Plugin).Assembly);
            StartCoroutine(CompatibilityFix.ApplyPrefabWheelsWhenReady());

            Logger.LogInfo("Loaded. Kestrel compatibility swaps will be applied when the Kestrel spawns.");
        }
    }

    internal static class CompatibilityFix
    {
        private const string KestrelKey = "kestrel";

        private static readonly FieldInfo TacScreenPrefabField =
            AccessTools.Field(typeof(Cockpit), "tacScreenUIPrefab");

        private static readonly FieldInfo LandingGearWheelsField =
            AccessTools.Field(typeof(LandingGear), "wheels");

        private static readonly Type BlueprinterPluginType =
            AccessTools.TypeByName("Blueprinter.Plugin");

        private static readonly PropertyInfo BlueprinterInstanceProperty =
            BlueprinterPluginType == null ? null : AccessTools.Property(BlueprinterPluginType, "Instance");

        private static readonly PropertyInfo BlueprinterPatchingCompleteProperty =
            BlueprinterPluginType == null ? null : AccessTools.Property(BlueprinterPluginType, "PatchingComplete");

        private static readonly HashSet<string> LoggedMessages = new HashSet<string>();
        private static AircraftDefinition cachedDonor;
        private static Material cachedRubberMaterial;

        private static bool IsKestrel(Aircraft aircraft)
        {
            return aircraft != null &&
                   aircraft.definition != null &&
                   string.Equals(aircraft.definition.jsonKey, KestrelKey, StringComparison.OrdinalIgnoreCase);
        }

        private static AircraftDefinition GetDonor()
        {
            string requestedKey = Plugin.DonorAircraftKey == null
                ? "trainer"
                : Plugin.DonorAircraftKey.Value;

            if (cachedDonor != null &&
                string.Equals(cachedDonor.jsonKey, requestedKey, StringComparison.OrdinalIgnoreCase))
            {
                return cachedDonor;
            }

            AircraftDefinition[] definitions =
                Resources.FindObjectsOfTypeAll<AircraftDefinition>();

            for (int i = 0; i < definitions.Length; i++)
            {
                AircraftDefinition definition = definitions[i];
                if (definition != null &&
                    string.Equals(definition.jsonKey, requestedKey, StringComparison.OrdinalIgnoreCase))
                {
                    cachedDonor = definition;
                    LogOnce(
                        "donor-found",
                        "Using " + definition.unitName + " (" + definition.jsonKey + ") as the Kestrel donor aircraft.",
                        false);
                    return cachedDonor;
                }
            }

            LogOnce(
                "donor-missing",
                "Could not find donor aircraft key '" + requestedKey + "'. Kestrel compatibility swaps were skipped.",
                true);
            return null;
        }

        internal static IEnumerator ApplyPrefabWheelsWhenReady()
        {
            yield return null;

            for (int attempt = 0; attempt < 120; attempt++)
            {
                if (!IsBlueprinterPatchingComplete())
                {
                    yield return new WaitForSeconds(0.5f);
                    continue;
                }

                AircraftDefinition kestrel = FindAircraftDefinition(KestrelKey);
                string donorKey = Plugin.DonorAircraftKey == null
                    ? "trainer"
                    : Plugin.DonorAircraftKey.Value;
                AircraftDefinition donor = FindAircraftDefinition(donorKey);

                if (kestrel != null && kestrel.unitPrefab != null &&
                    donor != null && donor.unitPrefab != null)
                {
                    cachedDonor = donor;
                    Aircraft prefabAircraft = kestrel.unitPrefab.GetComponentInChildren<Aircraft>(true);
                    if (prefabAircraft != null &&
                        Plugin.ReplaceWheelMaterials != null &&
                        Plugin.ReplaceWheelMaterials.Value)
                    {
                        ApplyWheelMaterials(prefabAircraft, donor);
                        LogOnce(
                            "prefab-wheel-pass",
                            "Applied the wheel compatibility pass to the Kestrel source prefab.",
                            false);
                    }

                    yield break;
                }

                yield return new WaitForSeconds(0.5f);
            }

            LogOnce(
                "prefab-wheel-timeout",
                "Blueprinter did not finish or the required aircraft prefabs did not become available. Early wheel compatibility was skipped.",
                true);
        }

        private static bool IsBlueprinterPatchingComplete()
        {
            if (BlueprinterInstanceProperty == null || BlueprinterPatchingCompleteProperty == null)
            {
                return false;
            }

            object instance = BlueprinterInstanceProperty.GetValue(null, null);
            return instance != null &&
                   BlueprinterPatchingCompleteProperty.GetValue(instance, null) is bool complete &&
                   complete;
        }

        private static AircraftDefinition FindAircraftDefinition(string key)
        {
            AircraftDefinition[] definitions = Resources.FindObjectsOfTypeAll<AircraftDefinition>();
            for (int i = 0; i < definitions.Length; i++)
            {
                AircraftDefinition definition = definitions[i];
                if (definition != null &&
                    string.Equals(definition.jsonKey, key, StringComparison.OrdinalIgnoreCase))
                {
                    return definition;
                }
            }

            return null;
        }

        private static void ApplyInterface(Aircraft aircraft)
        {
            if (!IsKestrel(aircraft))
            {
                return;
            }

            AircraftDefinition kestrel = aircraft.definition;
            AircraftDefinition donor = GetDonor();
            if (donor == null || donor.aircraftParameters == null || kestrel.aircraftParameters == null)
            {
                LogOnce(
                    "interface-parameters-missing",
                    "The Kestrel or donor aircraft parameters were missing. Interface swap was skipped.",
                    true);
                return;
            }

            List<string> changes = new List<string>();

            if (Plugin.ReplaceHud.Value && donor.aircraftParameters.HUDExtras != null)
            {
                kestrel.aircraftParameters.HUDExtras = donor.aircraftParameters.HUDExtras;
                changes.Add("HUD");
            }

            if (Plugin.ReplaceStatusDisplay.Value && donor.aircraftParameters.StatusDisplay != null)
            {
                kestrel.aircraftParameters.StatusDisplay = donor.aircraftParameters.StatusDisplay;
                changes.Add("status display");
            }

            if (changes.Count > 0)
            {
                LogOnce(
                    "interface-applied",
                    "Applied current donor " + string.Join(" and ", changes.ToArray()) + " to the Kestrel.",
                    false);
            }

            if (Plugin.ReplaceWheelMaterials.Value)
            {
                ApplyWheelMaterials(aircraft, donor);
            }
        }

        private static void ApplyTacScreen(Cockpit cockpit)
        {
            if (cockpit == null || TacScreenPrefabField == null)
            {
                return;
            }

            Aircraft aircraft = cockpit.GetComponentInParent<Aircraft>();
            if (!IsKestrel(aircraft))
            {
                return;
            }

            AircraftDefinition donor = GetDonor();
            if (donor == null || donor.unitPrefab == null)
            {
                return;
            }

            Cockpit donorCockpit = donor.unitPrefab.GetComponentInChildren<Cockpit>(true);
            if (donorCockpit == null)
            {
                LogOnce(
                    "donor-cockpit-missing",
                    "The donor aircraft cockpit was not found. Tactical-screen swap was skipped.",
                    true);
                return;
            }

            GameObject donorScreen = TacScreenPrefabField.GetValue(donorCockpit) as GameObject;
            if (donorScreen == null)
            {
                LogOnce(
                    "donor-screen-missing",
                    "The donor aircraft tactical-screen prefab was not found. Tactical-screen swap was skipped.",
                    true);
                return;
            }

            TacScreenPrefabField.SetValue(cockpit, donorScreen);
            LogOnce(
                "screen-applied",
                "Applied current donor tactical screen '" + donorScreen.name + "' to the Kestrel.",
                false);
        }

        private static void ApplyWheelMaterials(Aircraft kestrelAircraft, AircraftDefinition donor)
        {
            if (donor.unitPrefab == null || LandingGearWheelsField == null)
            {
                return;
            }

            List<Renderer> donorRenderers = GetLandingGearWheelRenderers(donor.unitPrefab);
            List<Renderer> kestrelRenderers = GetLandingGearWheelRenderers(kestrelAircraft.gameObject);

            if (kestrelRenderers.Count == 0)
            {
                kestrelRenderers = GetHierarchyWheelRenderers(kestrelAircraft.gameObject);
                LogOnce(
                    "wheel-hierarchy-fallback",
                    "The Kestrel landing-gear wheel list was empty. Using wheel hierarchy and mesh names instead.",
                    false);
            }

            if (kestrelRenderers.Count == 0)
            {
                kestrelRenderers = GetBrokenMaterialRenderers(kestrelAircraft.gameObject);
                LogOnce(
                    "wheel-broken-material-fallback",
                    "No named Kestrel wheel renderers were found. Using only renderers with missing or unsupported materials.",
                    false);
            }

            LogWheelInventory("donor", donor.unitPrefab, donorRenderers);
            LogWheelInventory("Kestrel", kestrelAircraft.gameObject, kestrelRenderers);

            Material donorMaterial = ChooseDonorWheelMaterial(donorRenderers);
            if (donorMaterial == null)
            {
                LogOnce(
                    "donor-wheel-material-ambiguous",
                    "Could not identify one safe donor wheel material. Wheel swap was skipped.",
                    true);
                return;
            }

            Material replacementMaterial = GetOrCreateRubberMaterial(donorMaterial);
            if (replacementMaterial == null)
            {
                LogOnce(
                    "rubber-material-missing",
                    "Could not create the Kestrel rubber material. Wheel swap was skipped.",
                    true);
                return;
            }

            int changedRenderers = 0;
            int changedSlots = 0;

            for (int i = 0; i < kestrelRenderers.Count; i++)
            {
                Renderer renderer = kestrelRenderers[i];
                if (renderer == null)
                {
                    continue;
                }

                Material[] materials = renderer.sharedMaterials;
                bool changedRenderer = false;
                if (materials.Length == 0)
                {
                    renderer.sharedMaterial = replacementMaterial;
                    changedSlots++;
                    changedRenderers++;
                    continue;
                }

                for (int slot = 0; slot < materials.Length; slot++)
                {
                    if (ShouldReplaceWheelMaterial(materials[slot], materials.Length))
                    {
                        materials[slot] = replacementMaterial;
                        changedSlots++;
                        changedRenderer = true;
                    }
                }

                if (changedRenderer)
                {
                    renderer.sharedMaterials = materials;
                    changedRenderers++;
                }
            }

            if (changedSlots > 0)
            {
                LogOnce(
                    "wheels-applied",
                    "Applied dark rubber material '" + replacementMaterial.name + "' to " +
                    changedSlots + " material slot(s) on " + changedRenderers + " Kestrel wheel renderer(s).",
                    false);
            }
            else
            {
                LogOnce(
                    "wheels-not-replaceable",
                    "Kestrel wheel renderers were found, but no safe tire material slots were identified. Wheel swap was skipped.",
                    true);
            }
        }

        private static List<Renderer> GetLandingGearWheelRenderers(GameObject root)
        {
            List<Renderer> result = new List<Renderer>();
            HashSet<Renderer> seen = new HashSet<Renderer>();
            LandingGear[] landingGears = root.GetComponentsInChildren<LandingGear>(true);

            for (int gearIndex = 0; gearIndex < landingGears.Length; gearIndex++)
            {
                Transform[] wheels = LandingGearWheelsField.GetValue(landingGears[gearIndex]) as Transform[];
                if (wheels == null)
                {
                    continue;
                }

                for (int wheelIndex = 0; wheelIndex < wheels.Length; wheelIndex++)
                {
                    Transform wheel = wheels[wheelIndex];
                    if (wheel == null)
                    {
                        continue;
                    }

                    Renderer[] renderers = wheel.GetComponentsInChildren<Renderer>(true);
                    for (int rendererIndex = 0; rendererIndex < renderers.Length; rendererIndex++)
                    {
                        Renderer renderer = renderers[rendererIndex];
                        if (renderer != null &&
                            !(renderer is ParticleSystemRenderer) &&
                            seen.Add(renderer))
                        {
                            result.Add(renderer);
                        }
                    }
                }
            }

            return result;
        }

        private static List<Renderer> GetHierarchyWheelRenderers(GameObject root)
        {
            List<Renderer> result = new List<Renderer>();
            Renderer[] renderers = root.GetComponentsInChildren<Renderer>(true);

            for (int i = 0; i < renderers.Length; i++)
            {
                Renderer renderer = renderers[i];
                if (renderer == null || renderer is ParticleSystemRenderer)
                {
                    continue;
                }

                bool isWheel = HierarchyContainsWheelWord(root.transform, renderer.transform);

                MeshFilter meshFilter = renderer.GetComponent<MeshFilter>();
                if (!isWheel && meshFilter != null && meshFilter.sharedMesh != null)
                {
                    isWheel = ContainsTireWord(meshFilter.sharedMesh.name);
                }

                SkinnedMeshRenderer skinned = renderer as SkinnedMeshRenderer;
                if (!isWheel && skinned != null && skinned.sharedMesh != null)
                {
                    isWheel = ContainsTireWord(skinned.sharedMesh.name);
                }

                if (isWheel)
                {
                    result.Add(renderer);
                }
            }

            return result;
        }

        private static List<Renderer> GetBrokenMaterialRenderers(GameObject root)
        {
            List<Renderer> result = new List<Renderer>();
            Renderer[] renderers = root.GetComponentsInChildren<Renderer>(true);

            for (int i = 0; i < renderers.Length; i++)
            {
                Renderer renderer = renderers[i];
                if (renderer == null || renderer is ParticleSystemRenderer)
                {
                    continue;
                }

                Material[] materials = renderer.sharedMaterials;
                for (int slot = 0; slot < materials.Length; slot++)
                {
                    if (IsBrokenMaterial(materials[slot]))
                    {
                        result.Add(renderer);
                        break;
                    }
                }
            }

            return result;
        }

        private static bool HierarchyContainsWheelWord(Transform root, Transform item)
        {
            Transform current = item;
            while (current != null)
            {
                if (ContainsTireWord(current.name))
                {
                    return true;
                }

                if (current == root)
                {
                    break;
                }

                current = current.parent;
            }

            return false;
        }

        private static Material ChooseDonorWheelMaterial(List<Renderer> renderers)
        {
            Dictionary<Material, int> uses = new Dictionary<Material, int>();
            Dictionary<Material, int> singleSlotUses = new Dictionary<Material, int>();

            for (int i = 0; i < renderers.Count; i++)
            {
                Material[] materials = renderers[i].sharedMaterials;
                HashSet<Material> counted = new HashSet<Material>();

                for (int slot = 0; slot < materials.Length; slot++)
                {
                    Material material = materials[slot];
                    if (material == null || !counted.Add(material))
                    {
                        continue;
                    }

                    uses[material] = uses.ContainsKey(material) ? uses[material] + 1 : 1;
                    if (materials.Length == 1)
                    {
                        singleSlotUses[material] = singleSlotUses.ContainsKey(material)
                            ? singleSlotUses[material] + 1
                            : 1;
                    }
                }
            }

            if (uses.Count == 1)
            {
                foreach (Material onlyMaterial in uses.Keys)
                {
                    return onlyMaterial;
                }
            }

            Material best = null;
            int bestScore = int.MinValue;
            int secondScore = int.MinValue;
            bool bestHasTireName = false;

            foreach (KeyValuePair<Material, int> pair in uses)
            {
                Material material = pair.Key;
                bool hasTireName = ContainsTireWord(material.name) ||
                                   (material.mainTexture != null && ContainsTireWord(material.mainTexture.name));
                int singleUses = singleSlotUses.ContainsKey(material) ? singleSlotUses[material] : 0;
                int score = pair.Value * 10 + singleUses * 100 + (hasTireName ? 1000 : 0);

                if (score > bestScore)
                {
                    secondScore = bestScore;
                    bestScore = score;
                    best = material;
                    bestHasTireName = hasTireName;
                }
                else if (score > secondScore)
                {
                    secondScore = score;
                }
            }

            if (bestHasTireName || bestScore > secondScore)
            {
                return best;
            }

            return null;
        }

        private static bool ShouldReplaceWheelMaterial(Material material, int materialCount)
        {
            if (IsBrokenMaterial(material) || materialCount == 1)
            {
                return true;
            }

            return ContainsTireWord(material.name) ||
                   (material.mainTexture != null && ContainsTireWord(material.mainTexture.name));
        }

        private static bool IsBrokenMaterial(Material material)
        {
            return material == null ||
                   material.shader == null ||
                   !material.shader.isSupported ||
                   material.shader.name.IndexOf("InternalError", StringComparison.OrdinalIgnoreCase) >= 0;
        }

        private static Material GetOrCreateRubberMaterial(Material template)
        {
            if (cachedRubberMaterial != null)
            {
                return cachedRubberMaterial;
            }

            Shader shader = Shader.Find("Universal Render Pipeline/Lit");
            if (shader == null && template != null)
            {
                shader = template.shader;
            }

            if (shader == null)
            {
                return null;
            }

            Material rubber = new Material(shader);
            rubber.name = "Kestrel_Rubber_Compatibility";
            Color rubberColor = new Color(0.045f, 0.045f, 0.045f, 1f);

            if (rubber.HasProperty("_BaseMap"))
            {
                rubber.SetTexture("_BaseMap", Texture2D.whiteTexture);
            }

            if (rubber.HasProperty("_MainTex"))
            {
                rubber.SetTexture("_MainTex", Texture2D.whiteTexture);
            }

            if (rubber.HasProperty("_BaseColor"))
            {
                rubber.SetColor("_BaseColor", rubberColor);
            }

            if (rubber.HasProperty("_Color"))
            {
                rubber.SetColor("_Color", rubberColor);
            }

            if (rubber.HasProperty("_Metallic"))
            {
                rubber.SetFloat("_Metallic", 0f);
            }

            if (rubber.HasProperty("_Smoothness"))
            {
                rubber.SetFloat("_Smoothness", 0.18f);
            }

            if (rubber.HasProperty("_Glossiness"))
            {
                rubber.SetFloat("_Glossiness", 0.18f);
            }

            rubber.enableInstancing = true;
            UnityEngine.Object.DontDestroyOnLoad(rubber);
            cachedRubberMaterial = rubber;
            return cachedRubberMaterial;
        }

        private static bool ContainsTireWord(string value)
        {
            if (string.IsNullOrEmpty(value))
            {
                return false;
            }

            return value.IndexOf("tire", StringComparison.OrdinalIgnoreCase) >= 0 ||
                   value.IndexOf("tyre", StringComparison.OrdinalIgnoreCase) >= 0 ||
                   value.IndexOf("rubber", StringComparison.OrdinalIgnoreCase) >= 0 ||
                   value.IndexOf("wheel", StringComparison.OrdinalIgnoreCase) >= 0;
        }

        private static void LogWheelInventory(string label, GameObject root, List<Renderer> renderers)
        {
            List<string> descriptions = new List<string>();
            for (int i = 0; i < renderers.Count; i++)
            {
                Renderer renderer = renderers[i];
                Material[] materials = renderer.sharedMaterials;
                List<string> names = new List<string>();
                for (int slot = 0; slot < materials.Length; slot++)
                {
                    names.Add(materials[slot] == null ? "<null>" : materials[slot].name);
                }

                descriptions.Add(GetRelativePath(root.transform, renderer.transform) + " [" +
                                 string.Join(", ", names.ToArray()) + "]");
            }

            LogOnce(
                "wheel-inventory-" + label,
                label + " landing-gear wheel renderers: " +
                (descriptions.Count == 0 ? "<none>" : string.Join("; ", descriptions.ToArray())),
                descriptions.Count == 0);
        }

        private static string GetRelativePath(Transform root, Transform item)
        {
            List<string> names = new List<string>();
            Transform current = item;
            while (current != null)
            {
                names.Add(current.name);
                if (current == root)
                {
                    break;
                }

                current = current.parent;
            }

            names.Reverse();
            return string.Join("/", names.ToArray());
        }

        private static void LogOnce(string key, string message, bool warning)
        {
            if (!LoggedMessages.Add(key) || Plugin.PluginLogger == null)
            {
                return;
            }

            if (warning)
            {
                Plugin.PluginLogger.LogWarning(message);
            }
            else
            {
                Plugin.PluginLogger.LogInfo(message);
            }
        }

        [HarmonyPatch(typeof(Aircraft), "SetupLocalPlayerAndUI")]
        private static class AircraftSetupLocalPlayerAndUiPatch
        {
            private static void Prefix(Aircraft __instance)
            {
                try
                {
                    ApplyInterface(__instance);
                }
                catch (Exception exception)
                {
                    LogOnce(
                        "interface-exception",
                        "Kestrel interface swap failed: " + exception,
                        true);
                }
            }
        }

        [HarmonyPatch(typeof(Cockpit), "Cockpit_OnAircraftInitialize")]
        private static class CockpitAircraftInitializePatch
        {
            private static void Prefix(Cockpit __instance)
            {
                if (Plugin.ReplaceTacScreen == null || !Plugin.ReplaceTacScreen.Value)
                {
                    return;
                }

                try
                {
                    ApplyTacScreen(__instance);
                }
                catch (Exception exception)
                {
                    LogOnce(
                        "screen-exception",
                        "Kestrel tactical-screen swap failed: " + exception,
                        true);
                }
            }
        }
    }
}
