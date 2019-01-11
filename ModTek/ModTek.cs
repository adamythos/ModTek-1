using BattleTech;
using BattleTech.Data;
using BattleTechModLoader;
using Harmony;
using HBS.Util;
using JetBrains.Annotations;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.RegularExpressions;

// ReSharper disable FieldCanBeMadeReadOnly.Local

namespace ModTek
{
    using static Logger;

    public static class ModTek
    {
        private static readonly string[] IGNORE_LIST = { ".DS_STORE", "~", ".nomedia" };

        // game paths/directories
        public static string GameDirectory { get; private set; }
        public static string ModsDirectory { get; private set; }
        public static string StreamingAssetsDirectory { get; private set; }
        public static string MDDBPath { get; private set; }

        // file/directory names
        private const string MODS_DIRECTORY_NAME = "Mods";
        private const string MOD_JSON_NAME = "mod.json";
        private const string MODTEK_DIRECTORY_NAME = ".modtek";
        private const string CACHE_DIRECTORY_NAME = "Cache";
        private const string MERGE_CACHE_FILE_NAME = "merge_cache.json";
        private const string TYPE_CACHE_FILE_NAME = "type_cache.json";
        private const string LOG_NAME = "ModTek.log";
        private const string LOAD_ORDER_FILE_NAME = "load_order.json";
        private const string DATABASE_DIRECTORY_NAME = "Database";
        private const string MDD_FILE_NAME = "MetadataDatabase.db";
        private const string DB_CACHE_FILE_NAME = "database_cache.json";
        private const string HARMONY_SUMMARY_FILE_NAME = "harmony_summary.log";

        // ModTek paths/directories
        internal static string ModTekDirectory { get; private set; }
        internal static string CacheDirectory { get; private set; }
        internal static string DatabaseDirectory { get; private set; }
        internal static string MergeCachePath { get; private set; }
        internal static string TypeCachePath { get; private set; }
        internal static string ModMDDBPath { get; private set; }
        internal static string DBCachePath { get; private set; }
        internal static string LoadOrderPath { get; private set; }
        internal static string HarmonySummaryPath { get; private set; }

        // files that are read and written to (located in .modtek)
        private static List<string> modLoadOrder;
        private static MergeCache jsonMergeCache;
        private static Dictionary<string, List<string>> typeCache;
        private static Dictionary<string, DateTime> dbCache;

        internal static VersionManifest CachedVersionManifest = null;
        internal static List<ModDef.ManifestEntry> BTRLEntries = new List<ModDef.ManifestEntry>();

        internal static Dictionary<string, string> ModAssetBundlePaths { get; } = new Dictionary<string, string>();
        internal static HashSet<string> ModTexture2Ds { get; } = new HashSet<string>();
        internal static Dictionary<string, string> ModVideos { get; } = new Dictionary<string, string>();

        private static bool BuiltNewTypeCache = false;
        private static Dictionary<string, List<ModDef.ManifestEntry>> entriesByMod = new Dictionary<string, List<ModDef.ManifestEntry>>();
        private static Stopwatch stopwatch = new Stopwatch();


        // INITIALIZATION (called by BTML)
        [UsedImplicitly]
        public static void Init()
        {
            stopwatch.Start();

            // if the manifest directory is null, there is something seriously wrong
            var manifestDirectory = Path.GetDirectoryName(VersionManifestUtilities.MANIFEST_FILEPATH);
            if (manifestDirectory == null)
                return;

            // setup directories
            ModsDirectory = Path.GetFullPath(
                Path.Combine(manifestDirectory,
                    Path.Combine(Path.Combine(Path.Combine(
                        "..", ".."), ".."), MODS_DIRECTORY_NAME)));

            StreamingAssetsDirectory = Path.GetFullPath(Path.Combine(manifestDirectory, ".."));
            GameDirectory = Path.GetFullPath(Path.Combine(Path.Combine(StreamingAssetsDirectory, ".."), ".."));
            MDDBPath = Path.Combine(Path.Combine(StreamingAssetsDirectory, "MDD"), MDD_FILE_NAME);

            ModTekDirectory = Path.Combine(ModsDirectory, MODTEK_DIRECTORY_NAME);
            CacheDirectory = Path.Combine(ModTekDirectory, CACHE_DIRECTORY_NAME);
            DatabaseDirectory = Path.Combine(ModTekDirectory, DATABASE_DIRECTORY_NAME);

            LogPath = Path.Combine(ModTekDirectory, LOG_NAME);
            HarmonySummaryPath = Path.Combine(ModTekDirectory, HARMONY_SUMMARY_FILE_NAME);
            LoadOrderPath = Path.Combine(ModTekDirectory, LOAD_ORDER_FILE_NAME);
            MergeCachePath = Path.Combine(CacheDirectory, MERGE_CACHE_FILE_NAME);
            TypeCachePath = Path.Combine(CacheDirectory, TYPE_CACHE_FILE_NAME);
            ModMDDBPath = Path.Combine(DatabaseDirectory, MDD_FILE_NAME);
            DBCachePath = Path.Combine(DatabaseDirectory, DB_CACHE_FILE_NAME);

            // creates the directories above it as well
            Directory.CreateDirectory(CacheDirectory);
            Directory.CreateDirectory(DatabaseDirectory);

            // create log file, overwritting if it's already there
            using (var logWriter = File.CreateText(LogPath))
            {
                logWriter.WriteLine($"ModTek v{Assembly.GetExecutingAssembly().GetName().Version} -- {DateTime.Now}");
            }

            // load progress bar
            if (!ProgressPanel.Initialize(ModsDirectory, $"ModTek v{Assembly.GetExecutingAssembly().GetName().Version}"))
            {
                Log("Failed to load progress bar.  Skipping mod loading completely.");
                CloseLogStream();
            }

            // create all of the caches
            dbCache = LoadOrCreateDBCache(DBCachePath);
            jsonMergeCache = LoadOrCreateMergeCache(MergeCachePath);
            typeCache = LoadOrCreateTypeCache(TypeCachePath);

            UpdateCacheToRelativePath(dbCache);
            UpdateCacheToRelativePath(typeCache);
            jsonMergeCache.UpdateToRelativePaths();

            // init harmony and patch the stuff that comes with ModTek (contained in Patches.cs)
            var harmony = HarmonyInstance.Create("io.github.mpstark.ModTek");
            harmony.PatchAll(Assembly.GetExecutingAssembly());

            LoadMods();
            BuildModManifestEntries();

            stopwatch.Stop();
        }


