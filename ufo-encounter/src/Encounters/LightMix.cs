using UfoEncounter.Util;
using Kind = UfoEncounter.Util.LightPackageGenerator.LightKind;

namespace UfoEncounter.Encounters;

/// <summary>
/// Chooses the title of every object in a session ("Lichtstile automatisch mischen").
/// Package titles in the user's list (UFOE_…) only name COLOURS; each object's style
/// follows from its role:
///  • index 0, the leader: steady (Ruhig; Kugel by day; Kern when small lights go with it),
///  • further leaders: a share of them blink, mostly softly (Glimmen/Funkeln never go
///    dark), the others are steady,
///  • satellites: small sparks (Funke), never orbs, cores or aircraft.
/// About 70 % of the objects share one main colour per encounter. Other entries
/// (aircraft, "obj:" and unknown titles) are picked as listed, in proportion to their
/// count. A package title is used only if it is installed or literally listed, so an
/// older package never gets titles it lacks. Diagnostic titles are never mixed in.
/// Pure; deterministic for a given <see cref="Random"/>.
/// </summary>
public static class LightMix
{
    private const double MainColorShare = 0.7;
    private const double SoftShare = 2.0 / 3;   // blinkers: soft (never dark) vs hard on/off

    /// <summary>A package style (kind + style name) with its relative weight in a tier.</summary>
    private readonly record struct Style(Kind Kind, string Name, double Weight = 1);

    /// <summary>Tiers of interchangeable styles, best first. Within a role the colour
    /// beats the tier; across roles the role beats the colour.</summary>
    private sealed class Role
    {
        public Role(params Style[][] tiers) => Tiers = tiers;
        public Style[][] Tiers { get; }
    }

    private static readonly Style Ruhig = new(Kind.Steady, "Ruhig");
    private static readonly Style Kugel = new(Kind.Orb, "Kugel");
    private static readonly Style Kern = new(Kind.Core, "Kern");
    private static readonly Style[] Soft = { new(Kind.Soft, "Glimmen"), new(Kind.Soft, "Funkeln") };
    private static readonly Style[] Hard = { new(Kind.Hard, "Puls"), new(Kind.Hard, "Blitz") };

    // (kind, style, colour) → title, from the generator's metadata.
    private static readonly Dictionary<(Kind, string, string), string> Catalog = BuildCatalog();

    private static Dictionary<(Kind, string, string), string> BuildCatalog()
    {
        var d = new Dictionary<(Kind, string, string), string>();
        foreach (var v in LightPackageGenerator.Variants)
            if (v.Kind != Kind.Diagnostic && v.ColorName.Length > 0)
                d.TryAdd((v.Kind, v.StyleName, v.ColorName), v.Title);
        return d;
    }

