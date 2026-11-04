using System.Numerics;

namespace MagPlaySid.Player;

/// <summary>
/// A fighter of the Last Ninja meter: a body of joints in three dimensions, posed by angles - each
/// limb's from hanging straight down, positive towards the way the fighter faces, the lower limbs'
/// from the upper's - and moved through its moves by key poses in time, eased between, back to its
/// stance at the end. A move can lift it, carry it forward, turn it about on the spot (a roundhouse
/// kick) or head over heels (a flip).
/// </summary>
internal sealed class Fighter(Fighter.Clothes look)
{
    /// <summary>What a fighter wears: its clothes and their sheen, the wraps of its hands, its sash,
    /// its head and eyes, a guard's helmet and staff (0 for none).</summary>
    public sealed record Clothes(uint Cloth, uint Shine, uint Wrap, uint Sash, uint Head, uint Eyes, uint Helmet, uint Staff, Pose Stance);

    public readonly record struct Pose(
        float Lean,
        float FrontArm, float FrontForearm, float BackArm, float BackForearm,
        float FrontThigh, float FrontShin, float BackThigh, float BackShin,
        float Lift = 0, float Flip = 0, float Twist = 0, float Forward = 0, float Spread = 0, float Sword = 0)
    {
        public static Pose Between(Pose a, Pose b, float t)
        {
            float Mix(float x, float y) => x + ((y - x) * t);
            return new(Mix(a.Lean, b.Lean), Mix(a.FrontArm, b.FrontArm), Mix(a.FrontForearm, b.FrontForearm),
                Mix(a.BackArm, b.BackArm), Mix(a.BackForearm, b.BackForearm), Mix(a.FrontThigh, b.FrontThigh),
                Mix(a.FrontShin, b.FrontShin), Mix(a.BackThigh, b.BackThigh), Mix(a.BackShin, b.BackShin),
                Mix(a.Lift, b.Lift), Mix(a.Flip, b.Flip), Mix(a.Twist, b.Twist), Mix(a.Forward, b.Forward),
                Mix(a.Spread, b.Spread), Mix(a.Sword, b.Sword));
        }
    }

    /// <summary>A move: its key poses, each at its seconds into the move; the seconds back to the
    /// stance after the last; when it lands on the one it is aimed at (0 for never); whether it
    /// lands with a foot - a kick's thump - and hard enough to knock down; whether it is fast enough
    /// to blur; and the seconds before another note may cut it short.</summary>
    public sealed record Act((float At, Pose Pose)[] Keys, float Out, float Lands = 0, bool Kick = false, bool Heavy = false, bool Fast = false, float Committed = 0.15f)
    {
        public float Length => Keys[^1].At + Out;
    }

    // ------------------------------------------------------------------ the fighters' clothes

    private static readonly Pose NinjaStance = new(0.15f, 0.95f, -1.75f, 0.35f, -1.35f, 0.45f, -0.4f, -0.4f, -0.3f, Lift: -0.06f);
    private static readonly Pose GuardStance = new(0.1f, 0.9f, -1.0f, 0.7f, -1.2f, 0.3f, -0.25f, -0.3f, -0.2f);

    public static readonly Clothes Ninja = new(0xFF0C0E16, 0x5A6A8AC8, 0xFF2A2E3A, 0xFFC8202C, 0xFF10121A, 0xFFE8F4FF, 0, 0, NinjaStance);

    public static readonly Clothes[] Guards =
    [
        new(0xFF4A1418, 0x60FF6A5A, 0xFF2A1A16, 0xFFE0A030, 0xFFC08A6A, 0xFFFFE0A0, 0xFF6A1A1E, 0xFF8A6A40, GuardStance),
        new(0xFF241A46, 0x60A07AFF, 0xFF1E1A2A, 0xFF40D0C8, 0xFFB08A6A, 0xFFB0FFF0, 0xFF2E2258, 0xFF7A6A50, GuardStance),
        new(0xFF16362A, 0x6060E0A0, 0xFF1A221E, 0xFFE05A3A, 0xFFB88A66, 0xFFFFF0B0, 0xFF143A2A, 0xFF6A5A3A, GuardStance),
    ];

    // ------------------------------------------------------------------ the moves

    private static Pose P(float lean, float fa, float ff, float ba, float bf, float ft, float fs, float bt, float bs,
                          float lift = 0, float flip = 0, float twist = 0, float forward = 0, float spread = 0, float sword = 0) =>
        new(lean, fa, ff, ba, bf, ft, fs, bt, bs, lift, flip, twist, forward, spread, sword);