        // UTIL
        private static void PrintHarmonySummary(string path)
        {
            var harmony = HarmonyInstance.Create("io.github.mpstark.ModTek");

            var patchedMethods = harmony.GetPatchedMethods().ToArray();
            if (patchedMethods.Length == 0)
                return;

            using (var writer = File.CreateText(path))
            {
                writer.WriteLine($"Harmony Patched Methods (after ModTek startup) -- {DateTime.Now}\n");

                foreach (var method in patchedMethods)
                {
                    var info = harmony.GetPatchInfo(method);

                    if (info == null || method.ReflectedType == null)
                        continue;

                    writer.WriteLine($"{method.ReflectedType.FullName}.{method.Name}:");

                    // prefixes
                    if (info.Prefixes.Count != 0)
                        writer.WriteLine("\tPrefixes:");
                    foreach (var patch in info.Prefixes)
                        writer.WriteLine($"\t\t{patch.owner}");

                    // transpilers
                    if (info.Transpilers.Count != 0)
                        writer.WriteLine("\tTranspilers:");
                    foreach (var patch in info.Transpilers)
                        writer.WriteLine($"\t\t{patch.owner}");

                    // postfixes
                    if (info.Postfixes.Count != 0)
                        writer.WriteLine("\tPostfixes:");
                    foreach (var patch in info.Postfixes)
                        writer.WriteLine($"\t\t{patch.owner}");

                    writer.WriteLine("");
                }
            }
        }

        private static bool FileIsOnDenyList(string filePath)
        {
            return IGNORE_LIST.Any(x => filePath.EndsWith(x, StringComparison.InvariantCultureIgnoreCase));
        }

        public static string ResolvePath(string path, string rootPathToUse)
        {
            path = path.Replace("{{Mods}}", ModsDirectory);

            if (!Path.IsPathRooted(path))
                path = Path.Combine(rootPathToUse, path);

            return Path.GetFullPath(path);
        }

        public static string GetRelativePath(string path, string rootPath)
        {
            if (!Path.IsPathRooted(path))
                return path;

            rootPath = Path.GetFullPath(rootPath);
            if (rootPath.Last() != Path.DirectorySeparatorChar)
                rootPath += Path.DirectorySeparatorChar;

            var pathUri = new Uri(Path.GetFullPath(path), UriKind.Absolute);
            var rootUri = new Uri(rootPath, UriKind.Absolute);

            if (pathUri.Scheme != rootUri.Scheme)
                return path;

            var relativeUri = rootUri.MakeRelativeUri(pathUri);
            var relativePath = Uri.UnescapeDataString(relativeUri.ToString());

            if (pathUri.Scheme.Equals("file", StringComparison.InvariantCultureIgnoreCase))
                relativePath = relativePath.Replace(Path.AltDirectorySeparatorChar, Path.DirectorySeparatorChar);

            return relativePath;
        }

