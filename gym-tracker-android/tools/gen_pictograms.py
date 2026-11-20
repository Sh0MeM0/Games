#!/usr/bin/env python3
"""Generates exercise pictograms (Android VectorDrawable XML + an SVG preview sheet).

Every picture is a stick figure built from joint angles. The start position of the
movement is drawn semi-transparent ("ghost"), the end position solid.

Usage: python3 tools/gen_pictograms.py [preview.html]
Writes res/drawable/ex_<key>.xml for every exercise below.
"""
import math
import os
import sys

ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
G = 90.0  # ground line

# segment lengths (viewport is 100 x 100)
TORSO, NECK, HEAD_R = 25.0, 3.0, 6.5
UARM, FARM = 14.5, 13.5
THIGH, SHIN, FOOT = 19.0, 19.0, 6.0

# color keys -> (android color resource, preview color)
COLORS = {
    'body': ('@color/pict_body', '#3D5AFE'),
    'ghost': ('@color/pict_ghost', 'rgba(61,90,254,0.30)'),
    'equip': ('@color/pict_equip', '#A3ACBB'),
    'weight': ('@color/pict_weight', '#4A5568'),
    'arrow': ('@color/pict_arrow', '#1E9E4A'),
}


def P(p, ang, length):
    a = math.radians(ang)
    return (p[0] + length * math.cos(a), p[1] + length * math.sin(a))


class Pic:
    def __init__(self):
        self.items = []  # (kind, data..., color)

    def poly(self, pts, w, c):
        self.items.append(('poly', [tuple(p) for p in pts], w, c))

    def line(self, a, b, w, c):
        self.poly([a, b], w, c)

    def circle(self, p, r, c):
        self.items.append(('circle', tuple(p), r, c))

    def rect(self, x, y, w, h, c, rx=1.5):
        self.items.append(('rect', (x, y, w, h), rx, c))

    def arrow(self, a, b, c='arrow', w=2.2):
        self.line(a, b, w, c)
        ang = math.degrees(math.atan2(b[1] - a[1], b[0] - a[0]))
        self.poly([P(b, ang + 150, 4.5), b, P(b, ang - 150, 4.5)], w, c)

    # ------------------------------------------------------------ equipment
    def barbell(self, p, c='weight'):
        self.circle(p, 7.0, c)
        self.circle(p, 2.0, 'equip' if c == 'weight' else c)

    def dumbbell(self, p, c='weight'):
        self.circle(p, 4.2, c)

    def dumbbell_front(self, p, c='weight'):
        # horizontal dumbbell seen from the front
        x, y = p
        self.line((x - 6, y), (x + 6, y), 2.2, c)
        self.rect(x - 7.5, y - 3.5, 3.5, 7, c, 1)
        self.rect(x + 4, y - 3.5, 3.5, 7, c, 1)

    def bench(self, x1, x2, top, c='equip'):
        self.rect(x1, top, x2 - x1, 4.5, c, 1.5)
        self.line((x1 + 6, top + 4), (x1 + 6, G), 2.5, c)
        self.line((x2 - 6, top + 4), (x2 - 6, G), 2.5, c)

    def ground(self):
        self.line((6, G + 1.5), (94, G + 1.5), 1.6, 'equip')

    # ------------------------------------------------------------ figures
    def side(self, c, hip, torso, arm, leg, arm2=None, leg2=None, foot=0, foot2=None):
        """Side view, faces the direction given by the angles. Returns joints."""
        j = {'hip': hip}
        j['shoulder'] = P(hip, torso, TORSO)
        j['neck'] = P(j['shoulder'], torso, NECK)
        j['head'] = P(j['neck'], torso, HEAD_R)
        j['elbow'] = P(j['shoulder'], arm[0], UARM)
        j['hand'] = P(j['elbow'], arm[1], FARM)
        j['knee'] = P(hip, leg[0], THIGH)
        j['ankle'] = P(j['knee'], leg[1], SHIN)
        j['toe'] = P(j['ankle'], foot, FOOT)
        if leg2:
            j['knee2'] = P(hip, leg2[0], THIGH)
            j['ankle2'] = P(j['knee2'], leg2[1], SHIN)
            j['toe2'] = P(j['ankle2'], foot if foot2 is None else foot2, FOOT)
            self.poly([hip, j['knee2'], j['ankle2'], j['toe2']], 4.6, c)
        if arm2:
            j['elbow2'] = P(j['shoulder'], arm2[0], UARM)
            j['hand2'] = P(j['elbow2'], arm2[1], FARM)
            self.poly([j['shoulder'], j['elbow2'], j['hand2']], 4.0, c)
        self.poly([hip, j['knee'], j['ankle'], j['toe']], 5.0, c)
        self.line(hip, j['shoulder'], 6.5, c)
        self.circle(j['head'], HEAD_R, c)
        self.poly([j['shoulder'], j['elbow'], j['hand']], 4.4, c)
        return j

    def front(self, c, cx, hip_y, arm, leg, arm_r=None, leg_r=None, shoulder_dy=0.0):
        """Front view, symmetric unless right limbs are given (angles for the LEFT side
        of the picture; the right side is mirrored)."""
        def mirror(a):
            return 180.0 - a
        j = {}
        sy = hip_y - TORSO + shoulder_dy
        j['sl'], j['sr'] = (cx - 8, sy), (cx + 8, sy)
        j['hl'], j['hr'] = (cx - 5, hip_y), (cx + 5, hip_y)
        head = (cx, sy - NECK - HEAD_R - shoulder_dy)
        j['head'] = head
        arm_r = arm_r or (mirror(arm[0]), mirror(arm[1]))
        leg_r = leg_r or (mirror(leg[0]), mirror(leg[1]))
        for side, s, h, a, l in (('l', j['sl'], j['hl'], arm, leg), ('r', j['sr'], j['hr'], arm_r, leg_r)):
            e = P(s, a[0], UARM)
            hand = P(e, a[1], FARM)
            k = P(h, l[0], THIGH)
            an = P(k, l[1], SHIN)
            j['e' + side], j['hand' + side], j['k' + side], j['a' + side] = e, hand, k, an
            self.poly([h, k, an], 5.0, c)
            self.poly([s, e, hand], 4.4, c)
        self.poly([(cx - 6, hip_y), (cx - 7.5, sy + 2), (cx + 7.5, sy + 2), (cx + 6, hip_y)], 6.0, c)
        self.line((cx - 6, hip_y - 1), (cx + 6, hip_y - 1), 6.0, c)
        self.line((cx - 2.5, sy + 4), (cx + 2.5, sy + 4), 9.0, c)
        self.line(j['sl'], j['sr'], 5.0, c)
        self.circle(head, HEAD_R, c)
        return j