    public static readonly Act Punch = new(
        [(0.06f, P(0.32f, 1.55f, 0f, -0.25f, -1.75f, 0.6f, -0.35f, -0.55f, -0.2f, -0.06f, forward: 0.15f))],
        0.24f, Lands: 0.06f);

    public static readonly Act PalmPush = new(
        [(0.07f, P(-0.1f, 0.3f, -2.1f, 0.3f, -2.1f, 0.45f, -0.4f, -0.45f, -0.3f, -0.1f)),
         (0.14f, P(0.38f, 1.5f, -0.1f, 1.45f, -0.1f, 0.7f, -0.3f, -0.6f, -0.1f, -0.12f, forward: 0.4f, spread: 0.15f)),
         (0.26f, P(0.38f, 1.5f, -0.1f, 1.45f, -0.1f, 0.7f, -0.3f, -0.6f, -0.1f, -0.12f, forward: 0.4f, spread: 0.15f))],
        0.25f, Lands: 0.14f);

    public static readonly Act Uppercut = new(
        [(0.08f, P(0.3f, 0.2f, -1.9f, 0.4f, -1.4f, 0.6f, -0.8f, -0.5f, -0.6f, -0.18f)),
         (0.17f, P(-0.12f, 2.85f, -0.3f, 0.2f, -1.6f, 0.35f, -0.2f, -0.3f, -0.2f, 0.1f))],
        0.26f, Lands: 0.17f);

    public static readonly Act FrontKick = new(
        [(0.07f, P(-0.1f, 0.8f, -1.6f, 0.9f, -1.3f, 1.65f, -2.3f, -0.2f, -0.1f)),
         (0.13f, P(-0.3f, 0.6f, -1.5f, 1.0f, -1.2f, 1.65f, 0f, -0.15f, -0.05f, forward: 0.15f)),
         (0.22f, P(-0.3f, 0.6f, -1.5f, 1.0f, -1.2f, 1.65f, 0f, -0.15f, -0.05f, forward: 0.15f))],
        0.25f, Lands: 0.13f, Kick: true, Fast: true);

    public static readonly Act Roundhouse = new(
        [(0.08f, P(-0.1f, 1.3f, -1.4f, 0.4f, -1.2f, 1.4f, -2.0f, -0.1f, 0f, twist: 0.8f)),
         (0.2f, P(-0.5f, 1.6f, -0.6f, -0.6f, -0.8f, 1.7f, -0.05f, -0.05f, 0f, 0.05f, twist: 3.6f)),
         (0.32f, P(-0.2f, 1.2f, -1.4f, 0.4f, -1.2f, 0.6f, -0.4f, -0.3f, -0.2f, twist: MathF.Tau))],
        0.25f, Lands: 0.2f, Kick: true, Heavy: true, Fast: true, Committed: 0.3f);

    public static readonly Act FlyingKick = new(
        [(0.1f, P(0.4f, 0.4f, -1.6f, 0.3f, -1.5f, 0.9f, -1.6f, -0.2f, -1.2f, -0.25f)),
         (0.26f, P(-0.35f, 2.2f, -0.8f, -0.8f, -0.4f, 1.6f, 0f, 1.0f, -2.3f, 0.75f, forward: 1.0f)),
         (0.36f, P(-0.35f, 2.2f, -0.8f, -0.8f, -0.4f, 1.6f, 0f, 1.0f, -2.3f, 0.6f, forward: 1.25f)),
         (0.48f, P(0.3f, 1.2f, -1.5f, 0.4f, -1.3f, 0.6f, -0.9f, -0.5f, -0.4f, -0.2f, forward: 1.3f))],
        0.4f, Lands: 0.3f, Kick: true, Heavy: true, Fast: true, Committed: 0.45f);

    /// <summary>The seconds into a back flip it lands, kneeling.</summary>
    public const float FlipLands = 0.55f;

