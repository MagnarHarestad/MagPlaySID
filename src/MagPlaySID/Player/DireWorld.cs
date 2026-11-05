using System.Numerics;
using Avalonia;
using SkiaSharp;

namespace MagPlaySid.Player;

/// <summary>
/// The Dire Guys' world: the two guys dancing on top of a great mirror ball in a dark club, the
/// ball turning beneath their feet and flashing back the spotlights, the club's walls running with
/// its dots of light. Every beat of the tune - a drum, the bass - dips them both in a groove; the
/// melody's notes are the red guy's moves - pointing to the sky and down, waving, kicking, spinning on
/// a leap - and the drums and bass the blue guy's: claps, fist pumps, steps, twists. The spotlights
/// take the colour of the voice that last played. All of it moves by the tune's own time.
/// </summary>
internal sealed class DireWorld
{
    public enum Beat { Kick, Snare, Bass, Middle, Melody, Leap, Fall }

    // ------------------------------------------------------------------ the dance

    /// <summary>A pose's angles and offsets: how high he jumps and how low he dips, sways and turns;
    /// his spine's lean, tilt and twist; his head's nod, tilt and turn; each arm raised forward, out
    /// to the side and bent at the elbow; each leg lifted forward, out to the side and bent at the knee.</summary>
    private enum J { Jump, Dip, Sway, Turn, Lean, Tilt, Twist, Nod, HeadTilt, HeadTurn, RaiseL, OutL, ElbowL, RaiseR, OutR, ElbowR, LiftL, SpreadL, KneeL, LiftR, SpreadR, KneeR }

    private const int Angles = 22;

    private static float[] P(params (J Angle, float Value)[] values)
    {
        var pose = new float[Angles];
        foreach (var (angle, value) in values)
            pose[(int)angle] = value;
        return pose;
    }

    /// <summary>A move: its poses at their seconds - each over the groove - the seconds back to
    /// the groove after the last, and the seconds before another may cut it short.</summary>
    private sealed record Move((float At, float[] Pose)[] Keys, float Out, float Committed)
    {
        public float Length => Keys[^1].At + Out;
    }

    private static readonly float[] Stance = P((J.RaiseL, 0.1f), (J.RaiseR, 0.1f), (J.OutL, 0.08f), (J.OutR, 0.08f), (J.ElbowL, 0.35f), (J.ElbowR, 0.35f));

    /// <summary>The disco point: the right arm flung up to the sky, the left hand on the hip, then
    /// held a moment.</summary>
    private static readonly Move Point = new(
        [(0.1f, P((J.RaiseR, 2.6f), (J.OutR, 0.55f), (J.ElbowR, -0.35f), (J.Tilt, -0.14f), (J.Nod, -0.3f), (J.HeadTurn, -0.25f), (J.OutL, 0.5f), (J.ElbowL, 1.2f), (J.Sway, 0.05f))),
         (0.32f, P((J.RaiseR, 2.6f), (J.OutR, 0.55f), (J.ElbowR, -0.35f), (J.Tilt, -0.14f), (J.Nod, -0.3f), (J.HeadTurn, -0.25f), (J.OutL, 0.5f), (J.ElbowL, 1.2f), (J.Sway, 0.05f)))],
        0.25f, 0.2f);

    /// <summary>The point down: the right arm across the body to the left hip.</summary>
    private static readonly Move PointDown = new(
        [(0.1f, P((J.RaiseR, 0.6f), (J.OutR, -0.45f), (J.ElbowR, -0.3f), (J.Lean, 0.15f), (J.Tilt, 0.12f), (J.Nod, 0.25f), (J.HeadTurn, 0.3f), (J.OutL, 0.5f), (J.ElbowL, 1.2f), (J.Sway, -0.05f))),
         (0.3f, P((J.RaiseR, 0.6f), (J.OutR, -0.45f), (J.ElbowR, -0.3f), (J.Lean, 0.15f), (J.Tilt, 0.12f), (J.Nod, 0.25f), (J.HeadTurn, 0.3f), (J.OutL, 0.5f), (J.ElbowL, 1.2f), (J.Sway, -0.05f)))],
        0.25f, 0.2f);

    /// <summary>Fists pumped up twice.</summary>
    private static readonly Move Pump = new(
        [(0.07f, P((J.RaiseL, 1.2f), (J.RaiseR, 1.2f), (J.ElbowL, 1.5f), (J.ElbowR, 1.5f), (J.OutL, 0.3f), (J.OutR, 0.3f), (J.Nod, 0.15f))),
         (0.15f, P((J.RaiseL, 2.5f), (J.RaiseR, 2.5f), (J.ElbowL, 0.3f), (J.ElbowR, 0.3f), (J.OutL, 0.35f), (J.OutR, 0.35f), (J.Nod, -0.2f))),
         (0.24f, P((J.RaiseL, 1.2f), (J.RaiseR, 1.2f), (J.ElbowL, 1.5f), (J.ElbowR, 1.5f), (J.OutL, 0.3f), (J.OutR, 0.3f), (J.Nod, 0.15f))),
         (0.32f, P((J.RaiseL, 2.5f), (J.RaiseR, 2.5f), (J.ElbowL, 0.3f), (J.ElbowR, 0.3f), (J.OutL, 0.35f), (J.OutR, 0.35f), (J.Nod, -0.2f)))],
        0.25f, 0.2f);

    /// <summary>A clap high over the head.</summary>
    private static readonly Move Clap = new(
        [(0.07f, P((J.RaiseL, 2.5f), (J.RaiseR, 2.5f), (J.OutL, 0.5f), (J.OutR, 0.5f), (J.ElbowL, 0.4f), (J.ElbowR, 0.4f), (J.Nod, -0.2f))),
         (0.13f, P((J.RaiseL, 2.75f), (J.RaiseR, 2.75f), (J.OutL, -0.1f), (J.OutR, -0.1f), (J.ElbowL, 0.25f), (J.ElbowR, 0.25f), (J.Nod, -0.25f)))],
        0.22f, 0.12f);

    /// <summary>A wave of the right hand, the arm out to the side.</summary>
    private static readonly Move Wave = new(
        [(0.08f, P((J.OutR, 1.45f), (J.RaiseR, 0.3f), (J.ElbowR, 1.3f), (J.Tilt, 0.08f), (J.HeadTurn, -0.3f))),
         (0.18f, P((J.OutR, 1.45f), (J.RaiseR, 0.3f), (J.ElbowR, 0.4f), (J.Tilt, 0.08f), (J.HeadTurn, -0.3f))),
         (0.28f, P((J.OutR, 1.45f), (J.RaiseR, 0.3f), (J.ElbowR, 1.3f), (J.Tilt, 0.08f), (J.HeadTurn, -0.3f))),
         (0.38f, P((J.OutR, 1.45f), (J.RaiseR, 0.3f), (J.ElbowR, 0.4f), (J.Tilt, 0.08f), (J.HeadTurn, -0.3f)))],
        0.25f, 0.2f);

    /// <summary>A kick out in front, arms out for balance.</summary>
    private static readonly Move Kick = new(
        [(0.08f, P((J.LiftR, 0.9f), (J.KneeR, 1.3f), (J.Lean, -0.1f), (J.OutL, 0.6f), (J.OutR, 0.6f))),
         (0.15f, P((J.LiftR, 1.25f), (J.KneeR, 0.05f), (J.Lean, -0.2f), (J.OutL, 0.7f), (J.OutR, 0.7f), (J.Nod, 0.1f))),
         (0.24f, P((J.LiftR, 1.2f), (J.KneeR, 0.1f), (J.Lean, -0.2f), (J.OutL, 0.7f), (J.OutR, 0.7f), (J.Nod, 0.1f)))],
        0.22f, 0.22f);

    /// <summary>A spin on the spot, arms out.</summary>
    private static readonly Move Spin = new(
        [(0.08f, P((J.OutL, 1.1f), (J.OutR, 1.1f), (J.Turn, 0.6f), (J.Jump, 0.05f))),
         (0.42f, P((J.OutL, 1.2f), (J.OutR, 1.2f), (J.Turn, MathF.Tau), (J.Jump, 0.05f)))],
        0.2f, 0.4f);

