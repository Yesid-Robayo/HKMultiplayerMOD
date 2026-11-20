using UnityEngine;

namespace HKMultiplayer.Game.Players;

/// <summary>Cajas en el plano del juego (x e y), sin la profundidad.</summary>
internal static class Bounds2D {
    /// <summary>
    /// Si dos cajas se tocan en x e y. <see cref="UnityEngine.Bounds.Intersects"/> mira también la z, y muchos objetos del
    /// juego tienen una z propia (<c>SetZ</c>, <c>SetZRandom</c>): con la caja de un avatar en z=0 no se tocaban nunca.
    /// </summary>
    public static bool Overlaps(UnityEngine.Bounds a, UnityEngine.Bounds b) =>
        a.min.x <= b.max.x && b.min.x <= a.max.x && a.min.y <= b.max.y && b.min.y <= a.max.y;
}