# ====================================================================== exercises
EX = {}


def ex(key):
    def deco(fn):
        EX[key] = fn
        return fn
    return deco


@ex('bench_press')
def _(p):
    p.ground(); p.bench(14, 70, 63)
    for c, arm in (('ghost', (20, -100)), ('body', (-82, -90))):
        j = p.side(c, (58, 59), 180, arm, (28, 92))
        p.barbell(j['hand'], 'ghost' if c == 'ghost' else 'weight')


@ex('dumbbell_press')
def _(p):
    p.ground(); p.bench(14, 70, 63)
    for c, arm in (('ghost', (20, -100)), ('body', (-82, -90))):
        j = p.side(c, (58, 59), 180, arm, (28, 92))
        p.dumbbell(j['hand'], 'ghost' if c == 'ghost' else 'weight')


@ex('incline_press')
def _(p):
    p.ground()
    p.rect(50, 70, 30, 4.5, 'equip')
    p.line((63, 74), (63, G), 2.5, 'equip')
    p.line((52, 70), (25, 46), 6, 'equip')
    p.line((30, 52), (30, G), 2.5, 'equip')
    for c, arm in (('ghost', (35, -95)), ('body', (-80, -88))):
        j = p.side(c, (58, 66), 213, arm, (0, 95))
        p.barbell(j['hand'], 'ghost' if c == 'ghost' else 'weight')


@ex('dumbbell_fly')
def _(p):
    p.ground(); p.bench(14, 70, 63)
    j = p.side('ghost', (58, 59), 180, (150, 165), (28, 92))
    p.dumbbell(j['hand'], 'ghost')
    j = p.side('body', (58, 59), 180, (-88, -88), (28, 92))
    p.dumbbell(j['hand'])

@ex('pec_deck')
def _(p):
    p.ground()
    p.line((14, 12), (14, G), 3, 'equip'); p.line((86, 12), (86, G), 3, 'equip')
    p.line((14, 12), (86, 12), 3, 'equip')
    p.rect(38, 60, 24, 5, 'equip')
    j = p.front('ghost', 50, 60, (180, -90), (100, 90))
    j = p.front('body', 50, 60, (200, -60), (100, 90))
    p.rect(j['handl'][0] - 3, j['handl'][1] - 2, 6, 14, 'weight', 2)
    p.rect(j['handr'][0] - 3, j['handr'][1] - 2, 6, 14, 'weight', 2)


@ex('cable_crossover')
def _(p):
    p.ground()
    p.line((8, 6), (8, G), 3, 'equip'); p.line((92, 6), (92, G), 3, 'equip')
    p.circle((10, 10), 2.5, 'weight'); p.circle((90, 10), 2.5, 'weight')
    j = p.front('ghost', 50, 54, (200, 190), (95, 90))
    j = p.front('body', 50, 54, (110, 60), (95, 90))
    p.line((10, 10), j['handl'], 1.4, 'weight'); p.line((90, 10), j['handr'], 1.4, 'weight')


@ex('chest_press_machine')
def _(p):
    p.ground()
    p.rect(26, 66, 28, 5, 'equip')
    p.line((24, 66), (22, 26), 6, 'equip')
    p.line((40, 71), (40, G), 3, 'equip')
    p.line((86, 20), (86, G), 3, 'equip')
    for c, arm in (('ghost', (140, -10)), ('body', (5, 0))):
        j = p.side(c, (32, 62), -88, arm, (0, 90))
        p.line(j['hand'], (86, j['hand'][1] - 6), 2.5, 'ghost' if c == 'ghost' else 'weight')


@ex('dips')
def _(p):
    p.ground()
    p.line((30, 47), (74, 47), 3.5, 'equip'); p.line((66, 47), (66, G), 3, 'equip')
    p.side('ghost', (52, 55), -80, (150, 60), (100, 140))
    p.side('body', (52, 43), -80, (95, 85), (100, 140))

@ex('pushups')
def _(p):
    p.ground()
    for c, arm, hip in (('ghost', (60, 135), (55, 80)), ('body', (95, 90), (55, 70))):
        # straight body from feet (right) to head (left)
        ang = math.degrees(math.atan2(hip[1] - (G - 1), hip[0] - 90))
        j = p.side(c, hip, ang, arm, (ang + 180, ang + 180), foot=90)


@ex('lat_pulldown')
def _(p):
    p.ground()
    p.line((50, 4), (50, 10), 1.4, 'weight')
    p.rect(40, 64, 20, 5, 'equip')
    p.rect(30, 56, 40, 4, 'equip')
    for c, arm in (('ghost', (-110, -95)), ('body', (165, -60))):
        j = p.front(c, 50, 64, arm, (85, 80))
        y = (j['handl'][1] + j['handr'][1]) / 2
        cc = 'ghost' if c == 'ghost' else 'weight'
        p.line((j['handl'][0] - 6, y - 2), (j['handr'][0] + 6, y - 2), 2.6, cc)
        p.line((50, 10 if c == 'ghost' else 4), (50, y - 2), 1.4, cc)


@ex('seated_row')
def _(p):
    p.ground()
    p.rect(14, 72, 30, 5, 'equip')
    p.line((22, 77), (22, G), 3, 'equip')
    p.line((76, 66), (76, G), 4, 'equip')
    p.circle((90, 80), 3, 'weight')
    for c, arm in (('ghost', (5, 0)), ('body', (115, -10))):
        j = p.side(c, (28, 69), -95 if c == 'body' else -70, arm, (-8, 15), foot=-80)
        p.line(j['hand'], (90, 80), 1.4, 'ghost' if c == 'ghost' else 'weight')