    public static readonly Act BackFlip = new(
        [(0.08f, P(0.4f, -0.3f, -0.6f, -0.3f, -0.6f, 0.8f, -1.6f, 0.6f, -1.6f, -0.25f)),
         (0.28f, P(0f, 2.6f, -0.6f, 2.6f, -0.6f, 1.6f, -2.4f, 1.5f, -2.4f, 1.05f, flip: -MathF.PI, forward: -0.45f)),
         (0.44f, P(0f, 2.0f, -0.4f, 2.0f, -0.4f, 1.2f, -1.6f, 1.0f, -1.6f, 0.45f, flip: -MathF.Tau, forward: -0.85f)),
         (FlipLands, P(0.25f, 1.25f, -0.7f, -0.35f, -0.4f, 1.5f, -1.5f, -0.1f, -1.55f, -0.42f, flip: -MathF.Tau, forward: -0.9f)),
         (0.95f, P(0.25f, 1.25f, -0.7f, -0.35f, -0.4f, 1.5f, -1.5f, -0.1f, -1.55f, -0.42f, flip: -MathF.Tau, forward: -0.9f))],
        0.35f, Fast: true, Committed: 0.6f);

    public static readonly Act Sweep = new(
        [(0.08f, P(0.5f, 1.2f, -0.6f, -0.3f, -0.8f, 0.9f, -1.8f, -1.0f, 1.6f, -0.5f)),
         (0.24f, P(0.55f, 1.3f, -0.4f, -0.5f, -0.8f, 1.6f, 0f, -1.2f, 1.9f, -0.52f, twist: MathF.PI)),
         (0.34f, P(0.5f, 1.2f, -0.6f, -0.3f, -0.8f, 1.6f, 0f, -1.2f, 1.9f, -0.5f, twist: MathF.Tau))],
        0.3f, Lands: 0.2f, Kick: true, Fast: true, Committed: 0.3f);

    public static readonly Act Stomp = new(
        [(0.1f, P(-0.1f, 1.3f, -1.6f, 0.6f, -1.4f, 1.3f, -1.7f, -0.2f, -0.2f, 0.12f)),
         (0.16f, P(0.2f, 1.0f, -1.9f, 0.4f, -1.5f, 0.35f, -0.15f, -0.4f, -0.3f, -0.14f))],
        0.25f, Lands: 0.16f, Kick: true);

    public static readonly Act Block = new(
        [(0.06f, P(-0.1f, 1.95f, -2.3f, 1.75f, -2.1f, 0.35f, -0.3f, -0.45f, -0.3f, -0.05f)),
         (0.26f, P(-0.15f, 1.95f, -2.3f, 1.75f, -2.1f, 0.35f, -0.3f, -0.45f, -0.3f, -0.08f, forward: -0.15f))],
        0.25f);

    public static readonly Act Slash = new(
        [(0.09f, P(-0.15f, 3.05f, -0.5f, 0.3f, -1.5f, 0.45f, -0.3f, -0.45f, -0.3f, sword: 1)),
         (0.17f, P(0.4f, 1.0f, 0.1f, -0.25f, -1.6f, 0.7f, -0.4f, -0.6f, -0.2f, -0.12f, forward: 0.25f, sword: 1)),
         (0.27f, P(0.4f, 1.0f, 0.1f, -0.25f, -1.6f, 0.7f, -0.4f, -0.6f, -0.2f, -0.12f, forward: 0.25f, sword: 1))],
        0.3f, Lands: 0.17f, Fast: true);

    /// <summary>A guard's swing of its staff, high, down at the ninja.</summary>
    public static readonly Act Swing = new(
        [(0.14f, P(-0.2f, 2.9f, -0.6f, 2.6f, -0.8f, 0.3f, -0.2f, -0.3f, -0.2f)),
         (0.24f, P(0.45f, 1.3f, -0.2f, 1.1f, -0.4f, 0.6f, -0.4f, -0.5f, -0.2f, -0.1f, forward: 0.2f))],
        0.3f);

    /// <summary>A guard struck: rocked back, and back to its stance.</summary>
    public static readonly Act Reel = new(
        [(0.05f, P(-0.55f, 0.3f, -0.6f, -0.4f, -0.4f, -0.1f, -0.2f, -0.6f, -0.1f, forward: -0.35f)),
         (0.16f, P(-0.4f, 0.4f, -0.8f, -0.3f, -0.6f, 0.1f, -0.3f, -0.5f, -0.2f, forward: -0.45f))],
        0.4f);

