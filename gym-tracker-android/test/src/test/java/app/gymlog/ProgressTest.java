package app.gymlog;

import static org.junit.Assert.assertArrayEquals;
import static org.junit.Assert.assertEquals;
import static org.junit.Assert.assertNull;

import java.util.ArrayList;
import java.util.List;

import org.junit.Test;

/** Monthly progress statistics (pure JVM). */
public class ProgressTest {
    private static Model.Session s(long ex, String name, String day, double... repsWeight) {
        Model.Session x = new Model.Session();
        x.exerciseId = ex;
        x.name = name;
        x.day = day;
        for (int i = 0; i < repsWeight.length; i += 2) {
            x.sets.add(new Model.WSet((int) repsWeight[i], repsWeight[i + 1] == 0 ? null : repsWeight[i + 1]));
        }
        return x;
    }

    @Test
    public void percentsAlwaysAddUpTo100() {
        assertArrayEquals(new int[]{34, 33, 33}, Progress.percents(new int[]{1, 1, 1}));
        assertArrayEquals(new int[]{67, 33, 0}, Progress.percents(new int[]{2, 1, 0}));
        assertArrayEquals(new int[]{0, 0, 0}, Progress.percents(new int[]{0, 0, 0}));
        assertArrayEquals(new int[]{50, 25, 25}, Progress.percents(new int[]{2, 1, 1}));
    }

    @Test
    public void monthlyCountsAndChange() {
        List<Model.Session> all = new ArrayList<>();
        // Bench press: October 10x50, 8x55; November 10x52.5 (up), 8x55 (equal); then 9x55, 7x55 (down, down)
        all.add(s(1, "Жим лёжа", "2026-10-28", 10, 50, 8, 55));
        all.add(s(1, "Жим лёжа", "2026-11-03", 10, 52.5, 8, 55));
        all.add(s(1, "Жим лёжа", "2026-11-10", 9, 55, 7, 55));
        // Pull-ups without weight: November only, 8 -> 10 reps (up)
        all.add(s(2, "Подтягивания", "2026-11-03", 8, 0));
        all.add(s(2, "Подтягивания", "2026-11-10", 10, 0));

        Progress.Result r = Progress.compute(all);
        assertEquals(2, r.months.size());
        Progress.Month nov = r.months.get(0);
        Progress.Month oct = r.months.get(1);
        assertEquals("2026-11", nov.key);
        assertEquals("2026-10", oct.key);
        assertEquals(0, oct.counts.total());
        assertEquals(2, nov.days.size());

        // Nov 3 vs Oct 28: set1 50 -> 52.5 up, set2 equal.
        // Nov 10 vs Nov 3: set1 weight 52.5 -> 55 up (weight first), set2 8 -> 7 reps down.
        // Pull-ups: 8 -> 10 up.
        assertEquals(3, nov.counts.up);
        assertEquals(1, nov.counts.down);
        assertEquals(1, nov.counts.equal);
        assertArrayEquals(new int[]{60, 20, 20}, nov.counts.percents());
        assertEquals(5, r.total.total());

        Progress.ExerciseMonth bench = nov.exercises.get(0);
        assertEquals("Жим лёжа", bench.name);
        // best weight: last session before November = 55 kg, last session in November = 55 kg
        assertEquals("макс. вес 55 → 55 кг (0%)", bench.change);
        Progress.ExerciseMonth pull = nov.exercises.get(1);
        assertEquals("макс. повторений 8 → 10 (+25%)", pull.change);
        assertNull(oct.exercises.get(0).change);
        assertEquals("Ноябрь 2026", Progress.monthTitle(nov.key));
    }
}