        internal static JObject ParseGameJSONFile(string path)
        {
            // because StripHBSCommentsFromJSON is private, use Harmony to call the method
            var commentsStripped = Traverse.Create(typeof(JSONSerializationUtility)).Method("StripHBSCommentsFromJSON", File.ReadAllText(path)).GetValue<string>();

            if (commentsStripped == null)
                throw new Exception("StripHBSCommentsFromJSON returned null.");

            // add missing commas, this only fixes if there is a newline
            var rgx = new Regex(@"(\]|\}|""|[A-Za-z0-9])\s*\n\s*(\[|\{|"")", RegexOptions.Singleline);
            var commasAdded = rgx.Replace(commentsStripped, "$1,\n$2");

            return JObject.Parse(commasAdded);
        }

        private static string InferIDFromJObject(JObject jObj)
        {
            if (jObj == null)
                return null;

            // go through the different kinds of id storage in JSONS
            string[] jPaths = { "Description.Id", "id", "Id", "ID", "identifier", "Identifier" };
            foreach (var jPath in jPaths)
            {
                var id = (string)jObj.SelectToken(jPath);
                if (id != null)
                    return id;
            }

            return null;
        }

        private static string InferIDFromFile(string path)
        {
            // if not json, return the file name without the extension, as this is what HBS uses
            var ext = Path.GetExtension(path);
            if (ext == null || ext.ToLower() != ".json" || !File.Exists(path))
                return Path.GetFileNameWithoutExtension(path);

            // read the json and get ID out of it if able to
            return InferIDFromJObject(ParseGameJSONFile(path)) ?? Path.GetFileNameWithoutExtension(path);
        }


        // CACHES
        internal static void WriteJsonFile(string path, object obj)
        {
            File.WriteAllText(path, JsonConvert.SerializeObject(obj, Formatting.Indented));
        }

        internal static void UpdateCacheToRelativePath<T>(Dictionary<string, T> cache)
        {
            var toRemove = new List<string>();
            var toAdd = new Dictionary<string, T>();

            foreach (var path in cache.Keys)
            {
                if (Path.IsPathRooted(path))
                {
                    var relativePath = GetRelativePath(path, GameDirectory);
                    toAdd[relativePath] = cache[path];
                    toRemove.Add(path);
                }
            }

            foreach (var addKVP in toAdd)
                cache.Add(addKVP.Key, addKVP.Value);

            foreach (var path in toRemove)
                cache.Remove(path);
        }

        internal static MergeCache LoadOrCreateMergeCache(string path)
        {
            MergeCache mergeCache;

            if (File.Exists(path))
            {
                try
                {
                    mergeCache = JsonConvert.DeserializeObject<MergeCache>(File.ReadAllText(path));
                    Log("Loaded merge cache.");
                    return mergeCache;
                }
                catch (Exception e)
                {
                    Log("Loading merge cache failed -- will rebuild it.");
                    Log($"\t{e.Message}");
                }
            }

            // create a new one if it doesn't exist or couldn't be added'
            Log("Building new Merge Cache.");
            mergeCache = new MergeCache();
            return mergeCache;
        }

        internal static Dictionary<string, List<string>> LoadOrCreateTypeCache(string path)
        {
            Dictionary<string, List<string>> cache;

            if (File.Exists(path))
            {
                try
                {
                    cache = JsonConvert.DeserializeObject<Dictionary<string, List<string>>>(File.ReadAllText(path));
                    Log("Loaded type cache.");
                    return cache;
                }
                catch (Exception e)
                {
                    Log("Loading type cache failed -- will rebuild it.");
                    Log($"\t{e.Message}");
                }
            }

            // create a new one if it doesn't exist or couldn't be added
            Log("Building new Type Cache.");
            BuiltNewTypeCache = true;
            cache = new Dictionary<string, List<string>>();
            return cache;
        }

        internal static List<string> GetTypesFromCache(string absolutePath)
        {
            var relativePath = GetRelativePath(absolutePath, GameDirectory);

            if (typeCache.ContainsKey(relativePath))
                return typeCache[relativePath];

            return null;
        }

        internal static List<string> GetTypesFromCacheOrManifest(VersionManifest manifest, string absolutePath)
        {
            var types = GetTypesFromCache(absolutePath);
            if (types != null)
                return types;

            // get the type from the manifest
            var matchingEntries = manifest.FindAll(x => Path.GetFullPath(x.FilePath) == absolutePath);

            if (matchingEntries == null || matchingEntries.Count == 0)
                return null;

            types = new List<string>();

            foreach (var existingEntry in matchingEntries)
                types.Add(existingEntry.Type);

            var relativePath = GetRelativePath(absolutePath, GameDirectory);
            typeCache[relativePath] = types;
            return typeCache[relativePath];
        }

        internal static void TryAddTypeToCache(string absolutePath, string type)
        {
            var types = GetTypesFromCache(absolutePath);
            if (types != null && types.Contains(type))
                return;

            if (types != null && !types.Contains(type))
            {
                types.Add(type);
                return;
            }

            // add the new entry
            typeCache[GetRelativePath(absolutePath, GameDirectory)] = new List<string> { type };
        }

