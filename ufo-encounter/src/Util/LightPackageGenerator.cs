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
///  • "Licht" variants: no mesh at all, just a node carrying an ASOBO_street_light
///    (the MSFS 2024 light extension; keys as written by Asobo's exporter) —
///    a glowing point with lens flare that can also light up the aircraft.
///  • "Kugel" variants: a small emissive sphere plus the same light, for orbs that
///    stay visible in daylight.
///  • One diagnostic variant with the older ASOBO_macro_light (MSFS 2020 format).
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

    public enum Style { Steady, Pulse, Strobe }

    public sealed record Variant(string Title, string Model, double[] Color, Style Style, bool Orb, bool MacroLight);

    public sealed record Result(string PackagePath, IReadOnlyList<string> LightTitles,
        IReadOnlyList<string> OrbTitles, IReadOnlyList<string> DiagnosticTitles);

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

    public static IReadOnlyList<Variant> Variants { get; } = BuildVariants();

    private static List<Variant> BuildVariants()
    {
        var list = new List<Variant>();
        foreach (var (name, rgb) in Colors)
        {
            list.Add(new($"{TitlePrefix}Licht_{name}_Ruhig", $"Licht_{name}_Ruhig", rgb, Style.Steady, false, false));
            list.Add(new($"{TitlePrefix}Licht_{name}_Puls", $"Licht_{name}_Puls", rgb, Style.Pulse, false, false));
            list.Add(new($"{TitlePrefix}Licht_{name}_Blitz", $"Licht_{name}_Blitz", rgb, Style.Strobe, false, false));
        }
        foreach (var (name, rgb) in Colors.Take(4))
            list.Add(new($"{TitlePrefix}Kugel_{name}", $"Kugel_{name}", rgb, Style.Steady, true, false));
        list.Add(new($"{TitlePrefix}Test_Macro_Weiss", "Test_Macro_Weiss", Colors[0].Rgb, Style.Steady, false, true));
        return list;
    }

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
                throw new IOException($"Der Ordner „{root}“ existiert bereits und stammt nicht von diesem Programm.");
            Directory.Delete(root, recursive: true);
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
                byte[] bin = BuildSphereBin(0.6, out int vertexCount, out int indexCount, out double radius);
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
            Variants.Where(v => !v.Orb && !v.MacroLight).Select(v => v.Title).ToList(),
            Variants.Where(v => v.Orb).Select(v => v.Title).ToList(),
            Variants.Where(v => v.MacroLight).Select(v => v.Title).ToList());
    }

    // ---------------- glTF writers ----------------

    private static JsonObject LightExtension(Variant v)
    {
        var (freq, dur) = v.Style switch
        {
            Style.Pulse => (48.0, 0.55),   // flashes per MINUTE, seconds
            Style.Strobe => (60.0, 0.08),
            _ => (0.0, 0.2),
        };
        if (v.MacroLight)
        {
            return new JsonObject
            {
                ["ASOBO_macro_light"] = new JsonObject
                {
                    ["color"] = Arr(v.Color),
                    ["intensity"] = 30.0,
                    ["cone_angle"] = 360.0,
                    ["has_symmetry"] = false,
                    ["flash_frequency"] = freq,
                    ["flash_duration"] = dur,
                    ["flash_phase"] = 0.0,
                    ["rotation_speed"] = 0.0,
                    ["day_night_cycle"] = false,
                },
            };
        }
        return new JsonObject
        {
            ["ASOBO_street_light"] = new JsonObject
            {
                ["color"] = Arr(v.Color),
                ["intensity"] = 20000.0,          // candela at night
                ["daytime_intensity"] = 60000.0,  // used because day_night_cycle = false
                ["day_night_cycle"] = false,
                ["cone_angle"] = 360.0,
                ["has_symmetry"] = false,
                ["flash_frequency"] = freq,
                ["flash_duration"] = dur,
                ["flash_phase"] = 0.0,
                ["rotation_phase"] = 0.0,
                ["rotation_speed"] = 0.0,
                ["random_phase"] = true,
                ["flare_enabled"] = true,
                ["flare_only"] = false,
            },
        };
    }

    private static JsonObject LightNode(Variant v)
    {
        var ext = LightExtension(v);
        ext["ASOBO_unique_id"] = new JsonObject { ["id"] = "UfoLight" };
        return new JsonObject { ["name"] = "UfoLight", ["extensions"] = ext };
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
        var doc = new JsonObject
        {
            ["asset"] = Asset(optimized: true),
            ["extensionsUsed"] = Arr("ASOBO_normal_map_convention", "ASOBO_unique_id", "ASOBO_asset_optimized", lightExt),
            ["nodes"] = new JsonArray
            {
                new JsonObject
                {
                    ["name"] = "Root",
                    ["children"] = new JsonArray(1),
                    ["extensions"] = new JsonObject { ["ASOBO_unique_id"] = new JsonObject { ["id"] = "Root" } },
                },
                LightNode(v),
            },
            ["scene"] = 0,
            ["scenes"] = new JsonArray { new JsonObject { ["name"] = "Scene", ["nodes"] = new JsonArray(0) } },
        };
        return doc.ToJsonString(Indented);
    }

    private static string OrbGltf(Variant v, int binLength, int vertexCount, int indexCount, double r)
    {
        int posBytes = vertexCount * 12;
        int idxBytes = indexCount * 2;
        var doc = new JsonObject
        {
            ["asset"] = Asset(optimized: false),
            ["extensionsUsed"] = Arr("ASOBO_normal_map_convention", "ASOBO_unique_id", "ASOBO_street_light", "KHR_materials_emissive_strength"),
            ["nodes"] = new JsonArray
            {
                new JsonObject
                {
                    ["name"] = "Root",
                    ["children"] = new JsonArray(1, 2),
                    ["extensions"] = new JsonObject { ["ASOBO_unique_id"] = new JsonObject { ["id"] = "Root" } },
                },
                new JsonObject
                {
                    ["name"] = "Orb",
                    ["mesh"] = 0,
                    ["extensions"] = new JsonObject { ["ASOBO_unique_id"] = new JsonObject { ["id"] = "Orb" } },
                },
                LightNode(v),
            },
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
                        ["KHR_materials_emissive_strength"] = new JsonObject { ["emissiveStrength"] = 40.0 },
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