    /// <summary>A twist, the hips and knees one way and the shoulders the other.</summary>
    private static readonly Move Twist = new(
        [(0.1f, P((J.Twist, 0.45f), (J.Turn, -0.25f), (J.KneeL, 0.3f), (J.RaiseL, 0.9f), (J.RaiseR, 0.9f), (J.ElbowL, 1.2f), (J.ElbowR, 1.2f), (J.Dip, 0.04f))),
         (0.25f, P((J.Twist, -0.45f), (J.Turn, 0.25f), (J.KneeR, 0.3f), (J.RaiseL, 0.9f), (J.RaiseR, 0.9f), (J.ElbowL, 1.2f), (J.ElbowR, 1.2f), (J.Dip, 0.04f))),
         (0.4f, P((J.Twist, 0.4f), (J.Turn, -0.2f), (J.KneeL, 0.3f), (J.RaiseL, 0.9f), (J.RaiseR, 0.9f), (J.ElbowL, 1.2f), (J.ElbowR, 1.2f), (J.Dip, 0.04f)))],
        0.25f, 0.25f);

    /// <summary>A step to the side and back, the arms swinging.</summary>
    private static readonly Move Step = new(
        [(0.1f, P((J.Sway, 0.16f), (J.SpreadL, 0.25f), (J.Tilt, -0.08f), (J.RaiseL, 0.6f), (J.RaiseR, -0.4f), (J.ElbowL, 0.9f))),
         (0.26f, P((J.Sway, -0.12f), (J.SpreadR, 0.25f), (J.Tilt, 0.08f), (J.RaiseR, 0.6f), (J.RaiseL, -0.4f), (J.ElbowR, 0.9f)))],
        0.25f, 0.2f);

    /// <summary>The head banged, twice, the shoulders with it.</summary>
    private static readonly Move HeadBang = new(
        [(0.06f, P((J.Nod, 0.55f), (J.Lean, 0.2f), (J.ElbowL, 0.8f), (J.ElbowR, 0.8f), (J.RaiseL, 0.4f), (J.RaiseR, 0.4f))),
         (0.14f, P((J.Nod, -0.15f), (J.Lean, 0.02f))),
         (0.2f, P((J.Nod, 0.55f), (J.Lean, 0.2f), (J.ElbowL, 0.8f), (J.ElbowR, 0.8f), (J.RaiseL, 0.4f), (J.RaiseR, 0.4f)))],
        0.22f, 0.15f);

    /// <summary>The hustle: hips to one side, a hand rolling in front.</summary>
    private static readonly Move Hustle = new(
        [(0.1f, P((J.Tilt, 0.15f), (J.Sway, 0.08f), (J.RaiseL, 0.75f), (J.ElbowL, 0.9f), (J.OutL, -0.3f), (J.RaiseR, 0.45f), (J.ElbowR, 1.1f), (J.OutR, -0.3f), (J.HeadTilt, -0.15f))),
         (0.25f, P((J.Tilt, -0.15f), (J.Sway, -0.08f), (J.RaiseL, 0.45f), (J.ElbowL, 1.1f), (J.OutL, -0.3f), (J.RaiseR, 0.75f), (J.ElbowR, 0.9f), (J.OutR, -0.3f), (J.HeadTilt, 0.15f)))],
        0.25f, 0.2f);

    /// <summary>A jump, knees tucked, arms flung up.</summary>
    private static readonly Move Jump = new(
        [(0.08f, P((J.Dip, 0.08f), (J.RaiseL, -0.3f), (J.RaiseR, -0.3f))),
         (0.22f, P((J.Jump, 0.32f), (J.LiftL, 0.5f), (J.LiftR, 0.5f), (J.KneeL, 0.9f), (J.KneeR, 0.9f), (J.RaiseL, 2.5f), (J.RaiseR, 2.5f), (J.OutL, 0.5f), (J.OutR, 0.5f), (J.Nod, -0.2f)))],
        0.22f, 0.35f);


    // The newer moves - the club's and the internet's: the floss, the dab, the running man, the
    // robot, the sprinkler, the cabbage patch, the moonwalk, the Carlton, the griddy, the shuffle,
    // the horse ride, the lawnmower and the hip bump.

    /// <summary>The floss: straight arms swung from behind on one side to in front on the other,
    /// the hips swinging the other way, twice over.</summary>
    private static readonly float[] FlossA = P((J.OutR, 0.4f), (J.RaiseR, -0.4f), (J.ElbowR, -0.3f), (J.OutL, -0.4f), (J.RaiseL, 0.4f), (J.ElbowL, -0.3f), (J.Sway, -0.06f), (J.Tilt, 0.1f));
    private static readonly float[] FlossB = P((J.OutR, -0.4f), (J.RaiseR, 0.4f), (J.ElbowR, -0.3f), (J.OutL, 0.4f), (J.RaiseL, -0.4f), (J.ElbowL, -0.3f), (J.Sway, 0.06f), (J.Tilt, -0.1f));

    private static readonly Move Floss = new([(0.08f, FlossA), (0.18f, FlossB), (0.28f, FlossA), (0.38f, FlossB)], 0.2f, 0.3f);

    /// <summary>The dab: the head dropped into the crook of one elbow, the other arm flung up and out.</summary>
    private static readonly float[] DabPose = P((J.RaiseR, 2.2f), (J.OutR, 0.75f), (J.ElbowR, -0.35f), (J.RaiseL, 1.5f), (J.OutL, -0.55f), (J.ElbowL, 2.2f), (J.Nod, 0.5f), (J.HeadTurn, 0.45f), (J.Lean, 0.2f), (J.Tilt, -0.1f));

    private static readonly Move Dab = new([(0.09f, DabPose), (0.42f, DabPose)], 0.25f, 0.3f);

    /// <summary>The running man: a knee up and the foot slid back, the other, on the spot, the arms
    /// pumping with them.</summary>
    private static readonly float[] RunA = P((J.LiftL, 0.8f), (J.KneeL, 1.3f), (J.RaiseR, 0.7f), (J.ElbowR, 1.2f), (J.RaiseL, -0.5f), (J.ElbowL, 1.2f), (J.Dip, 0.03f), (J.Lean, 0.08f));
    private static readonly float[] RunB = P((J.LiftR, 0.8f), (J.KneeR, 1.3f), (J.RaiseL, 0.7f), (J.ElbowL, 1.2f), (J.RaiseR, -0.5f), (J.ElbowR, 1.2f), (J.Dip, 0.03f), (J.Lean, 0.08f));

    private static readonly Move RunningMan = new([(0.1f, RunA), (0.22f, RunB), (0.34f, RunA), (0.46f, RunB)], 0.22f, 0.35f);

    /// <summary>The robot: arms bent square, the body and head turning in stiff jerks, held between.</summary>
    private static readonly float[] RobotA = P((J.RaiseL, 0.25f), (J.RaiseR, 0.25f), (J.ElbowL, 1.25f), (J.ElbowR, 1.25f), (J.OutL, 0.15f), (J.OutR, 0.15f), (J.Turn, 0.35f), (J.HeadTurn, -0.4f));
    private static readonly float[] RobotB = P((J.RaiseL, 1.3f), (J.RaiseR, 0.25f), (J.ElbowL, 0.25f), (J.ElbowR, 1.25f), (J.OutL, 0.15f), (J.OutR, 0.15f), (J.Turn, 0f), (J.HeadTurn, 0.3f));
    private static readonly float[] RobotC = P((J.RaiseL, 0.25f), (J.RaiseR, 1.3f), (J.ElbowL, 1.25f), (J.ElbowR, 0.25f), (J.OutL, 0.15f), (J.OutR, 0.15f), (J.Turn, -0.35f), (J.HeadTurn, 0.4f));

    private static readonly Move Robot = new([(0.05f, RobotA), (0.16f, RobotA), (0.2f, RobotB), (0.31f, RobotB), (0.35f, RobotC), (0.48f, RobotC)], 0.15f, 0.4f);

    /// <summary>The sprinkler: a hand behind the head, the other arm out in front, ticking round
    /// in steps and sweeping back.</summary>
    private static float[] Sprinkle(float turn, float dip) => P((J.RaiseL, 2.6f), (J.OutL, 1f), (J.ElbowL, 2.2f), (J.RaiseR, 1.55f), (J.OutR, 0.2f), (J.ElbowR, -0.3f), (J.Turn, turn), (J.Dip, dip), (J.HeadTurn, turn * 0.5f));

