using System.Globalization;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace UfoEncounter.Util;

/// <summary>
/// Generates a light-only MSFS Community package — no Blender needed. Every
/// variant is a plain SimObject (category SimpleObject) whose model is a tiny
/// hand-written glTF:
///  • "Licht" variants: no mesh at all, just nodes carrying an ASOBO_street_light
///    (the MSFS 2024 light extension; keys as written by Asobo's exporter) —
///    a glowing point with lens flare that can also light up the aircraft.
///    Ruhig/Puls/Blitz have one light; Glimmen/Funkeln stack a steady base and
///    flare-only overlays at the same point, so they sparkle but never go dark.
///  • "Funke" variants: the same idea, small and dim (satellites).
///  • "Kugel"/"Kern" variants: an emissive sphere plus lights, for orbs that
///    stay visible in daylight (Kern = big and bright).
///  • Diagnostic variants ("Test_"): the older ASOBO_macro_light (MSFS 2020 format),
///    node offsets, flash_phase unit and flare_only visibility.
/// The mesh-less glTF + ModelInfo/LODS XML structure mirrors files that ship in
/// production packages (e.g. FlyByWire's model.aic/empty.gltf).
/// </summary>
public static class LightPackageGenerator
{
    public const string TitlePrefix = "UFOE_";
    public const string PackageFolderName = "ufo-encounter-lights";
    private const string SimObjectDir = "SimObjects/Misc/UFOE_Lights";
    private const string Creator = "UFO Encounter";

    // Copy of the default options (keeps its TypeInfoResolver) with indentation.
    private static readonly JsonSerializerOptions Indented = new(JsonSerializerOptions.Default) { WriteIndented = true };

    /// <summary>Role of a variant: Steady/Soft/Hard are the mesh-less "Licht" titles,
    /// Orb = Kugel, Core = Kern, Spark = Funke.</summary>
    public enum LightKind { Steady, Soft, Hard, Orb, Core, Spark, Diagnostic }

    /// <summary>One light node: candela at night/day, flashes per MINUTE (0 = steady),
    /// flash duration in seconds, optional node translation in metres (glTF axes).</summary>
    public sealed record LightSpec(double[] Color, double NightCd, double DayCd,
        double FlashPerMin = 0, double FlashSec = 0.2, double FlashPhase = 0,
        bool RandomPhase = true, bool FlareOnly = false, double[]? Translation = null);

    /// <summary>One SimObject title. ColorName is "" for diagnostics; OrbRadius 0 = no mesh.</summary>
    public sealed record Variant(string Title, string Model, string ColorName, string StyleName, LightKind Kind,
        IReadOnlyList<LightSpec> Lights, double[] Color, double OrbRadius = 0, double EmissiveStrength = 0,
        bool MacroLight = false)
    {
        public bool Orb => OrbRadius > 0;
    }

    /// <summary>LightTitles = UFOE_Licht_* (Ruhig, Glimmen, Funkeln, Puls, Blitz); the
    /// other kinds have their own lists. DiagnosticTitles[0] is the macro-light test.</summary>
    public sealed record Result(string PackagePath, IReadOnlyList<string> LightTitles,
        IReadOnlyList<string> OrbTitles, IReadOnlyList<string> DiagnosticTitles,
        IReadOnlyList<string> CoreTitles, IReadOnlyList<string> SparkTitles);

    private static readonly (string Name, double[] Rgb)[] Colors =
    {
        ("Weiss", new[] { 1.0, 1.0, 1.0 }),
        ("Rot", new[] { 1.0, 0.08, 0.05 }),
        ("Gruen", new[] { 0.1, 1.0, 0.25 }),
        ("Blau", new[] { 0.15, 0.35, 1.0 }),
        ("Bernstein", new[] { 1.0, 0.6, 0.1 }),
        ("Violett", new[] { 0.65, 0.2, 1.0 }),
        ("Cyan", new[] { 0.2, 0.95, 1.0 }),
    };

    public static IReadOnlyList<string> ColorNames { get; } = Colors.Select(c => c.Name).ToArray();

    // Spark rhythm per colour (flashes/min, s): primes, so no two colours share a beat.
    private static readonly double[] SparkFreq = { 97, 101, 103, 107, 109, 113, 127 };
    private static readonly double[] SparkDur = { 0.06, 0.05, 0.07, 0.055, 0.08, 0.065, 0.075 };

