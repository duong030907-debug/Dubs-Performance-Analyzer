using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Verse;

namespace Analyzer.Profiling
{
    /// <summary>
    /// Creates one Tick-tab entry per loaded mod, containing its locally declared
    /// tick callback methods. Methods are instrumented by the normal entry pipeline.
    /// </summary>
    internal static class AllModsTickEntries
    {
        private static readonly string[] TickMethodNames =
        {
            "Tick", "TickRare", "TickLong", "TickInterval",
            "CompTick", "CompTickRare", "CompTickLong",
            "MapComponentTick", "WorldComponentTick", "GameComponentTick",
            "WorldObjectTick", "CaravanTick", "LordTick", "JobTick",
            "DoSingleTick", "TickAll"
        };

        public static void Initialise()
        {
            var methodsByMod = new SortedDictionary<string, HashSet<MethodInfo>>(StringComparer.OrdinalIgnoreCase);

            foreach (var mod in LoadedModManager.RunningMods)
            {
                foreach (var assembly in mod.assemblies.loadedAssemblies)
                {
                    if (!ModInfoCache.AssemblyToModname.TryGetValue(assembly.FullName, out var modName))
                        continue;

                    if (!methodsByMod.TryGetValue(modName, out var methods))
                        methodsByMod.Add(modName, methods = new HashSet<MethodInfo>());

                    foreach (var type in GetLoadableTypes(assembly))
                    {
                        MethodInfo[] declaredMethods;
                        try
                        {
                            declaredMethods = type.GetMethods(BindingFlags.Public | BindingFlags.NonPublic |
                                                              BindingFlags.Instance | BindingFlags.Static |
                                                              BindingFlags.DeclaredOnly);
                        }
                        catch
                        {
                            continue;
                        }

                        foreach (var method in declaredMethods)
                        {
                            if (IsTickMethod(method))
                                methods.Add(method);
                        }
                    }
                }
            }

            var index = 0;
            foreach (var pair in methodsByMod)
            {
                if (pair.Value.Count == 0)
                    continue;

                // Use a generated identifier rather than the mod's display name:
                // punctuation and duplicate display names must not collide.
                var typeName = "AllModsTickEntry" + index++;
                var type = DynamicTypeBuilder.CreateType(typeName, pair.Value);
                var entry = Entry.Create("All Mods - " + pair.Key, Category.Tick, type, true, true);

                if (!GUIController.Tab(Category.Tick).entries.ContainsKey(entry))
                    GUIController.Tab(Category.Tick).entries.Add(entry, type);
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