    /// <summary>Split the comma list: trimmed, without empty and bare "obj:" entries.</summary>
    public static IReadOnlyList<string> Entries(string? csv) =>
        (csv ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(t => !t.Equals(LightChoreographer.ObjectPrefix, StringComparison.OrdinalIgnoreCase))
            .ToArray();

    /// <summary>One title per object, drawn uniformly from the list exactly as written
    /// (the plain behaviour, "automatisch mischen" off). Empty if the list is empty.</summary>
    public static string[] PickUniform(IReadOnlyList<string> entries, int count, Random rng)
    {
        var list = Clean(entries);
        var picked = new string[list.Count == 0 ? 0 : Math.Max(1, count)];
        for (int i = 0; i < picked.Length; i++) picked[i] = list[rng.Next(list.Count)];
        return picked;
    }

    /// <summary>True if <see cref="Compose"/> can fill satellites: the list names a
    /// package colour that has a usable spark or soft (never dark) light title.</summary>
    public static bool CanOrbit(IReadOnlyList<string> entries, IReadOnlySet<string>? installed)
    {
        var pool = new Pool(entries, installed);
        return pool.Colors.Any(c => SatelliteRoles().Any(r => r.Tiers.Any(t => t.Any(s => pool.Usable(s, c) != null))));
    }

    /// <summary>Titles for one session: <paramref name="leaders"/> entries (index 0 is
    /// the main light), then <paramref name="satellites"/> spark titles.
    /// Without package titles in the list every object is picked uniformly from the
    /// list as before and no satellites are returned; check <see cref="CanOrbit"/>
    /// before asking for satellites. A satellite that cannot be filled is left out,
    /// so the satellites returned are always Length − leaders.</summary>
    public static string[] Compose(IReadOnlyList<string> entries, int leaders, int satellites, double blinkShare,
        bool isDay, bool smallFollowers, IReadOnlySet<string>? installed, Random rng)
    {
        var pool = new Pool(entries, installed);
        leaders = Math.Max(1, leaders);
        if (pool.Known.Count == 0)
            return PickUniform(pool.Other.Count > 0 ? pool.Other : pool.Diagnostic, leaders, rng);

        satellites = Math.Max(0, satellites);
        double share = double.IsFinite(blinkShare) ? Math.Clamp(blinkShare, 0, 1) : 0;
        string main = pool.Colors[rng.Next(pool.Colors.Count)];
        var blinking = PickBlinkers(leaders, share, rng);
        double pOther = (double)pool.Other.Count / (pool.Other.Count + pool.Known.Count);

        var result = new List<string>(leaders + satellites);
        for (int i = 0; i < leaders; i++)
        {
            // Satellites orbit the leader, so then it must be a package light.
            if (pool.Other.Count > 0 && !(i == 0 && satellites > 0) && rng.NextDouble() < pOther)
            {
                result.Add(pool.Other[rng.Next(pool.Other.Count)]);
                continue;
            }
            var roles = i == 0 ? LeaderRoles(isDay, big: satellites > 0 || smallFollowers)
                : blinking[i] ? BlinkerRoles(rng.NextDouble() < SoftShare, smallFollowers)
                : SteadyRoles(smallFollowers);
            string colour = i == 0 ? main : ObjectColor(pool, main, rng);
            result.Add(Pick(pool, ColorOrder(pool, colour, rng), roles, rng)
                       ?? pool.Known[rng.Next(pool.Known.Count)]);
        }
        for (int k = 0; k < satellites; k++)
        {
            string colour = ObjectColor(pool, main, rng);
            if (Pick(pool, ColorOrder(pool, colour, rng), SatelliteRoles(), rng) is string t) result.Add(t);
        }
        return result.ToArray();
    }

    // ---- Roles ----

    /// <summary>Steady main light. "Big" (satellites or a ring of sparks around it)
    /// prefers the bright Kern, daylight the Kugel, night the plain light.</summary>
    private static Role[] LeaderRoles(bool isDay, bool big) => new[]
    {
        big ? new Role(new[] { Kern }, new[] { Kugel }, new[] { Ruhig })
            : isDay ? new Role(new[] { Kugel }, new[] { Kern }, new[] { Ruhig })
            : new Role(new[] { Ruhig }, new[] { Kugel }, new[] { Kern }),
        new Role(Soft, Hard),
    };

    private static Role[] SteadyRoles(bool small)
    {
        var licht = new[] { new Role(new[] { Ruhig }), new Role(new[] { Kugel }), new Role(Soft), new Role(Hard) };
        return small ? licht.Prepend(new Role(new[] { Spark("Glimmen") })).ToArray() : licht;
    }

    private static Role[] BlinkerRoles(bool soft, bool small)
    {
        var licht = new[]
        {
            new Role(soft ? Soft : Hard, soft ? Hard : Soft),
            new Role(new[] { Ruhig }),
            new Role(new[] { Kugel }),
        };
        if (!small) return licht;
        var sparks = new Role(new[] { Spark(soft ? "Funkeln" : "Blitz") }, new[] { Spark(soft ? "Blitz" : "Funkeln") });
        return licht.Prepend(sparks).ToArray();
    }

    /// <summary>Small sparks first (Funkeln 40 %, Glimmen 30 %, Blitz 30 %), then the
    /// soft mesh-less lights; never hard blinkers (full-size lights going dark),
    /// orbs, cores or aircraft. An older package has neither, so no satellites.</summary>
    private static Role[] SatelliteRoles() => new[]
    {
        new Role(new[] { Spark("Funkeln", 0.4), Spark("Glimmen", 0.3), Spark("Blitz", 0.3) }),
        new Role(Soft),
    };

    private static Style Spark(string name, double weight = 1) => new(Kind.Spark, name, weight);

    // ---- Picking ----

    /// <summary>First usable title: roles in order; within a role the colours in
    /// order, within a colour the tiers; within a tier a weighted draw.</summary>
    private static string? Pick(Pool pool, IReadOnlyList<string> colours, Role[] roles, Random rng)
    {
        var usable = new List<(string Title, double Weight)>();
        foreach (var role in roles)
            foreach (var colour in colours)
                foreach (var tier in role.Tiers)
                {
                    usable.Clear();
                    foreach (var s in tier)
                        if (pool.Usable(s, colour) is string t) usable.Add((t, s.Weight));
                    if (usable.Count == 0) continue;
                    double r = rng.NextDouble() * usable.Sum(u => u.Weight);
                    foreach (var (title, weight) in usable)
                        if ((r -= weight) < 0) return title;
                    return usable[^1].Title;
                }
        return null;
    }

    /// <summary>Indices 1..leaders-1 that blink: floor((leaders-1)·share + u) of them, at random.</summary>
    private static bool[] PickBlinkers(int leaders, double share, Random rng)
    {
        var blinking = new bool[leaders];
        int count = Math.Clamp((int)Math.Floor((leaders - 1) * share + rng.NextDouble()), 0, leaders - 1);
        var idx = Enumerable.Range(1, leaders - 1).ToArray();
        Shuffle(idx, rng);
        for (int k = 0; k < count; k++) blinking[idx[k]] = true;
        return blinking;
    }

    private static string ObjectColor(Pool pool, string main, Random rng) =>
        rng.NextDouble() < MainColorShare ? main : pool.Colors[rng.Next(pool.Colors.Count)];

    /// <summary>The object's colour first, then the rest of the pool in random order.</summary>
    private static List<string> ColorOrder(Pool pool, string first, Random rng)
    {
        var rest = pool.Colors.Where(c => c != first).ToArray();
        Shuffle(rest, rng);
        var order = new List<string>(rest.Length + 1) { first };
        order.AddRange(rest);
        return order;
    }

    private static void Shuffle<T>(T[] a, Random rng)
    {
        for (int i = a.Length - 1; i > 0; i--)
        {
            int j = rng.Next(i + 1);
            (a[i], a[j]) = (a[j], a[i]);
        }
    }

    private static List<string> Clean(IReadOnlyList<string>? entries) =>
        (entries ?? Array.Empty<string>())
            .Select(t => (t ?? "").Trim())
            .Where(t => t.Length > 0 && !t.Equals(LightChoreographer.ObjectPrefix, StringComparison.OrdinalIgnoreCase))
            .ToList();

    /// <summary>The user's list, classified, plus which package titles may be used.</summary>
    private sealed class Pool
    {
        public readonly List<string> Colors = new();      // distinct, in list order
        public readonly List<string> Known = new();       // listed package titles (canonical spelling)
        public readonly List<string> Other = new();       // aircraft, "obj:" and unknown titles
        public readonly List<string> Diagnostic = new();  // listed test titles: never mixed in
        private readonly HashSet<string> _listed = new(StringComparer.OrdinalIgnoreCase);
        private readonly HashSet<string>? _installed;

        public Pool(IReadOnlyList<string>? entries, IReadOnlySet<string>? installed)
        {
            if (installed != null) _installed = new HashSet<string>(installed, StringComparer.OrdinalIgnoreCase);
            foreach (var e in Clean(entries))
            {
                _listed.Add(e);
                var v = e.StartsWith(LightChoreographer.ObjectPrefix, StringComparison.OrdinalIgnoreCase)
                    ? null : LightPackageGenerator.Find(e);
                if (v == null) Other.Add(e);
                else if (v.Kind == Kind.Diagnostic || v.ColorName.Length == 0) Diagnostic.Add(e);
                else
                {
                    Known.Add(v.Title);
                    if (!Colors.Contains(v.ColorName)) Colors.Add(v.ColorName);
                }
            }
        }

        /// <summary>The package title for style + colour, if it is installed or listed.</summary>
        public string? Usable(Style s, string colour) =>
            Catalog.TryGetValue((s.Kind, s.Name, colour), out var t)
            && (_listed.Contains(t) || _installed?.Contains(t) == true) ? t : null;
    }
}