    // Must stay below the tables above: static initializers run in textual order.
    public static IReadOnlyList<Variant> Variants { get; } = BuildVariants();

    private static readonly Dictionary<string, Variant> ByTitle =
        Variants.ToDictionary(v => v.Title, StringComparer.OrdinalIgnoreCase);

    /// <summary>Case-insensitive lookup of a generated title; null if it is not ours.</summary>
    public static Variant? Find(string? title) =>
        title is null ? null : ByTitle.GetValueOrDefault(title.Trim());

    /// <summary>Titles of the given kinds, in package order.</summary>
    public static IReadOnlyList<string> TitlesOf(params LightKind[] kinds) =>
        Variants.Where(v => kinds.Contains(v.Kind)).Select(v => v.Title).ToList();

    private static List<Variant> BuildVariants()
    {
        var list = new List<Variant>();
        foreach (var (name, rgb) in Colors)
        {
            list.Add(Licht(name, "Ruhig", LightKind.Steady, rgb, new LightSpec(rgb, 20000, 60000)));
            // Soft: steady base plus whitish flare-only glints at the same point. Random
            // phase and incommensurate rates keep them irregular and out of sync.
            list.Add(Licht(name, "Glimmen", LightKind.Soft, rgb,
                new LightSpec(rgb, 8000, 24000),
                Overlay(Whiten(rgb, 0.4), 16000, 48000, 30, 0.6)));
            list.Add(Licht(name, "Funkeln", LightKind.Soft, rgb,
                new LightSpec(rgb, 9000, 27000),
                Overlay(Whiten(rgb, 0.5), 6000, 18000, 37, 0.10),
                Overlay(Whiten(rgb, 0.5), 5000, 15000, 53, 0.07)));
            // Hard: the single light blinks fully off.
            list.Add(Licht(name, "Puls", LightKind.Hard, rgb, new LightSpec(rgb, 20000, 60000, 48, 0.55)));
            list.Add(Licht(name, "Blitz", LightKind.Hard, rgb, new LightSpec(rgb, 20000, 60000, 60, 0.08)));
        }
        foreach (var (name, rgb) in Colors.Take(4))
            list.Add(new($"{TitlePrefix}Kugel_{name}", $"Kugel_{name}", name, "Kugel", LightKind.Orb,
                new[] { new LightSpec(rgb, 20000, 60000) }, rgb, OrbRadius: 0.6, EmissiveStrength: 40));
        foreach (var (name, rgb) in Colors)
            list.Add(new($"{TitlePrefix}Kern_{name}", $"Kern_{name}", name, "Kern", LightKind.Core,
                new[] { new LightSpec(rgb, 50000, 150000), Overlay(Whiten(rgb, 0.5), 40000, 120000, 8, 1.0) },
                rgb, OrbRadius: 1.3, EmissiveStrength: 60));
        for (int i = 0; i < Colors.Length; i++)
        {
            // Small and dim: the base barely lights anything, the glints are flare-only.
            var (name, rgb) = Colors[i];
            double f = SparkFreq[i], d = SparkDur[i];
            var glint = Whiten(rgb, 0.5);
            list.Add(Funke(name, "Glimmen", rgb, new LightSpec(rgb, 1500, 5000),
                Overlay(Whiten(rgb, 0.4), 4000, 12000, Math.Round(f * 0.4, 1), 0.5)));
            list.Add(Funke(name, "Funkeln", rgb, new LightSpec(rgb, 1500, 5000),
                Overlay(glint, 5000, 15000, Math.Round(f * 0.37, 1), d),
                Overlay(glint, 4000, 12000, Math.Round(f * 0.61, 1), Math.Round(d * 0.8, 3))));
            list.Add(Funke(name, "Blitz", rgb, new LightSpec(rgb, 800, 2500),
                Overlay(glint, 6000, 18000, f, d)));
        }

        // Diagnostics: fixed phase (no random_phase), for one night test in the sim.
        double[] white = Rgb("Weiss"), red = Rgb("Rot"), green = Rgb("Gruen"), blue = Rgb("Blau");
        list.Add(new($"{TitlePrefix}Test_Macro_Weiss", "Test_Macro_Weiss", "", "Macro_Weiss", LightKind.Diagnostic,
            new[] { new LightSpec(white, 30, 0) }, white, MacroLight: true));
        list.Add(Test("Versatz", white,
            Fixed(red, -4, 0, 0), Fixed(green, 4, 0, 0), Fixed(white, 0, 4, 0)));
        // A chase around the square, phases in seconds (period 4 s) vs. fractions of the
        // period: whichever of the two runs as an even chase reveals the unit.
        double[][] chase = { red, green, blue, white };
        double[][] corners = { new[] { 2.0, 0, 2 }, new[] { 2.0, 0, -2 }, new[] { -2.0, 0, -2 }, new[] { -2.0, 0, 2 } };
        list.Add(Test("Phase_Sek", white, Enumerable.Range(0, 4).Select(k =>
            new LightSpec(chase[k], 20000, 60000, 15, 0.6, k, RandomPhase: false, Translation: corners[k])).ToArray()));
        list.Add(Test("Phase_Anteil", white, Enumerable.Range(0, 4).Select(k =>
            new LightSpec(chase[k], 20000, 60000, 15, 0.6, k * 0.25, RandomPhase: false, Translation: corners[k])).ToArray()));
        list.Add(Test("FlareOnly", white, new LightSpec(white, 20000, 60000, RandomPhase: false, FlareOnly: true)));
        return list;

        static Variant Licht(string color, string style, LightKind kind, double[] rgb, params LightSpec[] lights) =>
            new($"{TitlePrefix}Licht_{color}_{style}", $"Licht_{color}_{style}", color, style, kind, lights, rgb);
        static Variant Funke(string color, string style, double[] rgb, params LightSpec[] lights) =>
            new($"{TitlePrefix}Funke_{color}_{style}", $"Funke_{color}_{style}", color, style, LightKind.Spark, lights, rgb);
        static Variant Test(string name, double[] rgb, params LightSpec[] lights) =>
            new($"{TitlePrefix}Test_{name}", $"Test_{name}", "", name, LightKind.Diagnostic, lights, rgb);
        static LightSpec Overlay(double[] rgb, double night, double day, double perMin, double sec) =>
            new(rgb, night, day, perMin, sec, FlareOnly: true);
        static LightSpec Fixed(double[] rgb, double x, double y, double z) =>
            new(rgb, 20000, 60000, RandomPhase: false, Translation: new[] { x, y, z });
    }

