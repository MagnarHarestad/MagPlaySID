using System.Numerics;
using Avalonia;

namespace MagPlaySid.Player;

/// <summary>
/// The Last Ninja meter's world, in three dimensions: a stone arena on a hilltop at dusk, and below
/// it a Japanese countryside running off into the evening haze - rice paddies holding the sky, fields
/// in a patchwork between winding lanes and hedgerows, round trees and pines, farmhouses with their
/// windows lit, a pagoda on a far hill. The camera is close, a little above, and slowly circles the
/// fight: a black ninja at the middle and guards coming at it from all around in turn. The wind
/// streams the ninja's headband and sash, fireflies drift, petals blow across. The fight follows the
/// music - <see cref="Strike"/> starts a move, <see cref="Advance"/> moves the world on - and each
/// frame is laid out with <see cref="Compose"/> as shapes and lit meshes, farthest first, for
/// <see cref="NinjaView"/> to paint. The look is after Jerome Liard's English Lane on Shadertoy, the
/// ideas only - its lanes, hedges, round trees and haze - not its code.
/// </summary>
internal sealed class NinjaWorld
{
    // ------------------------------------------------------------------ the camera

    /// <summary>The camera circles the fight this fast, radians a second of the tune.</summary>
    private const float Orbit = 0.07f;

    /// <summary>The camera's place: this far out from the ninja, this high - close, the fighters
    /// filling the view and cut off at its edges.</summary>
    private const float CameraDistance = 5.4f, CameraHeight = 2.1f;

    /// <summary>The focal length, as a share of the view's height: a fighter as tall as the view.</summary>
    private const float Focal = 3.45f;

    private Vector3 _eye, _right, _up, _forward, _target;
    private float _focal, _cx, _cy, _width, _height;

    /// <summary>Scenery passing right by the camera - a stone lantern on the hilltop's rim -
    /// fades out rather than fill the view; the fighters do not.</summary>
    private const float NearFade = 5.5f;

    private bool _scenery;

    private uint NearFaded(uint colour, float depth)
    {
        if (!_scenery || depth >= NearFade)
            return colour;
        float keep = Math.Clamp((depth - (NearFade / 2)) / (NearFade / 2), 0, 1);
        return WithAlpha(colour, (byte)((colour >> 24) * keep));
    }

    /// <summary>Whether a round thing at (x, y) as wide as r lies wholly off the view.</summary>
    private bool OffView(float x, float y, float r) => x + r < 0 || y + r < 0 || x - r > _width || y - r > _height;
    private readonly Func<Vector3, (bool, float, float, float)> _project;

    private void Aim(float yaw, Size size)
    {
        // The camera follows the ninja, softly, so a leap does not jolt the view.
        var goal = _ninja.Position + new Vector3(0, 0.95f, 0);
        _target = _target == default ? goal : _target + ((goal - _target) * 0.12f);
        _eye = _target + new Vector3(MathF.Sin(yaw) * CameraDistance, CameraHeight, MathF.Cos(yaw) * CameraDistance);
        _forward = Vector3.Normalize(_target - _eye);
        _right = Vector3.Normalize(Vector3.Cross(_forward, Vector3.UnitY));
        _up = Vector3.Cross(_right, _forward);
        _focal = (float)size.Height * Focal;
        (_width, _height) = ((float)size.Width, (float)size.Height);
        float shake = (float)Math.Max(0, _shake - ((_time - _shakeAt) * 30)) * (float)size.Height / 150f;
        _cx = ((float)size.Width / 2) + (shake * MathF.Sin((float)_time * 90));
        _cy = ((float)size.Height / 2) + (shake * MathF.Cos((float)_time * 77));
    }

    /// <summary>A point of the world on the view, its distance in front of the camera, and how many
    /// pixels a metre is there; false behind the camera, or too close to it.</summary>
    private bool Project(Vector3 p, out float x, out float y, out float depth, out float scale)
    {
        var d = p - _eye;
        depth = Vector3.Dot(d, _forward);
        if (depth < 0.5f)
        {
            x = y = scale = 0;
            return false;
        }
        scale = _focal / depth;
        x = _cx + (Vector3.Dot(d, _right) * scale);
        y = _cy - (Vector3.Dot(d, _up) * scale);
        return true;
    }

    // ------------------------------------------------------------------ the land

    /// <summary>The hilltop the arena stands on, and how far out the hill has fallen to the valley.</summary>
    private const float Hilltop = 5.4f, HillFoot = 13f;

    private const float ArenaRadius = 4.3f, RuneRadius = 3.55f;

    /// <summary>The height of the land: the hilltop flat, falling to the valley's rolling floor and
    /// its fields, rising again into far hills.</summary>
    public static float Height(float x, float z)
    {
        float r = MathF.Sqrt((x * x) + (z * z));
        if (r <= Hilltop)
            return 0;
        float bumps = ((MathF.Sin(x * 0.11f) * MathF.Sin(z * 0.13f)) + 1) * 0.5f;
        float valley = -11f + (bumps * bumps * 3.2f) + (MathF.Max(0, r - 48) * 0.35f);
        float t = Math.Clamp((r - Hilltop) / (HillFoot - Hilltop), 0, 1);
        return valley * t * t * (3 - (2 * t));
    }

    private static Vector3 OnGround(float x, float z, float lift = 0) => new(x, Height(x, z) + lift, z);

    /// <summary>Whether a point below the hilltop is hidden from the camera by it: the line from the
    /// camera to it passes through the hilltop's flat. Hidden things are not laid out at all.</summary>
    private bool BehindHill(Vector3 p)
    {
        if (p.Y > -0.05f || _eye.Y <= 0)
            return false;
        float t = _eye.Y / (_eye.Y - p.Y);
        float x = _eye.X + ((p.X - _eye.X) * t), z = _eye.Z + ((p.Z - _eye.Z) * t);
        return (x * x) + (z * z) < (Hilltop - 0.15f) * (Hilltop - 0.15f);
    }

    /// <summary>The field a place is in: the lanes wind, so the fields between them are not square.
    /// Each has a kind - a paddy of water, young rice, ripe grain, a dark orchard - and the distance
    /// to the nearest lane, for the lane's own colour.</summary>
    private static (int Kind, float ToLane) Field(float x, float z)
    {
        float u = (x + (2.4f * MathF.Sin(z * 0.11f))) / LaneAcross, v = (z + (2.1f * MathF.Sin(x * 0.09f))) / LaneAlong;
        float fu = u - MathF.Floor(u), fv = v - MathF.Floor(v);
        float toLane = MathF.Min(MathF.Min(fu, 1 - fu) * LaneAcross, MathF.Min(fv, 1 - fv) * LaneAlong);
        uint hash = (uint)((int)MathF.Floor(u) * 73856093) ^ (uint)((int)MathF.Floor(v) * 19349663);
        hash = (hash ^ (hash >> 13)) * 0x5bd1e995u;
        int kind = (int)((hash >> 8) % 100) switch { < 42 => 0, < 72 => 1, < 88 => 2, _ => 3 };
        return (kind, toLane);
    }

    private const float LaneAcross = 9.5f, LaneAlong = 7.5f;

    private readonly NinjaMesh _ground = new();

