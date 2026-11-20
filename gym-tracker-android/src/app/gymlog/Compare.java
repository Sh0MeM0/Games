package app.gymlog;

import java.util.List;

/**
 * Compares today's sets with the previous time the same exercise was done.
 *
 * Rule for one set (set N today vs set N last time):
 *   1. weight is compared first: more weight = better (green up), less = worse (red down);
 *   2. if the weight is the same, repetitions decide: more = better, fewer = worse;
 *   3. same weight and same repetitions = no change ("=").
 */
public final class Compare {
    private Compare() {}

    public enum Status { UP, DOWN, EQUAL, MIXED, NEW, NONE }

    public static final class Result {
        public final Status status;
        public final String title;
        public final String message;
        /** Short reason used in the per-exercise summary, e.g. "вес +5 кг". */
        public final String shortText;

        Result(Status status, String title, String message, String shortText) {
            this.status = status;
            this.title = title;
            this.message = message;
            this.shortText = shortText;
        }
    }

    static final double EPS = 1e-6;

    static final String LEGEND = "Обозначения:\n"
            + "• зелёная стрелка вверх — стало лучше;\n"
            + "• красная стрелка вниз — стало хуже;\n"
            + "• знак «=» — без изменений.\n\n"
            + "Сначала сравнивается вес, а если вес тот же — количество повторений.";

    private static final Result NONE_RESULT = new Result(Status.NONE, "", "", "");

    private static double w(Double v) {
        return v == null ? 0 : v;
    }

    public static Result compareSet(String name, String curDay, int index, Model.WSet cur,
                                    Model.Session prev) {
        return compareSet(name, curDay, index, cur, prev, false);
    }

    /**
     * @param extra true for own-body-weight exercises (crunches, pull-ups...): the weight column
     *              is the additional weight.
     */
    public static Result compareSet(String name, String curDay, int index, Model.WSet cur,
                                    Model.Session prev, boolean extra) {
        final String bigW = extra ? "Доп. вес" : "Вес";
        final String smallW = extra ? "доп. вес" : "вес";
        if (cur == null || !cur.filled()) return NONE_RESULT;
        String setName = "Подход " + (index + 1);
        if (prev == null) {
            return new Result(Status.NEW, setName + ": первая запись",
                    "«" + name + "» записан впервые — сравнивать пока не с чем.\n\n"
                            + "В следующий раз рядом с подходом появится стрелка.\n\n" + LEGEND,
                    "первая запись");
        }
        String prevDate = Fmt.dateLong(prev.day);
        if (index >= prev.sets.size()) {
            return new Result(Status.NEW, setName + ": новый подход",
                    "В прошлый раз (" + prevDate + ") было " + Fmt.setsCount(prev.sets.size())
                            + ", а " + (index + 1) + "-го подхода не было — сравнивать не с чем.",
                    "в прошлый раз такого подхода не было");
        }
        Model.WSet p = prev.sets.get(index);
        double dw = w(cur.weight) - w(p.weight);
        int dr = cur.reps - p.reps;
        Status st;
        if (dw > EPS) st = Status.UP;
        else if (dw < -EPS) st = Status.DOWN;
        else if (dr > 0) st = Status.UP;
        else if (dr < 0) st = Status.DOWN;
        else st = Status.EQUAL;

        StringBuilder m = new StringBuilder();
        m.append("Этот раз (").append(Fmt.dateLong(curDay)).append("):\n")
                .append(Fmt.setLong(cur, extra)).append("\n\n");
        m.append("Прошлый раз (").append(prevDate).append("):\n")
                .append(Fmt.setLong(p, extra)).append("\n\n");

        StringBuilder sh = new StringBuilder();
        if (st == Status.EQUAL) {
            m.append(bigW).append(" и количество повторений такие же, как ").append(prevDate).append('.');
            sh.append("без изменений");
        } else {
            if (dw > EPS) {
                m.append(bigW).append(" больше на ").append(Fmt.num(dw)).append(" кг: ").append(prevDate)
                        .append(" было ").append(Fmt.weight(p.weight, extra)).append('.');
                sh.append(smallW).append(" +").append(Fmt.num(dw)).append(" кг");
            } else if (dw < -EPS) {
                m.append(bigW).append(" меньше на ").append(Fmt.num(-dw)).append(" кг: ").append(prevDate)
                        .append(' ').append(smallW).append(" был больше — ").append(Fmt.weight(p.weight, extra)).append('.');
                sh.append(smallW).append(" −").append(Fmt.num(-dw)).append(" кг");
            } else if (w(cur.weight) > EPS) {
                m.append(bigW).append(" тот же (").append(Fmt.weight(cur.weight, extra)).append(").");
            } else {
                m.append(extra ? "Без доп. веса, как и в прошлый раз." : "Без веса, как и в прошлый раз.");
            }
            m.append('\n');
            if (dr > 0) {
                m.append("Повторений больше на ").append(dr).append(": ").append(prevDate)
                        .append(" было ").append(p.reps).append('.');
                if (sh.length() > 0) sh.append(", ");
                sh.append("повторений +").append(dr);
            } else if (dr < 0) {
                m.append("Повторений меньше на ").append(-dr).append(": ").append(prevDate)
                        .append(" повторений было больше — ").append(p.reps).append('.');
                if (sh.length() > 0) sh.append(", ");
                sh.append("повторений −").append(-dr);
            } else {
                m.append("Повторений столько же (").append(cur.reps).append(").");
            }
            if (dw > EPS && dr < 0) {
                m.append("\n\nСтрелка вверх, потому что вырос ").append(smallW).append(" — ").append(smallW)
                        .append(" важнее количества повторений.");
            } else if (dw < -EPS && dr > 0) {
                m.append("\n\nСтрелка вниз, потому что ").append(smallW)
                        .append(" снизился, хотя повторений стало больше.");
            }
        }
        String title;
        switch (st) {
            case UP: title = setName + ": лучше, чем в прошлый раз"; break;
            case DOWN: title = setName + ": хуже, чем в прошлый раз"; break;
            default: title = setName + ": без изменений"; break;
        }
        return new Result(st, title, m.toString(), sh.toString());
    }

