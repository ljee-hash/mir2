using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace ResourceChecker
{
    // Lists and format constants below are extracted from the actual client
    // code so this validator stays in sync with what Client.exe will load:
    //   - CORE_DATA_LIBS  → Client/MirGraphics/MLibrary.cs (static readonly MLibrary fields)
    //   - NUMBERED_LIB_DIRS → same file, the InitLibrary(...) calls in the ctor
    //   - MAP_LIB_GROUPS  → same file, the #region Maplibs section
    //   - LIB_VERSION_EXPECTED → MLibrary.cs `LibVersion = 3`
    // Update these when the client's asset dependencies change.
    public static class ResourceChecks
    {
        public const int LibVersionExpected = 3;

        public static readonly string[] CoreDataLibs = new[]
        {
            "ChrSel", "Prguse", "Prguse2", "Prguse3", "UI_32bit",
            "BuffIcon", "Help", "MMap", "MapLinkIcon", "Title",
            "MagIcon", "MagIcon2", "Magic", "Magic2", "Magic3",
            "Effect", "MagicC", "GuildSkill", "Weather",
            "Background", "Dragon",
            "Items", "StateItem", "DNItems", "Items_Tooltip_32bit",
            "Deco",
        };

        public sealed class NumberedDir
        {
            public string Path { get; init; }
            public string Hint { get; init; }
        }

        public static readonly NumberedDir[] NumberedLibDirs = new NumberedDir[]
        {
            new() { Path = @"Data\CArmour",               Hint = "Warrior/Wizard/Taoist armour" },
            new() { Path = @"Data\CHair",                 Hint = "Warrior/Wizard/Taoist hair" },
            new() { Path = @"Data\CWeapon",               Hint = "Warrior/Wizard/Taoist weapon" },
            new() { Path = @"Data\CWeaponEffect",         Hint = "Weapon effects" },
            new() { Path = @"Data\CHumEffect",            Hint = "Player-cast effects" },
            new() { Path = @"Data\AArmour",               Hint = "Assassin armour" },
            new() { Path = @"Data\AHair",                 Hint = "Assassin hair" },
            new() { Path = @"Data\AWeapon",               Hint = "Assassin weapons (needs \" L\"/\" R\" pairs)" },
            new() { Path = @"Data\AHumEffect",            Hint = "Assassin effects" },
            new() { Path = @"Data\ARArmour",              Hint = "Archer armour" },
            new() { Path = @"Data\ARHair",                Hint = "Archer hair" },
            new() { Path = @"Data\ARWeapon",              Hint = "Archer weapons (needs \" S\" pair)" },
            new() { Path = @"Data\ARHumEffect",           Hint = "Archer effects" },
            new() { Path = @"Data\Monster",               Hint = "Monster sprites (000.Lib … 999.Lib)" },
            new() { Path = @"Data\Gate",                  Hint = "City gates" },
            new() { Path = @"Data\Flag",                  Hint = "Guild flags" },
            new() { Path = @"Data\Siege",                 Hint = "Sabuk castle siege" },
            new() { Path = @"Data\NPC",                   Hint = "NPCs" },
            new() { Path = @"Data\Mount",                 Hint = "Mounts" },
            new() { Path = @"Data\Fishing",               Hint = "Fishing rods/lures" },
            new() { Path = @"Data\Pet",                   Hint = "Pets / Intelligent creatures" },
            new() { Path = @"Data\Transform",             Hint = "Transform disguise" },
            new() { Path = @"Data\TransformRide2",        Hint = "Transform mounts" },
            new() { Path = @"Data\TransformEffect",       Hint = "Transform effects" },
            new() { Path = @"Data\TransformWeaponEffect", Hint = "Transform weapon fx" },
        };

        public sealed class MapGroup
        {
            public string Id { get; init; }
            public string Dir { get; init; }
            public bool Optional { get; init; }
            public string[] Files { get; init; }
        }

        public static readonly MapGroup[] MapLibGroups = BuildMapGroups();

        static MapGroup[] BuildMapGroups()
        {
            var wemadeMir2 = new List<string> { "Tiles", "Smtiles", "Objects" };
            for (int i = 2; i < 28; i++) wemadeMir2.Add("Objects" + i);
            wemadeMir2.Add("Objects_32bit");

            var shandaMir2 = new List<string> { "Tiles" };
            for (int i = 2; i <= 10; i++) shandaMir2.Add("Tiles" + i);
            shandaMir2.Add("SmTiles");
            for (int i = 2; i <= 10; i++) shandaMir2.Add("SmTiles" + i);
            shandaMir2.Add("Objects");
            for (int i = 2; i <= 31; i++) shandaMir2.Add("Objects" + i);
            shandaMir2.Add("AniTiles1");

            return new[]
            {
                new MapGroup { Id = "WemadeMir2", Dir = @"Data\Map\WemadeMir2", Files = wemadeMir2.ToArray() },
                new MapGroup { Id = "ShandaMir2", Dir = @"Data\Map\ShandaMir2", Files = shandaMir2.ToArray() },
                new MapGroup { Id = "WemadeMir3", Dir = @"Data\Map\WemadeMir3", Optional = true, Files = Array.Empty<string>() },
                new MapGroup { Id = "ShandaMir3", Dir = @"Data\Map\ShandaMir3", Optional = true, Files = Array.Empty<string>() },
            };
        }

        public enum Severity { Info, Ok, Warn, Fail }

        public sealed class Finding
        {
            public Severity Level { get; init; }
            public string Section { get; init; }
            public string Message { get; init; }
            public string Hint { get; init; }
        }

        public sealed class Report
        {
            public List<Finding> Findings { get; } = new();
            public int CoreMissing;
            public int CoreWrongVersion;
            public int DirectoriesMissing;
            public bool ShandaComplete;
            public bool WemadeComplete;

            public bool IsFatal => CoreMissing > 0 || DirectoriesMissing > 0;

            public void Add(Severity lvl, string section, string message, string hint = null)
                => Findings.Add(new Finding { Level = lvl, Section = section, Message = message, Hint = hint });
        }

        // Case-insensitive file lookup (Windows is fine either way; but a resource
        // pack authored on Linux may use lowercase names).
        static string ResolveFile(string dir, string wanted)
        {
            if (!Directory.Exists(dir)) return null;
            var lower = wanted.ToLowerInvariant();
            foreach (var f in Directory.EnumerateFiles(dir))
                if (Path.GetFileName(f).ToLowerInvariant() == lower)
                    return f;
            return null;
        }

        // Read the .Lib 8-byte header: [int32 version][int32 count].
        // Returns null if unreadable or too short.
        static (int version, int count)? PeekLibHeader(string filepath)
        {
            try
            {
                using var fs = new FileStream(filepath, FileMode.Open, FileAccess.Read, FileShare.Read);
                using var br = new BinaryReader(fs);
                if (fs.Length < 8) return null;
                return (br.ReadInt32(), br.ReadInt32());
            }
            catch
            {
                return null;
            }
        }

        public static Report Run(string clientRoot, Action<string> progress = null)
        {
            var r = new Report();
            progress ??= _ => { };

            // 1. Top-level dirs
            progress("Checking top-level directories…");
            foreach (var d in new[] { "Data", "Map", "Sound" })
            {
                if (Directory.Exists(Path.Combine(clientRoot, d)))
                    r.Add(Severity.Ok, "Layout", $"{d}\\ present");
                else
                    r.Add(Severity.Fail, "Layout", $"{d}\\ missing", $"Create {d}\\ or point the picker at the folder that contains it.");
            }

            // 2. Core Data\*.Lib
            progress("Checking core Data\\*.Lib …");
            var dataDir = Path.Combine(clientRoot, "Data");
            foreach (var name in CoreDataLibs)
            {
                var file = ResolveFile(dataDir, name + ".Lib");
                if (file == null)
                {
                    r.CoreMissing++;
                    r.Add(Severity.Fail, "Core lib", $"{name}.Lib missing",
                        $"Copy {name}.Lib into Data\\. Every full Mir 2 client pack ships this — its absence means the pack is a partial download.");
                    continue;
                }
                var h = PeekLibHeader(file);
                if (h == null)
                {
                    r.CoreMissing++;
                    r.Add(Severity.Fail, "Core lib", $"{name}.Lib unreadable / truncated",
                        "Re-download the resource pack — this file is under 8 bytes or locked.");
                }
                else if (h.Value.version != LibVersionExpected)
                {
                    r.CoreWrongVersion++;
                    r.Add(Severity.Warn, "Core lib",
                        $"{name}.Lib has version {h.Value.version}, expected {LibVersionExpected}",
                        "This looks like an old WeMade .wil renamed to .Lib. Open it in LibraryEditor, resave as v3, then replace.");
                }
                else
                {
                    r.Add(Severity.Ok, "Core lib", $"{name}.Lib (v{h.Value.version}, {h.Value.count} images)");
                }
            }

            // 3. Numbered class libs
            progress("Checking numbered class libraries…");
            foreach (var def in NumberedLibDirs)
            {
                var dir = Path.Combine(clientRoot, def.Path);
                if (!Directory.Exists(dir))
                {
                    r.DirectoriesMissing++;
                    r.Add(Severity.Fail, "Class lib", $"{def.Path}\\ missing — {def.Hint}",
                        $"Create the folder and drop 00.Lib (and higher-indexed files) in. Without it, {def.Hint.ToLowerInvariant()} will show as invisible / white blocks in-game.");
                    continue;
                }
                var libs = Directory.EnumerateFiles(dir, "*.Lib", SearchOption.TopDirectoryOnly).ToArray();
                if (libs.Length == 0)
                {
                    r.Add(Severity.Warn, "Class lib", $"{def.Path}\\ exists but is empty — {def.Hint}",
                        "Populate at least 00.Lib. If your client only needs class 0 sprites this may be intentional.");
                    continue;
                }
                // Peek first 3
                var wrong = new List<string>();
                foreach (var lib in libs.Take(3))
                {
                    var h = PeekLibHeader(lib);
                    if (h != null && h.Value.version != LibVersionExpected)
                        wrong.Add($"{Path.GetFileName(lib)} v{h.Value.version}");
                }
                if (wrong.Count > 0)
                    r.Add(Severity.Warn, "Class lib", $"{def.Path}\\ has wrong-version files: {string.Join(", ", wrong)}",
                        "Same fix as core libs — resave in LibraryEditor.");
                else
                    r.Add(Severity.Ok, "Class lib", $"{def.Path}\\ ({libs.Length} files) — {def.Hint}");
            }

            // 4. Map lib groups
            progress("Checking map tile libraries…");
            foreach (var g in MapLibGroups)
            {
                var dir = Path.Combine(clientRoot, g.Dir);
                if (!Directory.Exists(dir))
                {
                    if (g.Optional)
                        r.Add(Severity.Info, "Map tiles", $"{g.Id}: directory missing (optional — Mir3 tiles)");
                    else
                        r.Add(Severity.Fail, "Map tiles", $"{g.Id}: directory {g.Dir}\\ missing",
                            g.Id.StartsWith("Shanda")
                                ? "Without this, all Shanda-style ('仿盛大') maps render as blank. Get a KR/CN client pack."
                                : "Without this, original WeMade maps render as blank.");
                    continue;
                }
                if (g.Files.Length == 0)
                {
                    r.Add(Severity.Info, "Map tiles", $"{g.Id}: directory present (Mir3 files not enumerated)");
                    continue;
                }
                int present = 0;
                var missing = new List<string>();
                foreach (var f in g.Files)
                {
                    if (ResolveFile(dir, f + ".Lib") != null) present++;
                    else missing.Add(f);
                }
                if (missing.Count == 0)
                {
                    r.Add(Severity.Ok, "Map tiles", $"{g.Id}: all {g.Files.Length} tile libs present");
                    if (g.Id == "ShandaMir2") r.ShandaComplete = true;
                    if (g.Id == "WemadeMir2") r.WemadeComplete = true;
                }
                else if (present == 0)
                {
                    r.Add(Severity.Fail, "Map tiles", $"{g.Id}: 0/{g.Files.Length} present",
                        "Directory exists but is empty — download the corresponding tile pack.");
                }
                else
                {
                    var preview = missing.Take(5);
                    var extra = missing.Count > 5 ? $", … (+{missing.Count - 5} more)" : "";
                    r.Add(Severity.Warn, "Map tiles",
                        $"{g.Id}: {present}/{g.Files.Length} present. Missing: {string.Join(", ", preview)}{extra}",
                        "Some maps referencing the missing tiles will show holes. Complete the pack or accept the gaps.");
                }
            }

            // 5. Map + Sound counts
            progress("Counting Map/ and Sound/ contents…");
            foreach (var pair in new[] { ("Map", new[] { ".map" }), ("Sound", new[] { ".wav", ".mp3", ".ogg", ".mid" }) })
            {
                var dir = Path.Combine(clientRoot, pair.Item1);
                if (!Directory.Exists(dir)) continue;
                var count = Directory.EnumerateFiles(dir, "*.*", SearchOption.TopDirectoryOnly)
                                     .Count(f => pair.Item2.Any(ext => f.EndsWith(ext, StringComparison.OrdinalIgnoreCase)));
                if (count > 0)
                    r.Add(Severity.Ok, "Content", $"{pair.Item1}\\ has {count} file(s)");
                else
                    r.Add(Severity.Warn, "Content", $"{pair.Item1}\\ has no expected files",
                        $"Copy .map / audio files in — server can start without these, but players will crash entering an area with no map or hitting sound events.");
            }

            return r;
        }
    }
}