    private static readonly Move Sprinkler = new(
        [(0.06f, Sprinkle(-0.45f, 0.03f)), (0.14f, Sprinkle(-0.45f, 0)), (0.18f, Sprinkle(-0.15f, 0.03f)), (0.26f, Sprinkle(-0.15f, 0)),
         (0.3f, Sprinkle(0.15f, 0.03f)), (0.38f, Sprinkle(0.15f, 0)), (0.42f, Sprinkle(0.45f, 0.03f)), (0.62f, Sprinkle(-0.45f, 0))],
        0.2f, 0.45f);

    /// <summary>The cabbage patch: fists together in front, stirring round in a circle with the hips.</summary>
    private static float[] Stir(float sway, float raise, float twist) => P((J.RaiseL, 0.5f + raise), (J.RaiseR, 0.5f + raise), (J.OutL, -0.45f), (J.OutR, -0.45f), (J.ElbowL, 1.1f), (J.ElbowR, 1.1f), (J.Sway, sway), (J.Twist, twist), (J.Nod, 0.1f));

    private static readonly Move CabbagePatch = new(
        [(0.12f, Stir(0.06f, 0.25f, 0.2f)), (0.24f, Stir(0, -0.2f, 0)), (0.36f, Stir(-0.06f, 0.25f, -0.2f)), (0.48f, Stir(0, -0.2f, 0))],
        0.22f, 0.35f);

    /// <summary>The moonwalk: turned side on, gliding backwards a heel at a time.</summary>
    private static readonly Move Moonwalk = new(
        [(0.15f, P((J.Turn, 1.3f), (J.KneeL, 0.7f), (J.LiftL, 0.2f), (J.ElbowL, 0.9f), (J.ElbowR, 0.9f), (J.Nod, 0.1f))),
         (0.4f, P((J.Turn, 1.3f), (J.Sway, -0.12f), (J.KneeR, 0.7f), (J.LiftR, 0.2f), (J.ElbowL, 0.9f), (J.ElbowR, 0.9f), (J.Nod, 0.1f))),
         (0.65f, P((J.Turn, 1.3f), (J.Sway, -0.24f), (J.KneeL, 0.7f), (J.LiftL, 0.2f), (J.ElbowL, 0.9f), (J.ElbowR, 0.9f), (J.Nod, 0.1f))),
         (0.85f, P((J.Turn, 1.3f), (J.Sway, -0.32f), (J.KneeR, 0.5f), (J.LiftR, 0.1f), (J.ElbowL, 0.9f), (J.ElbowR, 0.9f), (J.Nod, 0.1f)))],
        0.45f, 0.8f);

    /// <summary>The Carlton: arms swung together side to side, bent, the hips and head swaying with them.</summary>
    private static readonly float[] CarltonA = P((J.RaiseL, 0.6f), (J.OutL, -0.5f), (J.ElbowL, 0.6f), (J.RaiseR, 0.4f), (J.OutR, 0.6f), (J.ElbowR, 0.6f), (J.Sway, -0.06f), (J.Tilt, 0.1f), (J.Nod, 0.1f), (J.HeadTurn, -0.3f));
    private static readonly float[] CarltonB = P((J.RaiseR, 0.6f), (J.OutR, -0.5f), (J.ElbowR, 0.6f), (J.RaiseL, 0.4f), (J.OutL, 0.6f), (J.ElbowL, 0.6f), (J.Sway, 0.06f), (J.Tilt, -0.1f), (J.Nod, 0.1f), (J.HeadTurn, 0.3f));

    private static readonly Move Carlton = new([(0.1f, CarltonA), (0.24f, CarltonB), (0.38f, CarltonA), (0.52f, CarltonB)], 0.22f, 0.4f);

    /// <summary>The griddy: leaning forward, heels kicked back in turn, the arms swinging.</summary>
    private static readonly float[] GriddyA = P((J.Lean, 0.3f), (J.LiftL, -0.25f), (J.KneeL, 1.4f), (J.RaiseR, 0.9f), (J.ElbowR, 0.8f), (J.RaiseL, -0.7f), (J.Nod, -0.1f));
    private static readonly float[] GriddyB = P((J.Lean, 0.3f), (J.LiftR, -0.25f), (J.KneeR, 1.4f), (J.RaiseL, 0.9f), (J.ElbowL, 0.8f), (J.RaiseR, -0.7f), (J.Nod, -0.1f));

    private static readonly Move Griddy = new([(0.09f, GriddyA), (0.19f, GriddyB), (0.29f, GriddyA), (0.39f, GriddyB)], 0.22f, 0.3f);

    /// <summary>The shuffle: a foot kicked out to the side and back in, the other, the arms with them.</summary>
    private static readonly float[] ShuffleA = P((J.LiftL, 0.4f), (J.SpreadL, 0.35f), (J.KneeL, 0.5f), (J.RaiseL, 0.8f), (J.ElbowL, 1.2f), (J.RaiseR, -0.3f), (J.Sway, 0.05f), (J.Dip, 0.02f));
    private static readonly float[] ShuffleB = P((J.LiftR, 0.4f), (J.SpreadR, 0.35f), (J.KneeR, 0.5f), (J.RaiseR, 0.8f), (J.ElbowR, 1.2f), (J.RaiseL, -0.3f), (J.Sway, -0.05f), (J.Dip, 0.02f));

    private static readonly Move Shuffle = new([(0.08f, ShuffleA), (0.17f, ShuffleB), (0.26f, ShuffleA), (0.35f, ShuffleB)], 0.2f, 0.3f);

    /// <summary>The horse ride: wrists crossed in front holding the reins, hopping from foot to foot.</summary>
    private static float[] Ride(J lift, J knee, float jump) => P((J.RaiseL, 1f), (J.RaiseR, 1f), (J.OutL, -0.35f), (J.OutR, -0.35f), (J.ElbowL, 0.35f), (J.ElbowR, 0.35f), (lift, 0.5f), (knee, 0.6f), (J.Jump, jump), (J.Dip, 0.04f));

    private static readonly Move HorseRide = new(
        [(0.1f, Ride(J.LiftL, J.KneeL, 0.06f)), (0.2f, Ride(J.LiftR, J.KneeR, 0)), (0.3f, Ride(J.LiftL, J.KneeL, 0.06f)), (0.4f, Ride(J.LiftR, J.KneeR, 0))],
        0.22f, 0.3f);

    /// <summary>The lawnmower: bent over, a hand on the knee, the other pulling the cord.</summary>
    private static readonly Move Lawnmower = new(
        [(0.1f, P((J.Lean, 0.35f), (J.RaiseL, 0.4f), (J.ElbowL, 0.4f), (J.RaiseR, 0.9f), (J.OutR, -0.2f), (J.ElbowR, 0.2f), (J.Nod, 0.15f))),
         (0.22f, P((J.Lean, 0.15f), (J.RaiseL, 0.4f), (J.ElbowL, 0.4f), (J.RaiseR, -0.5f), (J.OutR, 0.3f), (J.ElbowR, 1.6f), (J.Twist, -0.3f), (J.Nod, -0.1f))),
         (0.36f, P((J.Lean, 0.15f), (J.RaiseL, 0.4f), (J.ElbowL, 0.4f), (J.RaiseR, -0.5f), (J.OutR, 0.3f), (J.ElbowR, 1.6f), (J.Twist, -0.3f), (J.Nod, -0.1f)))],
        0.25f, 0.3f);

    /// <summary>The hip bump: the two of them bumping hips, each upper body leaning away - twice.
    /// The blue guy's to his left, toward the red guy; the red guy's to his right.</summary>
    private static float[] Bump(float side, float amount) => P((J.Sway, 0.12f * side * amount), (J.Tilt, 0.22f * side * amount), (J.OutL, 0.3f), (J.OutR, 0.3f), (J.ElbowL, 0.8f), (J.ElbowR, 0.8f), (J.HeadTurn, -0.4f * side));