@ex('bent_over_row')
def _(p):
    p.ground()
    j = p.side('ghost', (40, 52), -25, (90, 90), (110, 75))
    p.barbell(j['hand'], 'ghost')
    j = p.side('body', (40, 52), -25, (195, 95), (110, 75))
    p.barbell(j['hand'])

@ex('one_arm_row')
def _(p):
    p.ground(); p.bench(26, 86, 71)
    j = p.side('body', (40, 50), -20, (200, 95), (90, 180), arm2=(92, 88), leg2=(92, 89), foot=0, foot2=0)
    p.dumbbell(j['hand'])
    e = P(j['shoulder'], 90, UARM); h = P(e, 90, FARM)
    p.poly([j['shoulder'], e, h], 4.4, 'ghost'); p.dumbbell(h, 'ghost')

@ex('pullups')
def _(p):
    p.line((16, 10), (84, 10), 3.5, 'equip')
    p.line((18, 10), (18, G), 3, 'equip'); p.line((82, 10), (82, G), 3, 'equip')
    p.front('ghost', 50, 75, (-105, -92), (92, 100))
    p.front('body', 50, 56, (170, -75), (92, 100))


@ex('deadlift')
def _(p):
    p.ground()
    j = p.side('ghost', (30, 66), -35, (88, 90), (20, 115))
    p.barbell(j['hand'], 'ghost')
    j = p.side('body', (46, 50), -88, (95, 90), (90, 90))
    p.barbell(j['hand'])

@ex('romanian_deadlift')
def _(p):
    p.ground()
    j = p.side('ghost', (38, 53), -12, (95, 92), (95, 90))
    p.barbell(j['hand'], 'ghost')
    j = p.side('body', (46, 52), -88, (95, 90), (90, 90))
    p.barbell(j['hand'])


@ex('hyperextension')
def _(p):
    p.ground()
    p.line((38, 62), (78, 84), 4, 'equip')
    p.line((60, 74), (60, G), 3, 'equip')
    p.circle((80, 88), 3.2, 'weight')
    p.rect(40, 54, 8, 10, 'weight', 2)
    p.side('ghost', (44, 58), 100, (95, 95), (28, 28), foot=-60)
    p.side('body', (44, 58), 208, (240, 240), (28, 28), foot=-60)


@ex('squat')
def _(p):
    p.ground()
    j = p.side('ghost', (48, 50), -86, (160, 60), (90, 90))
    p.barbell(P(j['shoulder'], 180, 2), 'ghost')
    j = p.side('body', (30, 68), -50, (165, 40), (0, 110))
    p.barbell(P(j['shoulder'], 160, 2))

@ex('leg_press')
def _(p):
    p.ground()
    p.line((14, 50), (34, 80), 6, 'equip')
    p.rect(30, 78, 18, 4.5, 'equip')
    p.line((40, 82), (40, G), 3, 'equip')
    p.line((50, 88), (92, 40), 3, 'equip')
    for c, leg in (('ghost', (-95, -5)), ('body', (-45, -45))):
        j = p.side(c, (36, 76), 237, (-10, 60), leg, foot=-135)
        a = j['ankle']
        cc = 'ghost' if c == 'ghost' else 'weight'
        p.line(P(a, 135, 9), P(a, -45, 9), 4.5, cc)

@ex('leg_extension')
def _(p):
    p.ground()
    p.rect(24, 66, 30, 5, 'equip'); p.line((22, 66), (20, 30), 6, 'equip')
    p.line((40, 71), (40, G), 3, 'equip')
    for c, sh in (('ghost', 95), ('body', -5)):
        j = p.side(c, (32, 62), -90, (60, 20), (0, sh))
        p.circle(j['ankle'], 3.5, 'ghost' if c == 'ghost' else 'weight')


@ex('leg_curl')
def _(p):
    p.ground(); p.bench(8, 74, 62)
    for c, sh in (('ghost', 0), ('body', -95)):
        j = p.side(c, (44, 57), 180, (120, 30), (0, sh), foot=sh + 90)
        p.circle(P(j['ankle'], sh + 180, 1), 3.5, 'ghost' if c == 'ghost' else 'weight')


@ex('lunges')
def _(p):
    p.ground()
    j = p.side('ghost', (40, 50), -88, (95, 90), (90, 90))
    p.dumbbell(j['hand'], 'ghost')
    j = p.side('body', (48, 70), -88, (92, 90), (0, 90), leg2=(115, 180), foot2=-30)
    p.dumbbell(j['hand'])

@ex('hack_squat')
def _(p):
    p.ground()
    p.line((38, 82), (22, 30), 5, 'equip')
    p.rect(50, 86, 22, 4, 'equip')
    j = p.side('ghost', (48, 50), -100, (40, -120), (80, 100))
    j = p.side('body', (40, 70), -105, (40, -120), (-5, 100))
    p.rect(j['shoulder'][0] - 4, j['shoulder'][1] - 7, 9, 5, 'weight', 2)

@ex('calf_raise')
def _(p):
    p.ground()
    p.side('ghost', (50, 51), -90, (95, 90), (90, 90), foot=-10)
    j = p.side('body', (50, 46), -90, (95, 90), (90, 90), foot=50)
    p.dumbbell(j['hand'])
    p.arrow((72, 62), (72, 48))

@ex('hip_adduction')
def _(p):
    p.ground()
    p.rect(36, 60, 28, 5, 'equip')
    p.line((50, 65), (50, G), 3, 'equip')
    p.front('ghost', 50, 58, (100, 95), (140, 110))
    j = p.front('body', 50, 58, (100, 95), (98, 90))
    p.arrow((22, 76), (32, 76)); p.arrow((78, 76), (68, 76))


@ex('hip_abduction')
def _(p):
    p.ground()
    p.rect(36, 60, 28, 5, 'equip')
    p.line((50, 65), (50, G), 3, 'equip')
    p.front('ghost', 50, 58, (100, 95), (96, 90))
    p.front('body', 50, 58, (100, 95), (138, 112))
    p.arrow((40, 82), (28, 82)); p.arrow((60, 82), (72, 82))