    /// <summary>A guard knocked down: thrown back onto the ground, lying there, and up again.</summary>
    public static readonly Act KnockDown = new(
        [(0.08f, P(-0.7f, 0.4f, -0.4f, -0.4f, -0.3f, 0.3f, -0.3f, -0.5f, -0.2f, 0.15f, forward: -0.5f)),
         (0.38f, P(0f, 2.8f, -0.3f, 2.4f, -0.4f, 0.4f, -0.2f, 0.2f, -0.3f, -0.72f, flip: -MathF.PI / 2, forward: -0.9f)),
         (1.3f, P(0f, 2.8f, -0.3f, 2.4f, -0.4f, 0.4f, -0.2f, 0.2f, -0.3f, -0.72f, flip: -MathF.PI / 2, forward: -0.9f)),
         (1.65f, P(0.3f, 1.2f, -0.8f, 0.2f, -0.6f, 1.5f, -1.5f, -0.1f, -1.55f, -0.42f, forward: -0.8f))],
        0.45f);

    // ------------------------------------------------------------------ the fighter

    public Clothes Look { get; } = look;

    /// <summary>Where it stands, and the way it faces, round from the world's z towards its x.</summary>
    public Vector3 Home;
    public float Yaw;

    public Act? Move { get; private set; }
    public double Since { get; private set; } = double.NegativeInfinity;

    /// <summary>The colour of the voice whose note it struck with, and how loud the note was.</summary>
    public uint Colour = 0xFFFFFFFF;
    public float Loudness = 1;

    private Pose _from;
    private Pose _now;
    private double _settled;

    public Vector3 Facing => new(MathF.Sin(Yaw), 0, MathF.Cos(Yaw));

    /// <summary>Where it is now: its place, carried along by its move.</summary>
    public Vector3 Position => Home + (Facing * _now.Forward);

    public void Start(Act move, double time)
    {
        _from = PoseAt(time);
        // A turn or a flip ended whole starts the next from the same way round.
        _from = _from with { Twist = MathF.IEEERemainder(_from.Twist, MathF.Tau), Flip = MathF.IEEERemainder(_from.Flip, MathF.Tau) };
        Move = move;
        Since = time;
    }

    public void Stop()
    {
        Move = null;
        _from = _now = Look.Stance;
    }

    /// <summary>A move over is let go; the pose of now kept, for where it stands and its cloth.</summary>
    public void Settle(double time)
    {
        if (Move is { } move && time - Since > move.Length)
            Move = null;
        _now = PoseAt(time);
        _settled = time;
    }

    /// <summary>Lying on the ground, knocked down.</summary>
    public bool Down(double time) => Move == KnockDown && time - Since < KnockDown.Keys[^1].At;

    /// <summary>Its pose at <paramref name="time"/>: from the pose it was in towards each key in turn,
    /// eased, then back to its stance - breathing in it between moves.</summary>
    public Pose PoseAt(double time)
    {
        var stance = Breathe(Look.Stance, time);
        if (Move is not { } move || time < Since)
            return stance;
        float t = (float)(time - Since);
        var keys = move.Keys;
        if (t < keys[0].At)
            return Pose.Between(_from, keys[0].Pose, Ease(t / keys[0].At));
        for (int i = 1; i < keys.Length; i++)
        {
            if (t < keys[i].At)
                return Pose.Between(keys[i - 1].Pose, keys[i].Pose, Ease((t - keys[i - 1].At) / (keys[i].At - keys[i - 1].At)));
        }
        var last = keys[^1].Pose;
        // Back round to the stance from a whole turn or flip, not back the way it came.
        var home = stance with { Twist = MathF.Round(last.Twist / MathF.Tau) * MathF.Tau, Flip = MathF.Round(last.Flip / MathF.Tau) * MathF.Tau };
        float back = (t - keys[^1].At) / move.Out;
        return back >= 1 ? stance : Pose.Between(last, home, Ease(back));
    }

    private static Pose Breathe(Pose pose, double time)
    {
        float t = (float)time;
        return pose with { Lift = pose.Lift + (0.015f * MathF.Sin(t * 4.2f)), FrontArm = pose.FrontArm + (0.05f * MathF.Sin(t * 4.2f)) };
    }

    private static float Ease(float t) => t <= 0 ? 0 : t >= 1 ? 1 : t * t * (3 - (2 * t));

    // ------------------------------------------------------------------ the body

    public readonly record struct Body(
        Vector3 Hip, Vector3 Waist, Vector3 Neck, Vector3 Shoulder, Vector3 Head,
        Vector3 FrontKnee, Vector3 FrontFoot, Vector3 BackKnee, Vector3 BackFoot,
        Vector3 FrontElbow, Vector3 FrontHand, Vector3 BackElbow, Vector3 BackHand,
        Vector3 Facing, Vector3 Across);

    private const float Thigh = 0.46f, Shin = 0.46f, Arm = 0.31f, Forearm = 0.3f, Torso = 0.56f, Standing = 0.9f;