    private static readonly Move BumpBlue = new([(0.14f, Bump(1, 1)), (0.24f, Bump(1, 0.3f)), (0.36f, Bump(1, 1))], 0.3f, 0.4f);
    private static readonly Move BumpRed = new([(0.14f, Bump(-1, 1)), (0.24f, Bump(-1, 0.3f)), (0.36f, Bump(-1, 1))], 0.3f, 0.4f);

    /// <summary>The moves each part of the music starts, in turn: the melody's the red guy's, the
    /// bass's and the snare's the blue guy's; a leap of the melody a spin or a moonwalk.</summary>
    private static readonly Move[] MelodyMoves = [Point, Wave, Dab, PointDown, Floss, Kick, Sprinkler, Hustle, Robot, Carlton, Griddy, Lawnmower, Point, Pump];
    private static readonly Move[] BassMoves = [Step, Twist, RunningMan, HeadBang, Shuffle, CabbagePatch, HorseRide, Hustle];
    private static readonly Move[] SnareMoves = [Clap, Pump, Dab, Clap, Wave, Floss];
    private static readonly Move[] LeapMoves = [Spin, Moonwalk, Point];

    private sealed class Guy(int index, Vector3 home)
    {
        public readonly int Index = index;
        public readonly Vector3 Home = home;
        public bool Knees;
        public Move? Move;
        public double Since = double.NegativeInfinity;
        public readonly float[] From = new float[Angles];
        public double Beat = -10;
        public float Strength;
        public int Side = 1;
        public int Next;

        /// <summary>The melody's leaps so far, for the move each makes.</summary>
        public int Leaps;

        /// <summary>How hard he has been dancing lately: his moves, the big ones twice, fading.</summary>
        public float Action;
        public readonly float[] Pose = new float[Angles];
        public readonly float[] Scratch = new float[Angles];

        public bool Free(double time) => Move is null || time - Since >= Move.Committed;
    }

    private const float BallRadius = 1.7f;
    private static readonly Vector3 BallCentre = new(0, -BallRadius, 0);

    private static Vector3 OnBall(float x) => new(x, MathF.Sqrt((BallRadius * BallRadius) - (x * x)) - BallRadius, 0.05f);

    private readonly Guy[] _guys = [new(0, OnBall(-0.62f)), new(1, OnBall(0.6f))];
    private readonly DireModel _model = DireModel.Instance;
    private double _time;
    private double _lastJump = -10;
    private int _kicks;

    /// <summary>Each voice's colour as it last played and how bright its light still is.</summary>
    private readonly (uint Colour, float Level)[] _lights = [(0xFFFF4FD8, 0.4f), (0xFF46E0FF, 0.4f), (0xFFFFD54A, 0.4f)];
    private float _sparkle;
    private double _kickAt = -10;
    private float _kickStrength;

    public DireWorld()
    {
        foreach (var part in _model.Parts)
            if (part.Role == DireModel.Role.Shin)
                _guys[part.Guy].Knees = true;
        SnapCamera(Wide);
    }

    public void Reset()
    {
        foreach (var guy in _guys)
        {
            guy.Move = null;
            guy.Since = double.NegativeInfinity;
            guy.Beat = -10;
            guy.Strength = 0;
            Array.Clear(guy.From);
        }
        _time = 0;
        _lastJump = _kickAt = _floorBeatAt = -10;
        _beats = 0;
        _kicks = 0;
        _sparkle = 0;
        _energy = 0;
        _usual = UsualAtStart;
        _breath = (0, 0);
        _closeOn = 1;
        foreach (var guy in _guys)
            guy.Action = 0;
        _shotAt = 0;
        _shotBeats = _calmNext = _livelyNext = 0;
        SnapCamera(Wide);
    }

    public void Advance(double time)
    {
        double step = Math.Clamp(time - _time, 0, 0.25);
        _time = time;
        for (int i = 0; i < _lights.Length; i++)
            _lights[i].Level = Math.Max(0.35f, _lights[i].Level - (float)(step * 1.6));
        _sparkle = Math.Max(0, _sparkle - (float)(step * 2.5));
        _energy *= MathF.Exp(-(float)step / 2.5f);
        _usual += (_energy - _usual) * (1 - MathF.Exp(-(float)step / 25));
        foreach (var guy in _guys)
        {
            guy.Action *= MathF.Exp(-(float)step / 3);
            if (guy.Move is { } move && _time - guy.Since > move.Length)
                guy.Move = null;
        }
        MoveCamera((float)step);
    }

    /// <summary>A note of the tune: a beat to dip on, a move to make, a light to flash.</summary>
    public void Strike(Beat beat, int voice, uint colour, float loudness)
    {
        var light = _lights[voice % 3];
        _lights[voice % 3] = (colour, Math.Max(light.Level, 0.5f + (0.6f * loudness)));
        _sparkle = Math.Min(1.5f, _sparkle + (0.35f * loudness));
        _energy += 0.12f * loudness;
        Guy blue = _guys[0], red = _guys[1];

        if (beat is Beat.Kick or Beat.Snare or Beat.Bass)
            foreach (var guy in _guys)
            {
                guy.Beat = _time;
                guy.Strength = Math.Clamp(loudness, 0.5f, 1.1f);
                if (beat != Beat.Snare)
                    guy.Side = -guy.Side;
            }
        if (beat is Beat.Kick or Beat.Bass && _time - _floorBeatAt > 0.2)
        {
            _beats++;
            _floorBeatAt = _time;
        }

        switch (beat)
        {
            case Beat.Kick:
                (_kickAt, _kickStrength) = (_time, loudness);
                _breath.Speed += 0.17f * Math.Min(1, loudness) * Math.Max(0, 1 - (_breath.Value / DeepestBreath));
                // Now and then, on a loud kick, they both jump.
                if (++_kicks % 16 == 0 && _time - _lastJump > 6 && blue.Free(_time) && red.Free(_time))
                {
                    _lastJump = _time;
                    // A jump together, or a bump of the hips, in turn.
                    bool bump = (_kicks / 16) % 2 == 0;
                    Start(blue, bump ? BumpBlue : Jump);
                    Start(red, bump ? BumpRed : Jump);
                }
                break;
            case Beat.Snare:
                if (blue.Free(_time))
                    Start(blue, SnareMoves[blue.Next++ % SnareMoves.Length]);
                break;
            case Beat.Bass:
                if (blue.Free(_time))
                    Start(blue, BassMoves[blue.Next++ % BassMoves.Length]);
                break;
            case Beat.Middle:
                if (blue.Free(_time) && blue.Move is null)
                    Start(blue, Hustle);
                break;
            case Beat.Melody:
                if (red.Free(_time))
                    Start(red, MelodyMoves[red.Next++ % MelodyMoves.Length]);
                break;
            case Beat.Leap:
                if (red.Free(_time))
                    Start(red, LeapMoves[red.Leaps++ % LeapMoves.Length]);
                break;
            case Beat.Fall:
                if (red.Free(_time))
                    Start(red, PointDown);
                break;
        }
    }

    private void Start(Guy guy, Move move)
    {
        MoveAt(guy, _time, guy.From);
        // A turn ended whole starts the next from the front.
        guy.From[(int)J.Turn] = MathF.IEEERemainder(guy.From[(int)J.Turn], MathF.Tau);
        guy.Move = move;
        guy.Since = _time;
        guy.Action += move == Spin || move == Jump || move == Kick ? 2 : 1;
    }

    private static float Ease(float t) => t <= 0 ? 0 : t >= 1 ? 1 : t * t * (3 - (2 * t));

    /// <summary>The move's part of the pose at <paramref name="time"/>: from where it started to
    /// each key in turn, eased, then back to nothing - a whole turn kept whole on the way back.</summary>
    private static void MoveAt(Guy guy, double time, float[] into)
    {
        Array.Clear(into);
        if (guy.Move is not { } move || time < guy.Since)
            return;
        float t = (float)(time - guy.Since);
        var keys = move.Keys;
        if (t < keys[0].At)
        {
            Mix(guy.From, keys[0].Pose, Ease(t / keys[0].At), into);
            return;
        }
        for (int i = 1; i < keys.Length; i++)
            if (t < keys[i].At)
            {
                Mix(keys[i - 1].Pose, keys[i].Pose, Ease((t - keys[i - 1].At) / (keys[i].At - keys[i - 1].At)), into);
                return;
            }
        float back = Ease((t - keys[^1].At) / move.Out);
        var last = keys[^1].Pose;
        for (int i = 0; i < Angles; i++)
            into[i] = last[i] * (1 - back);
        float turn = last[(int)J.Turn];
        float whole = MathF.Round(turn / MathF.Tau) * MathF.Tau;
        into[(int)J.Turn] = turn + ((whole - turn) * back);
    }

