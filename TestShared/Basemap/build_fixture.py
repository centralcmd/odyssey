# Builds TestShared/Basemap/basemap.pmtiles (issue #384 test fixture). See ATTRIBUTION.md. Needs `pip install pmtiles`.
# Usage: python build_fixture.py basemap.pmtiles
import math, json, gzip, sys, urllib.request
opener = urllib.request.build_opener(); opener.addheaders = [("User-Agent", "odyssey-fixture-builder/1.0")]; urllib.request.install_opener(opener)
from pmtiles.writer import Writer
from pmtiles.tile import zxy_to_tileid, TileType, Compression
lat, lon = 61.52914, 10.13802
half_km = 1.0
dlat = half_km / 111.32
dlon = half_km / (111.32 * math.cos(math.radians(lat)))
minlon, minlat, maxlon, maxlat = lon - dlon, lat - dlat, lon + dlon, lat + dlat
def tile(lonv, latv, z):
    n = 2 ** z
    x = int((lonv + 180) / 360 * n)
    y = int((1 - math.asinh(math.tan(math.radians(latv))) / math.pi) / 2 * n)
    return x, y
template = json.load(urllib.request.urlopen("https://tiles.openfreemap.org/planet"))["tiles"][0]
minz, maxz = 10, 14
entries = []
for z in range(minz, maxz + 1):
    x0, y0 = tile(minlon, maxlat, z); x1, y1 = tile(maxlon, minlat, z)
    for x in range(x0, x1 + 1):
        for y in range(y0, y1 + 1):
            req = urllib.request.Request(template.format(z=z, x=x, y=y), headers={"Accept-Encoding": "gzip"})
            data = urllib.request.urlopen(req).read()
            if data[:2] != b"\x1f\x8b":
                data = gzip.compress(data)
            entries.append((zxy_to_tileid(z, x, y), data))
entries.sort()
out = sys.argv[1]
with open(out, "wb") as f:
    w = Writer(f)
    for tid, data in entries:
        w.write_tile(tid, data)
    header = {
        "tile_type": TileType.MVT, "tile_compression": Compression.GZIP,
        "min_zoom": minz, "max_zoom": maxz,
        "min_lon_e7": int(minlon * 1e7), "min_lat_e7": int(minlat * 1e7),
        "max_lon_e7": int(maxlon * 1e7), "max_lat_e7": int(maxlat * 1e7),
        "center_zoom": 14, "center_lon_e7": int(lon * 1e7), "center_lat_e7": int(lat * 1e7),
    }
    meta = {"name": "Odyssey test fixture (Ringebu)", "attribution": "© OpenMapTiles © OpenStreetMap contributors",
            "description": "Vector tiles z10-14 around the demo Cabin location, from OpenFreeMap's planet build " + template.split('/')[4],
            "vector_layers": json.load(urllib.request.urlopen("https://tiles.openfreemap.org/planet"))["vector_layers"]}
    w.finalize(header, meta)
print(len(entries), "tiles", f"bounds {minlon:.5f},{minlat:.5f},{maxlon:.5f},{maxlat:.5f}")
