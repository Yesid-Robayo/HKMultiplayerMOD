using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using UnityEngine;
using HKMultiplayer.Util;

namespace HKMultiplayer.Game.Features;

/// <summary>
/// Arregla hojas de skins hechas para la versión anterior del juego (todas las de CustomKnight). En la versión de
/// Unity 6 algunos sprites del HUD se movieron dentro de su hoja (las máscaras de vida, el geo, el orbe de alma): en
/// el PNG de la skin esa zona tiene otra cosa o está vacía y se veían mal o no se veían.
/// <para>
/// La tabla <see cref="HudMoves"/> dice dónde estaba cada sprite en la hoja antigua. Se sacó con una skin que
/// conserva las siluetas originales (Rick Knight): cada sprite se encontró en la hoja antigua con coincidencia casi
/// exacta. Como todas las skins de CustomKnight usan la hoja antigua, la tabla sirve para cualquiera.
/// </para>
/// </summary>
internal static class AtlasRepair {
    /// <summary>
    /// HUD Cln (2048x2048): esquina inferior izquierda, en píxeles, de cada sprite en la hoja antigua. La posición
    /// nueva y el tamaño se leen de la colección del juego.
    /// </summary>
    private static readonly Dictionary<string, (int X, int Y)> HudMoves = new() {
        { "refill0002", (1, 1135) },
        { "idle_v020000", (272, 1175) },
        { "idle_v020001", (73, 1135) },
        { "idle_v020002", (1, 1194) },
        { "idle_v020004", (200, 1219) },
        { "idle_v020005", (200, 1160) },
        { "HUD_coin_v020003", (1, 1373) },
        { "HUD_coin_v020004", (1, 1434) },
        { "coin_get0001", (190, 1337) },
        { "coin_get0002", (125, 1385) },
        { "coin_break0004", (1914, 1316) },
        { "coin_break0005", (1914, 1316) },
        { "full0000", (890, 1368) },
        { "full0003", (1, 1311) },
        { "full0005", (70, 1253) },
        { "full0006", (129, 1326) },
        { "full0007", (134, 1264) },
        { "full0009", (65, 1315) },
        { "level_010000", (319, 1320) },
        { "level_010001", (219, 1393) },
        { "level_010002", (287, 1359) },
        { "level_010004", (397, 1336) },
        { "level_010005", (397, 1336) },
        { "level_020000", (219, 1393) },
        { "level_020001", (180, 1391) },
        { "level_030005", (851, 1359) },
        { "appear0003 1", (272, 1234) },
        { "refill0001", (200, 1160) },
        { "refill0003", (200, 1160) },
        { "idle0002", (349, 1172) },
        { "idle0022", (349, 1172) },
        { "idle0023", (349, 1172) },
        { "appear0002", (302, 1130) },
        { "appear0005", (635, 1281) },
        { "appear0004 1", (726, 1342) },
        { "coin_get0000", (1848, 871) },
        { "coin_break0000", (1848, 871) },
        { "full0002", (337, 1258) },
        { "full0004", (64, 1377) },
        { "full0008", (64, 1377) }
    };

    /// <summary>Diferencia de silueta máxima para aceptar un fotograma hermano girado.</summary>
    private const float MaxRotatedError = 0.15f;

    /// <summary>Sprite recolocado: dónde estaba en la hoja antigua y dónde está en la nueva.</summary>
    private class Placed {
        public int OldX;
        public int OldY;
        public int NewX;
        public int NewY;
        public int W;
        public int H;
    }

    private class SpriteRect {
        public string Name;
        public int X;
        public int Y;
        public int W;
        public int H;
    }

