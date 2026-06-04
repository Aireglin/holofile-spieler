namespace UfoEncounter.Encounters;

/// <summary>
/// A named encounter preset loosely modeled on recurring pilot/UAP reports.
/// A scenario drives the hum, the electrics, and (optionally) a light pattern.
/// </summary>
public sealed record EncounterScenario(
    string Name,
    string Description,
    DisruptionKind Disruption,
    bool Hum,
    double HumIntensity,
    double HumBaseHz,
    LightPattern? Lights)
{
    /// <summary>Catalog of presets. Order is the order shown in the UI.</summary>
    public static readonly IReadOnlyList<EncounterScenario> Catalog = new[]
    {
        new EncounterScenario(
            "Tic-Tac (Nimitz)",
            "Lautlos, blitzschnelle Positionswechsel ohne Übergang. Kurzer, harter Elektronik-Glitch.",
            DisruptionKind.Stutter, Hum: false, HumIntensity: 0.0, HumBaseHz: 0,
            Lights: LightPattern.TicTac),

        new EncounterScenario(
            "Verfolger (Wingman)",
            "Hält Position hinter dem Flügel, spiegelt Manöver. Anhaltendes, tiefes Summen.",
            DisruptionKind.None, Hum: true, HumIntensity: 0.55, HumBaseHz: 65,
            Lights: LightPattern.Wingman),

        new EncounterScenario(
            "Fly-by",
            "Schneller Vorbeiflug von vorn. Kurzer Druck/Summen-Schwall, kein Ausfall.",
            DisruptionKind.None, Hum: true, HumIntensity: 0.7, HumBaseHz: 80,
            Lights: LightPattern.FlyBy),

        new EncounterScenario(
            "Pulk-Formation",
            "Mehrere Lichter im Formationstanz. Mittlerer Brumm, leichtes Flackern.",
            DisruptionKind.Stutter, Hum: true, HumIntensity: 0.5, HumBaseHz: 70,
            Lights: LightPattern.Formation),

        new EncounterScenario(
            "Pop-up & Verschwinden",
            "Taucht plötzlich vor dem Flugzeug auf, tanzt, verschwindet. Kurzer Hum.",
            DisruptionKind.None, Hum: true, HumIntensity: 0.6, HumBaseHz: 75,
            Lights: LightPattern.PopUp),

        new EncounterScenario(
            "RB-47 (E-Störung)",
            "Klassische Kopplung aus Lichtern und massiver Elektronikstörung: voller Blackout.",
            DisruptionKind.Blackout, Hum: true, HumIntensity: 0.8, HumBaseHz: 60,
            Lights: LightPattern.PopUp),

        new EncounterScenario(
            "Totaler Blackout",
            "Alles wird schwarz: Batterie + beide Generatoren + Avionik. Strom kommt zeitlich "
            + "entkoppelt vom Encounter zurück (eigene Zufallsdauer). Lichter beobachten aus dem Dunkeln.",
            DisruptionKind.DeepBlackout, Hum: true, HumIntensity: 0.7, HumBaseHz: 55,
            Lights: LightPattern.Wingman),
    };

    public static EncounterScenario ByName(string name) =>
        Catalog.FirstOrDefault(s => s.Name == name) ?? Catalog[0];
}