    /// <summary>Its joints in the world in <paramref name="pose"/>: the limbs laid out from the hips
    /// and shoulders in the plane it faces along, the whole turned about the hips for a flip.</summary>
    public Body Skeleton(Pose pose)
    {
        float yaw = Yaw + pose.Twist;
        var facing = new Vector3(MathF.Sin(yaw), 0, MathF.Cos(yaw));
        var across = new Vector3(MathF.Cos(yaw), 0, -MathF.Sin(yaw));
        var up = Vector3.UnitY;
        var hip = Home + (Facing * pose.Forward) + new Vector3(0, Standing + pose.Lift, 0);

        Vector3 Dir(float angle) => (-up * MathF.Cos(angle)) + (facing * MathF.Sin(angle));
        var torso = (up * MathF.Cos(pose.Lean)) + (facing * MathF.Sin(pose.Lean));
        var shoulder = hip + (torso * (Torso * 0.88f));
        var neck = hip + (torso * Torso);
        var head = neck + (torso * 0.15f) + (facing * 0.02f);
        var waist = hip + (torso * 0.08f);

        var frontHip = hip + (across * 0.11f);
        var backHip = hip - (across * 0.11f);
        var frontKnee = frontHip + (Dir(pose.FrontThigh) * Thigh);
        var frontFoot = frontKnee + (Dir(pose.FrontThigh + pose.FrontShin) * Shin);
        var backKnee = backHip + (Dir(pose.BackThigh) * Thigh);
        var backFoot = backKnee + (Dir(pose.BackThigh + pose.BackShin) * Shin);

        var spread = across * MathF.Sin(pose.Spread);
        var frontShoulder = shoulder + (across * 0.19f);
        var backShoulder = shoulder - (across * 0.19f);
        var frontElbow = frontShoulder + ((Dir(pose.FrontArm) - spread) * Arm);
        var frontHand = frontElbow + ((Dir(pose.FrontArm + pose.FrontForearm) - spread) * Forearm);
        var backElbow = backShoulder + ((Dir(pose.BackArm) + spread) * Arm);
        var backHand = backElbow + ((Dir(pose.BackArm + pose.BackForearm) + spread) * Forearm);

        if (pose.Flip != 0)
        {
            float c = MathF.Cos(pose.Flip), s = MathF.Sin(pose.Flip);
            Vector3 Turn(Vector3 p)
            {
                var v = p - hip;
                float f = Vector3.Dot(v, facing), u = Vector3.Dot(v, up), a = Vector3.Dot(v, across);
                return hip + (facing * ((f * c) - (u * s))) + (up * ((f * s) + (u * c))) + (across * a);
            }
            (waist, neck, shoulder, head) = (Turn(waist), Turn(neck), Turn(shoulder), Turn(head));
            (frontKnee, frontFoot, backKnee, backFoot) = (Turn(frontKnee), Turn(frontFoot), Turn(backKnee), Turn(backFoot));
            (frontElbow, frontHand, backElbow, backHand) = (Turn(frontElbow), Turn(frontHand), Turn(backElbow), Turn(backHand));
        }

        return new Body(hip, waist, neck, shoulder, head, frontKnee, frontFoot, backKnee, backFoot,
                        frontElbow, frontHand, backElbow, backHand, facing, across);
    }

    /// <summary>Where a strike lands: the front foot for a kick, else the front hand.</summary>
    public Vector3 Reach(bool kick)
    {
        var body = Skeleton(_now);
        return kick ? body.FrontFoot : body.FrontHand;
    }

    /// <summary>A hand or a foot now: 0 the front hand, 1 the back, 2 the front foot, 3 the back.</summary>
    public Vector3 Extremity(int limb)
    {
        var body = Skeleton(_now);
        return limb switch { 0 => body.FrontHand, 1 => body.BackHand, 2 => body.FrontFoot, _ => body.BackFoot };
    }

    /// <summary>Where a tail of cloth is tied: 0 and 1 the headband's, at the back of the head;
    /// 2 and 3 the sash's, at the back of the waist.</summary>
    public Vector3 Knot(int tail)
    {
        var body = Skeleton(_now);
        float side = tail % 2 == 0 ? 1 : -1;
        return tail < 2
            ? body.Head - (body.Facing * 0.12f) + (body.Across * (0.03f * side)) + new Vector3(0, 0.03f, 0)
            : body.Waist - (body.Facing * 0.16f) + (body.Across * (0.07f * side));
    }
}