    /// <summary>The land as a mesh of rings round the hill, close together near it and wider apart
    /// far off, each point coloured by its field and lit by the low sun.</summary>
    private void BuildGround()
    {
        const int Around = 84, Rings = 30;
        _ground.Begin();
        var random = new Random(5);
        for (int ring = 0; ring < Rings; ring++)
        {
            float r = (Hilltop - 0.3f) * MathF.Pow(1.13f, ring);
            for (int a = 0; a < Around; a++)
            {
                float angle = a * MathF.Tau / Around;
                float x = MathF.Sin(angle) * r, z = MathF.Cos(angle) * r;
                var at = OnGround(x, z);
                var normal = Vector3.Normalize(new Vector3(Height(x - 0.5f, z) - Height(x + 0.5f, z), 1, Height(x, z - 0.5f) - Height(x, z + 0.5f)));
                uint colour;
                var stuff = NinjaMesh.Stuff.Ground;
                if (r < HillFoot)
                {
                    // The hillside: grass and rock, darker where it is steep.
                    colour = normal.Y < 0.7f ? 0xFF3A3A34 : 0xFF2C4A2A;
                }
                else
                {
                    var (kind, toLane) = Field(x, z);
                    float jitter = random.NextSingle() * 0.12f;
                    if (toLane < 0.9f)
                        colour = 0xFF6E5A44;
                    else if (kind == 0)
                    {
                        // A paddy: the dusk sky in its water, from rose to violet.
                        colour = Mix(0xFF3A3452, 0xFF7A5254, jitter * 6);
                        stuff = NinjaMesh.Stuff.Water;
                    }
                    else
                    {
                        colour = kind switch { 1 => 0xFF3E6A34, 2 => 0xFF8A7A3A, _ => 0xFF1E3A26 };
                    }
                }
                _ground.Point(at, normal, colour, stuff);
            }
        }
        for (int ring = 0; ring < Rings - 1; ring++)
        {
            for (int a = 0; a < Around; a++)
            {
                int here = (ring * Around) + a, next = (ring * Around) + ((a + 1) % Around);
                _ground.Face(here, next, next + Around);
                _ground.Face(here, next + Around, here + Around);
            }
        }
    }

    // ------------------------------------------------------------------ the countryside

    private readonly record struct Tree(Vector3 At, float Height, float Width, uint Leaves, bool Pine, float Phase);
    private readonly record struct House(Vector3 At, float Yaw, float Width, float Depth);

    private readonly Tree[] _trees;
    private readonly House[] _houses;
    private readonly Vector3[] _lanterns;

    /// <summary>The lanes as points along them on the ground, and the hedges beside them.</summary>
    private readonly List<Vector3[]> _lanes = [];
    private readonly Vector3[] _hedges;

    private static readonly Vector3 Pagoda = OnGround(38, -30);

    private readonly Vector3[] _fireflies;
    private readonly float[] _fireflyPhase;
    private readonly Vector3[] _petals;

    public NinjaWorld()
    {
        var random = new Random(1987);
        float Angle() => random.NextSingle() * MathF.Tau;
        Vector3 Around(float from, float to)
        {
            float a = Angle(), r = from + (random.NextSingle() * (to - from));
            return OnGround(MathF.Sin(a) * r, MathF.Cos(a) * r);
        }

        BuildGround();

        // The lanes: along the lines between the fields, in the valley.
        for (int k = -7; k <= 7; k++)
        {
            var across = new List<Vector3>();
            var along = new List<Vector3>();
            for (float s = -64; s <= 64; s += 3.5f)
            {
                float x = (k * LaneAcross) - (2.4f * MathF.Sin(s * 0.11f));
                if (MathF.Sqrt((x * x) + (s * s)) > HillFoot)
                    across.Add(OnGround(x, s, 0.05f));
                float z = (k * LaneAlong) - (2.1f * MathF.Sin(s * 0.09f));
                if (MathF.Sqrt((s * s) + (z * z)) > HillFoot)
                    along.Add(OnGround(s, z, 0.05f));
            }
            if (across.Count > 1)
                _lanes.Add([.. across]);
            if (along.Count > 1)
                _lanes.Add([.. along]);
        }
        var hedges = new List<Vector3>();
        foreach (var lane in _lanes)
        {
            if (random.Next(3) == 0)
                continue;
            for (int i = 0; i < lane.Length; i++)
            {
                if (random.Next(4) == 0)
                    continue;
                var side = new Vector3(random.NextSingle() - 0.5f, 0, random.NextSingle() - 0.5f) * 1.6f;
                hedges.Add(OnGround(lane[i].X + side.X + 0.9f, lane[i].Z + side.Z, 0.45f));
            }
        }
        _hedges = [.. hedges];

        // Trees: round ones and pines in the orchards and along the hedges, cherries among them,
        // and a ring of them round the hill's foot.
        var trees = new List<Tree>();
        while (trees.Count < 150)
        {
            var at = Around(HillFoot - 2, 62);
            var (kind, toLane) = Field(at.X, at.Z);
            bool hillside = at.Length() < HillFoot + 3;
            if (!hillside && kind != 3 && toLane > 2.2f && random.Next(5) != 0)
                continue;
            bool pine = random.Next(3) == 0;
            bool cherry = !pine && random.Next(4) == 0;
            trees.Add(new Tree(at, (pine ? 4.5f : 3.2f) + (random.NextSingle() * 2), 1.3f + (random.NextSingle() * 0.8f),
                cherry ? 0xFFC86A9A : pine ? 0xFF1E3A28 : random.Next(2) == 0 ? 0xFF2E5A2C : 0xFF3A6630, pine, Angle()));
        }
        _trees = [.. trees];

        // Farmhouses in the valley, by the lanes.
        var houses = new List<House>();
        while (houses.Count < 16)
        {
            var at = Around(HillFoot + 4, 55);
            if (Field(at.X, at.Z).ToLane is < 2.5f or > 4.5f)
                continue;
            houses.Add(new House(at, Angle(), 2.4f + (random.NextSingle() * 1.4f), 1.8f + (random.NextSingle() * 0.8f)));
        }
        _houses = [.. houses];

        _lanterns = [.. Enumerable.Range(0, 6).Select(i => (i * MathF.Tau / 6) + 0.5f).Select(a => new Vector3(MathF.Sin(a) * 4.85f, 0, MathF.Cos(a) * 4.85f))];
        _fireflies = [.. Enumerable.Range(0, 46).Select(_ => new Vector3((random.NextSingle() - 0.5f) * 14, 0.4f + (random.NextSingle() * 2.2f), (random.NextSingle() - 0.5f) * 14))];
        _fireflyPhase = [.. Enumerable.Range(0, _fireflies.Length).Select(_ => Angle())];
        _petals = [.. Enumerable.Range(0, 34).Select(_ => new Vector3((random.NextSingle() - 0.5f) * 16, random.NextSingle() * 4f, (random.NextSingle() - 0.5f) * 16))];

        _ninja = new Fighter(Fighter.Ninja) { Yaw = 0 };
        _guards = [.. Enumerable.Range(0, 3).Select(i => new Fighter(Fighter.Guards[i]))];
        foreach (var tail in _tails)
            Array.Fill(tail.Points, new Vector3(0, 1.6f, -0.2f));

        _project = p => Project(p, out float x, out float y, out float depth, out _) ? (true, x, y, depth) : (false, 0, 0, 0);
    }

    // ------------------------------------------------------------------ time and wind

    private double _time;

    /// <summary>The wind: where it blows, and how hard, gusting.</summary>
    private Vector3 Wind
    {
        get
        {
            float t = (float)_time;
            float strength = 0.55f + (0.3f * MathF.Sin(t * 0.37f)) + (0.25f * MathF.Max(0, MathF.Sin(t * 1.7f))) + _gust;
            return Vector3.Normalize(new Vector3(MathF.Cos(t * 0.05f), 0, 0.4f + MathF.Sin(t * 0.05f))) * strength;
        }
    }

    private float _gust;

    // ------------------------------------------------------------------ the fighters

    private readonly Fighter _ninja;
    private readonly Fighter[] _guards;
    private readonly NinjaMesh _body = new();

    /// <summary>The guard coming at the ninja now, and the angle the ring of guards stands at.</summary>
    private int _attacker;
    private float _ring;
    private int _strikes;

