package app.gymlog;

import java.time.LocalDate;
import java.util.List;
import java.util.Locale;

/** Russian formatting and parsing helpers (pure Java). */
public final class Fmt {
    private Fmt() {}

    private static final Locale RU = new Locale("ru");

    static final String[] MONTHS_NOM = {"Январь", "Февраль", "Март", "Апрель", "Май", "Июнь",
            "Июль", "Август", "Сентябрь", "Октябрь", "Ноябрь", "Декабрь"};
    static final String[] MONTHS_GEN = {"января", "февраля", "марта", "апреля", "мая", "июня",
            "июля", "августа", "сентября", "октября", "ноября", "декабря"};
    static final String[] MONTHS_PREP = {"январе", "феврале", "марте", "апреле", "мае", "июне",
            "июле", "августе", "сентябре", "октябре", "ноябре", "декабре"};
    static final String[] WEEKDAYS_FULL = {"понедельник", "вторник", "среда", "четверг",
            "пятница", "суббота", "воскресенье"};
    static final String[] WEEKDAYS_SHORT = {"Пн", "Вт", "Ср", "Чт", "Пт", "Сб", "Вс"};

    public static LocalDate parseDay(String iso) {
        return LocalDate.parse(iso);
    }

    /** "15 ноября 2026" */
    public static String dateLong(String iso) {
        LocalDate d = parseDay(iso);
        return d.getDayOfMonth() + " " + MONTHS_GEN[d.getMonthValue() - 1] + " " + d.getYear();
    }

    /** "воскресенье" */
    public static String weekday(String iso) {
        return WEEKDAYS_FULL[parseDay(iso).getDayOfWeek().getValue() - 1];
    }

    public static String capitalize(String s) {
        if (s == null || s.isEmpty()) return s;
        return s.substring(0, 1).toUpperCase(RU) + s.substring(1);
    }

    /** "Ноябрь 2026" */
    public static String monthTitle(int year, int month) {
        return MONTHS_NOM[month - 1] + " " + year;
    }

    /** 50 -> "50", 22.5 -> "22,5", 1.25 -> "1,25" */
    public static String num(double v) {
        long r = Math.round(v * 100);
        if (r % 100 == 0) return Long.toString(r / 100);
        String s = String.format(Locale.US, "%.2f", r / 100.0);
        if (s.endsWith("0")) s = s.substring(0, s.length() - 1);
        return s.replace('.', ',');
    }

    /** Weight for messages: "50 кг" or "без веса". */
    public static String weight(Double w) {
        if (w == null || Math.abs(w) < 1e-9) return "без веса";
        return num(w) + " кг";
    }

    public static String plural(long n, String one, String few, String many) {
        long a = Math.abs(n) % 100;
        long b = a % 10;
        if (a > 10 && a < 20) return many;
        if (b == 1) return one;
        if (b >= 2 && b <= 4) return few;
        return many;
    }

    public static String reps(long n) {
        return n + " " + plural(n, "повторение", "повторения", "повторений");
    }

    public static String setsCount(long n) {
        return n + " " + plural(n, "подход", "подхода", "подходов");
    }

    public static String workouts(long n) {
        return n + " " + plural(n, "тренировка", "тренировки", "тренировок");
    }

    /** Compact set: "10 × 50 кг", "12 повт." (no weight), "—" (empty). */
    public static String setShort(Model.WSet s) {
        if (s == null || s.reps == null) return "—";
        if (s.weight == null || Math.abs(s.weight) < 1e-9) return s.reps + " повт.";
        return s.reps + " × " + num(s.weight) + " кг";
    }

    /** Full set: "10 повторений × 50 кг" / "12 повторений, без веса". */
    public static String setLong(Model.WSet s) {
        if (s == null || s.reps == null) return "не заполнено";
        if (s.weight == null || Math.abs(s.weight) < 1e-9) return reps(s.reps) + ", без веса";
        return reps(s.reps) + " × " + num(s.weight) + " кг";
    }

    public static String joinSets(List<Model.WSet> sets) {
        return joinSets(sets, false);
    }

    // ------------------------------------------------ own body weight + additional weight
    // For exercises like crunches, pull-ups or dips the weight column means "additional weight".

    /** "+10 кг" / "без доп. веса" (extra) or "50 кг" / "без веса". */
    public static String weight(Double w, boolean extra) {
        if (!extra) return weight(w);
        if (w == null || Math.abs(w) < 1e-9) return "без доп. веса";
        return "+" + num(w) + " кг";
    }

    /** "15 повт. +10 кг" / "15 повт." (extra) or the usual "10 × 50 кг". */
    public static String setShort(Model.WSet s, boolean extra) {
        if (!extra) return setShort(s);
        if (s == null || s.reps == null) return "—";
        if (s.weight == null || Math.abs(s.weight) < 1e-9) return s.reps + " повт.";
        return s.reps + " повт. +" + num(s.weight) + " кг";
    }

    /** "15 повторений, доп. вес +10 кг" / "15 повторений, без доп. веса". */
    public static String setLong(Model.WSet s, boolean extra) {
        if (!extra) return setLong(s);
        if (s == null || s.reps == null) return "не заполнено";
        return reps(s.reps) + ", " + (s.weight == null || Math.abs(s.weight) < 1e-9
                ? "без доп. веса" : "доп. вес +" + num(s.weight) + " кг");
    }

    public static String joinSets(List<Model.WSet> sets, boolean extra) {
        StringBuilder sb = new StringBuilder();
        for (Model.WSet s : sets) {
            if (!s.filled()) continue;
            if (sb.length() > 0) sb.append("  ·  ");
            sb.append(setShort(s, extra));
        }
        return sb.length() == 0 ? "—" : sb.toString();
    }

    public static Integer parseReps(String s) {
        if (s == null) return null;
        s = s.trim();
        if (s.isEmpty()) return null;
        try {
            int v = Integer.parseInt(s);
            return v < 0 ? null : v;
        } catch (NumberFormatException e) {
            return null;
        }
    }

    public static Double parseWeight(String s) {
        if (s == null) return null;
        s = s.trim().replace(',', '.');
        if (s.startsWith("+")) s = s.substring(1).trim();
        if (s.isEmpty() || !s.matches("[0-9]*\\.?[0-9]*") || s.equals(".")) return null;
        try {
            double v = Double.parseDouble(s);
            if (Double.isNaN(v) || Double.isInfinite(v) || v < 0) return null;
            return v;
        } catch (NumberFormatException e) {
            return null;
        }
    }

    /** Name as stored: trimmed, single spaces. */
    public static String cleanName(String name) {
        return name == null ? "" : name.trim().replaceAll("\\s+", " ");
    }

    /** Key for duplicate detection: case-insensitive (incl. Cyrillic), "ё" == "е". */
    public static String normName(String name) {
        return cleanName(name).toLowerCase(RU).replace('ё', 'е');
    }
}
