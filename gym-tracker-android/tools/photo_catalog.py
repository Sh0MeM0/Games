#!/usr/bin/env python3
"""Real exercise photos for the reference (from the Free Exercise DB).

Source: https://github.com/yuhonas/free-exercise-db (derived from github.com/wrkout/exercises.json).
Both projects publish the data and images as public domain (The Unlicense). Each exercise has
two photos: start (0.jpg) and end (1.jpg) of the movement.

This script
  1. maps every exercise of Catalog.java to a photo set (EXISTING),
  2. adds more exercises with Russian names (NEW here, MORE in tools/more_exercises.py),
  3. downloads the photos, scales them down and stores them as WebP: assets/ex/<id>_0.webp and _1.webp,
  4. writes src/app/gymlog/PhotoData.java.

Usage: python3 tools/photo_catalog.py [path/to/exercises.json]
       (without a path the JSON is downloaded from GitHub)
"""
import io
import json
import os
import sys
import urllib.request

ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
RAW = 'https://raw.githubusercontent.com/yuhonas/free-exercise-db/main/'
WIDTH = 340      # px; enough for the tiles and the info screen
QUALITY = 60     # WebP quality: about 40% smaller than the JPEGs at the same look
sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
from more_exercises import MORE  # noqa: E402

# dataset primary muscle -> app muscle group
GROUP_OF = {'abdominals': 'abs', 'abductors': 'glutes', 'adductors': 'legs', 'biceps': 'biceps',
            'calves': 'calves', 'chest': 'chest', 'forearms': 'forearms', 'glutes': 'glutes',
            'hamstrings': 'legs', 'lats': 'back', 'lower back': 'back', 'middle back': 'back', 'neck': 'traps',
            'quadriceps': 'legs', 'shoulders': 'shoulders', 'traps': 'traps', 'triceps': 'triceps'}

MUSCLES = {
    'abdominals': 'мышцы пресса', 'abductors': 'отводящие мышцы бедра', 'adductors': 'приводящие мышцы бедра',
    'biceps': 'бицепс', 'calves': 'икры', 'chest': 'грудные', 'forearms': 'предплечья', 'glutes': 'ягодичные',
    'hamstrings': 'задняя поверхность бедра', 'lats': 'широчайшие', 'lower back': 'поясница',
    'middle back': 'середина спины', 'neck': 'шея', 'quadriceps': 'квадрицепсы', 'shoulders': 'дельты',
    'traps': 'трапеции', 'triceps': 'трицепс',
}
# equipment buckets used by the filter chips in the app
EQUIP = {'machine': 'machine', 'cable': 'cable', 'barbell': 'barbell', 'e-z curl bar': 'barbell',
         'dumbbell': 'dumbbell', 'kettlebells': 'dumbbell', 'body only': 'body', 'other': 'other', None: 'other'}