    /// <summary>The headband's two tails and the sash's two, each a chain of points the wind blows.</summary>
    private readonly Cloth[] _tails = [new(0.075f), new(0.075f), new(0.09f), new(0.09f)];

    private sealed class Cloth(float link)
    {
        public readonly float Link = link;
        public readonly Vector3[] Points = new Vector3[7];
        public readonly Vector3[] Before = new Vector3[7];
    }

    /// <summary>The flashes and their kind: a kick's thump through the ground, a snare's white
    /// light, each from where and when, in which colour, how hard.</summary>
    private readonly record struct Burst(Vector3 At, double Time, uint Colour, float Strength, bool Kick);

    private readonly Burst[] _bursts = new Burst[8];
    private int _nextBurst;
    private float _shake;
    private double _shakeAt = double.NegativeInfinity;
    private double _runesLit = double.NegativeInfinity;

    /// <summary>Dust and sparks thrown up by a strike.</summary>
    private readonly (Vector3 At, Vector3 Speed, double Born, uint Colour)[] _sparks = new (Vector3, Vector3, double, uint)[64];
    private int _nextSpark;

    /// <summary>Where the ninja's hands and feet were lately, for their blur.</summary>
    private readonly Vector3[,] _trail = new Vector3[4, 10];
    private readonly double[] _trailTime = new double[10];
    private int _trailAt;

    // ------------------------------------------------------------------ the music's moves

    public enum Beat { Kick, Snare, Bass, Melody, Leap, Fall, Middle }

    /// <summary>A note heard: the ninja makes the move it calls for, at the guard coming at it, and
    /// the guard, when the middle voice strikes, swings at the ninja, who blocks.</summary>
    public void Strike(Beat beat, uint colour, float loudness)
    {
        if (_ninja.Move is { } going && _time - _ninja.Since < going.Committed && beat is not (Beat.Kick or Beat.Snare))
            return;

        var target = _guards[_attacker];
        Fighter.Act move = beat switch
        {
            Beat.Kick => (_strikes % 3) switch { 0 => Fighter.FrontKick, 1 => Fighter.Roundhouse, _ => Fighter.FlyingKick },
            Beat.Snare => _strikes % 2 == 0 ? Fighter.PalmPush : Fighter.Punch,
            Beat.Bass => _strikes % 2 == 0 ? Fighter.Sweep : Fighter.Stomp,
            Beat.Leap => Fighter.FlyingKick,
            Beat.Fall => Fighter.BackFlip,
            Beat.Middle => Fighter.Block,
            _ => (_strikes % 4) switch { 0 => Fighter.Punch, 1 => Fighter.Slash, 2 => Fighter.Uppercut, _ => Fighter.Roundhouse },
        };
        if (beat == Beat.Middle && target.Move is null)
            target.Start(Fighter.Swing, _time);
        _ninja.Start(move, _time);
        _ninja.Colour = colour;
        _ninja.Loudness = loudness;
        _strikes++;
    }

    /// <summary>The world moved on to <paramref name="time"/>, the tune's own seconds: the fighters'
    /// moves played on, a strike landed where one lands, the guards closing in, the cloth, the
    /// petals and the sparks blown and fallen.</summary>
    public void Advance(double time)
    {
        double step = Math.Clamp(time - _time, 0, 1 / 20.0);
        double before = _time;
        _time = time;
        if (time < before)
        {
            Reset();
            return;
        }

        // The ring of guards turns slowly, so they come from all sides as the camera turns too.
        _ring -= (float)step * 0.18f;
        for (int i = 0; i < _guards.Length; i++)
        {
            var guard = _guards[i];
            float angle = _ring + (i * MathF.Tau / _guards.Length);
            var direction = new Vector3(MathF.Sin(angle), 0, MathF.Cos(angle));
            float reach = i == _attacker && !guard.Down(time) ? 1.15f : 2.2f;
            var home = direction * reach;
            guard.Home += (home - guard.Home) * (float)Math.Min(1, step * 3);
            guard.Yaw = MathF.Atan2(-guard.Home.X, -guard.Home.Z);
        }
        var foe = _guards[_attacker].Home;
        _ninja.Yaw = TurnTowards(_ninja.Yaw, MathF.Atan2(foe.X, foe.Z), (float)step * 9);

        // A strike landing: the guard takes it, the flash and the thump where it lands.
        if (_ninja.Move is { } move && move.Lands > 0 && before - _ninja.Since < move.Lands && time - _ninja.Since >= move.Lands)
        {
            var guard = _guards[_attacker];
            var at = _ninja.Reach(move.Kick);
            guard.Start(move.Heavy ? Fighter.KnockDown : Fighter.Reel, time);
            Flash(at, _ninja.Colour, _ninja.Loudness, move.Kick);
            if (move.Heavy || _strikes % 3 == 0)
                _attacker = (_attacker + 1) % _guards.Length;
        }
        // Landing from a flip is felt through the ground too.
        if (_ninja.Move == Fighter.BackFlip && before - _ninja.Since < Fighter.FlipLands && time - _ninja.Since >= Fighter.FlipLands)
            Flash(_ninja.Position, _ninja.Colour, _ninja.Loudness, kick: true);

        _ninja.Settle(time);
        foreach (var guard in _guards)
            guard.Settle(time);
        _gust = MathF.Max(0, _gust - ((float)step * 1.5f));

        BlowCloth((float)step);
        BlowPetals((float)step);
        for (int i = 0; i < _sparks.Length; i++)
        {
            ref var spark = ref _sparks[i];
            spark.At += spark.Speed * (float)step;
            spark.Speed += new Vector3(0, -6f, 0) * (float)step;
            if (spark.At.Y < 0)
                spark.Speed = new Vector3(spark.Speed.X * 0.5f, -spark.Speed.Y * 0.3f, spark.Speed.Z * 0.5f);
        }

        // The blur of the hands and feet.
        _trailAt = (_trailAt + 1) % _trailTime.Length;
        _trailTime[_trailAt] = time;
        for (int limb = 0; limb < 4; limb++)
            _trail[limb, _trailAt] = _ninja.Extremity(limb);
    }

    private void Flash(Vector3 at, uint colour, float strength, bool kick)
    {
        _bursts[_nextBurst] = new Burst(at, _time, colour, strength, kick);
        _nextBurst = (_nextBurst + 1) % _bursts.Length;
        var random = Random.Shared;
        for (int i = 0; i < (kick ? 14 : 9); i++)
        {
            var speed = new Vector3(random.NextSingle() - 0.5f, random.NextSingle() * (kick ? 1.2f : 0.8f), random.NextSingle() - 0.5f) * (kick ? 5f : 7f);
            _sparks[_nextSpark] = (kick ? at with { Y = 0.05f } : at, speed, _time, kick ? 0xFFB8A890 : colour);
            _nextSpark = (_nextSpark + 1) % _sparks.Length;
        }
        if (kick)
        {
            _shake = 3.5f * strength;
            _shakeAt = _time;
            _runesLit = _time;
            _gust = 1.2f;
        }
    }

    private static float TurnTowards(float from, float to, float by)
    {
        float difference = MathF.IEEERemainder(to - from, MathF.Tau);
        return from + Math.Clamp(difference, -by, by);
    }

    /// <summary>Everything back to where it starts: a tune from its beginning.</summary>
    public void Reset()
    {
        _ninja.Stop();
        foreach (var guard in _guards)
            guard.Stop();
        _attacker = 0;
        _strikes = 0;
        _shake = 0;
        _target = default;
        for (int i = 0; i < _bursts.Length; i++)
            _bursts[i] = _bursts[i] with { Time = double.NegativeInfinity };
        for (int i = 0; i < _sparks.Length; i++)
            _sparks[i].Born = double.NegativeInfinity;
        foreach (var tail in _tails)
        {
            Array.Fill(tail.Points, _ninja.Position + new Vector3(0, 1.6f, 0));
            Array.Copy(tail.Points, tail.Before, tail.Points.Length);
        }
    }