    private static double[] Rgb(string colorName) => Colors.First(c => c.Name == colorName).Rgb;

    /// <summary>Blend a colour towards white (share 0..1), rounded for tidy JSON.</summary>
    private static double[] Whiten(double[] c, double share) =>
        c.Select(x => Math.Round(x * (1 - share) + share, 3)).ToArray();

    /// <summary>Find the MSFS 2024 Community folder via UserCfg.opt
    /// (InstalledPackagesPath), for the Steam and the Microsoft Store edition.</summary>
    public static string? DetectCommunityFolder()
    {
        string roaming = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        string local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        string[] candidates =
        {
            Path.Combine(roaming, "Microsoft Flight Simulator 2024", "UserCfg.opt"),
            Path.Combine(local, "Packages", "Microsoft.Limitless_8wekyb3d8bbwe", "LocalCache", "UserCfg.opt"),
        };
        foreach (var cfg in candidates)
        {
            try
            {
                if (!File.Exists(cfg)) continue;
                foreach (var line in File.ReadLines(cfg))
                {
                    var t = line.Trim();
                    if (!t.StartsWith("InstalledPackagesPath", StringComparison.OrdinalIgnoreCase)) continue;
                    int a = t.IndexOf('"'), b = t.LastIndexOf('"');
                    if (a < 0 || b <= a) continue;
                    var community = Path.Combine(t.Substring(a + 1, b - a - 1), "Community");
                    if (Directory.Exists(community)) return community;
                }
            }
            catch { /* try the next candidate */ }
        }
        return null;
    }

