using System.Numerics;
using System.Text;

namespace MagPlaySid.Player;

/// <summary>
/// The two Dire Guys as modelled in Blender (3d-objects/DireGuys_new.blend, exported by
/// 3d-objects/export-direguys.py into DireGuys.bin): each a set of rigid parts - a torso, a head, upper
/// arms, forearms, gloves, legs, a hat, a cigar - each part's triangles with their normals and
/// colours, and the joint it turns about, hung one from another. The blue guy is stocky, in
/// dungarees, with a cap and a cigar; the red guy tall and thin, in a striped shirt and a red hat.
/// Both stand with their feet at the origin, facing +Z, Y up.
/// </summary>
internal sealed class DireModel
{
    /// <summary>What a part does in a dance: which of a pose's angles turn it.</summary>
    public enum Role : byte { Body, Spine, Head, UpperArm, Forearm, Hand, Thigh, Shin, Rigid }

    public sealed record Part(string Name, int Guy, int Parent, Vector3 Joint, Role Role, int Side, Vector3[] Corners, Vector3[] Normals, uint[] Colours);

    /// <summary>The parts of both guys, every part after the one it hangs from.</summary>
    public IReadOnlyList<Part> Parts { get; }

    public static DireModel Instance => _instance ??= Load();

    private static DireModel? _instance;

    private DireModel(List<Part> parts) => Parts = parts;

    private static DireModel Load()
    {
        using var stream = typeof(DireModel).Assembly.GetManifestResourceStream("DireGuys.bin")
                           ?? throw new InvalidOperationException("The Dire Guys' model is missing.");
        using var reader = new BinaryReader(stream);
        if (Encoding.ASCII.GetString(reader.ReadBytes(4)) != "DIRE")
            throw new InvalidDataException("Not the Dire Guys' model.");
        int count = reader.ReadInt32();
        var raw = new List<(string Name, int Guy, int Parent, Vector3 Origin, Vector3[] Corners, Vector3[] Normals, uint[] Colours)>();
        for (int i = 0; i < count; i++)
        {
            string name = Encoding.UTF8.GetString(reader.ReadBytes(reader.ReadByte()));
            int guy = reader.ReadInt32(), parent = reader.ReadInt32();
            var origin = new Vector3(reader.ReadSingle(), reader.ReadSingle(), reader.ReadSingle());
            int triangles = reader.ReadInt32();
            var corners = new Vector3[triangles * 3];
            var normals = new Vector3[triangles];
            var colours = new uint[triangles];
            for (int t = 0; t < triangles; t++)
            {
                normals[t] = new Vector3(reader.ReadSingle(), reader.ReadSingle(), reader.ReadSingle());
                colours[t] = reader.ReadUInt32();
                for (int c = 0; c < 3; c++)
                    corners[(3 * t) + c] = new Vector3(reader.ReadSingle(), reader.ReadSingle(), reader.ReadSingle());
            }
            raw.Add((name, guy, parent, origin, corners, normals, colours));
        }

        // Each guy stood with his feet's middle at the origin.
        var feet = new Vector3[2];
        for (int guy = 0; guy < 2; guy++)
        {
            var points = raw.Where(p => p.Guy == guy).SelectMany(p => p.Corners).ToList();
            feet[guy] = new Vector3((points.Min(p => p.X) + points.Max(p => p.X)) / 2, points.Min(p => p.Y), (points.Min(p => p.Z) + points.Max(p => p.Z)) / 2);
        }

        // The rig: what each part is in a dance, and what it hangs from - in Blender the blue guy's
        // head and arms hang from nothing but him, here from his torso, so they go with it.
        var byName = raw.Select((p, i) => (p.Name, i)).ToDictionary(p => p.Name, p => p.i);
        string? HangsFrom(string name) => name switch
        {
            "Head.001" or "ArmLeftUpper" or "ArmRight.001" => "Torso",
            _ => null,
        };
        (Role Role, int Side) RoleOf(string name) => name switch
        {
            "Torso" or "Upperbody" => (Role.Spine, 0),
            "Head.001" or "Head" => (Role.Head, 0),
            "ArmLeftUpper" or "Armleft" => (Role.UpperArm, 1),
            "ArmRight.001" or "ArmRight" => (Role.UpperArm, -1),
            "ArmLeftLower" or "Armlower01" => (Role.Forearm, 1),
            "ArmRightLower" or "Armlower01.001" => (Role.Forearm, -1),
            "Glovejoint" or "Glovejoi01" => (Role.Hand, 1),
            "Glovejoint.001" or "Glovejoi01.001" => (Role.Hand, -1),
            "Legleft" or "LegLeft" => (Role.Thigh, 1),
            "Legright" or "LegRight" => (Role.Thigh, -1),
            "Leglorleft" => (Role.Shin, 1),
            "Legrilower" => (Role.Shin, -1),
            _ => (Role.Rigid, 0),
        };
        // The joints Blender's origins do not put where a body bends: the blue guy's torso turns
        // at his hips, the red guy's head at the top of his neck's base.
        Vector3 JointOf(string name, Vector3 origin) => name switch
        {
            "Torso" => origin with { Y = 0.6f },
            "Head" => origin with { Y = 1.64f },
            _ => origin,
        };

        // In an order where every part comes after the one it hangs from.
        var parents = raw.Select(p => HangsFrom(p.Name) is { } other ? byName[other] : p.Parent).ToArray();
        var order = new List<int>();
        var placed = new bool[raw.Count];
        while (order.Count < raw.Count)
            for (int i = 0; i < raw.Count; i++)
                if (!placed[i] && (parents[i] < 0 || placed[parents[i]]))
                {
                    placed[i] = true;
                    order.Add(i);
                }

        var parts = new List<Part>();
        var index = new int[raw.Count];
        foreach (int i in order)
        {
            var p = raw[i];
            var shift = feet[p.Guy];
            var (role, side) = RoleOf(p.Name);
            index[i] = parts.Count;
            parts.Add(new Part(p.Name, p.Guy, parents[i] < 0 ? -1 : index[parents[i]], JointOf(p.Name, p.Origin) - shift, role, side,
                Array.ConvertAll(p.Corners, c => c - shift), p.Normals, p.Colours));
        }
        return new DireModel(parts);
    }
}
