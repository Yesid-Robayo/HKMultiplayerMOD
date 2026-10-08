using System.Text;
using UnityEngine;

namespace HKMultiplayer.Game.Scene;

/// <summary>
/// Identificador de un objeto de la sala igual en todos los PCs: huella de su ruta en la jerarquía (nombre de la escena
/// y, por cada nivel, nombre e índice entre sus hermanos). Crea memoria: usar al buscar objetos, no cada frame.
/// </summary>
internal static class ScenePaths {
    private static readonly StringBuilder Path = new();

    public static uint Hash(Transform transform) {
        Path.Length = 0;
        for (var t = transform; t != null; t = t.parent) {
            Path.Insert(0, t.GetSiblingIndex()).Insert(0, '#').Insert(0, t.name).Insert(0, '/');
        }

        Path.Insert(0, transform.gameObject.scene.name);
        var hash = 2166136261u;
        for (var i = 0; i < Path.Length; i++) {
            hash = (hash ^ Path[i]) * 16777619u;
        }

        return hash;
    }
}