    /// <summary>The headband's and the sash's tails: each link pulled after the one before, falling,
    /// blown, the first held where it is tied.</summary>
    private void BlowCloth(float step)
    {
        if (step <= 0)
            return;
        var wind = Wind;
        for (int t = 0; t < _tails.Length; t++)
        {
            var tail = _tails[t];
            tail.Points[0] = _ninja.Knot(t);
            for (int i = 1; i < tail.Points.Length; i++)
            {
                var p = tail.Points[i];
                float flutter = MathF.Sin(((float)_time * 13) + (i * 1.3f) + t) * 0.6f;
                var push = (wind * 2.2f) + new Vector3(0, -1.6f + flutter, 0) + (Vector3.Cross(wind, Vector3.UnitY) * flutter * 0.5f);
                var next = p + ((p - tail.Before[i]) * 0.86f) + (push * step * step * 4);
                tail.Before[i] = p;
                tail.Points[i] = next;
            }
            for (int pass = 0; pass < 2; pass++)
            {
                for (int i = 1; i < tail.Points.Length; i++)
                {
                    var d = tail.Points[i] - tail.Points[i - 1];
                    float length = d.Length();
                    if (length > 1e-4f)
                        tail.Points[i] = tail.Points[i - 1] + (d * (tail.Link / length));
                }
            }
        }
    }

    private void BlowPetals(float step)
    {
        var wind = Wind;
        for (int i = 0; i < _petals.Length; i++)
        {
            var p = _petals[i] + (((wind * 0.9f) + new Vector3(MathF.Sin(((float)_time * 2) + i) * 0.3f, -0.35f, 0)) * step);
            if (p.Y < 0 || p.LengthSquared() > 160)
                p = new Vector3((-wind.X * 7) + ((Random.Shared.NextSingle() - 0.5f) * 10), 3 + (Random.Shared.NextSingle() * 2), (-wind.Z * 7) + ((Random.Shared.NextSingle() - 0.5f) * 10));
            _petals[i] = p;
        }
    }

    // ------------------------------------------------------------------ the frame

    /// <summary>The frame at the world's time, for a view of <paramref name="size"/>, into
    /// <paramref name="frame"/>: the land first, then all that stands on it farthest first, then
    /// the flashes over everything.</summary>
    public void Compose(NinjaFrame frame, Size size)
    {
        frame.Begin(size);
        Aim(((float)_time * Orbit) + 0.6f, size);
        var wind = Wind;
        float t = (float)_time;

        frame.Background(0xFF2A2238, 0xFF443650);
        _ground.Paint(frame, 0, _project, _eye, 1, key: NinjaFrame.Ground);
        frame.Layer = NinjaFrame.Layers.Valley;
        _scenery = true;
        ComposeCountryside(frame, t, wind);
        frame.Layer = NinjaFrame.Layers.Hill;
        ComposeHilltop(frame, t);
        _scenery = false;

        // The fighters, their blur and their cloth.
        for (int i = 0; i < _guards.Length; i++)
            ComposeBody(frame, _guards[i], _guards[i].PoseAt(_time), 2 + i);
        ComposeTrails(frame);
        if (_ninja.Move is { Fast: true })
        {
            ComposeGhost(frame, _ninja.PoseAt(_time - 0.05), 0.16f);
            ComposeGhost(frame, _ninja.PoseAt(_time - 0.1), 0.07f);
        }
        ComposeBody(frame, _ninja, _ninja.PoseAt(_time), 1);
        for (int i = 0; i < _tails.Length; i++)
        {
            var points = _tails[i].Points;
            for (int k = 1; k < points.Length; k++)
                Line3(frame, points[k - 1], points[k], i < 2 ? 0.045f * (1 - (k * 0.09f)) : 0.06f * (1 - (k * 0.08f)), 0xFFB01A28);
        }

        // Fireflies, petals and sparks.
        for (int i = 0; i < _fireflies.Length; i++)
        {
            float a = _fireflyPhase[i];
            var at = _fireflies[i] + new Vector3(MathF.Sin((t * 0.6f) + a) * 0.6f, MathF.Sin((t * 0.9f) + (a * 2)) * 0.3f, MathF.Cos((t * 0.5f) + a) * 0.6f);
            float twinkle = 0.5f + (0.5f * MathF.Sin((t * 3) + (a * 5)));
            Glow3(frame, at, 0.12f, WithAlpha(i % 3 == 0 ? 0xFFFFE08A : 0xFFB8FF8A, (byte)(220 * twinkle)), additive: true);
        }
        foreach (var petal in _petals)
            Ball3(frame, petal, 0.03f, 0xFFFFB0D8, 0);
        foreach (var (at, speed, born, colour) in _sparks)
        {
            double age = _time - born;
            if (age < 0 || age > 0.7)
                continue;
            Glow3(frame, at, 0.06f, WithAlpha(colour, (byte)(255 * (1 - (age / 0.7)))), additive: true);
        }
        ComposeBursts(frame);
        frame.Vignette();
    }

    /// <summary>The hilltop: its grassy edge, the arena's stone and its rings and joints, the runes
    /// lit by the kicks, the stone lanterns round it, the fighters' shadows.</summary>
    private void ComposeHilltop(NinjaFrame frame, float t)
    {
        GroundDisc(frame, Hilltop, 0xFF1E3020);
        GroundDisc(frame, ArenaRadius + 0.15f, 0xFF15161C);
        GroundDisc(frame, ArenaRadius, 0xFF3A3A44);
        GroundDisc(frame, ArenaRadius * 0.62f, 0xFF424250);
        for (float r = 1.2f; r < ArenaRadius; r += 1.2f)
            GroundCircle(frame, r, 0.03f, 0xFF22222A);
        for (int spoke = 0; spoke < 16; spoke++)
        {
            float a = spoke * MathF.Tau / 16;
            var from = new Vector3(MathF.Sin(a) * 1.2f, 0, MathF.Cos(a) * 1.2f);
            GroundLine(frame, from, from * (ArenaRadius / 1.2f), 0.03f, 0xFF22222A);
        }

        float lit = (float)Math.Max(0, 1 - ((_time - _runesLit) / 0.6));
        float pulse = 0.35f + (0.15f * MathF.Sin(t * 1.3f)) + (0.6f * lit);
        GroundCircle(frame, RuneRadius, 0.05f, WithAlpha(0xFF6AF0FF, (byte)(255 * Math.Min(1, pulse))), additive: true);
        for (int rune = 0; rune < 24; rune++)
        {
            float a = (rune * MathF.Tau / 24) + (t * 0.05f);
            var at = new Vector3(MathF.Sin(a) * RuneRadius, 0, MathF.Cos(a) * RuneRadius);
            var side = new Vector3(MathF.Cos(a), 0, -MathF.Sin(a)) * 0.12f;
            GroundLine(frame, at - side, at + side + new Vector3(0, 0, 0.05f), 0.035f,
                       WithAlpha(rune % 2 == 0 ? 0xFFB07CFF : 0xFF6AF0FF, (byte)(255 * Math.Min(1, pulse + 0.1f))), additive: true);
        }

        for (int i = 0; i <= _guards.Length; i++)
        {
            var fighter = i < _guards.Length ? _guards[i] : _ninja;
            var pose = fighter.PoseAt(_time);
            GroundGlow(frame, fighter.Position, 0.55f - (Math.Max(0, pose.Lift) * 0.3f), 0x90000000);
        }

        foreach (var lantern in _lanterns)
        {
            Line3(frame, lantern, lantern + new Vector3(0, 0.55f, 0), 0.16f, 0xFF6A665E);
            var light = lantern + new Vector3(0, 0.7f, 0);
            Ball3(frame, light, 0.16f, 0xFFFFB45A, 0);
            Glow3(frame, light, 0.9f, 0x66FF9A3C, additive: true);
            Line3(frame, light + new Vector3(-0.2f, 0.18f, 0), light + new Vector3(0.2f, 0.18f, 0), 0.08f, 0xFF7A766C);
        }
    }