@ex('hip_thrust')
def _(p):
    p.ground()
    p.rect(8, 50, 16, 40, 'equip', 2)
    j = p.side('ghost', (44, 76), 205, (50, -40), (-20, 85))
    p.barbell(P(j['hip'], -90, 6), 'ghost')
    j = p.side('body', (46, 56), 188, (60, -30), (10, 95))
    p.barbell(P(j['hip'], -90, 6))


@ex('overhead_press')
def _(p):
    p.ground()
    j = p.side('ghost', (48, 52), -90, (60, -110), (90, 90))
    p.barbell(j['hand'], 'ghost')
    j = p.side('body', (48, 52), -90, (-85, -90), (90, 90))
    p.barbell(j['hand'])

@ex('db_shoulder_press')
def _(p):
    p.ground()
    p.rect(36, 62, 28, 5, 'equip'); p.line((50, 67), (50, G), 3, 'equip')
    j = p.front('ghost', 50, 60, (180, -90), (98, 92))
    p.dumbbell_front(j['handl'], 'ghost'); p.dumbbell_front(j['handr'], 'ghost')
    j = p.front('body', 50, 60, (-110, -95), (98, 92))
    p.dumbbell_front(j['handl']); p.dumbbell_front(j['handr'])


@ex('lateral_raise')
def _(p):
    p.ground()
    j = p.front('ghost', 50, 54, (100, 95), (95, 90))
    p.dumbbell(j['handl'], 'ghost'); p.dumbbell(j['handr'], 'ghost')
    j = p.front('body', 50, 54, (180, 180), (95, 90))
    p.dumbbell(j['handl']); p.dumbbell(j['handr'])


@ex('upright_row')
def _(p):
    p.ground()
    j = p.front('ghost', 50, 54, (95, 80), (95, 90))
    p.line((j['handl'][0] - 8, j['handl'][1]), (j['handr'][0] + 8, j['handr'][1]), 3, 'ghost')
    j = p.front('body', 50, 54, (200, 20), (95, 90))
    p.line((j['handl'][0] - 8, j['handl'][1]), (j['handr'][0] + 8, j['handr'][1]), 3, 'weight')


@ex('rear_delt')
def _(p):
    p.ground()
    p.rect(38, 60, 24, 5, 'equip'); p.line((50, 65), (50, G), 3, 'equip')
    j = p.front('ghost', 50, 60, (-30, -10), (100, 90))
    j = p.front('body', 50, 60, (180, 180), (100, 90))
    p.rect(j['handl'][0] - 2, j['handl'][1] - 5, 4, 10, 'weight', 1.5)
    p.rect(j['handr'][0] - 2, j['handr'][1] - 5, 4, 10, 'weight', 1.5)


@ex('barbell_curl')
def _(p):
    p.ground()
    j = p.side('ghost', (48, 52), -90, (95, 92), (90, 90))
    p.barbell(j['hand'], 'ghost')
    j = p.side('body', (48, 52), -90, (95, -55), (90, 90))
    p.barbell(j['hand'])


@ex('dumbbell_curl')
def _(p):
    p.ground()
    j = p.side('ghost', (48, 52), -90, (92, 90), (90, 90))
    p.dumbbell(j['hand'], 'ghost')
    j = p.side('body', (48, 52), -90, (92, -60), (90, 90))
    p.dumbbell(j['hand'])


@ex('french_press')
def _(p):
    p.ground(); p.bench(14, 70, 63)
    j = p.side('ghost', (58, 59), 180, (-100, 140), (28, 92))
    p.barbell(j['hand'], 'ghost')
    j = p.side('body', (58, 59), 180, (-100, -95), (28, 92))
    p.barbell(j['hand'])


@ex('triceps_pushdown')
def _(p):
    p.ground()
    p.line((86, 6), (86, G), 3.5, 'equip'); p.circle((82, 10), 2.5, 'weight')
    for c, arm in (('ghost', (95, -40)), ('body', (95, 82))):
        j = p.side(c, (50, 52), -86, arm, (90, 90))
        p.line(j['hand'], (82, 10), 1.4, 'ghost' if c == 'ghost' else 'weight')
        p.line(P(j['hand'], 180, 3), P(j['hand'], 0, 4), 3, 'ghost' if c == 'ghost' else 'weight')


@ex('crunches')
def _(p):
    p.ground()
    p.side('ghost', (56, 86), 180, (200, 180), (-55, 55))
    p.side('body', (56, 86), 215, (-60, -40), (-55, 55))


@ex('hanging_leg_raise')
def _(p):
    p.line((20, 6), (80, 6), 3.5, 'equip')
    p.line((22, 6), (22, G), 3, 'equip'); p.line((78, 6), (78, G), 3, 'equip')
    p.side('ghost', (50, 60), -90, (-90, -90), (92, 92))
    p.side('body', (50, 60), -90, (-90, -90), (0, 0), foot=-90)

@ex('plank')
def _(p):
    p.ground()
    p.side('body', (52, 74), 188, (90, 180), (8, 8), foot=90)


@ex('shrugs')
def _(p):
    p.ground()
    j = p.front('body', 50, 54, (95, 90), (95, 90), shoulder_dy=-3)
    p.dumbbell_front(j['handl']); p.dumbbell_front(j['handr'])
    p.arrow((30, 34), (30, 22)); p.arrow((70, 34), (70, 22))


@ex('treadmill')
def _(p):
    p.rect(14, 82, 66, 5, 'equip', 2)
    p.line((76, 82), (86, 40), 3, 'equip'); p.rect(80, 34, 12, 6, 'equip', 2)
    p.line((16, 87), (16, G), 3, 'equip'); p.line((76, 87), (76, G), 3, 'equip')
    p.side('body', (46, 46), -80, (60, -20), (60, 110), arm2=(130, 80), leg2=(115, 165), foot=0, foot2=-60)