    /// <summary>Write (or rewrite) the package into <paramref name="communityFolder"/>.
    /// Refuses to overwrite a folder of the same name that isn't ours.</summary>
    public static Result Generate(string communityFolder)
    {
        string root = Path.Combine(communityFolder, PackageFolderName);
        if (Directory.Exists(root))
        {
            string manifest = Path.Combine(root, "manifest.json");
            if (!File.Exists(manifest) || !File.ReadAllText(manifest).Contains($"\"creator\": \"{Creator}\""))
                throw new InvalidOperationException($"Der Ordner „{root}“ existiert bereits und stammt nicht von diesem Programm.");
            // Clear the old content but keep manifest.json (rewritten at the end). If
            // MSFS or Explorer still holds a file open, the delete throws while the
            // manifest survives, so a retry after closing MSFS still recognises the folder.
            foreach (var dir in Directory.GetDirectories(root))
                Directory.Delete(dir, recursive: true);
            foreach (var file in Directory.GetFiles(root))
                if (!Path.GetFileName(file).Equals("manifest.json", StringComparison.OrdinalIgnoreCase))
                    File.Delete(file);
        }

        string simDir = Path.Combine(root, SimObjectDir.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(simDir);

        var sim = new StringBuilder();
        sim.AppendLine("; Generated by UFO Encounter. Light-only SimObjects, spawned via SimConnect.");
        sim.AppendLine("[VERSION]\nmajor = 1\nminor = 0\n");
        sim.AppendLine("[GENERAL]\ncategory = SimpleObject\n");
        for (int i = 0; i < Variants.Count; i++)
        {
            var v = Variants[i];
            sim.AppendLine($"[FLTSIM.{i}]");
            sim.AppendLine($"title = \"{v.Title}\"");
            sim.AppendLine($"model = \"{v.Model}\"");
            sim.AppendLine("texture = \"\"");
            sim.AppendLine();

            string modelDir = Path.Combine(simDir, "model." + v.Model);
            Directory.CreateDirectory(modelDir);
            WriteText(Path.Combine(modelDir, "model.cfg"), "[models]\nnormal = UFOE_Light.xml\n");
            WriteText(Path.Combine(modelDir, "UFOE_Light.xml"),
                "<ModelInfo>\n    <LODS>\n        <LOD minSize=\"0.0\" ModelFile=\"UFOE_Light.gltf\" />\n    </LODS>\n    <Behaviors/>\n</ModelInfo>\n");

            if (v.Orb)
            {
                byte[] bin = BuildSphereBin(v.OrbRadius, out int vertexCount, out int indexCount, out double radius);
                File.WriteAllBytes(Path.Combine(modelDir, "UFOE_Light.bin"), bin);
                WriteText(Path.Combine(modelDir, "UFOE_Light.gltf"), OrbGltf(v, bin.Length, vertexCount, indexCount, radius));
            }
            else
            {
                WriteText(Path.Combine(modelDir, "UFOE_Light.gltf"), LightGltf(v));
            }
        }
        WriteText(Path.Combine(simDir, "sim.cfg"), sim.ToString());

        long total = WriteLayout(root);
        WriteManifest(root, total);

        return new Result(root,
            TitlesOf(LightKind.Steady, LightKind.Soft, LightKind.Hard),
            TitlesOf(LightKind.Orb),
            TitlesOf(LightKind.Diagnostic),
            TitlesOf(LightKind.Core),
            TitlesOf(LightKind.Spark));
    }

    /// <summary>Titles in the installed package's sim.cfg (case-insensitive), or null
    /// if the package or its sim.cfg is missing or unreadable. Never throws.</summary>
    public static IReadOnlySet<string>? ReadInstalledTitles(string communityFolder)
    {
        try
        {
            string cfg = Path.Combine(communityFolder, PackageFolderName,
                SimObjectDir.Replace('/', Path.DirectorySeparatorChar), "sim.cfg");
            if (!File.Exists(cfg)) return null;
            var titles = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var line in File.ReadLines(cfg))
            {
                var t = line.Trim();
                int eq = t.IndexOf('=');
                if (eq < 0 || !t[..eq].Trim().Equals("title", StringComparison.OrdinalIgnoreCase)) continue;
                var value = t[(eq + 1)..].Trim();
                if (value.StartsWith('"'))
                {
                    int close = value.IndexOf('"', 1);
                    value = close > 0 ? value[1..close] : value[1..];
                }
                else
                {
                    int comment = value.IndexOf(';');
                    if (comment >= 0) value = value[..comment];
                }
                value = value.Trim();
                if (value.Length > 0) titles.Add(value);
            }
            return titles;
        }
        catch
        {
            return null;
        }
    }

    // ---------------- glTF writers ----------------

