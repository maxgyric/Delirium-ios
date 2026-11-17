# Delirium Sandbox test map: climbs, vaults, stairs, drops, slopes and a bike track (glTF, Y up, metres)
import math, numpy as np, trimesh

scene = trimesh.Scene()
n = [0]

def col(hexs):
    h = hexs.lstrip('#'); return [int(h[i:i+2], 16) / 255 for i in (0, 2, 4)] + [1.0]

def add(mesh, color, name):
    mesh = mesh.copy(); mesh.unmerge_vertices()   # flat faces
    mesh.visual = trimesh.visual.TextureVisuals(material=trimesh.visual.material.PBRMaterial(baseColorFactor=color, metallicFactor=0.0, roughnessFactor=0.9))
    n[0] += 1
    scene.add_geometry(mesh, node_name=f"{name}_{n[0]}", geom_name=f"{name}_{n[0]}")

def box(x0, y0, z0, x1, y1, z1, color, name="block"):
    m = trimesh.creation.box(extents=[x1 - x0, y1 - y0, z1 - z0])
    m.apply_translation([(x0 + x1) / 2, (y0 + y1) / 2, (z0 + z1) / 2])
    add(m, color, name)

def hull(points, color, name):
    add(trimesh.convex.convex_hull(np.array(points, float)), color, name)

def wedge(x0, x1, z0, z1, h, up_toward_z1=True, color=None, name="ramp"):
    # a ramp across x0..x1, rising along z from 0 to h (toward z1 or toward z0)
    lo, hi = (z0, z1) if up_toward_z1 else (z1, z0)
    pts = [[x, 0, lo] for x in (x0, x1)] + [[x, 0, hi] for x in (x0, x1)] + [[x, h, hi] for x in (x0, x1)]
    hull(pts, color, name)

GROUND, CONCRETE, WOOD, BRICK, RAMP, MARK = col('#6f8f5a'), col('#9a9a96'), col('#9b6b3d'), col('#a5543f'), col('#c9a227'), col('#e9e9e9')

# the ground: one thick slab, its top at 0
box(-150, -1, -150, 150, 0, 150, GROUND, "ground")

# 1. climbs: blocks 3 m deep at every height of GTA V's climbs (0.5 ... 2.95), and one too high (3.5)
heights = [0.5, 0.8, 1.1, 1.4, 1.8, 2.2, 2.6, 2.95, 3.5]
for i, h in enumerate(heights):
    x = -20 + i * 3.0
    t = i / (len(heights) - 1)
    c = [0.35 + 0.5 * t, 0.55 - 0.25 * t, 0.75 - 0.5 * t, 1.0]
    box(x, 0, 10, x + 2.4, h, 13, c, f"climb_{h:.2f}m".replace('.', '_'))

# 2. vaults: thin fences (0.2 m) with ground behind, and a low wide wall
for i, h in enumerate([0.6, 0.9, 1.2, 1.6]):
    x = -16 + i * 5.0
    box(x, 0, 22, x + 3.0, h, 22.2, WOOD, f"fence_{h:.1f}m".replace('.', '_'))
box(6, 0, 22, 10, 0.8, 22.6, BRICK, "low_wall_0_8m")

# 3. stairs to a walled roof (the Minecraft house case): fine steps and big 0.5 m steps
def stairs(x0, x1, z0, rise, run, count, color, name):
    for k in range(count):
        box(x0, 0, z0 + k * run, x1, (k + 1) * rise, z0 + (k + 1) * run, color, name)
    return z0 + count * run, count * rise
zt, ht = stairs(20, 23, 4, 0.18, 0.3, 15, CONCRETE, "stairs_fine")        # 2.7 m
box(18, 0, zt, 30, ht, zt + 6, CONCRETE, "roof_2_7m")
box(18, ht, zt + 5.6, 30, ht + 3.0, zt + 6, BRICK, "roof_back_wall")    # a wall to walk into at the top
box(29.6, ht, zt, 30, ht + 1.1, zt + 5.6, BRICK, "parapet")
zt2, ht2 = stairs(26, 29, zt - 6 * 0.6, 0.45, 0.6, 6, WOOD, "stairs_big")  # 0.45 m steps, Minecraft-like

# 4. drops: terraces 2, 4 and 6 m up (climb from one to the next), a ramp up to the first
for i, h in enumerate([2.0, 4.0, 6.0]):
    box(-40, 0, 30 + i * 4, -34, h, 34 + i * 4, BRICK if i % 2 else CONCRETE, f"terrace_{int(h)}m")
wedge(-40, -34, 18, 30, 2.0, True, RAMP, "ramp_to_terrace")

# 5. slopes: 15, 30, 45 degrees up to 2 m
for i, deg in enumerate([15, 30, 45]):
    L = 2.0 / math.tan(math.radians(deg))
    x = 40 + i * 5
    wedge(x, x + 3.5, 10, 10 + L, 2.0, True, RAMP, f"slope_{deg}deg")
    box(x, 0, 10 + L, x + 3.5, 2.0, 10 + L + 3, CONCRETE, f"slope_top_{deg}deg")

# 6. the bike track: a straight run south with kickers, a jump with a landing, a table-top and bumps
X0, X1 = -4, 4
wedge(X0, X1, -24, -28, 0.7, True, RAMP, "kicker_small")                 # small kicker
wedge(X0, X1, -40, -46, 1.5, True, RAMP, "kicker_medium")
wedge(X0, X1, -54, -62, 1.5, False, RAMP, "landing_medium")
wedge(X0, X1, -74, -84, 3.0, True, RAMP, "jump_big")                     # big jump, 12 m gap
wedge(X0, X1, -96, -110, 3.0, False, RAMP, "landing_big")
# table-top: up, 8 m flat, down
wedge(X0, X1, -122, -128, 1.8, True, RAMP, "tabletop_up")
box(X0, 0, -136, X1, 1.8, -128, CONCRETE, "tabletop")
wedge(X0, X1, -136, -142, 1.8, False, RAMP, "tabletop_down")
# rollers (small bumps)
for k in range(6):
    z = -14 - k * 1.6
    hull([[x, 0, z] for x in (X0, X1)] + [[x, 0, z - 1.2] for x in (X0, X1)] + [[x, 0.22, z - 0.6] for x in (X0, X1)], RAMP, "bump")
# distance posts every 20 m beside the track
for k in range(1, 8):
    box(5.5, 0, -k * 20 - 0.1, 5.7, 1.2, -k * 20 + 0.1, MARK, f"post_{k * 20}m")
# a second lane: a wide ramp to a 4 m drop-off (for crashes)
wedge(12, 20, -30, -45, 4.0, True, RAMP, "launch_4m")
box(12, 0, -51, 20, 4.0, -45, CONCRETE, "launch_deck")

# 7. boxes to knock about and stand on
for i in range(4):
    box(-8 + i * 1.2, 0, -8, -7 + i * 1.2, 1.0, -7, WOOD, "crate")
box(-7.4, 1.0, -8, -6.4, 2.0, -7, WOOD, "crate")

# where you start: facing the climbs
scene.graph.update(frame_to="Spawn", frame_from=scene.graph.base_frame, matrix=trimesh.transformations.translation_matrix([-12, 0, 2]))

scene.export("DeliriumTestMap.glb", include_normals=True)
print("meshes", n[0])