        internal static Dictionary<string, DateTime> LoadOrCreateDBCache(string path)
        {
            Dictionary<string, DateTime> cache;

            if (File.Exists(path) && File.Exists(ModMDDBPath))
            {
                try
                {
                    cache = JsonConvert.DeserializeObject<Dictionary<string, DateTime>>(File.ReadAllText(path));
                    Log("Loaded db cache.");
                    return cache;
                }
                catch (Exception e)
                {
                    Log("Loading db cache failed -- will rebuild it.");
                    Log($"\t{e.Message}");
                }
            }

            // delete mod db if it exists the cache does not
            if (File.Exists(ModMDDBPath))
                File.Delete(ModMDDBPath);

            File.Copy(Path.Combine(Path.Combine(StreamingAssetsDirectory, "MDD"), MDD_FILE_NAME), ModMDDBPath);

            // create a new one if it doesn't exist or couldn't be added
            Log("Copying over DB and building new DB Cache.");
            cache = new Dictionary<string, DateTime>();
            return cache;
        }


        // LOAD ORDER
        private static void PropagateConflictsForward(Dictionary<string, ModDef> modDefs)
        {
            // conflicts are a unidirectional edge, so make them one in ModDefs
            foreach (var modDef in modDefs.Values)
            {
                if (modDef.ConflictsWith.Count == 0)
                    continue;

                foreach (var conflict in modDef.ConflictsWith)
                {
                    if (modDefs.ContainsKey(conflict))
                        modDefs[conflict].ConflictsWith.Add(modDef.Name);
                }
            }
        }

        private static void FillInOptionalDependencies(Dictionary<string, ModDef> modDefs)
        {
            // add optional dependencies if they are present
            foreach (var modDef in modDefs.Values)
            {
                if (modDef.OptionallyDependsOn.Count == 0)
                    continue;

                foreach (var optDep in modDef.OptionallyDependsOn)
                {
                    if (modDefs.ContainsKey(optDep))
                        modDef.DependsOn.Add(optDep);
                }
            }
        }

        private static List<string> LoadLoadOrder(string path)
        {
            List<string> order;

            if (File.Exists(path))
            {
                try
                {
                    order = JsonConvert.DeserializeObject<List<string>>(File.ReadAllText(path));
                    Log("Loaded cached load order.");
                    return order;
                }
                catch (Exception e)
                {
                    Log("Loading cached load order failed, rebuilding it.");
                    Log($"\t{e.Message}");
                }
            }

            // create a new one if it doesn't exist or couldn't be added
            Log("Building new load order!");
            order = new List<string>();
            return order;
        }

        private static bool AreDependanciesResolved(ModDef modDef, HashSet<string> loaded)
        {
            return !(modDef.DependsOn.Count != 0 && modDef.DependsOn.Intersect(loaded).Count() != modDef.DependsOn.Count
                || modDef.ConflictsWith.Count != 0 && modDef.ConflictsWith.Intersect(loaded).Any());
        }

        private static List<string> GetLoadOrder(Dictionary<string, ModDef> modDefs, out List<string> unloaded)
        {
            var modDefsCopy = new Dictionary<string, ModDef>(modDefs);
            var cachedOrder = LoadLoadOrder(LoadOrderPath);
            var loadOrder = new List<string>();
            var loaded = new HashSet<string>();

            PropagateConflictsForward(modDefsCopy);
            FillInOptionalDependencies(modDefsCopy);

            // load the order specified in the file
            foreach (var modName in cachedOrder)
            {
                if (!modDefs.ContainsKey(modName) || !AreDependanciesResolved(modDefs[modName], loaded)) continue;

                modDefsCopy.Remove(modName);
                loadOrder.Add(modName);
                loaded.Add(modName);
            }

            // everything that is left in the copy hasn't been loaded before
            unloaded = modDefsCopy.Keys.OrderByDescending(x => x).ToList();

            // there is nothing left to load
            if (unloaded.Count == 0)
                return loadOrder;

            // this is the remainder that haven't been loaded before
            int removedThisPass;
            do
            {
                removedThisPass = 0;

                for (var i = unloaded.Count - 1; i >= 0; i--)
                {
                    var modDef = modDefs[unloaded[i]];

                    if (!AreDependanciesResolved(modDef, loaded)) continue;

                    unloaded.RemoveAt(i);
                    loadOrder.Add(modDef.Name);
                    loaded.Add(modDef.Name);
                    removedThisPass++;
                }
            } while (removedThisPass > 0 && unloaded.Count > 0);

            return loadOrder;
        }


