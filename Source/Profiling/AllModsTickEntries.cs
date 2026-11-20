using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using Verse;

namespace Analyzer.Profiling
{
    /// <summary>
    /// Discovers tick callbacks incrementally so large mod lists do not stall
    /// the game while the analyzer window is opening.
    /// </summary>
    internal static class AllModsTickEntries
    {
        // Limit reflection work to a small batch of types per UI frame.
        private const int TypesPerUpdate = 30;

        private static readonly string[] TickMethodNames =
        {
            "Tick", "TickRare", "TickLong", "TickInterval",
            "CompTick", "CompTickRare", "CompTickLong",
            "MapComponentTick", "WorldComponentTick", "GameComponentTick",
            "WorldObjectTick", "CaravanTick", "LordTick", "JobTick",
            "DoSingleTick", "TickAll"
        };

        private static readonly List<KeyValuePair<string, Assembly>> assemblies =
            new List<KeyValuePair<string, Assembly>>();
        private static readonly Dictionary<string, int> lastAssemblyIndexByMod =
            new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        private static readonly Dictionary<string, string> modNamesById =
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        private static readonly Dictionary<MethodInfo, string> modNameByMethod =
            new Dictionary<MethodInfo, string>();
        private static readonly Dictionary<string, HashSet<MethodInfo>> methodsByMod =
            new Dictionary<string, HashSet<MethodInfo>>(StringComparer.OrdinalIgnoreCase);
        // Combined method set backs the "All Mods" entry, allowing one profiler
        // view to compare tick methods collected from every loaded mod.
        private static readonly HashSet<MethodInfo> allModMethods = new HashSet<MethodInfo>();

        private static Type[] currentTypes;
        private static Task<Type[]> currentTypesTask;
        private static int typeIndex;
        private static int assemblyIndex;
        private static int generatedTypeIndex;
        private static bool started;
        private static bool completed;

        public static void Initialise()
        {
            if (started || completed)
                return;

            started = true;

            foreach (var mod in LoadedModManager.RunningMods)
            {
                // Package IDs are unique; display names are not.
                var modId = mod.PackageId;
                var displayName = mod.Name;
                if (string.IsNullOrEmpty(modId))
                    modId = "unknown-" + assemblies.Count + "-" + displayName;

                // Entry lookup is name-based, so disambiguate duplicate display names.
                if (LoadedModManager.RunningMods.Count(other =>
                        string.Equals(other.Name, mod.Name, StringComparison.OrdinalIgnoreCase)) > 1)
                    displayName += " (" + modId + ")";

                modNamesById[modId] = displayName;
                methodsByMod[modId] = new HashSet<MethodInfo>();

                foreach (var assembly in mod.assemblies.loadedAssemblies)
                {
                    assemblies.Add(new KeyValuePair<string, Assembly>(modId, assembly));
                    lastAssemblyIndexByMod[modId] = assemblies.Count - 1;
                }

                if (!lastAssemblyIndexByMod.ContainsKey(modId))
                    CreateModEntry(modId);
            }
        }

        /// <summary>
        /// Called only while the Modded tab is drawn. Each mod's entry is
        /// created as soon as its final assembly has been scanned.
        /// </summary>
        public static void Update()
        {
            if (!started || completed)
                return;

            var processed = 0;
            while (processed < TypesPerUpdate)
            {
                if (currentTypes == null)
                {
                    if (currentTypesTask == null)
                    {
                        if (assemblyIndex >= assemblies.Count)
                        {
                            CreateAllModsEntry();
                            completed = true;
                            assemblies.Clear();
                            lastAssemblyIndexByMod.Clear();
                            methodsByMod.Clear();
                            return;
                        }

                        // Assembly.GetTypes() is synchronous, so perform it off the UI thread.
                        var assembly = assemblies[assemblyIndex].Value;
                        currentTypesTask = Task.Factory.StartNew(
                            () => GetLoadableTypes(assembly).ToArray());
                        return;
                    }

                    if (!currentTypesTask.IsCompleted)
                        return;

                    try
                    {
                        currentTypes = currentTypesTask.GetAwaiter().GetResult();
                    }
                    catch (Exception e)
                    {
                        Log.Warning("[Analyzer] Failed to enumerate types in a mod assembly: " + e);
                        currentTypes = Array.Empty<Type>();
                    }

                    currentTypesTask = null;
                    typeIndex = 0;
                }

                if (typeIndex >= currentTypes.Length)
                {
                    currentTypes = null;

                    var finishedAssemblyIndex = assemblyIndex++;
                    var finishedModId = assemblies[finishedAssemblyIndex].Key;

                    // Do not wait for unrelated mods to finish scanning.
                    if (lastAssemblyIndexByMod.TryGetValue(finishedModId, out var lastIndex) &&
                        lastIndex == finishedAssemblyIndex)
                    {
                        CreateModEntry(finishedModId);
                    }

                    continue;
                }

                ProcessType(assemblies[assemblyIndex].Key, currentTypes[typeIndex++]);
                processed++;
            }
        }