@ex('exercise_bike')
def _(p):
    p.ground()
    p.circle((54, 76), 9, 'equip'); p.circle((54, 76), 6, 'weight')
    p.line((40, 52), (54, 76), 3, 'equip'); p.line((54, 76), (76, 50), 3, 'equip')
    p.line((30, 90), (80, 90), 4, 'equip'); p.line((54, 76), (54, 90), 3, 'equip')
    p.rect(32, 47, 14, 4, 'weight', 2)
    p.side('body', (40, 46), -65, (-5, 20), (20, 115), leg2=(65, 160))


@ex('rowing_machine')
def _(p):
    p.ground()
    p.rect(8, 82, 84, 4, 'equip', 2)
    p.circle((86, 74), 7, 'equip')
    p.rect(30, 75, 14, 4, 'weight', 1.5)
    p.side('ghost', (38, 72), -60, (0, 0), (-40, 40), foot=-60)
    p.side('body', (52, 72), -105, (150, -10), (-10, 20), foot=-80)


# ====================================================================== variants (v1.2)
def rails(p, x1, x2):
    """Smith machine guide rails."""
    p.line((x1, 6), (x1, G), 2, 'equip'); p.line((x2, 6), (x2, G), 2, 'equip')
    p.line((x1, 6), (x2, 6), 2, 'equip')


def wc(c):
    return 'ghost' if c == 'ghost' else 'weight'


@ex('smith_bench')
def _(p):
    rails(p, 30, 46)
    p.ground(); p.bench(14, 70, 63)
    for c, arm in (('ghost', (20, -100)), ('body', (-82, -90))):
        j = p.side(c, (58, 59), 180, arm, (28, 92))
        p.barbell(j['hand'], wc(c))


@ex('incline_db_press')
def _(p):
    p.ground()
    p.rect(50, 70, 30, 4.5, 'equip')
    p.line((63, 74), (63, G), 2.5, 'equip')
    p.line((52, 70), (25, 46), 6, 'equip')
    p.line((30, 52), (30, G), 2.5, 'equip')
    for c, arm in (('ghost', (35, -95)), ('body', (-80, -88))):
        j = p.side(c, (58, 66), 213, arm, (0, 95))
        p.dumbbell(j['hand'], wc(c))


@ex('decline_press')
def _(p):
    p.ground()
    p.line((70, 62), (22, 76), 6, 'equip')
    p.line((46, 70), (46, G), 2.5, 'equip')
    p.circle((80, 66), 3.5, 'equip')
    for c, arm in (('ghost', (60, -95)), ('body', (-85, -90))):
        j = p.side(c, (60, 57), 165, arm, (-35, 80))
        p.barbell(j['hand'], wc(c))


@ex('low_cable_fly')
def _(p):
    p.ground()
    p.line((8, 6), (8, G), 3, 'equip'); p.line((92, 6), (92, G), 3, 'equip')
    p.circle((10, 84), 2.5, 'weight'); p.circle((90, 84), 2.5, 'weight')
    p.front('ghost', 50, 54, (125, 120), (95, 90))
    j = p.front('body', 50, 54, (60, -10), (95, 90))
    p.line((10, 84), j['handl'], 1.4, 'weight'); p.line((90, 84), j['handr'], 1.4, 'weight')


@ex('db_pullover')
def _(p):
    p.ground(); p.bench(14, 70, 63)
    j = p.side('ghost', (58, 59), 180, (195, 192), (28, 92))
    p.dumbbell(j['hand'], 'ghost')
    j = p.side('body', (58, 59), 180, (-95, -95), (28, 92))
    p.dumbbell(j['hand'])


@ex('close_grip_pulldown')
def _(p):
    p.ground()
    p.line((50, 4), (50, 10), 1.4, 'weight')
    p.rect(40, 64, 20, 5, 'equip')
    p.rect(30, 56, 40, 4, 'equip')
    for c, arm in (('ghost', (-80, -85)), ('body', (115, -70))):
        j = p.front(c, 50, 64, arm, (85, 80))
        y = (j['handl'][1] + j['handr'][1]) / 2
        cc = wc(c)
        p.poly([j['handl'], (50, y - 6), j['handr']], 2.4, cc)
        p.line((50, 10 if c == 'ghost' else 4), (50, y - 6), 1.4, cc)


@ex('straight_arm_pulldown')
def _(p):
    p.ground()
    p.line((88, 4), (88, G), 3.5, 'equip'); p.circle((84, 8), 2.5, 'weight')
    for c, arm in (('ghost', (-45, -45)), ('body', (75, 75))):
        j = p.side(c, (44, 54), -75, arm, (95, 85))
        p.line(j['hand'], (84, 8), 1.4, wc(c))
        p.line(P(j['hand'], -90, 3), P(j['hand'], 90, 3), 3, wc(c))


@ex('machine_row')
def _(p):
    p.ground()
    p.rect(18, 66, 26, 5, 'equip'); p.line((30, 71), (30, G), 3, 'equip')
    p.rect(54, 34, 5, 22, 'equip', 2)
    p.line((86, 14), (86, G), 3, 'equip')
    for c, arm in (('ghost', (5, 0)), ('body', (165, 15))):
        j = p.side(c, (30, 62), -88, arm, (0, 90))
        p.line(j['hand'], (86, 30), 2.5, wc(c))


@ex('chest_supported_row')
def _(p):
    p.ground()
    p.line((30, 72), (64, 44), 5, 'equip')
    p.line((42, 64), (42, G), 2.5, 'equip')
    for c, arm in (('ghost', (90, 90)), ('body', (205, 100))):
        j = p.side(c, (34, 62), -38, arm, (100, 75))
        p.dumbbell(j['hand'], wc(c))


@ex('tbar_row')
def _(p):
    p.ground()
    for c, arm in (('ghost', (90, 90)), ('body', (195, 95))):
        j = p.side(c, (40, 52), -25, arm, (110, 75))
        p.line((88, G - 1), j['hand'], 2.6, wc(c))
        p.barbell(P(j['hand'], -10, 7), wc(c))


@ex('sumo_deadlift')
def _(p):
    p.ground()
    j = p.front('ghost', 50, 66, (100, 85), (125, 75))
    p.line((16, j['handl'][1]), (84, j['handr'][1]), 2.5, 'ghost')
    j = p.front('body', 50, 54, (100, 85), (118, 64))
    y = j['handl'][1]
    p.line((14, y), (86, y), 2.5, 'weight')
    p.rect(8, y - 9, 6, 18, 'weight', 1.5); p.rect(86, y - 9, 6, 18, 'weight', 1.5)