    private static void Mix(float[] a, float[] b, float t, float[] into)
    {
        for (int i = 0; i < Angles; i++)
            into[i] = a[i] + ((b[i] - a[i]) * t);
    }

    /// <summary>A guy's whole pose now: his stance, the groove's dip on the last beat and his sway
    /// to the side it stepped to, and his move over it.</summary>
    private void PoseOf(Guy guy)
    {
        var pose = guy.Pose;
        MoveAt(guy, _time, guy.Scratch);
        for (int i = 0; i < Angles; i++)
            pose[i] = Stance[i] + guy.Scratch[i];

        float since = (float)(_time - guy.Beat);
        float dip = since < 0 ? 0 : since < 0.07f ? since / 0.07f : MathF.Exp(-(since - 0.07f) * 6);
        dip *= guy.Strength;
        float idle = (float)Math.Sin((_time * 2.2) + guy.Index) * 0.5f;
        pose[(int)J.Dip] += 0.075f * dip;
        pose[(int)J.Nod] += (0.22f * dip) + (0.03f * idle);
        pose[(int)J.ElbowL] += 0.35f * dip;
        pose[(int)J.ElbowR] += 0.35f * dip;
        pose[(int)J.RaiseL] += 0.15f * dip;
        pose[(int)J.RaiseR] += 0.15f * dip;
        pose[(int)J.Sway] += 0.035f * guy.Side;
        pose[(int)J.Tilt] += (0.05f * guy.Side) + (0.02f * idle);
        pose[(int)J.HeadTilt] += 0.06f * guy.Side;
    }

    // ------------------------------------------------------------------ the camera

    private Vector3 _eye, _right, _up, _forward;
    private float _focal, _cx, _cy, _width, _height;

    /// <summary>A shot the camera works its way to: how far out it stands, the point it looks at,
    /// how far round from the front it is and how high above that point.</summary>
    private readonly record struct Shot(float Distance, float TargetX, float TargetY, float Yaw, float Lift);

    private static readonly Shot Wide = new(8.9f, 0, 0.17f, 0.15f, 0.6f);
    private static readonly Shot High = new(7f, 0, 0.5f, -0.35f, 2.4f);
    private static readonly Shot TwoShot = new(6.2f, 0, 0.8f, -0.12f, 0.55f);
    private static readonly Shot Low = new(5.6f, 0, 1.15f, 0.45f, -0.25f);
    private static readonly Shot Close = new(5f, 0, 1.12f, 0.1f, 0.4f);
    private static readonly Shot CloseBlue = new(3.7f, -0.62f, 1.12f, -0.25f, 0.35f);
    private static readonly Shot CloseRed = new(3.9f, 0.6f, 1.3f, 0.25f, 0.4f);

    /// <summary>The shots taken in turn while the music is quiet, and while it is busy.</summary>
    private static readonly Shot[] CalmShots = [Wide, TwoShot, High, Wide, Low, TwoShot];
    private static readonly Shot[] LivelyShots = [Close, Low, TwoShot, Close, High];

    /// <summary>A shot is held this long at least, and moved on from after this many beats - a
    /// phrase - or this long without them.</summary>
    private const double ShortestShot = 5.5, LongestShot = 13;

    private const int PhraseBeats = 16;

    /// <summary>How busy the music is: its notes, each as loud as it was, fading over a few seconds;
    /// and how busy it has been over the last half minute or so. Busier than it has been, and the
    /// camera goes in close; quieter, and it goes out wide - whatever the tune's own pace.</summary>
    private float _energy, _usual = UsualAtStart;

    private const float UsualAtStart = 2;

    /// <summary>Lively: busier than usual by this much, and not near silent.</summary>
    private bool Lively => _energy > _usual * 1.08f && _energy > 0.6f;

    /// <summary>The guy the next close-up is on: they take turns.</summary>
    private int _closeOn = 1;

    private Shot _shot = Wide;
    private double _shotAt;
    private int _shotBeats, _calmNext, _livelyNext;

    /// <summary>The camera's place as it moves - each part with its own speed - on springs that
    /// ease it off and in to every shot, never faster than a walk.</summary>
    private (float Value, float Speed) _distance, _targetX, _targetY, _yaw, _lift;

    /// <summary>How far the camera has breathed in, as a share of its distance: each kick nudges it
    /// in, a spring brings it back - never more than <see cref="DeepestBreath"/>, however fast they come.</summary>
    private (float Value, float Speed) _breath;

    private const float DeepestBreath = 0.035f;

    private void SnapCamera(Shot shot)
    {
        _shot = shot;
        _distance = (shot.Distance, 0);
        _targetX = (shot.TargetX, 0);
        _targetY = (shot.TargetY, 0);
        _yaw = (shot.Yaw, 0);
        _lift = (shot.Lift, 0);
    }

    /// <summary>The camera moved on by <paramref name="step"/> seconds towards its shot, and a new
    /// shot chosen at the end of a phrase.</summary>
    private void MoveCamera(float step)
    {
        double inShot = _time - _shotAt;
        if (inShot >= ShortestShot && (_beats - _shotBeats >= PhraseBeats || inShot >= LongestShot))
            ChooseShot();
        // Within a shot it creeps slowly in.
        float creep = 1 - (0.06f * (float)Math.Min(1, inShot / 12));
        Follow(ref _distance, _shot.Distance * creep, 2.2f, 1.1f, step);
        Follow(ref _targetX, _shot.TargetX, 2f, 0.45f, step);
        Follow(ref _targetY, _shot.TargetY, 2f, 0.45f, step);
        Follow(ref _yaw, _shot.Yaw, 2.8f, 0.15f, step);
        Follow(ref _lift, _shot.Lift, 2.4f, 0.5f, step);
        Follow(ref _breath, 0, 0.8f, 0.2f, step);
    }

    /// <summary>The next shot: in close on the guy dancing hardest when the music is busy, the
    /// lively shots in turn, or - when it is quiet - the wide ones; never the one it has.</summary>
    private void ChooseShot()
    {
        bool onOne = _shot == CloseBlue || _shot == CloseRed;
        bool lively = Lively;
        Shot next;
        if (lively && !onOne && Math.Max(_guys[0].Action, _guys[1].Action) > 1.5f)
        {
            // In close on the guy whose turn it is - unless he is standing about and the other is not.
            int on = _guys[_closeOn].Action < 1 ? 1 - _closeOn : _closeOn;
            next = on == 0 ? CloseBlue : CloseRed;
            _closeOn = 1 - on;
        }
        else if (lively)
        {
            next = LivelyShots[_livelyNext++ % LivelyShots.Length];
            if (next == _shot)
                next = LivelyShots[_livelyNext++ % LivelyShots.Length];
        }
        else
        {
            next = CalmShots[_calmNext++ % CalmShots.Length];
            if (next == _shot)
                next = CalmShots[_calmNext++ % CalmShots.Length];
        }
        _shot = next;
        _shotAt = _time;
        _shotBeats = _beats;
    }

    /// <summary>A value eased towards <paramref name="goal"/> on a critically damped spring that
    /// settles in about <paramref name="settle"/> seconds and moves no faster than
    /// <paramref name="fastest"/> a second.</summary>
    private static void Follow(ref (float Value, float Speed) state, float goal, float settle, float fastest, float step)
    {
        if (step <= 0)
            return;
        float omega = 2 / settle;
        float x = omega * step;
        float decay = 1 / (1 + x + (0.48f * x * x) + (0.235f * x * x * x));
        float change = Math.Clamp(state.Value - goal, -fastest * settle, fastest * settle);
        float target = state.Value - change;
        float temp = (state.Speed + (omega * change)) * step;
        float speed = (state.Speed - (omega * temp)) * decay;
        float value = target + ((change + temp) * decay);
        if ((goal - state.Value > 0) == (value > goal))
            (value, speed) = (goal, 0);
        state = (value, speed);
    }