        private static void CreateModEntry(string modId)
        {
            if (!methodsByMod.TryGetValue(modId, out var methods))
                methodsByMod.Add(modId, methods = new HashSet<MethodInfo>());

            var modName = modNamesById.TryGetValue(modId, out var displayName) ? displayName : modId;
            try
            {
                // Numeric suffixes are preserved by DynamicTypeBuilder.
                var typeName = "AllModsTickEntry" + generatedTypeIndex++;
                var type = DynamicTypeBuilder.CreateType(typeName, methods);
                var entry = Entry.Create(modName, Category.Modded, type, true, true);

                var moddedTab = GUIController.Tab(Category.Modded);
                if (!moddedTab.entries.ContainsKey(entry))
                    moddedTab.entries.Add(entry, type);
            }
            catch (Exception e)
            {
                Log.Error("[Analyzer] Failed to create modded tick entry for " + modName + ": " + e);
            }
        }

        private static void CreateAllModsEntry()
        {
            const string entryName = "All Mods";
            const string typeName = "AllModsCombinedTickEntry";

            try
            {
                var type = DynamicTypeBuilder.CreateType(typeName, allModMethods);
                var entry = Entry.Create(entryName, Category.Modded, type, true, true);

                var moddedTab = GUIController.Tab(Category.Modded);
                if (!moddedTab.entries.ContainsKey(entry))
                    moddedTab.entries.Add(entry, type);
            }
            catch (Exception e)
            {
                Log.Error("[Analyzer] Failed to create combined modded tick entry: " + e);
            }
        }

        public static bool TryGetModName(MethodInfo method, out string modName)
        {
            return modNameByMethod.TryGetValue(method, out modName);
        }

        private static void ProcessType(string modId, Type type)
        {
            if (type == null)
                return;

            MethodInfo[] declaredMethods;
            try
            {
                declaredMethods = type.GetMethods(BindingFlags.Public | BindingFlags.NonPublic |
                                                  BindingFlags.Instance | BindingFlags.Static |
                                                  BindingFlags.DeclaredOnly);
            }
            catch
            {
                return;
            }

            if (!methodsByMod.TryGetValue(modId, out var methods))
                methodsByMod.Add(modId, methods = new HashSet<MethodInfo>());

            foreach (var method in declaredMethods)
            {
                if (IsTickMethod(method))
                {
                    methods.Add(method);
                    allModMethods.Add(method);
                    if (modNamesById.TryGetValue(modId, out var displayName))
                        modNameByMethod[method] = displayName;
                }
            }
        }

        private static bool IsTickMethod(MethodInfo method)
        {
            if (method == null || method.IsAbstract || method.IsGenericMethod ||
                method.ContainsGenericParameters)
                return false;

            try
            {
                if (method.GetMethodBody() == null)
                    return false;
            }
            catch
            {
                return false;
            }

            var name = method.Name;
            return TickMethodNames.Contains(name) ||
                   name.EndsWith("Tick", StringComparison.Ordinal) ||
                   name.EndsWith("TickRare", StringComparison.Ordinal) ||
                   name.EndsWith("TickLong", StringComparison.Ordinal);
        }

        private static IEnumerable<Type> GetLoadableTypes(Assembly assembly)
        {
            try
            {
                return assembly.GetTypes();
            }
            catch (ReflectionTypeLoadException e)
            {
                return e.Types.Where(t => t != null);
            }
            catch
            {
                return Array.Empty<Type>();
            }
        }
    }
}