# Catalog.java key -> photo set id
EXISTING = {
    'bench_press': 'Barbell_Bench_Press_-_Medium_Grip', 'dumbbell_press': 'Dumbbell_Bench_Press',
    'incline_press': 'Barbell_Incline_Bench_Press_-_Medium_Grip', 'dumbbell_fly': 'Dumbbell_Flyes',
    'pec_deck': 'Butterfly', 'cable_crossover': 'Cable_Crossover', 'chest_press_machine': 'Machine_Bench_Press',
    'dips': 'Dips_-_Chest_Version', 'pushups': 'Pushups', 'lat_pulldown': 'Wide-Grip_Lat_Pulldown',
    'seated_row': 'Seated_Cable_Rows', 'bent_over_row': 'Bent_Over_Barbell_Row',
    'one_arm_row': 'One-Arm_Dumbbell_Row', 'pullups': 'Pullups', 'deadlift': 'Barbell_Deadlift',
    'romanian_deadlift': 'Romanian_Deadlift', 'hyperextension': 'Hyperextensions_Back_Extensions',
    'squat': 'Barbell_Squat', 'leg_press': 'Leg_Press', 'leg_extension': 'Leg_Extensions',
    'leg_curl': 'Lying_Leg_Curls', 'lunges': 'Dumbbell_Lunges', 'hack_squat': 'Hack_Squat',
    'calf_raise': 'Standing_Calf_Raises', 'hip_adduction': 'Thigh_Adductor', 'hip_abduction': 'Thigh_Abductor',
    'hip_thrust': 'Barbell_Hip_Thrust', 'overhead_press': 'Standing_Military_Press',
    'db_shoulder_press': 'Seated_Dumbbell_Press', 'lateral_raise': 'Side_Lateral_Raise',
    'upright_row': 'Upright_Barbell_Row', 'rear_delt': 'Reverse_Machine_Flyes', 'shrugs': 'Dumbbell_Shrug',
    'barbell_curl': 'Barbell_Curl', 'dumbbell_curl': 'Dumbbell_Bicep_Curl', 'french_press': 'EZ-Bar_Skullcrusher',
    'triceps_pushdown': 'Triceps_Pushdown', 'crunches': 'Crunches', 'hanging_leg_raise': 'Hanging_Leg_Raise',
    'plank': 'Plank', 'treadmill': 'Running_Treadmill', 'exercise_bike': 'Bicycling_Stationary',
    'rowing_machine': 'Rowing_Stationary', 'close_grip_bench': 'Close-Grip_Barbell_Bench_Press',
    'smith_bench': 'Smith_Machine_Bench_Press', 'incline_db_press': 'Incline_Dumbbell_Press',
    'decline_press': 'Decline_Barbell_Bench_Press', 'low_cable_fly': 'Low_Cable_Crossover',
    'db_pullover': 'Straight-Arm_Dumbbell_Pullover', 'close_grip_pulldown': 'V-Bar_Pulldown',
    'reverse_grip_pulldown': 'Underhand_Cable_Pulldowns', 'straight_arm_pulldown': 'Straight-Arm_Pulldown',
    'wide_seated_row': 'Seated_Cable_Rows', 'one_arm_cable_row': 'Seated_One-arm_Cable_Pulley_Rows',
    'machine_row': 'Leverage_Iso_Row', 'chest_supported_row': 'Dumbbell_Incline_Row',
    'tbar_row': 'T-Bar_Row_with_Handle', 'chinups': 'Chin-Up', 'sumo_deadlift': 'Sumo_Deadlift',
    'arnold_press': 'Arnold_Dumbbell_Press', 'machine_shoulder_press': 'Leverage_Shoulder_Press',
    'cable_lateral_raise': 'Standing_Low-Pulley_Deltoid_Raise', 'front_raise': 'Front_Dumbbell_Raise',
    'bent_over_lateral': 'Seated_Bent-Over_Rear_Delt_Raise', 'face_pull': 'Face_Pull',
    'barbell_shrugs': 'Barbell_Shrug', 'smith_shrugs': 'Smith_Machine_Behind_the_Back_Shrug',
    'preacher_curl': 'Preacher_Curl', 'hammer_curl': 'Hammer_Curls', 'concentration_curl': 'Concentration_Curls',
    'cable_curl': 'Standing_Biceps_Cable_Curl', 'reverse_curl': 'Reverse_Barbell_Curl',
    'wrist_curl': 'Palms-Up_Barbell_Wrist_Curl_Over_A_Bench', 'rope_pushdown': 'Triceps_Pushdown_-_Rope_Attachment',
    'reverse_pushdown': 'Reverse_Grip_Triceps_Pushdown', 'one_arm_pushdown': 'Cable_One_Arm_Tricep_Extension',
    'overhead_cable_ext': 'Cable_Rope_Overhead_Triceps_Extension', 'db_overhead_ext': 'Seated_Triceps_Press',
    'db_kickback': 'Tricep_Dumbbell_Kickback', 'bench_dips': 'Bench_Dips', 'triceps_machine': 'Dip_Machine',
    'front_squat': 'Front_Barbell_Squat', 'smith_squat': 'Smith_Machine_Squat', 'goblet_squat': 'Goblet_Squat',
    'bulgarian_split_squat': 'Smith_Single-Leg_Split_Squat', 'seated_leg_curl': 'Seated_Leg_Curl',
    'seated_calf_raise': 'Seated_Calf_Raise', 'cable_kickback': 'One-Legged_Cable_Kickback',
    'cable_crunch': 'Cable_Crunch', 'lying_leg_raise': 'Flat_Bench_Lying_Leg_Raise', 'elliptical': 'Elliptical_Trainer',
}