    /// <summary>A torii in the valley below, at the foot of the path up the hill, its paper lanterns
    /// swaying: a landmark the camera sees go round, never one it passes through.</summary>
    private void ComposeTorii(NinjaFrame frame)
    {
        var gate = Torii;
        if (BehindHill(gate + new Vector3(0, 7.4f, 0)))
            return;
        var across = new Vector3(0.8f, 0, 0.6f);
        const uint Red = 0xFFB22A28;
        Line3(frame, gate - (across * 2.6f), gate - (across * 2.6f) + new Vector3(0, 6.4f, 0), 0.44f, Red, hazy: true);
        Line3(frame, gate + (across * 2.6f), gate + (across * 2.6f) + new Vector3(0, 6.4f, 0), 0.44f, Red, hazy: true);
        Line3(frame, gate - (across * 3.8f) + new Vector3(0, 6.5f, 0), gate + (across * 3.8f) + new Vector3(0, 6.7f, 0), 0.48f, 0xFF2A1A1A, hazy: true);
        Line3(frame, gate - (across * 3.2f) + new Vector3(0, 5.4f, 0), gate + (across * 3.2f) + new Vector3(0, 5.4f, 0), 0.32f, Red, hazy: true);
        for (int i = -1; i <= 1; i++)
        {
            float sway = MathF.Sin(((float)_time * 1.7f) + i) * 0.12f;
            var lantern = gate + (across * (i * 1.8f)) + new Vector3(sway, 4.5f, 0);
            Line3(frame, lantern + new Vector3(-sway, 0.8f, 0), lantern, 0.03f, 0xFF201010, hazy: true);
            Ball3(frame, lantern, 0.34f, 0xFFE0402A, 0.25f, hazy: true);
            Glow3(frame, lantern, 1.6f, 0x99FF5A30, additive: true);
        }
    }

    private static readonly Vector3 Torii = OnGround(-15.5f, 19.5f);

    /// <summary>The valley: lanes winding between the fields, hedges along them, round trees and
    /// pines - cherries in blossom among them - in the wind, farmhouses with their windows lit, and
    /// the pagoda on its far hill. All of it fades into the evening haze with distance.</summary>
    private void ComposeCountryside(NinjaFrame frame, float t, Vector3 wind)
    {
        foreach (var lane in _lanes)
        {
            bool hidden = BehindHill(lane[0]);
            for (int i = 1; i < lane.Length; i++)
            {
                bool next = BehindHill(lane[i]);
                if (!hidden || !next)
                    Line3(frame, lane[i - 1], lane[i], 0.45f, 0xFF5E4C3C, hazy: true);
                hidden = next;
            }
        }
        foreach (var hedge in _hedges)
        {
            if (!BehindHill(hedge + new Vector3(0, 0.6f, 0)))
                Ball3(frame, hedge, 0.55f, 0xFF1C3A20, 0.1f, hazy: true);
        }

        foreach (var tree in _trees)
        {
            if (BehindHill(tree.At + new Vector3(0, tree.Height + tree.Width, 0)))
                continue;
            float sway = MathF.Sin((t * 1.3f) + tree.Phase) * 0.1f;
            var top = tree.At + new Vector3(0, tree.Height, 0) + (wind * (0.15f + sway));
            Line3(frame, tree.At, tree.At + new Vector3(0, tree.Height * 0.7f, 0), tree.Pine ? 0.14f : 0.2f, 0xFF3A2A20, hazy: true);
            if (tree.Pine)
            {
                // Tiers of needles, narrowing to the top.
                for (int tier = 0; tier < 4; tier++)
                {
                    float y = tree.Height * (0.35f + (tier * 0.17f)), w = tree.Width * (0.9f - (tier * 0.2f));
                    var c = tree.At + new Vector3(0, y, 0) + (wind * (0.05f + (tier * 0.04f)));
                    Cone3(frame, c, c + new Vector3(0, tree.Height * 0.32f, 0), w, Shaded(tree.Leaves, tier * 0.06f));
                }
            }
            else
            {
                // A round crown in clumps, lit by the low sun on one side.
                for (int c = 0; c < 5; c++)
                {
                    float a = tree.Phase + (c * 1.3f);
                    var at = top + new Vector3(MathF.Sin(a) * tree.Width * 0.5f, -c * 0.25f, MathF.Cos(a) * tree.Width * 0.5f)
                             + (wind * MathF.Sin((t * 2.1f) + a) * 0.08f);
                    float lightSide = Vector3.Dot(Vector3.Normalize(at - top + new Vector3(0, 0.3f, 0)), NinjaMesh.Sun);
                    Ball3(frame, at, tree.Width * (0.6f - (c * 0.05f)), Shaded(tree.Leaves, lightSide * 0.18f), 0.1f, hazy: true);
                }
            }
        }

        foreach (var house in _houses)
            ComposeHouse(frame, house, t);
        ComposeTorii(frame);

        // The pagoda on its far hill, its windows lit.
        float floor = Pagoda.Y;
        for (int storey = 0; storey < 4; storey++)
        {
            float half = 2.2f - (storey * 0.4f), height = 2.2f - (storey * 0.25f);
            Box3(frame, Pagoda + new Vector3(0, floor - Pagoda.Y, 0), 0, half, half, height, 0xFF1E1820, 0xFF2A2026);
            Glow3(frame, Pagoda + new Vector3(0, floor - Pagoda.Y + (height * 0.5f), 0), 1.4f, 0x88FFA850, additive: true);
            floor += height;
            Roof3(frame, Pagoda + new Vector3(0, floor - Pagoda.Y, 0), 0, half + 1.2f, half + 1.2f, 0.9f, 0xFF2A2026);
            floor += 0.5f;
        }
    }

    /// <summary>A farmhouse: plaster walls with dark timbers, a deep hipped roof, a lit window.</summary>
    private void ComposeHouse(NinjaFrame frame, House house, float t)
    {
        if (BehindHill(house.At + new Vector3(0, 2.9f, 0)))
            return;
        Box3(frame, house.At, house.Yaw, house.Width / 2, house.Depth / 2, 1.5f, 0xFF8A8478, 0xFF4A4038);
        Roof3(frame, house.At + new Vector3(0, 1.5f, 0), house.Yaw, (house.Width / 2) + 0.5f, (house.Depth / 2) + 0.5f, 1.4f, 0xFF3A2E2A);
        var facing = new Vector3(MathF.Sin(house.Yaw), 0, MathF.Cos(house.Yaw));
        float flicker = 0.85f + (0.15f * MathF.Sin((t * 3) + house.At.X));
        Glow3(frame, house.At + (facing * ((house.Depth / 2) + 0.05f)) + new Vector3(0, 0.75f, 0), 0.9f, WithAlpha(0xFFFFB050, (byte)(200 * flicker)), additive: true);
    }

