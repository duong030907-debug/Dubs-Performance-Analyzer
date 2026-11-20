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
        // Limit reflection work to a small batch of types per game update.
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
        private static readonly SortedDictionary<string, HashSet<MethodInfo>> methodsByMod =
            new SortedDictionary<string, HashSet<MethodInfo>>(StringComparer.OrdinalIgnoreCase);

        private static IEnumerator<Type> currentTypes;
        private static int assemblyIndex;
        private static List<string> modsToCreate;
        private static int modIndex;
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
                foreach (var assembly in mod.assemblies.loadedAssemblies)
                {
                    if (ModInfoCache.AssemblyToModname.TryGetValue(assembly.FullName, out var modName))
                        assemblies.Add(new KeyValuePair<string, Assembly>(modName, assembly));
                }
            }
        }

        /// <summary>
        /// Called once per game update. Discovery and generated-entry creation
        /// are both spread across updates.
        /// </summary>
        public static void Update()
        {
            if (!started || completed)
                return;

            if (modsToCreate == null)
            {
                var processed = 0;
                while (processed < TypesPerUpdate)
                {
                    if (currentTypes == null)
                    {
                        if (assemblyIndex >= assemblies.Count)
                        {
                            modsToCreate = methodsByMod.Keys.ToList();
                            break;
                        }

                        currentTypes = GetLoadableTypes(assemblies[assemblyIndex].Value).GetEnumerator();
                    }

                    if (!currentTypes.MoveNext())
                    {
                        currentTypes.Dispose();
                        currentTypes = null;
                        assemblyIndex++;
                        continue;
                    }

                    ProcessType(assemblies[assemblyIndex].Key, currentTypes.Current);
                    processed++;
                }

                // The final assembly may have completed exactly at the batch
                // boundary. Detect that on the next update rather than doing
                // another full scan here.
                return;
            }

            if (modIndex >= modsToCreate.Count)
            {
                completed = true;
                assemblies.Clear();
                methodsByMod.Clear();
                modsToCreate = null;
                return;
            }

            var modName = modsToCreate[modIndex++];
            var methods = methodsByMod[modName];
            if (methods.Count == 0)
                return;

            try
            {
                // Numeric suffixes are preserved by DynamicTypeBuilder.
                var typeName = "AllModsTickEntry" + generatedTypeIndex++;
                var type = DynamicTypeBuilder.CreateType(typeName, methods);
                var entry = Entry.Create(modName, Category.Modded, type, true, true);

                if (!GUIController.Tab(Category.Modded).entries.ContainsKey(entry))
                    GUIController.Tab(Category.Modded).entries.Add(entry, type);
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
