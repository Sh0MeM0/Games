package app.gymlog;

import java.util.ArrayList;
import java.util.Collections;
import java.util.Comparator;
import java.util.HashSet;
import java.util.LinkedHashMap;
import java.util.List;
import java.util.Map;
import java.util.Set;
import java.util.TreeMap;

/**
 * Monthly progress statistics (pure Java).
 *
 * Every filled set is compared with the same set of the previous session of that exercise,
 * using exactly the same rule as the arrows ({@link Compare#compareSet}). A month's
 * "improvements %" = improved sets / compared sets × 100 (same for worse and unchanged).
 */
public final class Progress {
    private Progress() {}

    public static final class Counts {
        public int up, down, equal;

        public int total() {
            return up + down + equal;
        }

        void add(Compare.Status s) {
            if (s == Compare.Status.UP) up++;
            else if (s == Compare.Status.DOWN) down++;
            else if (s == Compare.Status.EQUAL) equal++;
        }

        void add(Counts c) {
            up += c.up;
            down += c.down;
            equal += c.equal;
        }

        /** Percentages {up, down, equal} that add up to exactly 100 (largest remainder). */
        public int[] percents() {
            return Progress.percents(new int[]{up, down, equal});
        }
    }

    public static final class ExerciseMonth {
        public long exerciseId;
        public String name;
        public final Set<String> days = new HashSet<>();
        public final Counts counts = new Counts();
        /** e.g. "макс. вес 50 → 55 кг (+10%)" or null if there is nothing to compare. */
        public String change;
        /** Percent change of the best weight (or best reps for exercises without weight), or null. */
        public Double changePercent;
    }

    public static final class Month {
        /** "2026-11" */
        public String key;
        public final Set<String> days = new HashSet<>();
        public final Counts counts = new Counts();
        public final List<ExerciseMonth> exercises = new ArrayList<>();
    }

    public static final class Result {
        public final Counts total = new Counts();
        /** Newest month first. */
        public final List<Month> months = new ArrayList<>();
    }

    static String monthOf(String day) {
        return day.substring(0, 7);
    }

    /** @param sessions filled sessions ordered by exercise and day ({@link Db#allSessions()}). */
    public static Result compute(List<Model.Session> sessions) {
        Map<Long, List<Model.Session>> byExercise = new LinkedHashMap<>();
        for (Model.Session s : sessions) {
            List<Model.Session> l = byExercise.get(s.exerciseId);
            if (l == null) {
                l = new ArrayList<>();
                byExercise.put(s.exerciseId, l);
            }
            l.add(s);
        }
        TreeMap<String, Month> months = new TreeMap<>(Collections.<String>reverseOrder());
        Result res = new Result();

        for (List<Model.Session> list : byExercise.values()) {
            Collections.sort(list, new Comparator<Model.Session>() {
                @Override
                public int compare(Model.Session a, Model.Session b) {
                    return a.day.compareTo(b.day);
                }
            });
            Map<String, ExerciseMonth> perMonth = new LinkedHashMap<>();
            for (int i = 0; i < list.size(); i++) {
                Model.Session cur = list.get(i);
                Model.Session prev = i > 0 ? list.get(i - 1) : null;
                String mk = monthOf(cur.day);
                Month m = months.get(mk);
                if (m == null) {
                    m = new Month();
                    m.key = mk;
                    months.put(mk, m);
                }
                m.days.add(cur.day);
                ExerciseMonth em = perMonth.get(mk);
                if (em == null) {
                    em = new ExerciseMonth();
                    em.exerciseId = cur.exerciseId;
                    em.name = cur.name;
                    perMonth.put(mk, em);
                    m.exercises.add(em);
                }
                em.days.add(cur.day);
                if (prev == null) continue;
                for (int j = 0; j < cur.sets.size(); j++) {
                    Compare.Status st = Compare.compareSet(cur.name, cur.day, j, cur.sets.get(j), prev, cur.extra).status;
                    em.counts.add(st);
                }
            }
            // change of the best result over the month
            for (Map.Entry<String, ExerciseMonth> e : perMonth.entrySet()) {
                fillChange(e.getValue(), list, e.getKey());
            }
        }
        for (Month m : months.values()) {
            for (ExerciseMonth em : m.exercises) m.counts.add(em.counts);
            Collections.sort(m.exercises, new Comparator<ExerciseMonth>() {
                @Override
                public int compare(ExerciseMonth a, ExerciseMonth b) {
                    int d = b.days.size() - a.days.size();
                    return d != 0 ? d : a.name.compareTo(b.name);
                }
            });
            res.total.add(m.counts);
            res.months.add(m);
        }
        return res;
    }

    private static double bestWeight(Model.Session s) {
        double b = 0;
        for (Model.WSet w : s.sets) if (w.weight != null) b = Math.max(b, w.weight);
        return b;
    }

    private static int bestReps(Model.Session s) {
        int b = 0;
        for (Model.WSet w : s.sets) if (w.reps != null) b = Math.max(b, w.reps);
        return b;
    }

    private static void fillChange(ExerciseMonth em, List<Model.Session> list, String month) {
        Model.Session base = null, first = null, last = null;
        for (Model.Session s : list) {
            String mk = monthOf(s.day);
            if (mk.compareTo(month) < 0) base = s;
            else if (mk.equals(month)) {
                if (first == null) first = s;
                last = s;
            }
        }
        if (last == null) return;
        if (base == null) {
            if (first == last) return;
            base = first;
        }
        double b = bestWeight(base), e = bestWeight(last);
        if (b > 0 && e > 0) {
            em.changePercent = (e - b) / b * 100.0;
            em.change = (last.extra ? "макс. доп. вес " : "макс. вес ") + Fmt.num(b) + " → " + Fmt.num(e) + " кг (" + signedPercent(em.changePercent) + ")";
        } else {
            int br = bestReps(base), er = bestReps(last);
            if (br <= 0) return;
            em.changePercent = (er - br) * 100.0 / br;
            em.change = "макс. повторений " + br + " → " + er + " (" + signedPercent(em.changePercent) + ")";
        }
    }

    static String signedPercent(double p) {
        long r = Math.round(p);
        if (r == 0) return "0%";
        return (r > 0 ? "+" : "−") + Math.abs(r) + "%";
    }

    /** Integer percentages that add up to exactly 100 (largest remainder method). */
    static int[] percents(int[] counts) {
        int total = 0;
        for (int c : counts) total += c;
        int[] out = new int[counts.length];
        if (total == 0) return out;
        double[] rem = new double[counts.length];
        int sum = 0;
        for (int i = 0; i < counts.length; i++) {
            double exact = counts[i] * 100.0 / total;
            out[i] = (int) Math.floor(exact);
            rem[i] = exact - out[i];
            sum += out[i];
        }
        while (sum < 100) {
            int best = 0;
            for (int i = 1; i < rem.length; i++) if (rem[i] > rem[best]) best = i;
            out[best]++;
            rem[best] = -1;
            sum++;
        }
        return out;
    }

    /** "Ноябрь 2026" for "2026-11". */
    public static String monthTitle(String key) {
        return Fmt.monthTitle(Integer.parseInt(key.substring(0, 4)), Integer.parseInt(key.substring(5, 7)));
    }
}