@ex('machine_shoulder_press')
def _(p):
    p.ground()
    p.rect(36, 62, 28, 5, 'equip'); p.line((50, 67), (50, G), 3, 'equip')
    p.rect(42, 18, 16, 44, 'equip', 3)
    p.line((14, 4), (14, G), 3, 'equip'); p.line((86, 4), (86, G), 3, 'equip')
    for c, arm in (('ghost', (180, -90)), ('body', (-110, -95))):
        j = p.front(c, 50, 60, arm, (98, 92))
        cc = wc(c)
        p.line(j['handl'], (14, j['handl'][1] + 4), 2.2, cc); p.line(j['handr'], (86, j['handr'][1] + 4), 2.2, cc)


@ex('cable_lateral_raise')
def _(p):
    p.ground()
    p.line((8, 6), (8, G), 3, 'equip'); p.circle((11, 86), 2.5, 'weight')
    p.front('ghost', 52, 54, (95, 90), (95, 90))
    j = p.front('body', 52, 54, (95, 90), (95, 90), arm_r=(-5, 0))
    p.line((11, 86), j['handr'], 1.4, 'weight')


@ex('front_raise')
def _(p):
    p.ground()
    j = p.side('ghost', (44, 52), -90, (88, 88), (90, 90))
    p.dumbbell(j['hand'], 'ghost')
    j = p.side('body', (44, 52), -90, (-2, -2), (90, 90))
    p.dumbbell(j['hand'])


@ex('face_pull')
def _(p):
    p.ground()
    p.line((88, 4), (88, G), 3.5, 'equip'); p.circle((85, 22), 2.5, 'weight')
    for c, arm in (('ghost', (-8, -5)), ('body', (195, -30))):
        j = p.side(c, (40, 52), -90, arm, (90, 90))
        p.line(j['hand'], (85, 22), 1.4, wc(c))


@ex('barbell_shrugs')
def _(p):
    p.ground()
    j = p.front('body', 50, 54, (95, 90), (95, 90), shoulder_dy=-3)
    y = j['handl'][1]
    p.line((14, y), (86, y), 2.5, 'weight')
    p.rect(8, y - 8, 6, 16, 'weight', 1.5); p.rect(86, y - 8, 6, 16, 'weight', 1.5)
    p.arrow((30, 34), (30, 22)); p.arrow((70, 34), (70, 22))


@ex('smith_shrugs')
def _(p):
    rails(p, 10, 90)
    p.ground()
    j = p.front('body', 50, 54, (95, 90), (95, 90), shoulder_dy=-3)
    y = j['handl'][1]
    p.line((10, y), (90, y), 2.8, 'weight')
    p.arrow((30, 34), (30, 22)); p.arrow((70, 34), (70, 22))


@ex('preacher_curl')
def _(p):
    p.ground()
    p.rect(16, 66, 26, 5, 'equip'); p.line((28, 71), (28, G), 3, 'equip')
    p.line((44, 38), (62, 60), 6, 'equip'); p.line((56, 60), (56, G), 3, 'equip')
    for c, fa in (('ghost', 50), ('body', -60)):
        j = p.side(c, (28, 62), -88, (45, fa), (0, 90))
        p.barbell(j['hand'], wc(c))


@ex('hammer_curl')
def _(p):
    p.ground()
    for c, fa in (('ghost', 90), ('body', -60)):
        j = p.side(c, (48, 52), -90, (92, fa), (90, 90))
        x, y = j['hand']
        p.rect(x - 2.5, y - 6, 5, 12, wc(c), 2)


@ex('concentration_curl')
def _(p):
    p.ground(); p.bench(10, 44, 66)
    for c, fa in (('ghost', 92), ('body', -95)):
        j = p.side(c, (28, 62), -55, (85, fa), (0, 95))
        p.dumbbell(j['hand'], wc(c))


@ex('cable_curl')
def _(p):
    p.ground()
    p.line((88, 6), (88, G), 3.5, 'equip'); p.circle((85, 84), 2.5, 'weight')
    for c, fa in (('ghost', 90), ('body', -55)):
        j = p.side(c, (46, 52), -90, (95, fa), (90, 90))
        p.line(j['hand'], (85, 84), 1.4, wc(c))
        p.line(P(j['hand'], -90, 3), P(j['hand'], 90, 3), 3, wc(c))


@ex('wrist_curl')
def _(p):
    p.ground(); p.bench(14, 50, 66)
    p.side('body', (30, 62), -60, (70, -5), (0, 95))
    j = p.side('body', (30, 62), -60, (70, -5), (0, 95))
    p.barbell(P(j['hand'], -30, 4))
    p.arrow((78, 70), (78, 56))


@ex('rope_pushdown')
def _(p):
    p.ground()
    p.line((86, 6), (86, G), 3.5, 'equip'); p.circle((82, 10), 2.5, 'weight')
    for c, arm in (('ghost', (95, -40)), ('body', (95, 82))):
        j = p.side(c, (50, 52), -86, arm, (90, 90))
        p.line(j['hand'], (82, 10), 1.4, wc(c))
        p.poly([P(j['hand'], -60, 5), j['hand'], P(j['hand'], 200, 4)], 2.6, wc(c))


@ex('one_arm_pushdown')
def _(p):
    p.ground()
    p.line((86, 6), (86, G), 3.5, 'equip'); p.circle((82, 10), 2.5, 'weight')
    for c, arm in (('ghost', (95, -40)), ('body', (95, 82))):
        j = p.side(c, (50, 52), -86, arm, (90, 90))
        p.line(j['hand'], (82, 10), 1.4, wc(c))
        p.circle(j['hand'], 3, wc(c))


@ex('overhead_cable_ext')
def _(p):
    p.ground()
    p.line((10, 30), (10, G), 3.5, 'equip'); p.circle((13, 64), 2.5, 'weight')
    for c, fa in (('ghost', 150), ('body', -45)):
        j = p.side(c, (50, 52), -80, (-60, fa), (95, 85))
        p.line(j['hand'], (13, 64), 1.4, wc(c))