    /// <summary>A box standing on the ground, turned by <paramref name="yaw"/>: its four walls.</summary>
    private void Box3(NinjaFrame frame, Vector3 at, float yaw, float halfWidth, float halfDepth, float height, uint lit, uint shade)
    {
        var x = new Vector3(MathF.Cos(yaw), 0, -MathF.Sin(yaw)) * halfWidth;
        var z = new Vector3(MathF.Sin(yaw), 0, MathF.Cos(yaw)) * halfDepth;
        var up = new Vector3(0, height, 0);
        Span<Vector3> corners = [at - x - z, at + x - z, at + x + z, at - x + z];
        for (int side = 0; side < 4; side++)
        {
            var a = corners[side];
            var b = corners[(side + 1) % 4];
            var outward = Vector3.Normalize(Vector3.Cross(b - a, Vector3.UnitY));
            _quad[0] = a;
            _quad[1] = b;
            _quad[2] = b + up;
            _quad[3] = a + up;
            Poly3(frame, _quad, Vector3.Dot(outward, NinjaMesh.Sun) > 0 ? lit : shade);
        }
    }

    /// <summary>A hipped roof over a box: four slopes up to a ridge, the eaves overhanging.</summary>
    private void Roof3(NinjaFrame frame, Vector3 eaves, float yaw, float halfWidth, float halfDepth, float rise, uint colour)
    {
        var x = new Vector3(MathF.Cos(yaw), 0, -MathF.Sin(yaw));
        var z = new Vector3(MathF.Sin(yaw), 0, MathF.Cos(yaw));
        Span<Vector3> corners = [eaves - (x * halfWidth) - (z * halfDepth), eaves + (x * halfWidth) - (z * halfDepth),
                                 eaves + (x * halfWidth) + (z * halfDepth), eaves - (x * halfWidth) + (z * halfDepth)];
        float ridge = MathF.Max(0, halfWidth - halfDepth);
        var top = eaves + new Vector3(0, rise, 0);
        Span<Vector3> ridgeEnds = [top - (x * ridge), top + (x * ridge), top + (x * ridge), top - (x * ridge)];
        for (int side = 0; side < 4; side++)
        {
            var a = corners[side];
            var b = corners[(side + 1) % 4];
            var outward = Vector3.Normalize(Vector3.Cross(b - a, Vector3.UnitY));
            _quad[0] = a;
            _quad[1] = b;
            _quad[2] = ridgeEnds[(side + 1) % 4];
            _quad[3] = ridgeEnds[side];
            Poly3(frame, _quad, Shaded(colour, Vector3.Dot(outward, NinjaMesh.Sun) * 0.12f));
        }
    }

    private readonly Vector3[] _quad = new Vector3[4];
    private readonly Vector3[] _cone = new Vector3[3];

    /// <summary>A cone seen from the side: a triangle from its base, as wide as it, up to its tip.</summary>
    private void Cone3(NinjaFrame frame, Vector3 base_, Vector3 tip, float radius, uint colour)
    {
        _cone[0] = base_ - (_right * radius);
        _cone[1] = base_ + (_right * radius);
        _cone[2] = tip;
        Poly3(frame, _cone, colour);
    }

    /// <summary>A fighter as a lit mesh: the ninja in black - hood and steel mask, its eyes aglow,
    /// armoured shoulders, a leather strap across its chest, bare arms in steel bracers, a red sash,
    /// knee guards, wrapped shins, a sword on its back; a guard in a lacquered helmet and breastplate
    /// in its colour, its armoured skirt, its staff.</summary>
    private void ComposeBody(NinjaFrame frame, Fighter fighter, Fighter.Pose pose, int slot)
    {
        var body = fighter.Skeleton(pose);
        var look = fighter.Look;
        var up = Vector3.Normalize(body.Neck - body.Hip);
        var facing = Vector3.Normalize(Vector3.Cross(body.Across, up));
        bool ninja = fighter == _ninja;
        var m = _body;
        m.Begin();

        uint cloth = look.Cloth, leather = look.Wrap, metal = ninja ? 0xFF4A4C54 : 0xFF5A5040, skin = 0xFFB4845E;
        var frontShoulder = body.Shoulder + (body.Across * 0.19f);
        var backShoulder = body.Shoulder - (body.Across * 0.19f);
        var frontHip = body.Hip + (body.Across * 0.11f);
        var backHip = body.Hip - (body.Across * 0.11f);

        // Legs: trousers, knee guards, wrapped or armoured shins, feet.
        ReadOnlySpan<(Vector3, Vector3, Vector3)> legs = [(frontHip, body.FrontKnee, body.FrontFoot), (backHip, body.BackKnee, body.BackFoot)];
        foreach (var (hip, knee, foot) in legs)
        {
            m.Tube(hip, knee, 0.1f, 0.075f, cloth, NinjaMesh.Stuff.Cloth);
            m.Tube(knee, foot, 0.068f, 0.05f, ninja ? leather : look.Sash, ninja ? NinjaMesh.Stuff.Cloth : NinjaMesh.Stuff.Lacquer);
            var shin = Vector3.Normalize(foot - knee);
            m.Ball(knee + (Vector3.Normalize(Vector3.Cross(body.Across, shin)) * -0.035f), 0.065f, metal, NinjaMesh.Stuff.Metal);
            var toe = foot + (Vector3.Normalize(facing - (shin * Vector3.Dot(facing, shin))) * 0.17f);
            m.Tube(foot, toe, 0.05f, 0.04f, 0xFF15161A, NinjaMesh.Stuff.Cloth);
        }

        // The body: the torso, a vest or breastplate over it, the belt and sash.
        m.Tube(body.Waist, body.Neck, 0.15f, 0.17f, cloth, NinjaMesh.Stuff.Cloth, body.Across, 1.3f);
        var chestLow = Vector3.Lerp(body.Waist, body.Neck, 0.3f);
        var chestHigh = Vector3.Lerp(body.Waist, body.Neck, 0.86f);
        if (ninja)
        {
            m.Tube(chestLow, chestHigh, 0.163f, 0.183f, leather, NinjaMesh.Stuff.Lacquer, body.Across, 1.27f);
            // A strap across the chest, studded.
            var strapFrom = frontShoulder + (up * 0.03f) + (facing * 0.06f);
            var strapTo = backHip + (facing * 0.1f) + (up * 0.12f);
            m.Tube(strapFrom, strapTo, 0.028f, 0.028f, 0xFF2E2420, NinjaMesh.Stuff.Lacquer);
            for (int s = 1; s <= 3; s++)
                m.Ball(Vector3.Lerp(strapFrom, strapTo, s / 4f) + (facing * 0.025f), 0.02f, 0xFF9A9AA4, NinjaMesh.Stuff.Metal);
        }
        else
        {
            m.Tube(chestLow, chestHigh, 0.175f, 0.195f, look.Sash, NinjaMesh.Stuff.Lacquer, body.Across, 1.25f);
            // The armoured skirt, flaring.
            m.Tube(body.Waist + (up * 0.03f), body.Waist - (up * 0.3f), 0.18f, 0.26f, look.Sash, NinjaMesh.Stuff.Lacquer, body.Across, 1.2f);
        }
        m.Tube(body.Waist - (up * 0.05f), body.Waist + (up * 0.06f), 0.172f, 0.172f, ninja ? 0xFF8E1A20 : look.Helmet, NinjaMesh.Stuff.Cloth, body.Across, 1.3f);
        m.Ball(body.Waist + (facing * 0.165f), new Vector3(0.05f, 0, 0) with { X = body.Across.X * 0.05f, Z = body.Across.Z * 0.05f }, up * 0.045f, facing * 0.02f, 0xFFA8A8B0, NinjaMesh.Stuff.Metal);

        // Shoulders: armour pads.
        ReadOnlySpan<(Vector3, float)> pads = [(frontShoulder, 1f), (backShoulder, -1f)];
        foreach (var (shoulder, side) in pads)
            m.Ball(shoulder + (up * 0.04f), body.Across * (0.12f * side), up * 0.075f, facing * 0.11f, metal, NinjaMesh.Stuff.Metal);

        // Arms: bare and strong for the ninja, armoured steel bracers, fists.
        ReadOnlySpan<(Vector3, Vector3, Vector3)> arms = [(frontShoulder, body.FrontElbow, body.FrontHand), (backShoulder, body.BackElbow, body.BackHand)];
        foreach (var (shoulder, elbow, hand) in arms)
        {
            m.Tube(shoulder, elbow, 0.068f, 0.056f, ninja ? skin : cloth, ninja ? NinjaMesh.Stuff.Skin : NinjaMesh.Stuff.Cloth);
            m.Tube(elbow, hand, 0.06f, 0.05f, metal, NinjaMesh.Stuff.Metal);
            m.Ball(hand, 0.056f, 0xFF1C1E22, NinjaMesh.Stuff.Cloth);
        }

        // Neck and head: the ninja's hood, its steel mask and glowing eyes; a guard's face and helmet.
        m.Tube(body.Neck - (up * 0.03f), body.Head - (up * 0.08f), 0.06f, 0.055f, cloth, NinjaMesh.Stuff.Cloth);
        m.Ball(body.Head, body.Across * 0.11f, up * 0.13f, facing * 0.12f, ninja ? cloth : 0xFFB08A6A, ninja ? NinjaMesh.Stuff.Cloth : NinjaMesh.Stuff.Skin);
        if (ninja)
        {
            m.Ball(body.Head + (facing * 0.055f) - (up * 0.035f), body.Across * 0.095f, up * 0.065f, facing * 0.08f, metal, NinjaMesh.Stuff.Metal);
            for (float side = -1; side <= 1; side += 2)
                m.Ball(body.Head + (facing * 0.105f) + (up * 0.025f) + (body.Across * (0.038f * side)), 0.018f, look.Eyes, NinjaMesh.Stuff.Glow);
            // The sword on its back.
            var hilt = body.Shoulder - (facing * 0.15f) + (body.Across * 0.16f) + (up * 0.2f);
            var tip = body.Waist - (facing * 0.16f) - (body.Across * 0.24f) - (up * 0.25f);
            m.Tube(hilt, tip, 0.024f, 0.022f, 0xFF2A1416, NinjaMesh.Stuff.Lacquer);
            m.Tube(hilt, hilt + (Vector3.Normalize(hilt - tip) * 0.2f), 0.02f, 0.02f, 0xFFC8B070, NinjaMesh.Stuff.Metal);
        }
        else
        {
            m.Ball(body.Head + (up * 0.06f), body.Across * 0.16f, up * 0.1f, facing * 0.16f, look.Helmet, NinjaMesh.Stuff.Lacquer);
            m.Tube(body.Head + (up * 0.14f) - (body.Across * 0.12f), body.Head + (up * 0.24f) - (body.Across * 0.16f), 0.012f, 0.008f, 0xFFD8B050, NinjaMesh.Stuff.Metal);
            m.Tube(body.Head + (up * 0.14f) + (body.Across * 0.12f), body.Head + (up * 0.24f) + (body.Across * 0.16f), 0.012f, 0.008f, 0xFFD8B050, NinjaMesh.Stuff.Metal);
            if (look.Staff != 0)
            {
                var along = Vector3.Normalize(body.FrontHand - body.BackHand + new Vector3(0, 0.01f, 0));
                m.Tube(body.BackHand - (along * 0.5f), body.FrontHand + (along * 0.6f), 0.025f, 0.025f, look.Staff, NinjaMesh.Stuff.Lacquer);
            }
        }
        if (pose.Sword > 0.05f && ninja)
        {
            var blade = body.FrontHand + (Vector3.Normalize(body.FrontHand - body.FrontElbow) * 0.95f * pose.Sword);
            m.Tube(body.FrontHand, blade, 0.015f, 0.006f, 0xFFE8F0FF, NinjaMesh.Stuff.Metal);
            Line3(frame, body.FrontHand, blade, 0.12f, 0x307AC8FF, additive: true, blur: 0.4f);
        }
        m.Paint(frame, slot, _project, _eye, 1);
    }