    private static JsonObject LightExtension(LightSpec s, bool macro)
    {
        if (macro)
        {
            return new JsonObject
            {
                ["ASOBO_macro_light"] = new JsonObject
                {
                    ["color"] = Arr(s.Color),
                    ["intensity"] = s.NightCd,
                    ["cone_angle"] = 360.0,
                    ["has_symmetry"] = false,
                    ["flash_frequency"] = s.FlashPerMin,
                    ["flash_duration"] = s.FlashSec,
                    ["flash_phase"] = s.FlashPhase,
                    ["rotation_speed"] = 0.0,
                    ["day_night_cycle"] = false,
                },
            };
        }
        return new JsonObject
        {
            ["ASOBO_street_light"] = new JsonObject
            {
                ["color"] = Arr(s.Color),
                ["intensity"] = s.NightCd,          // candela at night
                ["daytime_intensity"] = s.DayCd,    // used because day_night_cycle = false
                ["day_night_cycle"] = false,
                ["cone_angle"] = 360.0,
                ["has_symmetry"] = false,
                ["flash_frequency"] = s.FlashPerMin,
                ["flash_duration"] = s.FlashSec,
                ["flash_phase"] = s.FlashPhase,
                ["rotation_phase"] = 0.0,
                ["rotation_speed"] = 0.0,
                ["random_phase"] = s.RandomPhase,
                ["flare_enabled"] = true,           // in the air only the flare is visible
                ["flare_only"] = s.FlareOnly,
            },
        };
    }

    /// <summary>One node per light: "UfoLight", "UfoLight1", ... (names double as unique ids).</summary>
    private static IEnumerable<JsonObject> LightNodes(Variant v) =>
        v.Lights.Select((s, i) =>
        {
            string name = i == 0 ? "UfoLight" : $"UfoLight{i}";
            var ext = LightExtension(s, v.MacroLight);
            ext["ASOBO_unique_id"] = new JsonObject { ["id"] = name };
            var node = new JsonObject { ["name"] = name };
            if (s.Translation is { } t) node["translation"] = Arr(t);
            node["extensions"] = ext;
            return node;
        });

    private static JsonObject RootNode(int childCount)
    {
        var children = new JsonArray();
        for (int i = 1; i <= childCount; i++) children.Add(JsonValue.Create(i));
        return new JsonObject
        {
            ["name"] = "Root",
            ["children"] = children,
            ["extensions"] = new JsonObject { ["ASOBO_unique_id"] = new JsonObject { ["id"] = "Root" } },
        };
    }

    private static JsonObject Asset(bool optimized)
    {
        var ext = new JsonObject
        {
            ["ASOBO_normal_map_convention"] = new JsonObject { ["tangent_space_convention"] = "DirectX" },
        };
        if (optimized) ext["ASOBO_asset_optimized"] = new JsonObject { ["MajorVersion"] = 4, ["MinorVersion"] = 4 };
        return new JsonObject { ["generator"] = "UFO Encounter light generator", ["version"] = "2.0", ["extensions"] = ext };
    }

    private static string LightGltf(Variant v)
    {
        string lightExt = v.MacroLight ? "ASOBO_macro_light" : "ASOBO_street_light";
        var nodes = new JsonArray { RootNode(v.Lights.Count) };
        foreach (var n in LightNodes(v)) nodes.Add(n);
        var doc = new JsonObject
        {
            ["asset"] = Asset(optimized: true),
            ["extensionsUsed"] = Arr("ASOBO_normal_map_convention", "ASOBO_unique_id", "ASOBO_asset_optimized", lightExt),
            ["nodes"] = nodes,
            ["scene"] = 0,
            ["scenes"] = new JsonArray { new JsonObject { ["name"] = "Scene", ["nodes"] = new JsonArray(0) } },
        };
        return doc.ToJsonString(Indented);
    }