# (key, photo set id, Russian title, groups, own body weight, extra synonyms)
NEW = [
    # chest
    ('wide_bench', 'Wide-Grip_Barbell_Bench_Press', 'Жим штанги лёжа широким хватом', 'chest', False, ['жим широким хватом']),
    ('neutral_db_press', 'Dumbbell_Bench_Press_with_Neutral_Grip', 'Жим гантелей лёжа нейтральным хватом', 'chest triceps', False, ['жим гантелей ладонями друг к другу']),
    ('decline_db_press', 'Decline_Dumbbell_Bench_Press', 'Жим гантелей с обратным наклоном', 'chest', False, ['жим гантелей головой вниз']),
    ('incline_db_fly', 'Incline_Dumbbell_Flyes', 'Разводка гантелей на наклонной скамье', 'chest', False, ['разводка на наклонной']),
    ('decline_db_fly', 'Decline_Dumbbell_Flyes', 'Разводка гантелей с обратным наклоном', 'chest', False, []),
    ('smith_incline_press', 'Smith_Machine_Incline_Bench_Press', 'Жим на наклонной скамье в Смите', 'chest shoulders', False, ['наклонный жим в смите']),
    ('smith_decline_press', 'Smith_Machine_Decline_Press', 'Жим с обратным наклоном в Смите', 'chest', False, []),
    ('lever_chest_press', 'Leverage_Chest_Press', 'Рычажный жим от груди (Хаммер)', 'chest', False, ['рычажный жим от груди', 'жим в хаммере']),
    ('lever_incline_press', 'Leverage_Incline_Chest_Press', 'Рычажный жим на верх груди', 'chest shoulders', False, ['наклонный жим в тренажёре', 'жим в хаммере на наклоне']),
    ('lever_decline_press', 'Leverage_Decline_Chest_Press', 'Рычажный жим на низ груди', 'chest', False, ['жим вниз в тренажёре']),
    ('cable_chest_press', 'Cable_Chest_Press', 'Жим от груди на блоках', 'chest', False, ['жим в кроссовере']),
    ('flat_cable_fly', 'Flat_Bench_Cable_Flyes', 'Разводка на блоках лёжа на скамье', 'chest', False, ['сведение на блоках лёжа']),
    ('incline_cable_fly', 'Incline_Cable_Flye', 'Разводка на блоках на наклонной скамье', 'chest', False, []),
    ('one_arm_crossover', 'Single-Arm_Cable_Crossover', 'Кроссовер одной рукой', 'chest', False, ['сведение одной рукой на блоке']),
    ('incline_pushup', 'Incline_Push-Up', 'Отжимания с упором в скамью', 'chest', True, ['отжимания от скамьи руками']),
    ('decline_pushup', 'Decline_Push-Up', 'Отжимания с ногами на скамье', 'chest shoulders', True, ['отжимания ноги на возвышении']),
    ('wide_pushup', 'Push-Up_Wide', 'Отжимания широким хватом', 'chest', True, []),
    # back
    ('behind_neck_pulldown', 'Wide-Grip_Pulldown_Behind_The_Neck', 'Тяга верхнего блока за голову', 'back', False, ['тяга за голову']),
    ('one_arm_pulldown', 'One_Arm_Lat_Pulldown', 'Тяга верхнего блока одной рукой', 'back', False, []),
    ('rope_straight_pulldown', 'Rope_Straight-Arm_Pulldown', 'Пуловер в блоке с канатом', 'back', False, ['тяга каната прямыми руками']),
    ('lever_high_row', 'Leverage_High_Row', 'Рычажная тяга сверху (верхняя тяга в тренажёре)', 'back', False, ['верхняя тяга в хаммере']),
    ('lying_tbar_row', 'Lying_T-Bar_Row', 'Тяга Т-грифа лёжа с упором грудью', 'back', False, ['т тяга лёжа', 'тяга т грифа с упором']),
    ('smith_row', 'Smith_Machine_Bent_Over_Row', 'Тяга в наклоне в Смите', 'back', False, ['тяга в смите']),
    ('db_bent_row', 'Bent_Over_Two-Dumbbell_Row', 'Тяга гантелей в наклоне', 'back', False, ['тяга двух гантелей в наклоне']),
    ('reverse_bent_row', 'Reverse_Grip_Bent-Over_Rows', 'Тяга штанги в наклоне обратным хватом', 'back biceps', False, ['тяга йейтса']),
    ('one_arm_landmine_row', 'Bent_Over_One-Arm_Long_Bar_Row', 'Тяга конца штанги одной рукой', 'back', False, ['тяга грифа одной рукой', 'мид роу']),
    ('vbar_pullup', 'V-Bar_Pullup', 'Подтягивания узким параллельным хватом', 'back biceps', True, ['подтягивания с v рукоятью']),
    ('behind_neck_pullup', 'Wide-Grip_Rear_Pull-Up', 'Подтягивания за голову', 'back', True, []),
    ('inverted_row', 'Inverted_Row', 'Австралийские подтягивания', 'back', True, ['горизонтальные подтягивания', 'тяга в висе лёжа']),
    ('rack_pull', 'Rack_Pulls', 'Тяга с плинтов (частичная становая)', 'back', False, ['тяга с упоров', 'rack pull']),
    ('trap_bar_deadlift', 'Trap_Bar_Deadlift', 'Становая тяга с трэп-грифом', 'legs back glutes', False, ['трэп гриф', 'шестиугольный гриф']),
    ('good_morning', 'Good_Morning', 'Наклоны со штангой на плечах (гуд морнинг)', 'back legs', False, ['гуд морнинг', 'доброе утро']),
    ('stiff_leg_deadlift', 'Stiff-Legged_Barbell_Deadlift', 'Становая тяга на прямых ногах', 'legs back', False, ['становая на прямых ногах']),
    ('db_romanian_deadlift', 'Stiff-Legged_Dumbbell_Deadlift', 'Румынская тяга с гантелями', 'legs glutes', False, ['мёртвая тяга с гантелями']),
    ('reverse_hyper', 'Reverse_Hyperextension', 'Обратная гиперэкстензия', 'glutes back', False, []),
    # shoulders & traps
    ('seated_barbell_press', 'Seated_Barbell_Military_Press', 'Жим штанги сидя', 'shoulders', False, ['жим штанги сидя на плечи']),
    ('behind_neck_press', 'Standing_Barbell_Press_Behind_Neck', 'Жим штанги из-за головы', 'shoulders', False, ['жим из-за головы']),
    ('smith_shoulder_press', 'Smith_Machine_Overhead_Shoulder_Press', 'Жим сидя в Смите (на плечи)', 'shoulders', False, ['жим на плечи в смите']),
    ('machine_military_press', 'Machine_Shoulder_Military_Press', 'Жим вверх в тренажёре (вертикальный)', 'shoulders', False, []),
    ('standing_db_press', 'Standing_Dumbbell_Press', 'Жим гантелей стоя', 'shoulders', False, []),
    ('one_arm_db_press', 'Dumbbell_One-Arm_Shoulder_Press', 'Жим гантели одной рукой', 'shoulders', False, []),
    ('cable_shoulder_press', 'Seated_Cable_Shoulder_Press', 'Жим на плечи на нижних блоках', 'shoulders', False, []),
    ('seated_lateral_raise', 'Seated_Side_Lateral_Raise', 'Махи гантелями в стороны сидя', 'shoulders', False, []),
    ('one_arm_lateral_raise', 'One-Arm_Side_Laterals', 'Мах одной рукой в сторону с гантелью', 'shoulders', False, []),
    ('plate_front_raise', 'Front_Plate_Raise', 'Подъём блина перед собой', 'shoulders', False, ['блин перед собой']),
    ('cable_front_raise', 'Front_Cable_Raise', 'Подъём руки перед собой на нижнем блоке', 'shoulders', False, []),
    ('barbell_front_raise', 'Standing_Front_Barbell_Raise_Over_Head', 'Подъём штанги перед собой', 'shoulders', False, []),
    ('lying_rear_raise', 'Dumbbell_Lying_Rear_Lateral_Raise', 'Разведение гантелей лёжа на наклонной (задняя дельта)', 'shoulders', False, []),
    ('cable_rear_fly', 'Cable_Rear_Delt_Fly', 'Разведение на заднюю дельту в кроссовере', 'shoulders', False, ['обратный кроссовер']),
    ('cable_upright_row', 'Upright_Cable_Row', 'Тяга к подбородку на нижнем блоке', 'shoulders traps', False, []),
    ('smith_upright_row', 'Smith_Machine_Upright_Row', 'Тяга к подбородку в Смите', 'shoulders traps', False, []),
    ('db_upright_row', 'Standing_Dumbbell_Upright_Row', 'Тяга гантелей к подбородку', 'shoulders traps', False, []),
    ('cable_external_rotation', 'External_Rotation_with_Cable', 'Наружная ротация плеча на блоке', 'shoulders', False, ['ротатор на блоке']),
    ('db_external_rotation', 'External_Rotation', 'Наружная ротация плеча с гантелью', 'shoulders', False, ['ротаторы']),
    ('lever_shrug', 'Leverage_Shrug', 'Шраги в рычажном тренажёре', 'traps', False, []),
    ('cable_shrug', 'Cable_Shrugs', 'Шраги на нижнем блоке', 'traps', False, []),
    ('behind_back_shrug', 'Barbell_Shrug_Behind_The_Back', 'Шраги со штангой за спиной', 'traps', False, []),
    # biceps & forearms
    ('ez_curl', 'EZ-Bar_Curl', 'Подъём EZ-грифа на бицепс', 'biceps', False, ['изогнутый гриф на бицепс']),
    ('wide_barbell_curl', 'Wide-Grip_Standing_Barbell_Curl', 'Подъём штанги на бицепс широким хватом', 'biceps', False, []),
    ('alt_db_curl', 'Dumbbell_Alternate_Bicep_Curl', 'Попеременный подъём гантелей на бицепс', 'biceps', False, ['попеременные сгибания']),
    ('incline_db_curl', 'Incline_Dumbbell_Curl', 'Сгибания с гантелями на наклонной скамье', 'biceps', False, []),
    ('seated_db_curl', 'Seated_Dumbbell_Curl', 'Сгибания с гантелями сидя', 'biceps', False, []),
    ('cross_hammer_curl', 'Cross_Body_Hammer_Curl', 'Молотки поперёк корпуса', 'biceps forearms', False, []),
    ('rope_hammer_curl', 'Cable_Hammer_Curls_-_Rope_Attachment', 'Молотки на блоке с канатом', 'biceps forearms', False, []),
    ('spider_curl', 'Spider_Curl', 'Паучьи сгибания', 'biceps', False, ['спайдер'] ),
    ('machine_curl', 'Machine_Bicep_Curl', 'Сгибания на бицепс в тренажёре', 'biceps', False, ['бицепс в тренажёре']),
    ('machine_preacher_curl', 'Machine_Preacher_Curls', 'Скотт в тренажёре', 'biceps', False, []),
    ('cable_preacher_curl', 'Cable_Preacher_Curl', 'Скотт на нижнем блоке', 'biceps', False, []),
    ('db_preacher_curl', 'One_Arm_Dumbbell_Preacher_Curl', 'Скотт с гантелью одной рукой', 'biceps', False, []),
    ('high_cable_curl', 'High_Cable_Curls', 'Сгибания на верхних блоках', 'biceps', False, ['бицепс в кроссовере сверху']),
    ('one_arm_cable_curl', 'Standing_One-Arm_Cable_Curl', 'Сгибание одной рукой на нижнем блоке', 'biceps', False, []),
    ('zottman_curl', 'Zottman_Curl', 'Сгибания Зоттмана', 'biceps forearms', False, ['зоттман']),
    ('drag_curl', 'Drag_Curl', 'Драг-сгибания со штангой', 'biceps', False, ['драг керл']),
    ('reverse_wrist_curl', 'Palms-Down_Wrist_Curl_Over_A_Bench', 'Разгибание кистей со штангой', 'forearms', False, ['кисти хватом сверху']),
    ('db_wrist_curl', 'Seated_Dumbbell_Palms-Up_Wrist_Curl', 'Сгибание кистей с гантелями', 'forearms', False, []),
    ('wrist_roller', 'Wrist_Roller', 'Ролик для предплечий', 'forearms', False, ['наматывание груза']),
    # triceps
    ('vbar_pushdown', 'Triceps_Pushdown_-_V-Bar_Attachment', 'Разгибание рук на блоке с V-рукоятью', 'triceps', False, []),
    ('lying_cable_ext', 'Cable_Lying_Triceps_Extension', 'Французский жим на блоке лёжа', 'triceps', False, []),
    ('low_one_arm_overhead_ext', 'Standing_Low-Pulley_One-Arm_Triceps_Extension', 'Разгибание одной рукой из-за головы на нижнем блоке', 'triceps', False, []),
    ('lying_db_ext', 'Lying_Dumbbell_Tricep_Extension', 'Французский жим с гантелями лёжа', 'triceps', False, []),
    ('standing_db_ext', 'Standing_Dumbbell_Triceps_Extension', 'Разгибание гантели из-за головы стоя', 'triceps', False, []),
    ('one_arm_db_ext', 'Dumbbell_One-Arm_Triceps_Extension', 'Разгибание одной рукой из-за головы с гантелью', 'triceps', False, []),
    ('machine_triceps_ext', 'Machine_Triceps_Extension', 'Разгибание рук в тренажёре сидя', 'triceps', False, []),
    ('triceps_dips', 'Dips_-_Triceps_Version', 'Отжимания на брусьях на трицепс', 'triceps', True, ['брусья на трицепс']),
    ('close_pushup', 'Push-Ups_-_Close_Triceps_Position', 'Отжимания узким хватом', 'triceps chest', True, ['алмазные отжимания']),
    ('smith_close_bench', 'Smith_Machine_Close-Grip_Bench_Press', 'Жим узким хватом в Смите', 'triceps', False, []),
    ('jm_press', 'JM_Press', 'JM-жим', 'triceps', False, ['джей эм жим']),
    ('overhead_barbell_ext', 'Standing_Overhead_Barbell_Triceps_Extension', 'Французский жим стоя со штангой', 'triceps', False, []),
    # legs & glutes
    ('wide_squat', 'Wide_Stance_Barbell_Squat', 'Приседания со штангой широкой постановкой', 'legs glutes', False, ['широкий присед']),
    ('box_squat', 'Box_Squat', 'Приседания на ящик (скамью)', 'legs glutes', False, ['присед на скамью']),
    ('barbell_hack_squat', 'Barbell_Hack_Squat', 'Гакк-приседания со штангой сзади', 'legs', False, []),
    ('lying_machine_squat', 'Lying_Machine_Squat', 'Приседания в тренажёре лёжа', 'legs', False, []),
    ('one_leg_extension', 'Single-Leg_Leg_Extension', 'Разгибание одной ноги в тренажёре', 'legs', False, []),
    ('standing_leg_curl', 'Standing_Leg_Curl', 'Сгибание ноги стоя в тренажёре', 'legs', False, []),
    ('barbell_lunge', 'Barbell_Lunge', 'Выпады со штангой', 'legs glutes', False, []),
    ('walking_lunge', 'Barbell_Walking_Lunge', 'Шагающие выпады', 'legs glutes', False, ['выпады в движении']),
    ('rear_lunge', 'Dumbbell_Rear_Lunge', 'Выпады назад с гантелями', 'legs glutes', False, ['обратные выпады']),
    ('step_up', 'Dumbbell_Step_Ups', 'Зашагивания на платформу', 'legs glutes', False, ['зашагивания', 'степ ап']),
    ('db_split_squat', 'Split_Squat_with_Dumbbells', 'Сплит-приседания с гантелями', 'legs glutes', False, ['выпады на месте']),
    ('db_squat', 'Dumbbell_Squat', 'Приседания с гантелями', 'legs glutes', False, []),
    ('plie_squat', 'Plie_Dumbbell_Squat', 'Плие-приседания с гантелью', 'legs glutes', False, ['плие', 'сумо присед']),
    ('bodyweight_squat', 'Bodyweight_Squat', 'Приседания без веса', 'legs', True, ['приседания с собственным весом']),
    ('glute_kickback', 'Glute_Kickback', 'Махи ногой назад на четвереньках', 'glutes', True, []),
    ('glute_bridge', 'Barbell_Glute_Bridge', 'Ягодичный мост со штангой с пола', 'glutes', False, ['мостик со штангой с пола']),
    ('one_leg_bridge', 'Single_Leg_Glute_Bridge', 'Ягодичный мост на одной ноге', 'glutes', True, []),
    ('pull_through', 'Pull_Through', 'Протяжка каната между ног', 'glutes legs', False, ['пул тру']),
    ('cable_adduction', 'Cable_Hip_Adduction', 'Приведение ноги на нижнем блоке', 'legs', False, []),
    ('leg_press_calf', 'Calf_Press_On_The_Leg_Press_Machine', 'Икры в тренажёре для жима ногами', 'calves', False, ['носки в жиме ногами']),
    ('smith_calf_raise', 'Smith_Machine_Calf_Raise', 'Подъём на носки в Смите', 'calves', False, []),
    ('db_calf_raise', 'Standing_Dumbbell_Calf_Raise', 'Подъём на носки с гантелями', 'calves', False, []),
    ('donkey_calf_raise', 'Donkey_Calf_Raises', 'Ослиные подъёмы на носки', 'calves', False, []),
    # abs
    ('ab_machine', 'Ab_Crunch_Machine', 'Скручивания в тренажёре', 'abs', False, ['пресс в тренажёре']),
    ('decline_crunch', 'Decline_Crunch', 'Скручивания на скамье головой вниз', 'abs', True, []),
    ('reverse_crunch', 'Reverse_Crunch', 'Обратные скручивания', 'abs', True, []),
    ('oblique_crunch', 'Oblique_Crunches', 'Косые скручивания', 'abs', True, ['боковые скручивания']),
    ('sit_up', 'Sit-Up', 'Подъём корпуса (сит-ап)', 'abs', True, ['подъём корпуса']),
    ('russian_twist', 'Russian_Twist', 'Русские скручивания', 'abs', True, ['русский твист']),
    ('db_side_bend', 'Dumbbell_Side_Bend', 'Наклоны в сторону с гантелью', 'abs', False, []),
    ('ab_roller', 'Ab_Roller', 'Ролик для пресса', 'abs', True, ['колесо для пресса']),
    ('knee_raise_dip', 'Knee_Hip_Raise_On_Parallel_Bars', 'Подъём коленей в упоре на локтях', 'abs', True, ['подъём ног в упоре']),
    ('wood_chop', 'Standing_Cable_Wood_Chop', 'Дровосек на блоке', 'abs', False, ['вращения на блоке']),
    ('pallof_press', 'Pallof_Press', 'Пресс Паллофа', 'abs', False, []),
    ('side_plank', 'Side_Bridge', 'Боковая планка', 'abs', True, []),
    ('bench_knee_tuck', 'Flat_Bench_Leg_Pull-In', 'Подтягивание коленей на скамье', 'abs', True, []),
    ('air_bike', 'Air_Bike', 'Велосипед (скручивания лёжа)', 'abs', True, []),
    # cardio
    ('stairmaster', 'Stairmaster', 'Степпер-лестница', 'cardio', False, ['степпер', 'лестница']),
    ('recumbent_bike', 'Recumbent_Bike', 'Горизонтальный велотренажёр', 'cardio', False, []),
    ('jump_rope', 'Rope_Jumping', 'Скакалка', 'cardio', False, []),
    ('battle_ropes', 'Battling_Ropes', 'Канаты (battle rope)', 'cardio shoulders', False, ['батл роуп']),
]


