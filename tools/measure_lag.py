"""Mide el retraso de la copia de un enemigo en el cliente respecto al enemigo real del host.

Uso: python measure_lag.py <ModLog host> <nombre host> <ModLog cliente> <nombre cliente>
Lee las líneas "[TEST] track HH:MM:SS.fff '<nombre>' pos=(x, y)" de cada log y busca el desfase temporal que
mejor alinea la trayectoria del cliente con la del host.
"""
import re
import sys
from datetime import datetime

LINE = re.compile(r"\[TEST\] track (\d\d:\d\d:\d\d\.\d{3}) '(.+?)' pos=\(([-\d,\.]+), ([-\d,\.]+)\)")


def load(path, name):
    samples = []
    with open(path, encoding="utf-8-sig", errors="replace") as f:
        for line in f:
            m = LINE.search(line)
            if m and m.group(2) == name:
                t = datetime.strptime(m.group(1), "%H:%M:%S.%f")
                x = float(m.group(3).replace(",", "."))
                samples.append((t.hour * 3600 + t.minute * 60 + t.second + t.microsecond / 1e6, x))
    return samples


def interpolate(samples, t):
    for (t0, x0), (t1, x1) in zip(samples, samples[1:]):
        if t0 <= t <= t1:
            return x0 + (x1 - x0) * (t - t0) / (t1 - t0) if t1 > t0 else x0
    return None


def main():
    host = load(sys.argv[1], sys.argv[2])
    client = load(sys.argv[3], sys.argv[4])
    if len(host) < 10 or len(client) < 10:
        print(f"pocas muestras: host={len(host)} cliente={len(client)}")
        return

    best = None
    for lag_ms in range(0, 501, 5):
        lag = lag_ms / 1000
        errors = []
        for t, x in client:
            hx = interpolate(host, t - lag)
            if hx is not None:
                errors.append(abs(hx - x))
        if len(errors) > len(client) // 2:
            mean = sum(errors) / len(errors)
            if best is None or mean < best[1]:
                best = (lag_ms, mean)

    raw = [abs(interpolate(host, t) - x) for t, x in client if interpolate(host, t) is not None]
    speeds = [abs(x1 - x0) / (t1 - t0) for (t0, x0), (t1, x1) in zip(host, host[1:]) if t1 > t0]
    print(f"muestras: host={len(host)} cliente={len(client)}")
    print(f"velocidad media del enemigo: {sum(speeds) / len(speeds):.2f} unidades/s")
    print(f"diferencia de posición en el mismo instante: media {sum(raw) / len(raw):.3f}, máx {max(raw):.3f} unidades")
    print(f"retraso estimado de la copia del cliente: {best[0]} ms (error residual {best[1]:.3f} unidades)")


if __name__ == "__main__":
    main()