    private static string OrbGltf(Variant v, int binLength, int vertexCount, int indexCount, double r)
    {
        int posBytes = vertexCount * 12;
        int idxBytes = indexCount * 2;
        // The mesh node stays at index 1; the light nodes follow from index 2.
        var nodes = new JsonArray
        {
            RootNode(1 + v.Lights.Count),
            new JsonObject
            {
                ["name"] = "Orb",
                ["mesh"] = 0,
                ["extensions"] = new JsonObject { ["ASOBO_unique_id"] = new JsonObject { ["id"] = "Orb" } },
            },
        };
        foreach (var n in LightNodes(v)) nodes.Add(n);
        var doc = new JsonObject
        {
            ["asset"] = Asset(optimized: false),
            ["extensionsUsed"] = Arr("ASOBO_normal_map_convention", "ASOBO_unique_id", "ASOBO_street_light", "KHR_materials_emissive_strength"),
            ["nodes"] = nodes,
            ["meshes"] = new JsonArray
            {
                new JsonObject
                {
                    ["name"] = "Orb",
                    ["primitives"] = new JsonArray
                    {
                        new JsonObject
                        {
                            ["attributes"] = new JsonObject { ["POSITION"] = 0, ["NORMAL"] = 1 },
                            ["indices"] = 2,
                            ["material"] = 0,
                        },
                    },
                },
            },
            ["materials"] = new JsonArray
            {
                new JsonObject
                {
                    ["name"] = "OrbGlow",
                    ["pbrMetallicRoughness"] = new JsonObject
                    {
                        ["baseColorFactor"] = Arr(v.Color[0], v.Color[1], v.Color[2], 1.0),
                        ["metallicFactor"] = 0.0,
                        ["roughnessFactor"] = 1.0,
                    },
                    ["emissiveFactor"] = Arr(v.Color),
                    ["extensions"] = new JsonObject
                    {
                        ["KHR_materials_emissive_strength"] = new JsonObject { ["emissiveStrength"] = v.EmissiveStrength },
                    },
                },
            },
            ["accessors"] = new JsonArray
            {
                new JsonObject
                {
                    ["bufferView"] = 0, ["componentType"] = 5126, ["count"] = vertexCount, ["type"] = "VEC3",
                    ["min"] = Arr(-r, -r, -r), ["max"] = Arr(r, r, r),
                },
                new JsonObject { ["bufferView"] = 1, ["componentType"] = 5126, ["count"] = vertexCount, ["type"] = "VEC3" },
                new JsonObject { ["bufferView"] = 2, ["componentType"] = 5123, ["count"] = indexCount, ["type"] = "SCALAR" },
            },
            ["bufferViews"] = new JsonArray
            {
                new JsonObject { ["buffer"] = 0, ["byteOffset"] = 0, ["byteLength"] = posBytes, ["target"] = 34962 },
                new JsonObject { ["buffer"] = 0, ["byteOffset"] = posBytes, ["byteLength"] = posBytes, ["target"] = 34962 },
                new JsonObject { ["buffer"] = 0, ["byteOffset"] = 2 * posBytes, ["byteLength"] = idxBytes, ["target"] = 34963 },
            },
            ["buffers"] = new JsonArray { new JsonObject { ["uri"] = "UFOE_Light.bin", ["byteLength"] = binLength } },
            ["scene"] = 0,
            ["scenes"] = new JsonArray { new JsonObject { ["name"] = "Scene", ["nodes"] = new JsonArray(0) } },
        };
        return doc.ToJsonString(Indented);
    }

    /// <summary>Icosphere (one subdivision: 42 vertices, 80 triangles). Layout:
    /// positions (float3), normals (float3), indices (uint16) — all 4-byte aligned.</summary>
    private static byte[] BuildSphereBin(double radius, out int vertexCount, out int indexCount, out double r)
    {
        double t = (1 + Math.Sqrt(5)) / 2;
        var verts = new List<double[]>
        {
            new[] { -1, t, 0.0 }, new[] { 1, t, 0.0 }, new[] { -1, -t, 0.0 }, new[] { 1, -t, 0.0 },
            new[] { 0.0, -1, t }, new[] { 0.0, 1, t }, new[] { 0.0, -1, -t }, new[] { 0.0, 1, -t },
            new[] { t, 0.0, -1 }, new[] { t, 0.0, 1 }, new[] { -t, 0.0, -1 }, new[] { -t, 0.0, 1 },
        };
        for (int i = 0; i < verts.Count; i++) verts[i] = Normalize(verts[i]);
        var faces = new List<int[]>
        {
            new[] { 0, 11, 5 }, new[] { 0, 5, 1 }, new[] { 0, 1, 7 }, new[] { 0, 7, 10 }, new[] { 0, 10, 11 },
            new[] { 1, 5, 9 }, new[] { 5, 11, 4 }, new[] { 11, 10, 2 }, new[] { 10, 7, 6 }, new[] { 7, 1, 8 },
            new[] { 3, 9, 4 }, new[] { 3, 4, 2 }, new[] { 3, 2, 6 }, new[] { 3, 6, 8 }, new[] { 3, 8, 9 },
            new[] { 4, 9, 5 }, new[] { 2, 4, 11 }, new[] { 6, 2, 10 }, new[] { 8, 6, 7 }, new[] { 9, 8, 1 },
        };
        var cache = new Dictionary<(int, int), int>();
        int Mid(int a, int b)
        {
            var key = a < b ? (a, b) : (b, a);
            if (cache.TryGetValue(key, out int m)) return m;
            var va = verts[a]; var vb = verts[b];
            verts.Add(Normalize(new[] { (va[0] + vb[0]) / 2, (va[1] + vb[1]) / 2, (va[2] + vb[2]) / 2 }));
            return cache[key] = verts.Count - 1;
        }
        var sub = new List<int[]>();
        foreach (var f in faces)
        {
            int a = Mid(f[0], f[1]), b = Mid(f[1], f[2]), c = Mid(f[2], f[0]);
            sub.Add(new[] { f[0], a, c }); sub.Add(new[] { f[1], b, a });
            sub.Add(new[] { f[2], c, b }); sub.Add(new[] { a, b, c });
        }

        vertexCount = verts.Count;
        indexCount = sub.Count * 3;
        r = radius;
        using var ms = new MemoryStream();
        using var w = new BinaryWriter(ms);
        foreach (var v in verts) { w.Write((float)(v[0] * radius)); w.Write((float)(v[1] * radius)); w.Write((float)(v[2] * radius)); }
        foreach (var v in verts) { w.Write((float)v[0]); w.Write((float)v[1]); w.Write((float)v[2]); }
        foreach (var f in sub) { w.Write((ushort)f[0]); w.Write((ushort)f[1]); w.Write((ushort)f[2]); }
        while (ms.Length % 4 != 0) w.Write((byte)0);
        w.Flush();
        return ms.ToArray();
    }