    /// <summary>The ghost of a fast move: the ninja a moment ago, in one translucent blue.</summary>
    private void ComposeGhost(NinjaFrame frame, Fighter.Pose pose, float alpha)
    {
        var body = _ninja.Skeleton(pose);
        uint tint = WithAlpha(0xFF7AA8FF, (byte)(255 * alpha));
        Line3(frame, body.Hip, body.BackKnee, 0.18f, tint);
        Line3(frame, body.BackKnee, body.BackFoot, 0.14f, tint);
        Line3(frame, body.Hip, body.FrontKnee, 0.18f, tint);
        Line3(frame, body.FrontKnee, body.FrontFoot, 0.14f, tint);
        Line3(frame, body.Hip, body.Neck, 0.32f, tint);
        Line3(frame, body.Shoulder, body.BackElbow, 0.13f, tint);
        Line3(frame, body.BackElbow, body.BackHand, 0.11f, tint);
        Line3(frame, body.Shoulder, body.FrontElbow, 0.13f, tint);
        Line3(frame, body.FrontElbow, body.FrontHand, 0.11f, tint);
        Ball3(frame, body.Head, 0.13f, tint, 0);
    }

    private void ComposeTrails(NinjaFrame frame)
    {
        if (_ninja.Move is null)
            return;
        for (int limb = 0; limb < 4; limb++)
        {
            for (int k = 1; k < _trailTime.Length - 1; k++)
            {
                int now = (_trailAt - k + 1 + _trailTime.Length) % _trailTime.Length, then = (now - 1 + _trailTime.Length) % _trailTime.Length;
                double age = _time - _trailTime[then];
                if (age > 0.18 || _trailTime[then] < _ninja.Since)
                    break;
                var a = _trail[limb, now];
                var b = _trail[limb, then];
                if (Vector3.DistanceSquared(a, b) < 0.0004f)
                    continue;
                byte fade = (byte)(150 * (1 - (age / 0.18)));
                Line3(frame, a, b, 0.09f * (1 - (k * 0.07f)), WithAlpha(_ninja.Colour, fade), additive: true, blur: 0.4f);
            }
        }
    }

    private void ComposeBursts(NinjaFrame frame)
    {
        foreach (var burst in _bursts)
        {
            double age = _time - burst.Time;
            if (age < 0 || age > 0.5)
                continue;
            float fade = (float)(1 - (age / 0.5));
            if (burst.Kick)
            {
                // A ring of force running out over the ground, and a warm flash.
                float radius = 0.3f + ((float)age * 4.5f);
                GroundRing(frame, burst.At with { Y = 0 }, radius, 0.08f * fade, WithAlpha(burst.Colour, (byte)(230 * fade)));
                if (age < 0.12)
                    frame.Flash(WithAlpha(0xFFFFC890, (byte)(70 * burst.Strength * (1 - (age / 0.12)))));
            }
            else
            {
                // White light and streaks bursting out from where the blow lands.
                if (age < 0.15)
                    frame.Flash(WithAlpha(0xFFDCEEFF, (byte)(110 * burst.Strength * (1 - (age / 0.15)))));
                if (Project(burst.At, out float x, out float y, out _, out float scale))
                {
                    for (int ray = 0; ray < 14; ray++)
                    {
                        float a = ray * MathF.Tau / 14;
                        float inner = scale * (0.15f + ((float)age * 2.5f)), outer = inner + (scale * 0.5f * fade);
                        frame.Line(1e7f, x + (MathF.Cos(a) * inner), y + (MathF.Sin(a) * inner), x + (MathF.Cos(a) * outer), y + (MathF.Sin(a) * outer),
                                   MathF.Max(1, scale * 0.03f), WithAlpha(burst.Colour, (byte)(220 * fade)), additive: true, blur: 1);
                    }
                }
            }
            Glow3(frame, burst.At, 0.5f + ((float)age * 2), WithAlpha(burst.Colour, (byte)(160 * fade)), additive: true, overlay: true);
        }
    }