    /// <summary>
    /// Repara <paramref name="skin"/> (legible) para la colección <paramref name="data"/>, con su hoja original
    /// <paramref name="vanilla"/>. Devuelve cuántos sprites se recolocaron y cuántos quedaron con el dibujo del juego.
    /// </summary>
    public static (int Moved, int Original) Repair(Texture2D skin, Texture vanilla, tk2dSpriteCollectionData data) {
        var w = skin.width;
        var h = skin.height;
        if (vanilla == null || vanilla.width != w || vanilla.height != h || data?.spriteDefinitions == null) {
            return (0, 0);
        }

        var moves = data.spriteCollectionName == "HUD Cln" && w == 2048 && h == 2048
            ? HudMoves
            : new Dictionary<string, (int X, int Y)>();
        var original = ReadPixels(vanilla, w, h);
        var pixels = skin.GetPixels32();
        // Se copia siempre desde la skin tal como venía: lo ya recolocado no sirve de fuente para otro sprite
        var source = (Color32[]) pixels.Clone();
        var rects = SpriteRects(data, w, h);
        var moved = 0;
        var kept = 0;
        var placed = new Dictionary<string, Placed>();

        // 1. Sprites que se movieron entre versiones: se traen de su sitio antiguo si ahí la skin se parece más (una
        // skin ya hecha para esta versión los tiene en su sitio nuevo y no se toca)
        foreach (var rect in rects) {
            if (!moves.TryGetValue(rect.Name, out var old) || old.X + rect.W > w || old.Y + rect.H > h) {
                continue;
            }

            var errorHere = AlphaError(source, original, w, rect.X, rect.Y, rect.X, rect.Y, rect.W, rect.H);
            var errorOld = AlphaError(source, original, w, old.X, old.Y, rect.X, rect.Y, rect.W, rect.H);
            placed[rect.Name] = new Placed { OldX = old.X, OldY = old.Y, NewX = rect.X, NewY = rect.Y, W = rect.W, H = rect.H };
            if (errorOld < errorHere) {
                CopyBlock(source, pixels, w, old.X, old.Y, rect.X, rect.Y, rect.W, rect.H);
                moved++;
            }
        }

        // 2. Sprites que la skin tiene vacíos: un fotograma hermano girado (algunos se guardan girados en esta
        // versión) o, si no hay, el dibujo del juego para que no quede un hueco
        foreach (var rect in rects) {
            if (moves.ContainsKey(rect.Name)) {
                continue;
            }

            var originalAlpha = MeanAlpha(original, w, rect.X, rect.Y, rect.W, rect.H);
            if (originalAlpha < 0.1f || MeanAlpha(pixels, w, rect.X, rect.Y, rect.W, rect.H) >= originalAlpha * 0.35f) {
                continue;
            }

            if (TryRotatedSibling(rect, placed, source, original, pixels, w)) {
                moved++;
            } else {
                CopyBlock(original, pixels, w, rect.X, rect.Y, rect.X, rect.Y, rect.W, rect.H);
                kept++;
            }
        }

        if (moved + kept > 0) {
            skin.SetPixels32(pixels);
            Log.Info($"Hoja {data.spriteCollectionName} de la skin ajustada a esta versión: {moved} sprites recolocados, " +
                        $"{kept} con el dibujo original");
        }

        return (moved, kept);
    }

    /// <summary>
    /// Busca un fotograma de la misma animación ya recolocado con las medidas cruzadas y lo copia girado. El giro
    /// correcto (hay cuatro maneras de cruzar las medidas) se decide con la hoja original del juego, donde los dos
    /// fotogramas están en su sitio nuevo: así la máscara nunca sale volteada aunque su silueta sea simétrica.
    /// </summary>
    private static bool TryRotatedSibling(SpriteRect rect, Dictionary<string, Placed> placed, Color32[] source,
        Color32[] original, Color32[] pixels, int w) {
        var family = BaseName(rect.Name);
        foreach (var pair in placed) {
            var sibling = pair.Value;
            if (BaseName(pair.Key) != family || sibling.W != rect.H || sibling.H != rect.W) {
                continue;
            }

            // En la hoja original: qué giro convierte el hermano (en su sitio nuevo) en este fotograma
            var bestError = float.MaxValue;
            var bestMode = -1;
            for (var mode = 0; mode < 4; mode++) {
                var turned = Turn(original, w, sibling.NewX, sibling.NewY, sibling.W, sibling.H, mode);
                var error = ColorError(turned, rect.W, original, w, rect.X, rect.Y, rect.W, rect.H);
                if (error < bestError) {
                    bestError = error;
                    bestMode = mode;
                }
            }

            if (bestMode < 0 || bestError > MaxRotatedError) {
                continue;
            }

            Debug.Log($"[HollowKnightCoop] {rect.Name}: girado desde {pair.Key} (modo {bestMode}, diferencia {bestError:0.000})");

            // El mismo giro, aplicado al hermano de la skin (en su sitio antiguo)
            var block = Turn(source, w, sibling.OldX, sibling.OldY, sibling.W, sibling.H, bestMode);
            for (var y = 0; y < rect.H; y++) {
                System.Array.Copy(block, y * rect.W, pixels, (rect.Y + y) * w + rect.X, rect.W);
            }

            return true;
        }

        return false;
    }

    private static string BaseName(string name) => Regex.Replace(name, @"\d+$", string.Empty);