        // READING mod.json AND INIT MODS
        private static void LoadMod(ModDef modDef)
        {
            var potentialAdditions = new List<ModDef.ManifestEntry>();

            // load out of the manifest
            if (modDef.LoadImplicitManifest && modDef.Manifest.All(x => Path.GetFullPath(Path.Combine(modDef.Directory, x.Path)) != Path.GetFullPath(Path.Combine(modDef.Directory, "StreamingAssets"))))
                modDef.Manifest.Add(new ModDef.ManifestEntry("StreamingAssets", true));

            // note: if a JSON has errors, this mod will not load, since InferIDFromFile will throw from parsing the JSON
            foreach (var entry in modDef.Manifest)
            {
                // handle prefabs; they have potential internal path to assetbundle
                if (entry.Type == "Prefab" && !string.IsNullOrEmpty(entry.AssetBundleName))
                {
                    if (!potentialAdditions.Any(x => x.Type == "AssetBundle" && x.Id == entry.AssetBundleName))
                    {
                        Log($"\t{modDef.Name} has a Prefab that's referencing an AssetBundle that hasn't been loaded. Put the assetbundle first in the manifest!");
                        return;
                    }

                    entry.Id = Path.GetFileNameWithoutExtension(entry.Path);
                    if (!FileIsOnDenyList(entry.Path)) potentialAdditions.Add(entry);
                    continue;
                }

                if (string.IsNullOrEmpty(entry.Path) && string.IsNullOrEmpty(entry.Type) && entry.Path != "StreamingAssets")
                {
                    Log($"\t{modDef.Name} has a manifest entry that is missing its path or type! Aborting load.");
                    return;
                }

                var entryPath = Path.GetFullPath(Path.Combine(modDef.Directory, entry.Path));
                if (Directory.Exists(entryPath))
                {
                    // path is a directory, add all the files there
                    var files = Directory.GetFiles(entryPath, "*", SearchOption.AllDirectories).Where(filePath => !FileIsOnDenyList(filePath));
                    foreach (var filePath in files)
                    {
                        var childModDef = new ModDef.ManifestEntry(entry, Path.GetFullPath(filePath), InferIDFromFile(filePath));
                        potentialAdditions.Add(childModDef);
                    }
                }
                else if (File.Exists(entryPath) && !FileIsOnDenyList(entryPath))
                {
                    // path is a file, add the single entry
                    entry.Id = entry.Id ?? InferIDFromFile(entryPath);
                    entry.Path = entryPath;
                    potentialAdditions.Add(entry);
                }
                else if (entry.Path != "StreamingAssets")
                {
                    // path is not streamingassets and it's missing
                    Log($"\tMissing Entry: Manifest specifies file/directory of {entry.Type} at path {entry.Path}, but it's not there. Continuing to load.");
                }
            }

            // load mod dll
            if (modDef.DLL != null)
            {
                var dllPath = Path.Combine(modDef.Directory, modDef.DLL);
                string typeName = null;
                var methodName = "Init";

                if (!File.Exists(dllPath))
                {
                    Log($"\t{modDef.Name} has a DLL specified ({dllPath}), but it's missing! Aborting load.");
                    return;
                }

                if (modDef.DLLEntryPoint != null)
                {
                    var pos = modDef.DLLEntryPoint.LastIndexOf('.');
                    if (pos == -1)
                    {
                        methodName = modDef.DLLEntryPoint;
                    }
                    else
                    {
                        typeName = modDef.DLLEntryPoint.Substring(0, pos);
                        methodName = modDef.DLLEntryPoint.Substring(pos + 1);
                    }
                }

                BTModLoader.LoadDLL(dllPath, methodName, typeName,
                    new object[] { modDef.Directory, modDef.Settings.ToString(Formatting.None) });
            }

            if (potentialAdditions.Count <= 0)
                return;

            // actually add the additions, since we successfully got through loading the other stuff
            Log($"{modDef.Name} {modDef.Version} : {potentialAdditions.Count} entries : {modDef.DLL ?? "No DLL"}");
            entriesByMod[modDef.Name] = potentialAdditions;
        }

        internal static void LoadMods()
        {
            ProgressPanel.SubmitWork(LoadMoadsLoop);
        }