    public static Result compareExercise(String name, String curDay, List<Model.WSet> cur,
                                         Model.Session prev) {
        return compareExercise(name, curDay, cur, prev, false);
    }

    public static Result compareExercise(String name, String curDay, List<Model.WSet> cur,
                                         Model.Session prev, boolean extra) {
        int filled = 0, up = 0, down = 0, eq = 0;
        double best = 0, volume = 0;
        int totalReps = 0;
        StringBuilder lines = new StringBuilder();
        for (int i = 0; i < cur.size(); i++) {
            Model.WSet s = cur.get(i);
            if (!s.filled()) continue;
            filled++;
            best = Math.max(best, w(s.weight));
            totalReps += s.reps;
            volume += w(s.weight) * s.reps;
            if (prev == null) continue;
            Result r = compareSet(name, curDay, i, s, prev, extra);
            lines.append("Подход ").append(i + 1).append(": ");
            switch (r.status) {
                case UP: up++; lines.append("лучше (").append(r.shortText).append(')'); break;
                case DOWN: down++; lines.append("хуже (").append(r.shortText).append(')'); break;
                case EQUAL: eq++; lines.append("без изменений"); break;
                default: lines.append("новый подход, сравнивать не с чем"); break;
            }
            lines.append('\n');
        }
        if (filled == 0) return NONE_RESULT;
        if (prev == null) {
            return new Result(Status.NEW, name + ": первая запись",
                    "Это первая тренировка на «" + name + "» — сравнивать пока не с чем.\n\n"
                            + "В следующий раз здесь появится стрелка.\n\n" + LEGEND,
                    "первая запись");
        }
        Status st;
        if (up + down + eq == 0) st = Status.NEW;
        else if (up > 0 && down > 0) st = Status.MIXED;
        else if (up > 0) st = Status.UP;
        else if (down > 0) st = Status.DOWN;
        else st = Status.EQUAL;

        double pBest = 0, pVolume = 0;
        int pReps = 0;
        for (Model.WSet p : prev.sets) {
            pBest = Math.max(pBest, w(p.weight));
            pReps += p.reps;
            pVolume += w(p.weight) * p.reps;
        }

        StringBuilder m = new StringBuilder();
        if (st == Status.MIXED) m.append("Часть подходов стала лучше, часть — хуже.\n\n");
        m.append("Сравнение с прошлым разом (").append(Fmt.dateLong(prev.day)).append("):\n\n");
        m.append(lines).append('\n');
        m.append("Подходов: ").append(filled).append(" (было ").append(prev.sets.size()).append(")\n");
        m.append(extra ? "Максимальный доп. вес: " : "Максимальный вес: ").append(Fmt.weight(best, extra))
                .append(" (было ").append(Fmt.weight(pBest, extra)).append(")\n");
        m.append("Всего повторений: ").append(totalReps).append(" (было ").append(pReps).append(')');
        if (!extra && (volume > EPS || pVolume > EPS)) {
            m.append("\nОбъём (вес × повторения): ").append(Fmt.num(volume))
                    .append(" кг (было ").append(Fmt.num(pVolume)).append(" кг)");
        }
        String title;
        switch (st) {
            case UP: title = name + ": лучше, чем в прошлый раз"; break;
            case DOWN: title = name + ": хуже, чем в прошлый раз"; break;
            case MIXED: title = name + ": есть и рост, и спад"; break;
            case NEW: title = name + ": новые подходы"; break;
            default: title = name + ": без изменений"; break;
        }
        return new Result(st, title, m.toString(), "");
    }
}