    // ------------------------------------------------------------------ shapes in the world

    /// <summary>Faded into the evening haze by its distance from the camera, as the land is.</summary>
    private uint Hazed(uint colour, float depth)
    {
        float haze = depth < 14 ? 0 : (1 - MathF.Exp(-(depth - 14) * 0.03f)) * 0.85f;
        if (haze <= 0)
            return colour;
        uint Channel(int shift, float to)
        {
            float c = ((colour >> shift) & 0xFF) / 255f;
            return (uint)Math.Clamp((c + ((to - c) * haze)) * 255, 0, 255) << shift;
        }
        return (colour & 0xFF000000) | Channel(16, NinjaMesh.HazeR) | Channel(8, NinjaMesh.HazeG) | Channel(0, NinjaMesh.HazeB);
    }

    private void Line3(NinjaFrame frame, Vector3 a, Vector3 b, float width, uint colour, float bias = 0, bool additive = false, float blur = 0, bool hazy = false)
    {
        if (!Project(a, out float ax, out float ay, out float ad, out float sa) || !Project(b, out float bx, out float by, out float bd, out float sb))
            return;
        float scale = (sa + sb) / 2, depth = (ad + bd) / 2;
        float half = width * scale;
        if (MathF.Max(ax, bx) + half < 0 || MathF.Max(ay, by) + half < 0 || MathF.Min(ax, bx) - half > _width || MathF.Min(ay, by) - half > _height)
            return;
        colour = NearFaded(colour, MathF.Min(ad, bd));
        if (colour >> 24 == 0)
            return;
        frame.Line(-(depth + bias), ax, ay, bx, by, MathF.Max(0.6f, width * scale), hazy ? Hazed(colour, depth) : colour, additive, blur * width * scale);
    }

    /// <summary>A ball: a disc, its edge softened, and a lighter disc where the sun lights it.</summary>
    private void Ball3(NinjaFrame frame, Vector3 at, float radius, uint colour, float shine, bool hazy = false)
    {
        if (!Project(at, out float x, out float y, out float depth, out float scale))
            return;
        float r = MathF.Max(0.6f, radius * scale);
        if (OffView(x, y, r * 1.15f))
            return;
        var c = NearFaded(hazy ? Hazed(colour, depth) : colour, depth);
        if (c >> 24 == 0)
            return;
        frame.Disc(-depth, x, y, r * 1.15f, WithAlpha(c, (byte)((c >> 24) / 2)), 0, false);
        frame.Disc(-depth + 0.0005f, x, y, r, c, 0, false);
        if (shine > 0)
            frame.Disc(-depth + 0.001f, x + (r * 0.3f), y - (r * 0.3f), r * 0.55f, NearFaded(hazy ? Hazed(Shaded(colour, shine), depth) : Shaded(colour, shine), depth), 0, false);
    }

    /// <summary>A light: a faint halo and a bright core over one another.</summary>
    private void Glow3(NinjaFrame frame, Vector3 at, float radius, uint colour, bool additive = false, bool overlay = false)
    {
        if (!Project(at, out float x, out float y, out float depth, out float scale))
            return;
        float r = MathF.Max(1, radius * scale);
        if (OffView(x, y, r))
            return;
        colour = NearFaded(colour, depth);
        byte alpha = (byte)(colour >> 24);
        if (alpha == 0)
            return;
        float key = overlay ? 1e7f : -depth;
        frame.Disc(key, x, y, r, WithAlpha(colour, (byte)(alpha / 4)), 0, additive);
        frame.Disc(key + 0.0001f, x, y, r * 0.55f, WithAlpha(colour, (byte)(alpha / 3)), 0, additive);
        frame.Disc(key + 0.0002f, x, y, r * 0.25f, colour, 0, additive);
    }

    /// <summary>A flat shape standing in the world, at the depth of its middle, hazed with it.</summary>
    private void Poly3(NinjaFrame frame, Vector3[] corners, uint colour)
    {
        frame.BeginPolygon();
        float depth = 0;
        foreach (var corner in corners)
        {
            if (!Project(corner, out float x, out float y, out float d, out _))
            {
                frame.EndPolygon(true, 0, 0, false, 0, key: 0);
                return;
            }
            frame.Point(x, y);
            depth += d;
        }
        depth /= corners.Length;
        frame.EndPolygon(true, 0, Hazed(colour, depth), false, 0, key: -depth);
    }

    private void GroundDisc(NinjaFrame frame, float radius, uint colour) => GroundRing(frame, Vector3.Zero, radius, 0, colour, fill: true);

    private void GroundCircle(NinjaFrame frame, float radius, float width, uint colour, bool additive = false) =>
        GroundRing(frame, Vector3.Zero, radius, width, colour, additive: additive);

    /// <summary>A circle on the hilltop, filled or a ring: a polygon of its points as the camera sees them.</summary>
    private void GroundRing(NinjaFrame frame, Vector3 centre, float radius, float width, uint colour, bool fill = false, bool additive = false)
    {
        const int Sides = 56;
        frame.BeginPolygon();
        float scale = 0;
        for (int i = 0; i <= Sides; i++)
        {
            float a = i * MathF.Tau / Sides;
            if (Project(centre + new Vector3(MathF.Sin(a) * radius, 0, MathF.Cos(a) * radius), out float x, out float y, out _, out float s))
            {
                frame.Point(x, y);
                scale = MathF.Max(scale, s);
            }
        }
        frame.EndPolygon(fill, MathF.Max(0.6f, width * scale), colour, additive, 0);
    }

    private void GroundLine(NinjaFrame frame, Vector3 a, Vector3 b, float width, uint colour, bool additive = false)
    {
        if (Project(a, out float ax, out float ay, out _, out float sa) && Project(b, out float bx, out float by, out _, out float sb))
            frame.Line(NinjaFrame.Ground, ax, ay, bx, by, MathF.Max(0.5f, width * (sa + sb) / 2), colour, additive, 0);
    }

    /// <summary>A soft patch on the ground, a shadow: two ellipses, the smaller over the larger.</summary>
    private void GroundGlow(NinjaFrame frame, Vector3 at, float radius, uint colour)
    {
        if (!Project(at, out float x, out float y, out _, out float scale))
            return;
        var faint = WithAlpha(colour, (byte)((colour >> 24) / 2));
        for (int ring = 0; ring < 2; ring++)
        {
            float r = radius * scale * (1 - (ring * 0.4f));
            frame.Ellipse(NinjaFrame.Ground, x, y, r, r * 0.45f, faint, 0);
        }
    }

    public static uint WithAlpha(uint colour, byte alpha) => (colour & 0x00FFFFFF) | ((uint)alpha << 24);

    /// <summary>A colour lightened towards white by <paramref name="by"/>, or darkened when it is less than nothing.</summary>
    private static uint Shaded(uint colour, float by)
    {
        uint Channel(int shift)
        {
            float c = (colour >> shift) & 0xFF;
            return (uint)Math.Clamp(by >= 0 ? c + ((255 - c) * by) : c * (1 + by), 0, 255) << shift;
        }
        return (colour & 0xFF000000) | Channel(16) | Channel(8) | Channel(0);
    }

    private static uint Mix(uint a, uint b, float t)
    {
        t = Math.Clamp(t, 0, 1);
        uint Channel(int shift) => (uint)((((a >> shift) & 0xFF) * (1 - t)) + (((b >> shift) & 0xFF) * t)) << shift;
        return 0xFF000000 | Channel(16) | Channel(8) | Channel(0);
    }
}