        internal static IEnumerator<ProgressReport> LoadMoadsLoop()
        {
            stopwatch.Start();

            Log("");
            yield return new ProgressReport(1, "Initializing Mods", "");

            // find all sub-directories that have a mod.json file
            var modDirectories = Directory.GetDirectories(ModsDirectory)
                .Where(x => File.Exists(Path.Combine(x, MOD_JSON_NAME))).ToArray();

            if (modDirectories.Length == 0)
            {
                Log("No ModTek-compatable mods found.");
                yield break;
            }

            // create ModDef objects for each mod.json file
            var modDefs = new Dictionary<string, ModDef>();
            foreach (var modDirectory in modDirectories)
            {
                ModDef modDef;
                var modDefPath = Path.Combine(modDirectory, MOD_JSON_NAME);

                try
                {
                    modDef = ModDef.CreateFromPath(modDefPath);
                }
                catch (Exception e)
                {
                    Log($"Caught exception while parsing {MOD_JSON_NAME} at path {modDefPath}");
                    Log($"\t{e.Message}");
                    continue;
                }

                if (!modDef.Enabled)
                {
                    Log($"Will not load {modDef.Name} because it's disabled.");
                    continue;
                }

                if (modDefs.ContainsKey(modDef.Name))
                {
                    Log($"Already loaded a mod named {modDef.Name}. Skipping load from {modDef.Directory}.");
                    continue;
                }

                modDefs.Add(modDef.Name, modDef);
            }

            Log("");
            modLoadOrder = GetLoadOrder(modDefs, out var willNotLoad);
            foreach (var modName in willNotLoad)
            {
                Log($"Will not load {modName} because it's lacking a dependancy or a conflict loaded before it.");
            }
            Log("");

            // lists guarentee order
            var modLoaded = 0;
            foreach (var modName in modLoadOrder)
            {
                var modDef = modDefs[modName];
                yield return new ProgressReport(modLoaded++ / ((float)modLoadOrder.Count), "Initializing Mods", $"{modDef.Name} {modDef.Version}");
                try
                {
                    LoadMod(modDef);
                }
                catch (Exception e)
                {
                    Log($"Tried to load mod: {modDef.Name}, but something went wrong. Make sure all of your JSON is correct!");
                    Log($"\t{e.Message}");
                }
            }

            PrintHarmonySummary(HarmonySummaryPath);
            WriteJsonFile(LoadOrderPath, modLoadOrder);
            stopwatch.Stop();

            yield break;
        }


        // ADDING MOD CONTENT TO THE GAME
        private static void AddModEntry(VersionManifest manifest, ModDef.ManifestEntry modEntry)
        {
            if (modEntry.Path == null)
                return;

            VersionManifestAddendum addendum = null;
            if (!string.IsNullOrEmpty(modEntry.AddToAddendum))
            {
                addendum = manifest.GetAddendumByName(modEntry.AddToAddendum);

                if (addendum == null)
                {
                    Log($"\tCannot add {modEntry.Id} to {modEntry.AddToAddendum} because addendum doesn't exist in the manifest.");
                    return;
                }
            }

            // add special handling for particular types
            switch (modEntry.Type)
            {
                case "AssetBundle":
                    ModAssetBundlePaths[modEntry.Id] = modEntry.Path;
                    break;
                case "Texture2D":
                    ModTexture2Ds.Add(modEntry.Id);
                    break;
            }

            // add to addendum instead of adding to manifest
            if (addendum != null)
                Log($"\tAdd/Replace: \"{GetRelativePath(modEntry.Path, ModsDirectory)}\" ({modEntry.Type}) [{addendum.Name}]");
            else
                Log($"\tAdd/Replace: \"{GetRelativePath(modEntry.Path, ModsDirectory)}\" ({modEntry.Type})");

            // not added to addendum, not added to jsonmerges
            BTRLEntries.Add(modEntry);
            return;
        }

        private static bool AddModEntryToDB(MetadataDatabase db, string absolutePath, string typeStr)
        {
            if (Path.GetExtension(absolutePath)?.ToLower() != ".json")
                return false;

            var type = (BattleTechResourceType)Enum.Parse(typeof(BattleTechResourceType), typeStr);
            var relativePath = GetRelativePath(absolutePath, GameDirectory);

            switch (type) // switch is to avoid poisoning the output_log.txt with known types that don't use MDD
            {
                case BattleTechResourceType.TurretDef:
                case BattleTechResourceType.UpgradeDef:
                case BattleTechResourceType.VehicleDef:
                case BattleTechResourceType.ContractOverride:
                case BattleTechResourceType.SimGameEventDef:
                case BattleTechResourceType.LanceDef:
                case BattleTechResourceType.MechDef:
                case BattleTechResourceType.PilotDef:
                case BattleTechResourceType.WeaponDef:
                    if (!dbCache.ContainsKey(relativePath) || dbCache[relativePath] != File.GetLastWriteTimeUtc(absolutePath))
                    {
                        try
                        {
                            VersionManifestHotReload.InstantiateResourceAndUpdateMDDB(type, absolutePath, db);
                            dbCache[relativePath] = File.GetLastWriteTimeUtc(absolutePath);
                            return true;
                        }
                        catch (Exception e)
                        {
                            Log($"\tAdd to DB failed for {Path.GetFileName(absolutePath)}, exception caught:");
                            Log($"\t\t{e.Message}");
                            return false;
                        }
                    }
                    break;
            }

            return false;
        }

        internal static void BuildModManifestEntries()
        {
            CachedVersionManifest = VersionManifestUtilities.LoadDefaultManifest();
            ProgressPanel.SubmitWork(BuildModManifestEntriesLoop);
        }