    private void Aim(Size size)
    {
        float t = (float)_time;
        // It drifts a little round, and each kick it breathes in a touch and out again.
        float distance = _distance.Value * (1 - Math.Clamp(_breath.Value, 0, DeepestBreath));
        var target = new Vector3(_targetX.Value, _targetY.Value, 0);
        float yaw = _yaw.Value + (0.07f * MathF.Sin(t * 0.23f));
        float lift = _lift.Value;
        _eye = target + new Vector3(MathF.Sin(yaw) * distance, lift, MathF.Cos(yaw) * distance);
        _forward = Vector3.Normalize(target - _eye);
        _right = Vector3.Normalize(Vector3.Cross(_forward, Vector3.UnitY));
        _up = Vector3.Cross(_right, _forward);
        _focal = (float)size.Height * 2.4f;
        (_width, _height) = ((float)size.Width, (float)size.Height);
        (_cx, _cy) = (_width / 2, _height / 2);
    }

    private bool Project(Vector3 p, out float x, out float y, out float depth, out float scale)
    {
        var d = p - _eye;
        depth = Vector3.Dot(d, _forward);
        if (depth < 0.3f)
        {
            x = y = scale = 0;
            return false;
        }
        scale = _focal / depth;
        x = _cx + (Vector3.Dot(d, _right) * scale);
        y = _cy - (Vector3.Dot(d, _up) * scale);
        return true;
    }

    // ------------------------------------------------------------------ the frame

    private readonly FlatMesh _ballMesh = new();
    private readonly FlatMesh _guyMesh = new();
    private readonly Matrix4x4[] _placed = new Matrix4x4[64];

    /// <summary>The brightest of the ball's tiles this frame, for their sparkle and their rays.</summary>
    private readonly (Vector3 At, Vector3 Ray, float Bright, uint Colour)[] _glints = new (Vector3, Vector3, float, uint)[14];
    private int _glintCount;

    public void Compose(NinjaFrame frame, Size size)
    {
        frame.Begin(size);
        frame.Background(0xFF07040E, 0xFF1A0D2A);
        Aim(size);
        ComposeFloor(frame);
        ComposeSpotlights(frame);
        ComposeWallDots(frame);
        ComposeBall(frame);
        foreach (var guy in _guys)
            PoseOf(guy);
        _guyMesh.Begin();
        foreach (var guy in _guys)
            ComposeGuy(guy);
        _guyMesh.Paint(frame, 1, this);
        ComposeGlints(frame);

        // A kick lights the whole club a moment.
        float sinceKick = (float)(_time - _kickAt);
        if (sinceKick < 0.12f)
            frame.Flash(WithAlpha(0xFFFFFFFF, (byte)(26 * (1 - (sinceKick / 0.12f)) * Math.Min(1, _kickStrength))));
        frame.Vignette();
    }

    private static uint WithAlpha(uint colour, byte alpha) => (colour & 0x00FFFFFF) | ((uint)alpha << 24);

    /// <summary>The three spotlights high above, each sweeping its beam across the ball.</summary>
    private Vector3 SpotlightAt(int k) => new((k - 1) * 3.2f, 6.5f, -1.5f + (k == 1 ? -1.5f : 0));

    private Vector3 SpotlightAims(int k)
    {
        float t = (float)_time;
        return new Vector3(MathF.Sin((t * 0.55f) + (k * 2.1f)) * 1.6f, 0.4f + (MathF.Sin((t * 0.37f) + k) * 0.5f), MathF.Cos((t * 0.43f) + (k * 1.3f)) * 0.8f);
    }

    private void ComposeSpotlights(NinjaFrame frame)
    {
        for (int k = 0; k < 3; k++)
        {
            var (colour, level) = _lights[k];
            var from = SpotlightAt(k);
            var to = SpotlightAims(k);
            var along = Vector3.Normalize(to - from);
            var across = Vector3.Normalize(Vector3.Cross(along, _forward)) * 0.9f;
            if (!Project(from, out float ax, out float ay, out _, out _)
                || !Project(to + across, out float bx, out float by, out _, out _)
                || !Project(to - across, out float cx, out float cy, out _, out _))
                continue;
            frame.BeginPolygon();
            frame.Point(ax, ay);
            frame.Point(bx, by);
            frame.Point(cx, cy);
            frame.EndPolygon(true, 0, WithAlpha(colour, (byte)Math.Min(90, 40 * level)), additive: true, blur: 10);
        }
    }

    /// <summary>The ball's dots of light running round the club's walls as it turns.</summary>
    private static readonly Vector3[] DotDirections = MakeDots();

    private static Vector3[] MakeDots()
    {
        var random = new Random(7);
        var dots = new Vector3[90];
        for (int i = 0; i < dots.Length; i++)
        {
            float y = (float)((random.NextDouble() * 1.3) - 0.35);
            float a = (float)(random.NextDouble() * MathF.Tau);
            dots[i] = new Vector3(MathF.Cos(a), y, MathF.Sin(a));
        }
        return dots;
    }

    private float BallTurn => (float)(_time * 0.3);

    private void ComposeWallDots(NinjaFrame frame)
    {
        float turn = BallTurn, c = MathF.Cos(turn), s = MathF.Sin(turn);
        for (int i = 0; i < DotDirections.Length; i++)
        {
            var d = DotDirections[i];
            var on = new Vector3((d.X * c) - (d.Z * s), d.Y, (d.X * s) + (d.Z * c));
            var wall = new Vector3(0, Floor + 6, 0) + (on * WallRadius);
            if (!Project(wall, out float x, out float y, out float depth, out float scale) || x < -10 || x > _width + 10 || y < -10 || y > _height + 10)
                continue;
            var (colour, level) = _lights[i % 3];
            uint tint = i % 4 == 0 ? 0xFFFFFFFF : colour;
            float r = Math.Clamp(0.22f * scale, 1.2f, 4f);
            frame.Disc(0, x, y, r, WithAlpha(tint, (byte)Math.Min(200, 70 + (90 * level) + (60 * _sparkle))), blur: r * 0.8f, additive: true);
        }
    }

    // ------------------------------------------------------------------ the floor

    /// <summary>The club's floor, the ball standing on it, and how far off its walls are.</summary>
    private const float Floor = -2 * BallRadius, WallRadius = 26, FloorTile = 2f;

    private const int FloorAcross = 24, FloorFrom = -13, FloorTo = 4;

    private readonly FlatMesh _floorMesh = new();

    /// <summary>The beats so far: the floor's lights change with each.</summary>
    private int _beats;

    /// <summary>The floor of glass tiles lit from below, as in the films: on every beat another
    /// pattern of them lights up, each in a voice's colour, fading into the dark far off.</summary>
    private void ComposeFloor(NinjaFrame frame)
    {
        _floorMesh.Begin();
        float since = (float)(_time - _floorBeatAt);
        float pulse = 0.55f + (0.45f * MathF.Exp(-since * 3));
        for (int i = -FloorAcross / 2; i < FloorAcross / 2; i++)
            for (int j = FloorFrom; j < FloorTo; j++)
            {
                float x0 = i * FloorTile, z0 = j * FloorTile;
                uint hash = (uint)((i * 73856093) ^ (j * 19349663) ^ (_beats * 83492791));
                hash ^= hash >> 13;
                hash *= 0x5bd1e995;
                hash ^= hash >> 15;
                bool lit = (hash & 7) < 3;
                var (colour, level) = _lights[(int)((hash >> 4) % 3)];
                float glow = lit ? pulse * Math.Min(1, level) : 0.06f;
                var middle = new Vector3(x0 + (FloorTile / 2), Floor, z0 + (FloorTile / 2));
                float far = Vector3.Distance(middle, _eye);
                glow *= MathF.Exp(-far * 0.035f);
                uint tint = lit ? colour : 0xFF8080A0;
                var c = new SKColor((byte)(((tint >> 16) & 0xFF) * glow), (byte)(((tint >> 8) & 0xFF) * glow), (byte)((tint & 0xFF) * glow));
                const float Inset = 0.08f;
                var a = new Vector3(x0 + Inset, Floor, z0 + Inset);
                var b = new Vector3(x0 + FloorTile - Inset, Floor, z0 + Inset);
                var d = new Vector3(x0 + FloorTile - Inset, Floor, z0 + FloorTile - Inset);
                var e = new Vector3(x0 + Inset, Floor, z0 + FloorTile - Inset);
                _floorMesh.Add(a, b, d, c);
                _floorMesh.Add(a, d, e, c);
            }
        _floorMesh.Paint(frame, 2, this);
    }

