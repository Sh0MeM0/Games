package app.gymlog;

import static org.junit.Assert.assertEquals;
import static org.junit.Assert.assertNull;
import static org.junit.Assert.assertTrue;

import java.util.ArrayList;
import java.util.Arrays;
import java.util.List;

import org.junit.Test;

/** Pure-JVM tests of the comparison rules and formatting. */
public class CompareTest {
    private static final String TODAY = "2026-11-15";
    private static final String PREV_DAY = "2026-11-12";

    private static Model.WSet s(Integer reps, Double weight) {
        return new Model.WSet(reps, weight);
    }

    private static Model.Session prev(Model.WSet... sets) {
        Model.Session p = new Model.Session();
        p.day = PREV_DAY;
        p.sets.addAll(Arrays.asList(sets));
        return p;
    }

    private static Compare.Result set(Model.WSet cur, Model.Session prev) {
        return Compare.compareSet("Жим лёжа", TODAY, 0, cur, prev);
    }

    @Test
    public void heavierWeightIsUp() {
        Compare.Result r = set(s(10, 55.0), prev(s(10, 50.0)));
        assertEquals(Compare.Status.UP, r.status);
        assertTrue(r.message, r.message.contains("Вес больше на 5 кг: 12 ноября 2026 было 50 кг."));
    }

    @Test
    public void lighterWeightIsDownAndExplainsWhy() {
        Compare.Result r = set(s(10, 45.0), prev(s(10, 50.0)));
        assertEquals(Compare.Status.DOWN, r.status);
        assertTrue(r.message, r.message.contains("Вес меньше на 5 кг: 12 ноября 2026 вес был больше — 50 кг."));
        assertTrue(r.title.startsWith("Подход 1: хуже"));
    }

    @Test
    public void sameWeightMoreRepsIsUp() {
        Compare.Result r = set(s(12, 50.0), prev(s(10, 50.0)));
        assertEquals(Compare.Status.UP, r.status);
        assertTrue(r.message, r.message.contains("Повторений больше на 2: 12 ноября 2026 было 10."));
    }

    @Test
    public void sameWeightFewerRepsIsDown() {
        Compare.Result r = set(s(8, 50.0), prev(s(10, 50.0)));
        assertEquals(Compare.Status.DOWN, r.status);
        assertTrue(r.message, r.message.contains("Повторений меньше на 2: 12 ноября 2026 повторений было больше — 10."));
    }

    @Test
    public void sameEverythingIsEqual() {
        Compare.Result r = set(s(10, 50.0), prev(s(10, 50.0)));
        assertEquals(Compare.Status.EQUAL, r.status);
        assertTrue(r.title.endsWith("без изменений"));
    }

    @Test
    public void weightWinsOverReps() {
        Compare.Result up = set(s(8, 55.0), prev(s(10, 50.0)));
        assertEquals(Compare.Status.UP, up.status);
        assertTrue(up.message.contains("вес важнее"));
        Compare.Result down = set(s(12, 45.0), prev(s(10, 50.0)));
        assertEquals(Compare.Status.DOWN, down.status);
        assertTrue(down.message.contains("хотя повторений стало больше"));
    }

    @Test
    public void bodyweightComparesReps() {
        assertEquals(Compare.Status.UP, set(s(12, null), prev(s(10, null))).status);
        assertEquals(Compare.Status.EQUAL, set(s(10, null), prev(s(10, 0.0))).status);
    }

    @Test
    public void decimalWeights() {
        Compare.Result r = set(s(10, 22.5), prev(s(10, 20.0)));
        assertEquals(Compare.Status.UP, r.status);
        assertTrue(r.message, r.message.contains("Вес больше на 2,5 кг"));
    }

    @Test
    public void firstTimeAndExtraSetsAreNew() {
        assertEquals(Compare.Status.NEW, set(s(10, 50.0), null).status);
        Compare.Result extra = Compare.compareSet("Жим лёжа", TODAY, 1, s(10, 50.0), prev(s(10, 50.0)));
        assertEquals(Compare.Status.NEW, extra.status);
        assertTrue(extra.message.contains("было 1 подход"));
    }

    @Test
    public void emptySetHasNoIndicator() {
        assertEquals(Compare.Status.NONE, set(s(null, 50.0), prev(s(10, 50.0))).status);
    }