    private static double[] Normalize(double[] v)
    {
        double l = Math.Sqrt(v[0] * v[0] + v[1] * v[1] + v[2] * v[2]);
        return new[] { v[0] / l, v[1] / l, v[2] / l };
    }

    // ---------------- package metadata ----------------

    /// <summary>layout.json lists every file except layout.json and manifest.json,
    /// with size and last-write time as a Windows FILETIME.</summary>
    private static long WriteLayout(string root)
    {
        var entries = new JsonArray();
        long total = 0;
        foreach (var file in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories).OrderBy(f => f, StringComparer.Ordinal))
        {
            string rel = Path.GetRelativePath(root, file).Replace('\\', '/');
            if (rel is "layout.json" or "manifest.json") continue;
            var info = new FileInfo(file);
            total += info.Length;
            entries.Add(new JsonObject
            {
                ["path"] = rel,
                ["size"] = info.Length,
                ["date"] = info.LastWriteTimeUtc.ToFileTimeUtc(),
            });
        }
        WriteText(Path.Combine(root, "layout.json"),
            new JsonObject { ["content"] = entries }.ToJsonString(Indented));
        return total;
    }

    private static void WriteManifest(string root, long totalSize)
    {
        // Hand-formatted so the creator line can be recognised when regenerating.
        string json =
            "{\n" +
            "  \"dependencies\": [],\n" +
            "  \"content_type\": \"MISC\",\n" +
            "  \"title\": \"UFO Encounter - Lichtobjekte\",\n" +
            "  \"manufacturer\": \"\",\n" +
            $"  \"creator\": \"{Creator}\",\n" +
            "  \"package_version\": \"1.0.0\",\n" +
            "  \"minimum_game_version\": \"1.0.0\",\n" +
            "  \"release_notes\": { \"neutral\": { \"LastUpdate\": \"\", \"OlderHistory\": \"\" } },\n" +
            $"  \"total_package_size\": \"{totalSize.ToString("D20", CultureInfo.InvariantCulture)}\"\n" +
            "}\n";
        WriteText(Path.Combine(root, "manifest.json"), json);
    }

    private static void WriteText(string path, string content) =>
        File.WriteAllText(path, content.Replace("\r\n", "\n"), new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));

    // JsonValue.Create(primitive) instead of the generic JsonArray.Add<T>: the latter
    // needs a TypeInfoResolver at serialization time.
    private static JsonArray Arr(params double[] values)
    {
        var a = new JsonArray();
        foreach (var v in values) a.Add(JsonValue.Create(v));
        return a;
    }

    private static JsonArray Arr(params string[] values)
    {
        var a = new JsonArray();
        foreach (var v in values) a.Add(JsonValue.Create(v));
        return a;
    }
}
