using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
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
        private static readonly Dictionary<string, HashSet<MethodInfo>> methodsByMod =
            new Dictionary<string, HashSet<MethodInfo>>(StringComparer.OrdinalIgnoreCase);

        private static IEnumerator<Type> currentTypes;
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
                // Keep an entry for every loaded mod, even if it has no matching tick methods.
                if (!methodsByMod.ContainsKey(mod.Name))
                    methodsByMod.Add(mod.Name, new HashSet<MethodInfo>());

                foreach (var assembly in mod.assemblies.loadedAssemblies)
                {
                    assemblies.Add(new KeyValuePair<string, Assembly>(mod.Name, assembly));
                    lastAssemblyIndexByMod[mod.Name] = assemblies.Count - 1;
                }

                if (!lastAssemblyIndexByMod.ContainsKey(mod.Name))
                    CreateModEntry(mod.Name);
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
                    if (assemblyIndex >= assemblies.Count)
                    {
                        completed = true;
                        assemblies.Clear();
                        lastAssemblyIndexByMod.Clear();
                        methodsByMod.Clear();
                        return;
                    }

                    currentTypes = GetLoadableTypes(assemblies[assemblyIndex].Value).GetEnumerator();
                }

                if (!currentTypes.MoveNext())
                {
                    currentTypes.Dispose();
                    currentTypes = null;

                    var finishedAssemblyIndex = assemblyIndex++;
                    var finishedModName = assemblies[finishedAssemblyIndex].Key;

                    // Do not wait for unrelated mods to finish scanning.
                    if (lastAssemblyIndexByMod.TryGetValue(finishedModName, out var lastIndex) &&
                        lastIndex == finishedAssemblyIndex)
                    {
                        CreateModEntry(finishedModName);
                    }

                    continue;
                }

                ProcessType(assemblies[assemblyIndex].Key, currentTypes.Current);
                processed++;
            }
        }

        private static void CreateModEntry(string modName)
        {
            if (!methodsByMod.TryGetValue(modName, out var methods))
                methodsByMod.Add(modName, methods = new HashSet<MethodInfo>());

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

        private static void ProcessType(string modName, Type type)
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

            if (!methodsByMod.TryGetValue(modName, out var methods))
                methodsByMod.Add(modName, methods = new HashSet<MethodInfo>());

            foreach (var method in declaredMethods)
            {
                if (IsTickMethod(method))
                    methods.Add(method);
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