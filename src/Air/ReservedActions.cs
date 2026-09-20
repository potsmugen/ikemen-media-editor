namespace Air;

/// <summary>
/// The action numbers MUGEN's common states use, with their conventional
/// names. This is deliberately just the specific named poses — no generic
/// "this number is somewhere in the special-move band" guessing, since a guess
/// like that is exactly the kind of thing that shouldn't get written into
/// someone's file as if it were a real name.
/// </summary>
public static class ReservedActions
{
    public static readonly IReadOnlyDictionary<int, string> BuiltIn = new Dictionary<int, string>
    {
        [0] = "Standing",
        [5] = "Stand Turning",
        [6] = "Crouch Turning",
        [10] = "Stand To Crouch",
        [11] = "Crouching",
        [12] = "Crouch To Stand",
        [20] = "Walking Forward",
        [21] = "Walking Back",
        [40] = "Jump Start",
        [41] = "Jump Neutral",
        [42] = "Jump Forward",
        [43] = "Jump Back",
        [44] = "Jump Neutral (Falling)",
        [45] = "Jump Forward (Falling)",
        [46] = "Jump Back (Falling)",
        [47] = "Jump Landing",
        [100] = "Dash Forward",
        [105] = "Dash Back",
        [120] = "Guard Start (Stand)",
        [121] = "Guard Start (Crouch)",
        [122] = "Guard Start (Air)",
        [130] = "Guard (Stand)",
        [131] = "Guard (Crouch)",
        [132] = "Guard (Air)",
        [140] = "Guard End (Stand)",
        [141] = "Guard End (Crouch)",
        [142] = "Guard End (Air)",
        [150] = "Guard Shaking (Stand)",
        [151] = "Guard Shaking (Crouch)",
        [152] = "Guard Shaking (Air)",
        [170] = "Lose",
        [175] = "Draw Game",
        [180] = "Win",
        [190] = "Intro",
        [195] = "Taunt",
        [5000] = "Stand/Air Hit High (Light)",
        [5001] = "Stand/Air Hit High (Medium)",
        [5002] = "Stand/Air Hit High (Hard)",
        [5005] = "Stand Recover High (Light)",
        [5006] = "Stand Recover High (Medium)",
        [5007] = "Stand Recover High (Hard)",
        [5010] = "Stand/Air Hit Low (Light)",
        [5011] = "Stand/Air Hit Low (Medium)",
        [5012] = "Stand/Air Hit Low (Hard)",
        [5015] = "Stand Recover Low (Light)",
        [5016] = "Stand Recover Low (Medium)",
        [5017] = "Stand Recover Low (Hard)",
        [5020] = "Crouch Hit (Light)",
        [5021] = "Crouch Hit (Medium)",
        [5022] = "Crouch Hit (Hard)",
        [5025] = "Crouch Recover (Light)",
        [5026] = "Crouch Recover (Medium)",
        [5027] = "Crouch Recover (Hard)",
        [5030] = "Stand/Air Hit Back",
        [5035] = "Stand/Air Hit Transition",
        [5040] = "Air Recover",
        [5050] = "Air Fall",
        [5051] = "Air Fall (Up)",
        [5052] = "Air Fall (DiagUp)",
        [5060] = "Air Fall Coming Down",
        [5061] = "Air Fall Coming Down (Up)",
        [5062] = "Air Fall Coming Down (DiagUp)",
        [5070] = "Tripped",
        [5080] = "LieDown Hit Stay Down",
        [5081] = "LieDown Hit Stay Down (Up)",
        [5082] = "LieDown Hit Stay Down (DiagUp)",
        [5090] = "LieDown Hit Up Into Air",
        [5100] = "Hitting Ground From Fall",
        [5101] = "Bounce From Ground Into Air (Up)",
        [5102] = "Bounce From Ground Into Air (DiagUp)",
        [5110] = "LieDown",
        [5111] = "LieDown (Up)",
        [5112] = "LieDown (DiagUp)",
        [5120] = "Get Up From LieDown",
        [5121] = "Get Up From LieDown (Up)",
        [5122] = "Get Up From LieDown (DiagUp)",
        [5140] = "LieDead (First Rounds)",
        [5150] = "LieDead (Final Round)",
        [5151] = "LieDead (First Rounds) (Up)",
        [5152] = "LieDead (First Rounds) (DiagUp)",
        [5156] = "LieDead (Final Round)",
        [5157] = "LieDead (Final Round)",
        [5160] = "Bounce Into Air",
        [5161] = "Bounce Into Air (Up)",
        [5162] = "Bounce Into Air (DiagUp)",
        [5170] = "Hit Ground From Bounce",
        [5171] = "Hit Ground From Bounce (Up)",
        [5172] = "Hit Ground From Bounce (DiagUp)",
        [5200] = "Fall Recovery Near Ground",
        [5210] = "Fall Recovery In Mid-Air",
        [5300] = "Dizzy",
        [5500] = "\"Continue?\" Screen",
        [5510] = "\"Yes\" To \"Continue\"",
        [5520] = "\"No\" To \"Continue\"",
    };

    /// <summary>
    /// User-supplied names, loaded once at startup and checked before
    /// <see cref="BuiltIn"/>. Lets someone rename or add entries without
    /// touching the source; see the Settings menu's "Edit Action Names".
    /// </summary>
    public static Dictionary<int, string> Custom { get; set; } = new();

    /// <summary>A name for this action number, custom overrides winning, or null.</summary>
    public static string? Name(int number) =>
        Custom.TryGetValue(number, out var custom) ? custom
        : BuiltIn.TryGetValue(number, out var known) ? known
        : null;
}
