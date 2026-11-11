using System.IO;
using System.Text;

namespace AnomalyDonationBridge.AppTests;

internal static class StationaryMgunModelTests
{
    public static async Task RunAsync()
    {
        var root = Path.Combine(AppContext.BaseDirectory, "addon-package", "gamedata");
        var model = await File.ReadAllBytesAsync(Path.Combine(root, "meshes", "anomaly_donation_bridge", "stationary_mgun.ogf"));
        var chunks = Chunks(model);
        using var bones = Reader(chunks[13]);
        var count = bones.ReadUInt32();
        Check(count == 11, "Do not silently replace the verified native skeleton.");
        var names = new List<string>();
        for (var i = 0; i < count; i++) { names.Add(Z(bones)); _ = Z(bones); bones.BaseStream.Position += 60; }
        using var ik = Reader(chunks[16]);
        foreach (var name in names)
        {
            var version = ik.ReadUInt32(); _ = Z(ik);
            var shapeOffset = (int)ik.BaseStream.Position;
            var shape = ik.ReadUInt16(); var flags = ik.ReadUInt16();
            ik.BaseStream.Position += 108;
            var joint = ik.ReadUInt32(); ik.BaseStream.Position += 68 + (version > 0 ? 4 : 0) + 24;
            var mass = ik.ReadSingle(); ik.BaseStream.Position += 12;
            if (name is "bone_rotate_x" or "bone_rotate_y")
            {
                Check(shape is >= 1 and <= 3 && (flags & 4) == 0 && joint == 2 && mass > 0,
                    "Native ResetBoneCallbacks requires an actual physical aiming element: " + name);
                if (name == "bone_rotate_x")
                    Check(shape == 2 && BitConverter.ToSingle(chunks[16], shapeOffset + 76) > 0,
                        "Pitch-bone sphere is absent/invalid; this caused the sg1 null CPHElement crash.");
                if (name == "bone_rotate_y")
                {
                    var min = BitConverter.ToSingle(chunks[16], shapeOffset + 112 + 4 + 16);
                    var max = BitConverter.ToSingle(chunks[16], shapeOffset + 112 + 4 + 20);
                    Check(min == -MathF.PI && max == MathF.PI,
                        "Full horizontal traverse must cross the rear +/-180 seam without a restricted native arc.");
                }
            }
        }
        Check(ik.BaseStream.Position == ik.BaseStream.Length, "Unexpected IK serialization layout.");
        var userdata = Encoding.ASCII.GetString(chunks[17]);
        Check(userdata == "#include \"models\\weapons\\anomaly_donation_bridge_stationary_mgun.ltx\"\0", "Use private userdata, not the original include.");
        var textures = Chunks(chunks[9]).Values.SelectMany(child =>
        {
            var childChunks = Chunks(child);
            return childChunks.TryGetValue(2, out var texture) ? new[] { Encoding.ASCII.GetString(texture).Split('\0')[0] } : [];
        }).ToHashSet();
        Check(textures.SetEquals(new[] { "wpn\\wpn_pkm_mounted", "wpn\\wpn_pkm_lenta" }), "Unexpected/missing tripod texture dependencies.");
        var config = await File.ReadAllTextAsync(Path.Combine(root, "configs", "models", "weapons", "anomaly_donation_bridge_stationary_mgun.ltx"));
        foreach (var name in new[] { "bone_fire", "bone_actor", "bone_rotate_x", "bone_rotate_y", "bone_camera" })
            Check(names.Contains(name) && config.Contains("= " + name), "Invalid native model bone reference: " + name);
        Check(config.Contains("exit_position = 0, 0, -1.8"), "Exit must be behind, not at the gun's origin.");
        Check(config.Contains("cam_first = adb_stationary_mgun_first_cam") &&
            config.Contains("cam_chase = adb_stationary_mgun_chase_cam"), "Both native cameras must use private unclamped profiles.");
        var system = await File.ReadAllTextAsync(Path.Combine(root, "configs", "mod_system_anomaly_donation_bridge.ltx"));
        foreach (var (name, parent) in new[] { ("adb_stationary_mgun_first_cam", "mounted_weapon_cam"), ("adb_stationary_mgun_chase_cam", "actor_look_cam") })
        {
            var section = System.Text.RegularExpressions.Regex.Match(system,
                @"(?ms)^\[" + name + @"\]:" + parent + @"\r?\n(.*?)(?=^\[|\z)").Groups[1].Value;
            Check(section.Contains("lim_yaw = 0, 0") && !section.Contains("lim_pitch") && !section.Contains("rot_speed"),
                "Remove yaw clamp only, retaining inherited pitch/speed: " + name);
        }
        Check(!system.Contains("![mounted_weapon_cam]") && !system.Contains("![actor_look_cam]") &&
            !system.Contains("[mounted_weapon_cam]"), "Never alter stock or other mods' cameras globally.");
        foreach (var speed in new[] { 60, 90, 120 })
        {
            var suffix = speed.ToString("D3", System.Globalization.CultureInfo.InvariantCulture);
            var name = "stationary_mgun_r" + suffix;
            var profile = Chunks(await File.ReadAllBytesAsync(Path.Combine(root, "meshes", "anomaly_donation_bridge", name + ".ogf")));
            Check(profile.Keys.SequenceEqual(chunks.Keys), "A speed profile changed the native chunk layout.");
            foreach (var chunk in chunks.Where(c => c.Key != 17))
                Check(profile[chunk.Key].SequenceEqual(chunk.Value), "A speed profile changed geometry/texture/PH/bones: " + name);
            var ini = Encoding.ASCII.GetString(profile[17]);
            var expected = config.Replace("[camera]", $"rotate_x_speed = {speed}\nrotate_y_speed = {speed}\n\n[camera]") + '\0';
            Check(ini == expected, "Native userdata must specify the exact selected speed on both axes: " + name);
            Check(system.Contains($"[adb_{name}]:adb_stationary_mgun\nvisual = anomaly_donation_bridge\\{name}.ogf\nadb_rotation_speed_dps = {speed}".Replace("\n", "\r\n")) ||
                system.Contains($"[adb_{name}]:adb_stationary_mgun\nvisual = anomaly_donation_bridge\\{name}.ogf\nadb_rotation_speed_dps = {speed}"),
                "Speed section must select its own model before native PH creation: " + name);
        }
    }
    private static BinaryReader Reader(byte[] data) => new(new MemoryStream(data, false));
    private static Dictionary<uint, byte[]> Chunks(byte[] data)
    {
        using var reader = Reader(data); var result = new Dictionary<uint, byte[]>();
        while (reader.BaseStream.Position < data.Length)
        {
            var id = reader.ReadUInt32(); var size = reader.ReadUInt32();
            Check(size <= data.Length - reader.BaseStream.Position && !result.ContainsKey(id), "Malformed OGF chunk.");
            result.Add(id, reader.ReadBytes(checked((int)size)));
        }
        return result;
    }
    private static string Z(BinaryReader reader)
    {
        var bytes = new List<byte>(); byte b;
        while ((b = reader.ReadByte()) != 0) { bytes.Add(b); Check(bytes.Count < 256, "Invalid OGF string."); }
        return Encoding.ASCII.GetString(bytes.ToArray());
    }
    private static void Check(bool valid, string message) { if (!valid) throw new InvalidOperationException(message); }
}