    private double _floorBeatAt = -10;

    // ------------------------------------------------------------------ the ball

    private sealed record Tile(Vector3[] Corners, Vector3 Normal, float Seed);

    private static readonly Tile[] Tiles = MakeTiles();

    /// <summary>The ball's mirror tiles, in rings from its top down past its middle, each a little
    /// smaller than its place so the dark ball shows between them.</summary>
    private static Tile[] MakeTiles()
    {
        const float Size = 0.15f, Gap = 0.86f;
        var tiles = new List<Tile>();
        var random = new Random(11);
        float band = Size / BallRadius;
        for (float polar = 0; polar < MathF.PI * 0.62f; polar += band)
        {
            float middle = polar + (band / 2);
            int count = Math.Max(5, (int)MathF.Round(MathF.Tau * BallRadius * MathF.Sin(middle) / Size));
            for (int i = 0; i < count; i++)
            {
                float a0 = i * MathF.Tau / count, a1 = (i + 1) * MathF.Tau / count;
                Vector3 At(float p, float a) => new(MathF.Sin(p) * MathF.Cos(a), MathF.Cos(p), MathF.Sin(p) * MathF.Sin(a));
                var corners = new[] { At(polar, a0), At(polar, a1), At(polar + band, a1), At(polar + band, a0) };
                var centre = At(middle, (a0 + a1) / 2);
                for (int k = 0; k < 4; k++)
                    corners[k] = (centre + ((corners[k] - centre) * Gap)) * BallRadius;
                tiles.Add(new Tile(corners, centre, (float)random.NextDouble()));
            }
        }
        return [.. tiles];
    }

    private void ComposeBall(NinjaFrame frame)
    {
        // The dark ball itself, seen between the tiles.
        if (Project(BallCentre, out float bx, out float by, out _, out float bs))
            frame.Disc(0, bx, by, BallRadius * bs * 0.995f, 0xFF15131C, 0, false);

        float turn = BallTurn;
        var spin = Matrix4x4.CreateRotationY(turn);
        _ballMesh.Begin();
        _glintCount = 0;
        float t = (float)_time;
        Span<Vector3> lights = stackalloc Vector3[3];
        for (int k = 0; k < 3; k++)
            lights[k] = Vector3.Normalize(SpotlightAt(k) - SpotlightAims(k));

        foreach (var tile in Tiles)
        {
            var normal = Vector3.TransformNormal(tile.Normal, spin);
            var centre = BallCentre + (normal * BallRadius);
            var view = _eye - centre;
            float facing = Vector3.Dot(normal, view);
            if (facing <= 0)
                continue;
            view = Vector3.Normalize(view);
            var reflected = (2 * Vector3.Dot(normal, view) * normal) - view;

            // Silver, the room's dark mirrored in it, brighter looking up; each spotlight a hot spot
            // where the tile throws it back at the camera; and tiles that twinkle with the music.
            float env = 0.16f + (0.22f * MathF.Max(0, reflected.Y)) + (tile.Seed * 0.1f);
            float r = env * 0.8f, g = env * 0.84f, b = env;
            float hottest = 0;
            uint hot = 0xFFFFFFFF;
            for (int k = 0; k < 3; k++)
            {
                var (colour, level) = _lights[k];
                float spot = MathF.Pow(MathF.Max(0, Vector3.Dot(reflected, lights[k])), 30) * 2.4f * level;
                r += spot * ((colour >> 16) & 0xFF) / 255f;
                g += spot * ((colour >> 8) & 0xFF) / 255f;
                b += spot * (colour & 0xFF) / 255f;
                if (spot > hottest)
                    (hottest, hot) = (spot, colour);
            }
            float twinkle = MathF.Pow(MathF.Max(0, MathF.Sin((t * 2.7f) + (tile.Seed * 60))), 24) * (0.25f + _sparkle);
            r += twinkle;
            g += twinkle;
            b += twinkle;
            var colourOf = new SKColor((byte)Math.Clamp(r * 255, 0, 255), (byte)Math.Clamp(g * 255, 0, 255), (byte)Math.Clamp(b * 255, 0, 255));

            var c = tile.Corners;
            var a0 = BallCentre + Vector3.Transform(c[0], spin);
            var a1 = BallCentre + Vector3.Transform(c[1], spin);
            var a2 = BallCentre + Vector3.Transform(c[2], spin);
            var a3 = BallCentre + Vector3.Transform(c[3], spin);
            _ballMesh.Add(a0, a1, a2, colourOf);
            _ballMesh.Add(a0, a2, a3, colourOf);

            float bright = hottest + twinkle;
            if (bright > 0.9f)
                KeepGlint(centre, reflected, bright, twinkle > hottest ? 0xFFFFFFFF : hot);
        }
        _ballMesh.Paint(frame, 0, this);
    }

    private void KeepGlint(Vector3 at, Vector3 ray, float bright, uint colour)
    {
        if (_glintCount < _glints.Length)
        {
            _glints[_glintCount++] = (at, ray, bright, colour);
            return;
        }
        int dimmest = 0;
        for (int i = 1; i < _glints.Length; i++)
            if (_glints[i].Bright < _glints[dimmest].Bright)
                dimmest = i;
        if (bright > _glints[dimmest].Bright)
            _glints[dimmest] = (at, ray, bright, colour);
    }

    /// <summary>The brightest tiles' sparkle - a star of light - and their rays through the smoke.</summary>
    private void ComposeGlints(NinjaFrame frame)
    {
        for (int i = 0; i < _glintCount; i++)
        {
            var (at, ray, bright, colour) = _glints[i];
            if (!Project(at, out float x, out float y, out _, out float scale))
                continue;
            float glow = Math.Clamp(bright - 0.6f, 0, 1.5f);
            if (Project(at + (ray * 3.5f), out float rx, out float ry, out _, out _))
                frame.Line(1e7f, x, y, rx, ry, Math.Max(1.5f, 0.05f * scale), WithAlpha(colour, (byte)(55 * glow)), additive: true, blur: 3);
            float arm = Math.Clamp(0.18f * scale * glow, 3, 16);
            frame.Line(1e7f, x - arm, y, x + arm, y, 1.2f, WithAlpha(0xFFFFFFFF, (byte)Math.Min(255, 200 * glow)), additive: true);
            frame.Line(1e7f, x, y - arm, x, y + arm, 1.2f, WithAlpha(0xFFFFFFFF, (byte)Math.Min(255, 200 * glow)), additive: true);
            frame.Disc(1e7f, x, y, arm * 0.45f, WithAlpha(colour, (byte)Math.Min(255, 160 * glow)), blur: arm * 0.4f, additive: true);
        }
    }

    // ------------------------------------------------------------------ the guys