    @Test
    public void exerciseSummary() {
        Model.Session p = prev(s(10, 50.0), s(8, 55.0));
        List<Model.WSet> cur = new ArrayList<>(Arrays.asList(s(10, 50.0), s(9, 55.0)));
        assertEquals(Compare.Status.UP, Compare.compareExercise("Жим", TODAY, cur, p).status);

        cur = new ArrayList<>(Arrays.asList(s(10, 45.0), s(9, 55.0)));
        Compare.Result mixed = Compare.compareExercise("Жим", TODAY, cur, p);
        assertEquals(Compare.Status.MIXED, mixed.status);
        assertTrue(mixed.message, mixed.message.contains("Подход 1: хуже (вес −5 кг)"));
        assertTrue(mixed.message, mixed.message.contains("Подход 2: лучше (повторений +1)"));

        cur = new ArrayList<>(Arrays.asList(s(10, 50.0), s(8, 55.0)));
        assertEquals(Compare.Status.EQUAL, Compare.compareExercise("Жим", TODAY, cur, p).status);

        cur = new ArrayList<>(Arrays.asList(s(9, 50.0), s(null, null)));
        assertEquals(Compare.Status.DOWN, Compare.compareExercise("Жим", TODAY, cur, p).status);

        cur = new ArrayList<>(Arrays.asList(s(null, null)));
        assertEquals(Compare.Status.NONE, Compare.compareExercise("Жим", TODAY, cur, p).status);
        assertEquals(Compare.Status.NEW,
                Compare.compareExercise("Жим", TODAY, Arrays.asList(s(5, 5.0)), null).status);
    }

    @Test
    public void formatting() {
        assertEquals("50", Fmt.num(50.0));
        assertEquals("22,5", Fmt.num(22.5));
        assertEquals("1,25", Fmt.num(1.25));
        assertEquals("1 подход", Fmt.setsCount(1));
        assertEquals("3 подхода", Fmt.setsCount(3));
        assertEquals("5 подходов", Fmt.setsCount(5));
        assertEquals("11 повторений", Fmt.reps(11));
        assertEquals("21 повторение", Fmt.reps(21));
        assertEquals("15 ноября 2026", Fmt.dateLong("2026-11-15"));
        assertEquals("воскресенье", Fmt.weekday("2026-11-15"));
        assertEquals(Double.valueOf(22.5), Fmt.parseWeight("22,5"));
        assertEquals(Double.valueOf(22.5), Fmt.parseWeight("22.5"));
        assertNull(Fmt.parseWeight("1.2.3"));
        assertNull(Fmt.parseWeight(","));
        assertNull(Fmt.parseReps(""));
        assertEquals(Integer.valueOf(12), Fmt.parseReps("12"));
        assertEquals("жим лежа", Fmt.normName("  Жим   ЛЁЖА "));
        assertEquals("10 × 50 кг", Fmt.setShort(s(10, 50.0)));
        assertEquals("12 повт.", Fmt.setShort(s(12, null)));
    }

    @Test
    public void additionalWeightForOwnBodyWeightExercises() {
        Compare.Result r = Compare.compareSet("Скручивания", TODAY, 0, s(20, 10.0), prev(s(20, null)), true);
        assertEquals(Compare.Status.UP, r.status);
        assertTrue(r.message, r.message.contains("20 повторений, доп. вес +10 кг"));
        assertTrue(r.message, r.message.contains("Доп. вес больше на 10 кг: 12 ноября 2026 было без доп. веса."));
        Compare.Result d = Compare.compareSet("Скручивания", TODAY, 0, s(18, null), prev(s(20, null)), true);
        assertEquals(Compare.Status.DOWN, d.status);
        assertTrue(d.message, d.message.contains("Без доп. веса, как и в прошлый раз."));
        assertEquals("20 повт. +10 кг", Fmt.setShort(s(20, 10.0), true));
        assertEquals("20 повт.", Fmt.setShort(s(20, null), true));
        assertEquals(Double.valueOf(10), Fmt.parseWeight("+10"));
        Compare.Result sum = Compare.compareExercise("Скручивания", TODAY,
                java.util.Arrays.asList(s(20, 5.0)), prev(s(20, null)), true);
        assertTrue(sum.message, sum.message.contains("Максимальный доп. вес: +5 кг (было без доп. веса)"));
        assertTrue(sum.message, !sum.message.contains("Объём"));
    }
}