@ex('db_overhead_ext')
def _(p):
    p.ground()
    p.rect(20, 66, 26, 5, 'equip'); p.line((18, 66), (16, 30), 6, 'equip'); p.line((32, 71), (32, G), 3, 'equip')
    for c, fa in (('ghost', 120), ('body', -88)):
        j = p.side(c, (28, 62), -88, (-80, fa), (0, 90))
        p.dumbbell(j['hand'], wc(c))


@ex('db_kickback')
def _(p):
    p.ground(); p.bench(26, 86, 71)
    j = p.side('body', (40, 50), -20, (165, 100), (90, 180), arm2=(92, 88), leg2=(92, 89), foot=0, foot2=0)
    p.dumbbell(j['hand'], 'ghost')
    e = j['elbow']
    h = P(e, 170, FARM)
    p.line(e, h, 4.4, 'body'); p.dumbbell(h)


@ex('bench_dips')
def _(p):
    p.ground(); p.rect(6, 60, 26, 5, 'equip'); p.line((12, 65), (12, G), 3, 'equip'); p.line((26, 65), (26, G), 3, 'equip')
    p.side('ghost', (40, 72), -88, (160, 70), (0, 66), foot=-45)
    p.side('body', (40, 60), -88, (113, 113), (47, 47), foot=-45)

@ex('triceps_machine')
def _(p):
    p.ground()
    p.rect(20, 66, 26, 5, 'equip'); p.line((18, 66), (16, 28), 6, 'equip'); p.line((32, 71), (32, G), 3, 'equip')
    p.line((62, 14), (62, G), 3, 'equip')
    for c, arm in (('ghost', (100, -45)), ('body', (95, 85))):
        j = p.side(c, (30, 62), -88, arm, (0, 90))
        p.line(j['hand'], (62, j['hand'][1] - 4), 2.5, wc(c))


@ex('front_squat')
def _(p):
    p.ground()
    j = p.side('ghost', (48, 50), -88, (10, 200), (90, 90))
    p.barbell(P(j['shoulder'], 0, 5), 'ghost')
    j = p.side('body', (34, 68), -68, (15, 200), (0, 110))
    p.barbell(P(j['shoulder'], 0, 5))


@ex('smith_squat')
def _(p):
    rails(p, 26, 42)
    p.ground()
    j = p.side('ghost', (40, 50), -88, (160, 60), (85, 95))
    p.barbell(P(j['shoulder'], 180, 2), 'ghost')
    j = p.side('body', (28, 68), -62, (165, 40), (0, 110))
    p.barbell(P(j['shoulder'], 160, 2))


@ex('goblet_squat')
def _(p):
    p.ground()
    j = p.side('ghost', (48, 50), -88, (70, -90), (90, 90))
    p.dumbbell(j['hand'], 'ghost')
    j = p.side('body', (34, 68), -62, (70, -70), (0, 110))
    p.dumbbell(j['hand'])


@ex('bulgarian_split_squat')
def _(p):
    p.ground(); p.rect(4, 70, 24, 5, 'equip'); p.line((10, 75), (10, G), 3, 'equip'); p.line((22, 75), (22, G), 3, 'equip')
    j = p.side('body', (46, 66), -85, (92, 90), (5, 95), leg2=(120, 220), foot2=-40)
    p.dumbbell(j['hand'])
    p.arrow((78, 50), (78, 64))

@ex('seated_leg_curl')
def _(p):
    p.ground()
    p.rect(18, 62, 30, 5, 'equip'); p.line((16, 62), (14, 26), 6, 'equip'); p.line((32, 67), (32, G), 3, 'equip')
    for c, sh in (('ghost', 0), ('body', 100)):
        j = p.side(c, (28, 58), -92, (60, 20), (0, sh), foot=sh - 90)
        p.circle(P(j['ankle'], sh + 90, 4), 3.5, wc(c))


@ex('seated_calf_raise')
def _(p):
    p.ground()
    p.rect(18, 62, 28, 5, 'equip'); p.line((30, 67), (30, G), 3, 'equip')
    p.rect(58, 84, 14, 6, 'equip', 1)
    p.side('ghost', (32, 58), -88, (70, 0), (0, 90), foot=-5)
    j = p.side('body', (32, 56), -88, (70, 0), (-6, 90), foot=40)
    p.rect(j['knee'][0] - 7, j['knee'][1] - 8, 14, 5, 'weight', 2)
    p.arrow((84, 74), (84, 60))


@ex('cable_kickback')
def _(p):
    p.ground()
    p.line((88, 6), (88, G), 3.5, 'equip'); p.circle((85, 86), 2.5, 'weight')
    p.side('body', (50, 52), -84, (5, 0), (90, 90))
    p.side('ghost', (50, 52), -84, (5, 0), (92, 92))
    j = p.side('body', (50, 52), -84, (5, 0), (125, 120), foot=60)
    p.line(j['ankle'], (85, 86), 1.4, 'weight')


@ex('cable_crunch')
def _(p):
    p.ground()
    p.line((82, 4), (82, G), 3.5, 'equip'); p.circle((78, 8), 2.5, 'weight')
    for c, torso, arm in (('ghost', -80, (-30, -150)), ('body', -15, (20, -130))):
        j = p.side(c, (36, 69), torso, arm, (90, 180), foot=0)
        p.line(j['hand'], (78, 8), 1.4, wc(c))


@ex('lying_leg_raise')
def _(p):
    p.ground()
    p.side('ghost', (52, 86), 180, (0, 0), (-3, 0), foot=-90)
    p.side('body', (52, 86), 180, (0, 0), (-88, -88), foot=0)


@ex('elliptical')
def _(p):
    p.rect(10, 84, 72, 5, 'equip', 2)
    p.line((76, 84), (82, 30), 3, 'equip'); p.circle((80, 76), 7, 'equip')
    p.line((30, 72), (60, 82), 3, 'weight')
    p.side('body', (44, 46), -82, (-10, -40), (70, 100), arm2=(30, -20), leg2=(105, 95), foot=0, foot2=0)