        internal static IEnumerator<ProgressReport> BuildModManifestEntriesLoop()
        {
            stopwatch.Start();

            // there are no mods loaded, just return
            if (modLoadOrder == null || modLoadOrder.Count == 0)
                yield break;

            Log("");

            var jsonMerges = new Dictionary<string, List<string>>();
            var manifestMods = modLoadOrder.Where(name => entriesByMod.ContainsKey(name)).ToList();

            var entryCount = 0;
            var numEntries = 0;
            entriesByMod.Do(entries => numEntries += entries.Value.Count);

            foreach (var modName in manifestMods)
            {
                Log($"{modName}:");

                if (!BuiltNewTypeCache)
                    yield return new ProgressReport(entryCount / ((float)numEntries), $"Loading Mods", modName);

                foreach (var modEntry in entriesByMod[modName])
                {
                    if (BuiltNewTypeCache)
                        yield return new ProgressReport(entryCount / ((float)numEntries), $"Loading {modName}", modEntry.Id);

                    entryCount++;

                    // type being null means we have to figure out the type from the path (StreamingAssets)
                    if (modEntry.Type == null)
                    {
                        // TODO: + 16 is a little bizzare looking, it's the length of the substring + 1 because we want to get rid of it and the \
                        var relPath = modEntry.Path.Substring(modEntry.Path.LastIndexOf("StreamingAssets", StringComparison.Ordinal) + 16);
                        var fakeStreamingAssetsPath = Path.GetFullPath(Path.Combine(StreamingAssetsDirectory, relPath));

                        var types = GetTypesFromCacheOrManifest(CachedVersionManifest, fakeStreamingAssetsPath);
                        if (types == null)
                        {
                            Log($"\tCould not find an existing VersionManifest entry for {modEntry.Id}. Is this supposed to be a new entry? Don't put new entries in StreamingAssets!");
                            continue;
                        }

                        // this is getting merged later and then added to the BTRL entries then
                        if (Path.GetExtension(modEntry.Path).ToLower() == ".json" && modEntry.ShouldMergeJSON)
                        {
                            if (!jsonMerges.ContainsKey(fakeStreamingAssetsPath))
                                jsonMerges[fakeStreamingAssetsPath] = new List<string>();

                            if (jsonMerges[fakeStreamingAssetsPath].Contains(modEntry.Path)) // TODO: is this necessary?
                                continue;

                            // this assumes that .json can only have a single type
                            // typeCache will always contain this path
                            modEntry.Type = GetTypesFromCache(fakeStreamingAssetsPath)[0];

                            Log($"\tMerge: \"{GetRelativePath(modEntry.Path, ModsDirectory)}\" ({modEntry.Type})");

                            jsonMerges[fakeStreamingAssetsPath].Add(modEntry.Path);
                            continue;
                        }

                        foreach (var type in types)
                        {
                            var subModEntry = new ModDef.ManifestEntry(modEntry, modEntry.Path, modEntry.Id);
                            subModEntry.Type = type;
                            AddModEntry(CachedVersionManifest, subModEntry);
                        }

                        continue;
                    }

                    // get "fake" entries that don't actually go into the game's VersionManifest
                    // add videos to be loaded from an external path
                    switch (modEntry.Type)
                    {
                        case "Video":
                            var fileName = Path.GetFileName(modEntry.Path);
                            if (fileName != null && File.Exists(modEntry.Path))
                            {
                                Log($"\tVideo: \"{GetRelativePath(modEntry.Path, ModsDirectory)}\"");
                                ModVideos.Add(fileName, modEntry.Path);
                            }
                            continue;
                        case "AdvancedJSONMerge":
                            var targetFileRelative = AdvancedJSONMerger.GetTargetFile(modEntry.Path);
                            var targetFile = ResolvePath(targetFileRelative, StreamingAssetsDirectory);

                            // need to add the types of the file to the typeCache, so that they can be used later
                            // this actually returns the type, but we don't actually care about that right now
                            GetTypesFromCacheOrManifest(CachedVersionManifest, targetFile);

                            if (!jsonMerges.ContainsKey(targetFile))
                                jsonMerges[targetFile] = new List<string>();

                            if (jsonMerges[targetFile].Contains(modEntry.Path)) // TODO: is this necessary?
                                continue;

                            Log($"\tAdvancedJSONMerge: \"{GetRelativePath(modEntry.Path, ModsDirectory)}\" ({modEntry.Type})");
                            jsonMerges[targetFile].Add(modEntry.Path);
                            continue;
                    }

                    // non-streamingassets json merges
                    if (Path.GetExtension(modEntry.Path)?.ToLower() == ".json" && modEntry.ShouldMergeJSON)
                    {
                        // have to find the original path for the manifest entry that we're merging onto
                        var matchingEntry = CachedVersionManifest.Find(x => x.Id == modEntry.Id);

                        if (matchingEntry == null)
                        {
                            Log($"\tCould not find an existing VersionManifest entry for {modEntry.Id}!");
                            continue;
                        }

                        var matchingPath = Path.GetFullPath(matchingEntry.FilePath);

                        if (!jsonMerges.ContainsKey(matchingPath))
                            jsonMerges[matchingPath] = new List<string>();

                        if (jsonMerges[matchingPath].Contains(modEntry.Path)) // TODO: is this necessary?
                            continue;

                        // this assumes that .json can only have a single type
                        modEntry.Type = matchingEntry.Type;

                        TryAddTypeToCache(matchingPath, modEntry.Type);

                        Log($"\tMerge: \"{GetRelativePath(modEntry.Path, ModsDirectory)}\" ({modEntry.Type})");

                        jsonMerges[matchingPath].Add(modEntry.Path);
                        continue;
                    }

                    AddModEntry(CachedVersionManifest, modEntry);
                }
            }

            WriteJsonFile(TypeCachePath, typeCache);

            // perform merges into cache
            Log("");
            LogWithDate("Doing merges...");
            yield return new ProgressReport(1, "Merging", "");

            var mergeCount = 0;
            foreach (var originalPath in jsonMerges.Keys)
            {
                var mergePaths = jsonMerges[originalPath];

                if (!jsonMergeCache.HasCachedEntry(originalPath, mergePaths))
                    yield return new ProgressReport(mergeCount++ / ((float)jsonMerges.Count), "Merging", Path.GetFileNameWithoutExtension(originalPath));

                var cachePath = jsonMergeCache.GetOrCreateCachedEntry(originalPath, mergePaths);

                // something went wrong (the parent json prob had errors)
                if (cachePath == null)
                    continue;

                var cacheEntry = new ModDef.ManifestEntry(cachePath)
                {
                    ShouldMergeJSON = false,
                    Type = GetTypesFromCache(originalPath)[0], // this assumes only one type for each json file
                    Id = InferIDFromFile(cachePath)
                };

                AddModEntry(CachedVersionManifest, cacheEntry);
            }

            jsonMergeCache.WriteCacheToDisk(Path.Combine(CacheDirectory, MERGE_CACHE_FILE_NAME));

            Log("");
            Log("Syncing Database");
            yield return new ProgressReport(1, "Syncing Database", "");

            // check if files removed from DB cache
            var rebuildDB = false;
            var replacementEntries = new List<VersionManifestEntry>();
            var removeEntries = new List<string>();
            foreach (var path in dbCache.Keys)
            {
                var absolutePath = ResolvePath(path, GameDirectory);

                // check if the file in the db cache is still used
                if (BTRLEntries.Exists(x => x.Path == absolutePath))
                    continue;

                Log($"\tNeed to remove DB entry from file in path: {path}");

                // file is missing, check if another entry exists with same filename in manifest or in BTRL entries
                var fileName = Path.GetFileName(path);
                var existingEntry = BTRLEntries.Find(x => Path.GetFileName(x.Path) == fileName)?.GetVersionManifestEntry()
                    ?? CachedVersionManifest.Find(x => Path.GetFileName(x.FilePath) == fileName);

                if (existingEntry == null)
                {
                    Log("\t\tHave to rebuild DB, no existing entry in VersionManifest matches removed entry");
                    rebuildDB = true;
                    break;
                }

                replacementEntries.Add(existingEntry);
                removeEntries.Add(path);
            }

            // add removed entries replacements to db
            if (!rebuildDB)
            {
                // remove old entries
                foreach (var removeEntry in removeEntries)
                    dbCache.Remove(removeEntry);

                using (var metadataDatabase = new MetadataDatabase())
                {
                    foreach (var replacementEntry in replacementEntries)
                    {
                        if (AddModEntryToDB(metadataDatabase, Path.GetFullPath(replacementEntry.FilePath), replacementEntry.Type))
                            Log($"\t\tReplaced DB entry with an existing entry in path: {Path.GetFullPath(replacementEntry.FilePath)}");
                    }
                }
            }

            // if an entry has been removed and we cannot find a replacement, have to rebuild the mod db
            if (rebuildDB)
            {
                if (File.Exists(ModMDDBPath))
                    File.Delete(ModMDDBPath);

                File.Copy(MDDBPath, ModMDDBPath);
                dbCache = new Dictionary<string, DateTime>();
            }

            // add needed files to db
            var addCount = 0;
            using (var metadataDatabase = new MetadataDatabase())
            {
                foreach (var modEntry in BTRLEntries)
                {
                    if (modEntry.AddToDB && AddModEntryToDB(metadataDatabase, modEntry.Path, modEntry.Type))
                    {
                        yield return new ProgressReport(addCount / ((float)BTRLEntries.Count), "Populating Database", modEntry.Id);
                        Log($"\tAdded/Updated {modEntry.Id} ({modEntry.Type})");
                    }
                    addCount++;
                }
            }

            // write db/type cache to disk
            WriteJsonFile(DBCachePath, dbCache);

            stopwatch.Stop();
            Log("");
            LogWithDate($"Done. Elapsed running time: {stopwatch.Elapsed.TotalSeconds} seconds\n");
            CloseLogStream();

            yield break;
        }
    }
}