def fetch(url):
    import time
    import urllib.error
    for attempt in range(8):
        try:
            with urllib.request.urlopen(url, timeout=60) as r:
                return r.read()
        except urllib.error.HTTPError as e:
            if e.code not in (429, 500, 502, 503, 504) or attempt == 7:
                raise
            wait = int(e.headers.get('Retry-After') or 0) or min(120, 10 * 2 ** attempt)
            print('HTTP %d, waiting %d s' % (e.code, wait), flush=True)
            time.sleep(wait)


def main():
    from PIL import Image
    if len(sys.argv) > 1:
        data = json.load(open(sys.argv[1], encoding='utf-8'))
    else:
        data = json.loads(fetch(RAW + 'dist/exercises.json'))
    by_id = {x['id']: x for x in data}
    used = list(dict.fromkeys(list(EXISTING.values()) + [n[1] for n in NEW] + list(MORE)))
    missing = [i for i in used if i not in by_id]
    if missing:
        raise SystemExit('unknown photo ids: %s' % missing)
    already = set(EXISTING.values()) | {n[1] for n in NEW}
    dup = [i for i in MORE if i in already]
    if dup:
        raise SystemExit('already used: %s' % dup)
    keys = [n[0] for n in NEW]
    assert len(keys) == len(set(keys)), 'duplicate keys'

    out_dir = os.path.join(ROOT, 'assets', 'ex')
    os.makedirs(out_dir, exist_ok=True)
    for pid in used:
        for frame in (0, 1):
            path = os.path.join(out_dir, '%s_%d.webp' % (pid, frame))
            if os.path.exists(path):
                continue
            cache = os.path.join(ROOT, '.sdk', 'photo-cache', '%s_%d.jpg' % (pid, frame))
            if os.path.exists(cache):
                raw = open(cache, 'rb').read()
            else:
                raw = fetch(RAW + 'exercises/' + by_id[pid]['images'][frame])
                os.makedirs(os.path.dirname(cache), exist_ok=True)
                open(cache, 'wb').write(raw)
            img = Image.open(io.BytesIO(raw)).convert('RGB')
            h = round(img.height * WIDTH / img.width)
            img.resize((WIDTH, h), Image.LANCZOS).save(path, 'WEBP', quality=QUALITY, method=6)
    for f in os.listdir(out_dir):
        if not f.endswith('.webp') or f.rsplit('_', 1)[0] not in used:
            os.remove(os.path.join(out_dir, f))

    def muscles(x):
        prim = [MUSCLES[m] for m in x['primaryMuscles']]
        sec = [MUSCLES[m] for m in x.get('secondaryMuscles', []) if MUSCLES[m] not in prim]
        return ', '.join(prim + sec)

    def js(s):
        return '"' + s.replace('\\', '\\\\').replace('"', '\\"') + '"'

    lines = ['package app.gymlog;', '',
             '/** Generated by tools/photo_catalog.py, do not edit. Photos: Free Exercise DB (public domain). */',
             'final class PhotoData {', '    private PhotoData() {}', '', '    static void apply() {']
    for key, pid in EXISTING.items():
        x = by_id[pid]
        lines.append('        Catalog.setPhoto(%s, %s, %s, %s);' % (js(key), js(pid), js(EQUIP[x.get('equipment')]), js(x['name'])))
    for key, pid, title, groups, body, syn in NEW:
        x = by_id[pid]
        syns = [title.lower(), x['name'].lower()] + syn
        lines.append('        Catalog.addPhotoItem(%s, %s, new String[]{%s}, %s, %s, %s, %s, %s, %s);' % (
            js(key), js(title), ', '.join(js(s) for s in syns), js(muscles(x)), js(groups),
            'true' if body else 'false', js(pid), js(EQUIP[x.get('equipment')]), js(x['name'])))
    for pid, title in MORE.items():
        x = by_id[pid]
        prim = x['primaryMuscles'][0]
        groups = ['cardio'] if x['category'] == 'cardio' else [GROUP_OF[prim]]
        for sec in x.get('secondaryMuscles', []):
            if sec in ('chest', 'glutes') and GROUP_OF[sec] not in groups:
                groups.append(GROUP_OF[sec])
        n = pid.replace('-', '_')
        body = (x.get('equipment') in ('body only', None) and x['category'] != 'cardio') or any(
            w in n for w in ('Push_Up', 'Pushup', 'Chin', 'Pull_Up', '_Dip'))
        key = 'x_' + ''.join(ch if ch.isalnum() else '_' for ch in pid.lower())
        lines.append('        Catalog.addPhotoItem(%s, %s, new String[]{%s}, %s, %s, %s, %s, %s, %s);' % (
            js(key), js(title), ', '.join(js(s) for s in [title.lower(), x['name'].lower()]), js(muscles(x)),
            js(' '.join(groups)), 'true' if body else 'false', js(pid), js(EQUIP[x.get('equipment')]), js(x['name'])))
    lines += ['    }', '}', '']
    with open(os.path.join(ROOT, 'src', 'app', 'gymlog', 'PhotoData.java'), 'w', encoding='utf-8') as f:
        f.write('\n'.join(lines))
    total = sum(os.path.getsize(os.path.join(out_dir, f)) for f in os.listdir(out_dir))
    print('photo sets: %d, exercises added here: %d + %d, assets: %.1f MB' % (len(used), len(NEW), len(MORE), total / 1e6))


if __name__ == '__main__':
    main()
