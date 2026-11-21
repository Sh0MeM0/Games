package app.gymlog;

import static org.junit.Assert.assertEquals;
import static org.junit.Assert.assertNull;
import static org.junit.Assert.assertTrue;

import org.junit.Test;

/** Automatic matching of typed machine names to the built-in catalog. */
public class MatcherTest {
    static final String[][] CASES = {
            {"Жим лёжа", "bench_press"},
            {"жим лежа", "bench_press"},
            {"Жим штанги лёжа", "bench_press"},
            {"жим лёжа в смите", "smith_bench"},
            {"Bench press", "bench_press"},
            {"Жим гантелей лёжа", "dumbbell_press"},
            {"жим гантелей", "dumbbell_press"},
            {"Жим на наклонной скамье", "incline_press"},
            {"жим гантелей на наклонной", "incline_db_press"},
            {"Разводка гантелей", "dumbbell_fly"},
            {"Бабочка", "pec_deck"},
            {"Пек-дек", "pec_deck"},
            {"Сведение рук в тренажёре", "pec_deck"},
            {"Кроссовер", "cable_crossover"},
            {"Сведение рук в кроссовере", "cable_crossover"},
            {"Жим от груди в тренажёре", "chest_press_machine"},
            {"Хаммер", "chest_press_machine"},
            {"Брусья", "dips"},
            {"Отжимания", "pushups"},
            {"Отжимания на брусьях", "dips"},
            {"Тяга верхнего блока", "lat_pulldown"},
            {"Тяга верх. блока", "lat_pulldown"},
            {"тяга вертикального блока", "lat_pulldown"},
            {"Тяга горизонтального блока", "seated_row"},
            {"тяга нижнего блока", "seated_row"},
            {"Тяга штанги в наклоне", "bent_over_row"},
            {"Тяга Т-грифа", "tbar_row"},
            {"Тяга гантели одной рукой", "one_arm_row"},
            {"Подтягивания", "pullups"},
            {"Турник", "pullups"},
            {"Становая тяга", "deadlift"},
            {"Становая", "deadlift"},
            {"Румынская тяга", "romanian_deadlift"},
            {"Мертвая тяга", "romanian_deadlift"},
            {"Гиперэкстензия", "hyperextension"},
            {"гиперекстензия", "hyperextension"},
            {"Приседания", "squat"},
            {"Присед", "squat"},
            {"Приседания со штангой", "squat"},
            {"приседания в смите", "smith_squat"},
            {"Жим ногами", "leg_press"},
            {"Платформа", "leg_press"},
            {"Разгибание ног", "leg_extension"},
            {"Разгибания ног сидя", "leg_extension"},
            {"Сгибание ног", "leg_curl"},
            {"сгибание ног лёжа", "leg_curl"},
            {"Выпады", "lunges"},
            {"Болгарские выпады", "bulgarian_split_squat"},
            {"Гакк", "hack_squat"},
            {"Гакк-приседания", "hack_squat"},
            {"Подъём на носки", "calf_raise"},
            {"Сведение ног", "hip_adduction"},
            {"Разведение ног", "hip_abduction"},
            {"Ягодичный мост", "hip_thrust"},
            {"Хип-траст", "hip_thrust"},
            {"Армейский жим", "overhead_press"},
            {"Жим штанги стоя", "overhead_press"},
            {"Жим гантелей сидя", "db_shoulder_press"},
            {"Жим Арнольда", "arnold_press"},
            {"Махи в стороны", "lateral_raise"},
            {"Махи гантелями", "lateral_raise"},
            {"Тяга к подбородку", "upright_row"},
            {"Обратная бабочка", "rear_delt"},
            {"Задняя дельта", "rear_delt"},
            {"Шраги", "shrugs"},
            {"Бицепс со штангой", "barbell_curl"},
            {"Подъём штанги на бицепс", "barbell_curl"},
            {"Молотки", "hammer_curl"},
            {"Бицепс с гантелями", "dumbbell_curl"},
            {"Французский жим", "french_press"},
            {"Трицепс на блоке", "triceps_pushdown"},
            {"Разгибание рук на блоке", "triceps_pushdown"},
            {"Скручивания", "crunches"},
            {"Подъём ног в висе", "hanging_leg_raise"},
            {"Планка", "plank"},
            {"Беговая дорожка", "treadmill"},
            {"Велотренажёр", "exercise_bike"},
            {"Гребной тренажёр", "rowing_machine"},
            // variants added in 1.2
            {"Шраги со штангой", "barbell_shrugs"},
            {"Шраги с гантелями", "shrugs"},
            {"Шраги в смите", "smith_shrugs"},
            {"Тяга нижнего блока одной рукой", "one_arm_cable_row"},
            {"Тяга нижнего блока широким хватом", "wide_seated_row"},
            {"Рычажная тяга", "machine_row"},
            {"Тяга с упором в грудь", "chest_supported_row"},
            {"Тяга верхнего блока узким хватом", "close_grip_pulldown"},
            {"Тяга верхнего блока обратным хватом", "reverse_grip_pulldown"},
            {"Пуловер в блоке", "straight_arm_pulldown"},
            {"Пуловер", "db_pullover"},
            {"Разгибание на блоке обратным хватом", "reverse_pushdown"},
            {"Трицепс канат", "rope_pushdown"},
            {"Разгибание из-за головы на блоке", "overhead_cable_ext"},
            {"Французский жим с гантелей", "db_overhead_ext"},
            {"Кикбэк", "db_kickback"},
            {"Обратные отжимания", "bench_dips"},
            {"Трицепс в тренажёре", "triceps_machine"},
            {"Жим узким хватом", "close_grip_bench"},
            {"Скамья Скотта", "preacher_curl"},
            {"Бицепс на блоке", "cable_curl"},
            {"Концентрированные сгибания", "concentration_curl"},
            {"Махи в сторону на блоке", "cable_lateral_raise"},
            {"Разведение в наклоне", "bent_over_lateral"},
            {"Тяга к лицу", "face_pull"},
            {"Жим на плечи в тренажёре", "machine_shoulder_press"},
            {"Подъём ног лёжа", "lying_leg_raise"},
            {"Скручивания на блоке", "cable_crunch"},
            {"Фронтальный присед", "front_squat"},
            {"Гоблет", "goblet_squat"},
            {"Сгибание ног сидя", "seated_leg_curl"},
            {"Подъём на носки сидя", "seated_calf_raise"},
            {"Отведение ноги назад", "cable_kickback"},
            {"Сведение через нижние блоки", "low_cable_fly"},
            {"Подтягивания обратным хватом", "chinups"},
            {"Становая сумо", "sumo_deadlift"},
            {"Эллипс", "elliptical"},
    };