# ====================================================================== output

def fit(pic, lo=3.0, hi=97.0):
    xs, ys = [], []
    for it in pic.items:
        if it[0] == 'poly':
            w = it[2] / 2
            for x, y in it[1]:
                xs += [x - w, x + w]; ys += [y - w, y + w]
        elif it[0] == 'circle':
            (x, y), r = it[1], it[2]
            xs += [x - r, x + r]; ys += [y - r, y + r]
        else:
            x, y, w, h = it[1]
            xs += [x, x + w]; ys += [y, y + h]
    x0, x1, y0, y1 = min(xs), max(xs), min(ys), max(ys)
    s = min(1.0, (hi - lo) / (x1 - x0), (hi - lo) / (y1 - y0))
    dx = (lo + hi) / 2 - (x0 + x1) / 2 * s if (x0 < lo or x1 > hi or s < 1) else 0
    dy = (lo + hi) / 2 - (y0 + y1) / 2 * s if (y0 < lo or y1 > hi or s < 1) else 0
    if s == 1.0 and dx == 0 and dy == 0:
        return
    t = lambda p: (p[0] * s + dx, p[1] * s + dy)
    out = []
    for it in pic.items:
        if it[0] == 'poly':
            out.append(('poly', [t(q) for q in it[1]], it[2] * s, it[3]))
        elif it[0] == 'circle':
            out.append(('circle', t(it[1]), it[2] * s, it[3]))
        else:
            x, y, w, h = it[1]
            nx, ny = t((x, y))
            out.append(('rect', (nx, ny, w * s, h * s), it[2] * s, it[3]))
    pic.items = out

def fmt(v):
    s = ('%.2f' % v).rstrip('0').rstrip('.')
    return '0' if s == '-0' else s


def to_vector(pic):
    out = ['<?xml version="1.0" encoding="utf-8"?>',
           '<!-- Generated by tools/gen_pictograms.py, do not edit. -->',
           '<vector xmlns:android="http://schemas.android.com/apk/res/android"',
           '    android:width="100dp" android:height="100dp"',
           '    android:viewportWidth="100" android:viewportHeight="100">']
    for it in pic.items:
        kind, c = it[0], COLORS[it[-1]][0]
        if kind == 'poly':
            d = 'M' + ' L'.join(fmt(x) + ',' + fmt(y) for x, y in it[1])
            out.append('    <path android:pathData="%s" android:strokeColor="%s" android:strokeWidth="%s"'
                       ' android:strokeLineCap="round" android:strokeLineJoin="round" />' % (d, c, fmt(it[2])))
        elif kind == 'circle':
            (x, y), r = it[1], it[2]
            d = 'M%s,%s a%s,%s 0 1,0 %s,0 a%s,%s 0 1,0 %s,0 Z' % (
                fmt(x - r), fmt(y), fmt(r), fmt(r), fmt(2 * r), fmt(r), fmt(r), fmt(-2 * r))
            out.append('    <path android:pathData="%s" android:fillColor="%s" />' % (d, c))
        elif kind == 'rect':
            (x, y, w, h), r = it[1], it[2]
            r = min(r, w / 2, h / 2)
            d = ('M%s,%s h%s a%s,%s 0 0,1 %s,%s v%s a%s,%s 0 0,1 %s,%s h%s a%s,%s 0 0,1 %s,%s v%s'
                 ' a%s,%s 0 0,1 %s,%s Z') % (
                fmt(x + r), fmt(y), fmt(w - 2 * r), fmt(r), fmt(r), fmt(r), fmt(r), fmt(h - 2 * r),
                fmt(r), fmt(r), fmt(-r), fmt(r), fmt(-(w - 2 * r)), fmt(r), fmt(r), fmt(-r), fmt(-r),
                fmt(-(h - 2 * r)), fmt(r), fmt(r), fmt(r), fmt(-r))
            out.append('    <path android:pathData="%s" android:fillColor="%s" />' % (d, c))
    out.append('</vector>')
    return '\n'.join(out) + '\n'


def to_svg(pic):
    out = ['<svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 100 100" width="150" height="150">']
    for it in pic.items:
        kind, c = it[0], COLORS[it[-1]][1]
        if kind == 'poly':
            pts = ' '.join('%s,%s' % (fmt(x), fmt(y)) for x, y in it[1])
            out.append('<polyline points="%s" fill="none" stroke="%s" stroke-width="%s" '
                       'stroke-linecap="round" stroke-linejoin="round"/>' % (pts, c, fmt(it[2])))
        elif kind == 'circle':
            out.append('<circle cx="%s" cy="%s" r="%s" fill="%s"/>' % (fmt(it[1][0]), fmt(it[1][1]), fmt(it[2]), c))
        elif kind == 'rect':
            x, y, w, h = it[1]
            out.append('<rect x="%s" y="%s" width="%s" height="%s" rx="%s" fill="%s"/>' % (
                fmt(x), fmt(y), fmt(w), fmt(h), fmt(min(it[2], w / 2, h / 2)), c))
    out.append('</svg>')
    return ''.join(out)


def main():
    drawable = os.path.join(ROOT, 'res', 'drawable')
    cells = []
    only = sys.argv[2].split(',') if len(sys.argv) > 2 else None
    for key, fn in EX.items():
        pic = Pic()
        fn(pic)
        fit(pic)
        with open(os.path.join(drawable, 'ex_%s.xml' % key), 'w', encoding='utf-8') as f:
            f.write(to_vector(pic))
        if only and key not in only:
            continue
        cells.append('<div style="display:inline-block;margin:4px;text-align:center;font:11px sans-serif;'
                     'background:#E4E9FF;border-radius:10px;padding:4px">%s<br>%s</div>' % (to_svg(pic), key))
    if len(sys.argv) > 1:
        with open(sys.argv[1], 'w', encoding='utf-8') as f:
            f.write('<html><body style="margin:6px;background:#fff;width:1200px">%s</body></html>' % ''.join(cells))
    print('%d pictograms' % len(EX))


if __name__ == '__main__':
    main()
