package app.gymlog;

import static org.junit.Assert.assertEquals;
import static org.junit.Assert.assertNull;

import org.junit.Test;

/** Automatic matching of typed machine names to the built-in catalog. */
public class MatcherTest {
    static final String[][] CASES = {
            {"Жим лёжа", "bench_press"},
            {"жим лежа", "bench_press"},
            {"Жим штанги лёжа", "bench_press"},
            {"жим лёжа в смите", "bench_press"},
            {"Bench press", "bench_press"},
            {"Жим гантелей лёжа", "dumbbell_press"},
            {"жим гантелей", "dumbbell_press"},
            {"Жим на наклонной скамье", "incline_press"},
            {"жим гантелей на наклонной", "incline_press"},
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
            {"Тяга Т-грифа", "bent_over_row"},
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
            {"приседания в смите", "squat"},
            {"Жим ногами", "leg_press"},
            {"Платформа", "leg_press"},
            {"Разгибание ног", "leg_extension"},
            {"Разгибания ног сидя", "leg_extension"},
            {"Сгибание ног", "leg_curl"},
            {"сгибание ног лёжа", "leg_curl"},
            {"Выпады", "lunges"},
            {"Болгарские выпады", "lunges"},
            {"Гакк", "hack_squat"},
            {"Гакк-приседания", "hack_squat"},
            {"Икры", "calf_raise"},
            {"Подъём на носки", "calf_raise"},
            {"Сведение ног", "hip_adduction"},
            {"Разведение ног", "hip_abduction"},
            {"Ягодичный мост", "hip_thrust"},
            {"Хип-траст", "hip_thrust"},
            {"Армейский жим", "overhead_press"},
            {"Жим штанги стоя", "overhead_press"},
            {"Жим гантелей сидя", "db_shoulder_press"},
            {"Жим Арнольда", "db_shoulder_press"},
            {"Махи в стороны", "lateral_raise"},
            {"Махи гантелями", "lateral_raise"},
            {"Тяга к подбородку", "upright_row"},
            {"Обратная бабочка", "rear_delt"},
            {"Задняя дельта", "rear_delt"},
            {"Шраги", "shrugs"},
            {"Бицепс со штангой", "barbell_curl"},
            {"Подъём штанги на бицепс", "barbell_curl"},
            {"Молотки", "dumbbell_curl"},
            {"Бицепс с гантелями", "dumbbell_curl"},
            {"Французский жим", "french_press"},
            {"Трицепс на блоке", "triceps_pushdown"},
            {"Разгибание рук на блоке", "triceps_pushdown"},
            {"Пресс", "crunches"},
            {"Скручивания", "crunches"},
            {"Подъём ног в висе", "hanging_leg_raise"},
            {"Планка", "plank"},
            {"Беговая дорожка", "treadmill"},
            {"Велотренажёр", "exercise_bike"},
            {"Гребной тренажёр", "rowing_machine"},
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
    public void unknownNamesGetNoPicture() {
        assertNull(Matcher.best("Тяга"));
        assertNull(Matcher.best("Мой любимый тренажёр"));
        assertNull(Matcher.best(""));
        assertNull(Matcher.best("абвгд"));
    }

    @Test
    public void everyCatalogItemHasContent() {
        for (Catalog.Item it : Catalog.all()) {
            assertEquals(it.key, Matcher.best(it.title).key);
            if (it.technique.length == 0 || it.about.isEmpty() || it.muscles.isEmpty()) {
                throw new AssertionError("empty content: " + it.key);
            }
        }
    }
}