    @Test
    public void matchesCommonNames() {
        StringBuilder wrong = new StringBuilder();
        for (String[] c : CASES) {
            Catalog.Item it = Matcher.best(c[0]);
            String got = it == null ? "null" : it.key;
            if (!got.equals(c[1])) {
                wrong.append(c[0]).append(" -> ").append(got).append(" (expected ").append(c[1])
                        .append(", top score ").append(String.format("%.2f", Matcher.rank(c[0]).get(0).score))
                        .append(")\n");
            }
        }
        assertEquals("", wrong.toString());
    }

    @Test
    public void muscleGroupsAreRecognised() {
        assertEquals("chest", Matcher.groupOf("грудь"));
        assertEquals("chest", Matcher.groupOf("Упражнения на грудь"));
        assertEquals("back", Matcher.groupOf("на спину"));
        assertEquals("triceps", Matcher.groupOf("Трицепс"));
        assertEquals("abs", Matcher.groupOf("Пресс"));
        assertNull(Matcher.groupOf("Трицепс на блоке"));
        assertNull(Matcher.groupOf("Жим ногами"));
        assertNull(Matcher.best("грудь"));
        assertEquals(Catalog.inGroup("chest").size(), Matcher.suggest("грудь", 5).size());
        for (Catalog.Item it : Matcher.suggest("грудь", 5)) assertTrue(it.key, it.inGroup("chest"));
        assertTrue(Catalog.inGroup("triceps").size() >= 10);
        assertTrue(Matcher.suggest("шраги", 20).size() >= 3);
    }

    @Test
    public void unknownNamesGetNoPicture() {
        assertNull(Matcher.best("Тяга"));
        assertNull(Matcher.best("Мой любимый тренажёр"));
        assertNull(Matcher.best(""));
        assertNull(Matcher.best("абвгд"));
    }

    @Test
    public void everyCatalogItemHasContent() {
        int withTechnique = 0;
        for (Catalog.Item it : Catalog.all()) {
            assertEquals(it.key, Matcher.best(it.title).key);
            if (it.photo() == null) throw new AssertionError("no photo: " + it.key);
            if (it.muscles.isEmpty()) throw new AssertionError("no muscles: " + it.key);
            if (it.technique.length > 0) withTechnique++;
        }
        assertTrue(Catalog.all().size() >= 200);
        assertEquals(91, withTechnique);
    }
}