    /// <summary>A guy posed and lit into the mesh: each part turned about its joint as the pose
    /// says, carried by the part it hangs from; each triangle facing the camera lit flat - a warm key
    /// light from the front, the spotlights' colours on the edges, the ball's glitter from below.</summary>
    private void ComposeGuy(Guy guy)
    {
        var pose = guy.Pose;
        float A(J angle) => pose[(int)angle];

        float dip = A(J.Dip);
        float drop = guy.Knees ? dip : 0;
        float bend = guy.Knees ? MathF.Acos(Math.Clamp(1 - (dip / 0.77f), -1, 1)) : 0;
        // Sliding to the side - a sway, a moonwalk - his feet keep to the ball's curve.
        var standing = OnBall(guy.Home.X + A(J.Sway)) with { Z = guy.Home.Z };
        var root = Matrix4x4.CreateRotationY(A(J.Turn)) * Matrix4x4.CreateTranslation(standing + new Vector3(0, A(J.Jump) - drop, 0));

        var parts = _model.Parts;
        for (int i = 0; i < parts.Count; i++)
        {
            var part = parts[i];
            if (part.Guy != guy.Index)
                continue;
            float s = part.Side;
            var turn = part.Role switch
            {
                DireModel.Role.Spine => Matrix4x4.CreateFromYawPitchRoll(A(J.Twist), A(J.Lean), A(J.Tilt)),
                DireModel.Role.Head => Matrix4x4.CreateFromYawPitchRoll(A(J.HeadTurn), A(J.Nod), A(J.HeadTilt)),
                // Out to the side first, then forward: an arm brought in across the body stays in
                // as it comes up in front - fists together for the cabbage patch.
                DireModel.Role.UpperArm => Matrix4x4.CreateRotationZ(s * (s > 0 ? A(J.OutL) : A(J.OutR))) * Matrix4x4.CreateRotationX(-(s > 0 ? A(J.RaiseL) : A(J.RaiseR))),
                DireModel.Role.Forearm => Matrix4x4.CreateRotationX(-(s > 0 ? A(J.ElbowL) : A(J.ElbowR))),
                DireModel.Role.Thigh => Matrix4x4.CreateRotationX(-((s > 0 ? A(J.LiftL) : A(J.LiftR)) + bend)) * Matrix4x4.CreateRotationZ(s * (s > 0 ? A(J.SpreadL) : A(J.SpreadR))),
                DireModel.Role.Shin => Matrix4x4.CreateRotationX((s > 0 ? A(J.KneeL) : A(J.KneeR)) + (2 * bend)),
                _ => Matrix4x4.Identity,
            };
            var local = Matrix4x4.CreateTranslation(-part.Joint) * turn * Matrix4x4.CreateTranslation(part.Joint);
            // The blue guy has no knees: his dip is his body sinking at the hips.
            if (part.Role == DireModel.Role.Spine && !guy.Knees)
                local *= Matrix4x4.CreateTranslation(0, -dip * 0.8f, 0);
            _placed[i] = local * (part.Parent < 0 ? root : _placed[part.Parent]);
            EmitPart(part, _placed[i]);
        }
    }

    private static readonly Vector3 KeyLight = Vector3.Normalize(new Vector3(0.35f, 0.75f, 0.6f));
    private static readonly Vector3 RimLeft = Vector3.Normalize(new Vector3(-0.85f, 0.25f, -0.45f));
    private static readonly Vector3 RimRight = Vector3.Normalize(new Vector3(0.85f, 0.25f, -0.45f));

    private void EmitPart(DireModel.Part part, Matrix4x4 placed)
    {
        var corners = part.Corners;
        var (rimA, levelA) = _lights[0];
        var (rimB, levelB) = _lights[1];
        var (under, levelC) = _lights[2];
        for (int t = 0; t < part.Normals.Length; t++)
        {
            var a = Vector3.Transform(corners[3 * t], placed);
            var b = Vector3.Transform(corners[(3 * t) + 1], placed);
            var c = Vector3.Transform(corners[(3 * t) + 2], placed);
            var normal = Vector3.TransformNormal(part.Normals[t], placed);
            // Two-sided, as Blender draws them: some of the model's faces point inwards - the blue
            // guy's bib among them - and left out as turned away, the shirt behind showed through.
            // Each is lit on the side the camera sees.
            var middle = (a + b + c) / 3;
            normal = Vector3.Normalize(normal);
            if (Vector3.Dot(normal, _eye - middle) < 0)
                normal = -normal;

            uint colour = part.Colours[t];
            float r = ((colour >> 16) & 0xFF) / 255f, g = ((colour >> 8) & 0xFF) / 255f, bl = (colour & 0xFF) / 255f;
            float light = 0.26f + (0.08f * normal.Y) + (0.7f * MathF.Max(0, Vector3.Dot(normal, KeyLight)));
            float left = MathF.Pow(MathF.Max(0, Vector3.Dot(normal, RimLeft)), 2) * 0.45f * levelA;
            float right = MathF.Pow(MathF.Max(0, Vector3.Dot(normal, RimRight)), 2) * 0.45f * levelB;
            float below = MathF.Max(0, -normal.Y) * 0.3f * (levelC + _sparkle);
            float lr = (r * light) + (left * ((rimA >> 16) & 0xFF) / 255f) + (right * ((rimB >> 16) & 0xFF) / 255f) + (below * ((under >> 16) & 0xFF) / 255f);
            float lg = (g * light) + (left * ((rimA >> 8) & 0xFF) / 255f) + (right * ((rimB >> 8) & 0xFF) / 255f) + (below * ((under >> 8) & 0xFF) / 255f);
            float lb = (bl * light) + (left * (rimA & 0xFF) / 255f) + (right * (rimB & 0xFF) / 255f) + (below * (under & 0xFF) / 255f);
            _guyMesh.Add(a, b, c, new SKColor((byte)Math.Clamp(lr * 255, 0, 255), (byte)Math.Clamp(lg * 255, 0, 255), (byte)Math.Clamp(lb * 255, 0, 255)), part.Layers[t] * LayerNearer);
        }
    }

    /// <summary>How much nearer a triangle lying on another surface of its part is taken to be, a
    /// layer, when they are laid back to front: the blue guy's bib lies a few millimetres in front of
    /// his shirt, too close for their middles to tell which is in front.</summary>
    private const float LayerNearer = 0.03f;

    /// <summary>Triangles of one colour each, laid back to front into a frame's mesh slot - each by
    /// its middle's distance, less what it is taken to be nearer.</summary>
    private sealed class FlatMesh
    {
        private Vector3[] _corners = new Vector3[3 * 4096];
        private SKColor[] _colours = new SKColor[4096];
        private float[] _nearer = new float[4096];
        private int _count;
        private SKPoint[] _screen = [];
        private float[] _depth = [];
        private int[] _order = [];

        public void Begin() => _count = 0;

        public void Add(Vector3 a, Vector3 b, Vector3 c, SKColor colour, float nearer = 0)
        {
            if (_count == _colours.Length)
            {
                Array.Resize(ref _colours, _count * 2);
                Array.Resize(ref _nearer, _count * 2);
                Array.Resize(ref _corners, _count * 6);
            }
            _corners[3 * _count] = a;
            _corners[(3 * _count) + 1] = b;
            _corners[(3 * _count) + 2] = c;
            _nearer[_count] = nearer;
            _colours[_count++] = colour;
        }

        public void Paint(NinjaFrame frame, int slot, DireWorld world)
        {
            if (_screen.Length < 3 * _count)
            {
                _screen = new SKPoint[3 * _colours.Length];
                _depth = new float[_colours.Length];
                _order = new int[_colours.Length];
            }
            int drawn = 0;
            for (int t = 0; t < _count; t++)
            {
                float sum = 0;
                bool seen = true;
                int outside = 15;
                for (int k = 0; k < 3 && seen; k++)
                {
                    seen = world.Project(_corners[(3 * t) + k], out float x, out float y, out float depth, out _);
                    _screen[(3 * t) + k] = new SKPoint(x, y);
                    sum += depth;
                    outside &= (x < 0 ? 1 : 0) | (x > world._width ? 2 : 0) | (y < 0 ? 4 : 0) | (y > world._height ? 8 : 0);
                }
                if (!seen || outside != 0)
                    continue;
                _depth[drawn] = -(sum - (3 * _nearer[t]));
                _order[drawn++] = t;
            }
            if (drawn == 0)
                return;
            Array.Sort(_depth, _order, 0, drawn);
            var (points, colours) = frame.MeshArrays(slot, ((drawn + 255) / 256) * 256 * 3);
            for (int k = 0; k < drawn; k++)
            {
                int t = _order[k];
                for (int corner = 0; corner < 3; corner++)
                {
                    points[(3 * k) + corner] = _screen[(3 * t) + corner];
                    colours[(3 * k) + corner] = _colours[t];
                }
            }
            Array.Fill(points, new SKPoint(-16, -16), drawn * 3, points.Length - (drawn * 3));
            Array.Fill(colours, SKColors.Transparent, drawn * 3, colours.Length - (drawn * 3));
            frame.Mesh(0, slot);
        }
    }
}