    /// <summary>
    /// Bloque con las medidas cruzadas: el resultado mide <paramref name="bh"/> de ancho y <paramref name="bw"/> de
    /// alto. Modos: 0 = transpuesto, 1 = giro horario, 2 = giro antihorario, 3 = transpuesto por la otra diagonal.
    /// </summary>
    private static Color32[] Turn(Color32[] pixels, int w, int bx, int by, int bw, int bh, int mode) {
        var result = new Color32[bw * bh];
        for (var y = 0; y < bh; y++) {
            for (var x = 0; x < bw; x++) {
                int nx, ny;
                switch (mode) {
                    case 0: nx = y; ny = x; break;
                    case 1: nx = y; ny = bw - 1 - x; break;
                    case 2: nx = bh - 1 - y; ny = x; break;
                    default: nx = bh - 1 - y; ny = bw - 1 - x; break;
                }

                result[ny * bh + nx] = pixels[(by + y) * w + bx + x];
            }
        }

        return result;
    }

    /// <summary>Diferencia media de color y alfa (0..1) entre un bloque suelto y una zona del original.</summary>
    private static float ColorError(Color32[] block, int bw, Color32[] original, int w, int ox, int oy, int rw, int rh) {
        long error = 0;
        var n = 0;
        for (var y = 0; y < rh; y++) {
            for (var x = 0; x < rw; x++) {
                var a = block[y * bw + x];
                var b = original[(oy + y) * w + ox + x];
                error += System.Math.Abs(a.r - b.r) + System.Math.Abs(a.g - b.g) + System.Math.Abs(a.b - b.b) +
                         System.Math.Abs(a.a - b.a);
                n++;
            }
        }

        return n == 0 ? 1f : error / (4f * 255f * n);
    }

    private static List<SpriteRect> SpriteRects(tk2dSpriteCollectionData data, int w, int h) {
        var rects = new List<SpriteRect>();
        foreach (var def in data.spriteDefinitions) {
            if (def?.uvs == null || def.uvs.Length == 0 || string.IsNullOrEmpty(def.name)) {
                continue;
            }

            var x0 = Mathf.Clamp((int) (def.uvs.Min(u => u.x) * w), 0, w - 1);
            var x1 = Mathf.Clamp((int) (def.uvs.Max(u => u.x) * w), 0, w);
            var y0 = Mathf.Clamp((int) (def.uvs.Min(u => u.y) * h), 0, h - 1);
            var y1 = Mathf.Clamp((int) (def.uvs.Max(u => u.y) * h), 0, h);
            if (x1 - x0 >= 4 && y1 - y0 >= 4) {
                rects.Add(new SpriteRect { Name = def.name, X = x0, Y = y0, W = x1 - x0, H = y1 - y0 });
            }
        }

        return rects;
    }

    /// <summary>Diferencia media de alfa (0..1) entre una zona de la skin y otra del original.</summary>
    private static float AlphaError(Color32[] skin, Color32[] original, int w, int sx, int sy, int ox, int oy, int rw, int rh) {
        long error = 0;
        var n = 0;
        for (var y = 0; y < rh; y += 2) {
            for (var x = 0; x < rw; x += 2) {
                var d = skin[(sy + y) * w + sx + x].a - original[(oy + y) * w + ox + x].a;
                error += d < 0 ? -d : d;
                n++;
            }
        }

        return n == 0 ? 1f : error / (255f * n);
    }

    private static float MeanAlpha(Color32[] pixels, int w, int x0, int y0, int rw, int rh) {
        long sum = 0;
        var n = 0;
        for (var y = y0; y < y0 + rh; y += 2) {
            for (var x = x0; x < x0 + rw; x += 2) {
                sum += pixels[y * w + x].a;
                n++;
            }
        }

        return n == 0 ? 0f : sum / (255f * n);
    }

    private static void CopyBlock(Color32[] from, Color32[] to, int w, int fx, int fy, int tx, int ty, int rw, int rh) {
        for (var y = 0; y < rh; y++) {
            System.Array.Copy(from, (fy + y) * w + fx, to, (ty + y) * w + tx, rw);
        }
    }

    /// <summary>Lee los píxeles de una textura no legible (pasándola por una RenderTexture).</summary>
    private static Color32[] ReadPixels(Texture texture, int w, int h) {
        var rt = RenderTexture.GetTemporary(w, h, 0, RenderTextureFormat.ARGB32, RenderTextureReadWrite.sRGB);
        Graphics.Blit(texture, rt);
        var previous = RenderTexture.active;
        RenderTexture.active = rt;
        var readable = new Texture2D(w, h, TextureFormat.RGBA32, false);
        readable.ReadPixels(new Rect(0, 0, w, h), 0, 0);
        readable.Apply();
        RenderTexture.active = previous;
        RenderTexture.ReleaseTemporary(rt);
        var pixels = readable.GetPixels32();
        Object.Destroy(readable);
        return pixels;
    }
}
