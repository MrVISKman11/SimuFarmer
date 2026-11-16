using System.Collections.Generic;
using System.Numerics;

namespace SimuFarmer.Models
{
    public record struct WaypointNode(float X, float Y)
    {
        public Vector2 ToVector2() => new(X, Y);

        public static implicit operator Vector2(WaypointNode node) => new(node.X, node.Y);
        public static implicit operator WaypointNode(Vector2 v) => new(v.X, v.Y);
    }

    public class ArenaProfile
    {
        public string Name { get; set; } = "";
        public string AreaKey { get; set; } = "";
        public WaypointNode? MonolithPosition { get; set; }
        public WaypointNode? StashPosition { get; set; }
        public WaypointNode? LootCenterPosition { get; set; }
        public List<WaypointNode> CircuitWaypoints { get; set; } = new();

        public ArenaProfile() { }

        public ArenaProfile(string name, string areaKey)
        {
            Name = name;
            AreaKey = areaKey;
        }
    }
}
