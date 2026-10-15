# Dibuja setup.ico, el icono del instalador: una sombra (la silueta negra de un caballero con cuernos, ojos blancos
# vacíos y la capa deshecha en humo) sobre el fondo azul oscuro de la ventana. Todo dibujado aquí, sin nada del juego.
# Uso: python make-icon.py [preview.png]   (necesita Pillow)
import math
import sys
from pathlib import Path

from PIL import Image, ImageDraw, ImageFilter

S = 1024  # se dibuja grande y se reduce a cada tamaño del icono
SIZES = [16, 24, 32, 48, 64, 256]


def lerp(a, b, t):
    return tuple(round(x + (y - x) * t) for x, y in zip(a, b))


def bezier(points, steps=40):
    """Puntos de una curva de Bézier cúbica."""
    (x0, y0), (x1, y1), (x2, y2), (x3, y3) = points
    out = []
    for i in range(steps + 1):
        t = i / steps
        u = 1 - t
        out.append((u ** 3 * x0 + 3 * u * u * t * x1 + 3 * u * t * t * x2 + t ** 3 * x3,
                    u ** 3 * y0 + 3 * u * u * t * y1 + 3 * u * t * t * y2 + t ** 3 * y3))
    return out


def background():
    """Cuadrado redondeado con el degradado de la ventana del instalador."""
    image = Image.new("RGBA", (S, S), (0, 0, 0, 0))
    gradient = Image.new("RGBA", (S, S))
    top, bottom = (36, 46, 82), (11, 14, 24)
    draw = ImageDraw.Draw(gradient)
    for y in range(S):
        draw.line([(0, y), (S, y)], fill=lerp(top, bottom, y / S) + (255,))
    mask = Image.new("L", (S, S), 0)
    ImageDraw.Draw(mask).rounded_rectangle([16, 16, S - 16, S - 16], radius=190, fill=255)
    image.paste(gradient, (0, 0), mask)
    return image, mask


def horn(draw, side):
    """Un cuerno: fino y largo, sube casi recto desde lo alto de la cabeza y se abre un poco hacia fuera al final."""
    cx = 512
    base_in, base_out = cx + side * 88, cx + side * 150
    tip = (cx + side * 190, 28)
    outer = bezier([(base_out, 340), (base_out + side * 12, 230), (base_out + side * 4, 110), tip])
    inner = bezier([tip, (base_in + side * 40, 120), (base_in + side * 18, 230), (base_in, 340)])
    draw.polygon(outer + inner, fill=255)


def silhouette():
    """Máscara de la sombra: cabeza, cuernos y capa con el borde de abajo en jirones desiguales."""
    mask = Image.new("L", (S, S), 0)
    draw = ImageDraw.Draw(mask)
    # Capa: se ensancha bajo la cabeza y acaba en puntas desiguales que se deshacen en humo
    top_y = 480
    left = bezier([(410, top_y), (320, 600), (270, 720), (240, 850)])
    right = bezier([(790, 850), (760, 720), (705, 600), (614, top_y)])
    tips = [(240, 850), (262, 945), (318, 870), (352, 985), (420, 890), (470, 960), (520, 905), (566, 995), (622, 885),
            (680, 950), (720, 875), (770, 930), (790, 850)]
    draw.polygon(left + tips + right, fill=255)
    # Cabeza: un óvalo grande
    draw.ellipse([300, 300, 724, 650], fill=255)
    horn(draw, -1)
    horn(draw, 1)
    return mask


def wisps():
    """Hilos de humo que suben desde los lados de la capa."""
    mask = Image.new("L", (S, S), 0)
    draw = ImageDraw.Draw(mask)
    for side in (-1, 1):
        for i, (start, height, sway, width) in enumerate([(700, 330, 70, 34), (800, 420, 110, 26), (600, 250, 50, 22)]):
            x0 = 512 + side * (250 + i * 40)
            path = bezier([(x0, start), (x0 + side * sway, start - height * 0.35), (x0 - side * sway * 0.4, start - height * 0.7),
                           (x0 + side * sway * 0.8, start - height)], steps=60)
            for j, (x, y) in enumerate(path):
                r = width * (1 - j / len(path)) + 2
                draw.ellipse([x - r, y - r, x + r, y + r], fill=255)
    return mask.filter(ImageFilter.GaussianBlur(10))


def eyes():
    """Los dos ojos: óvalos blancos inclinados hacia dentro, en la mitad de abajo de la cabeza."""
    layer = Image.new("RGBA", (S, S), (0, 0, 0, 0))
    for side in (-1, 1):
        eye = Image.new("RGBA", (S, S), (0, 0, 0, 0))
        ImageDraw.Draw(eye).ellipse([512 - 50, 425, 512 + 50, 600], fill=(236, 240, 248, 255))
        eye = eye.rotate(side * 7, center=(512, 512), resample=Image.BICUBIC)
        layer.alpha_composite(eye, (side * 98, 0))
    return layer


def draw_icon():
    image, frame = background()
    body = silhouette()
    smoke = wisps()

    # Brillo morado alrededor de la sombra
    glow_mask = Image.composite(Image.new("L", (S, S), 255), Image.new("L", (S, S), 0), body)
    glow_mask = Image.eval(glow_mask, lambda v: v).filter(ImageFilter.GaussianBlur(60))
    glow = Image.new("RGBA", (S, S), (126, 92, 255, 0))
    glow.putalpha(Image.eval(glow_mask, lambda v: int(v * 0.75)))
    image.alpha_composite(glow)

    # Humo y cuerpo, negro azulado con el borde un poco difuso
    dark = (8, 7, 14, 255)
    smoke_layer = Image.new("RGBA", (S, S), dark)
    smoke_layer.putalpha(Image.eval(smoke, lambda v: int(v * 0.85)))
    image.alpha_composite(smoke_layer)
    fade = Image.new("L", (S, S), 0)
    fade_draw = ImageDraw.Draw(fade)
    for y in range(S):
        fade_draw.line([(0, y), (S, y)], fill=max(0, min(255, int((y - 760) * 255 / 160))))
    soft = Image.composite(body.filter(ImageFilter.GaussianBlur(16)), body.filter(ImageFilter.GaussianBlur(3)), fade)
    body_layer = Image.new("RGBA", (S, S), dark)
    body_layer.putalpha(soft)
    image.alpha_composite(body_layer)

    image.alpha_composite(eyes())

    # Todo dentro del cuadrado redondeado
    clipped = Image.new("RGBA", (S, S), (0, 0, 0, 0))
    clipped.paste(image, (0, 0), frame)
    return clipped


def main():
    here = Path(__file__).resolve().parent
    icon = draw_icon()
    images = [icon.resize((size, size), Image.LANCZOS) for size in SIZES]
    images[-1].save(here / "setup.ico", format="ICO", sizes=[(size, size) for size in SIZES], append_images=images[:-1])
    if len(sys.argv) > 1:
        preview = Image.new("RGBA", (256 + 16 + 64 + 16 + 48 + 16 + 32 + 16 + 24 + 16 + 16, 256), (40, 40, 40, 255))
        x = 0
        for image in reversed(images):
            preview.alpha_composite(image, (x, 0))
            x += image.width + 16
        preview.save(sys.argv[1])


if __name__ == "__main__":
    main()
