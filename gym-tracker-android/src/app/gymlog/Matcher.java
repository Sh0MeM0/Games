package app.gymlog;

import java.util.ArrayList;
import java.util.Collections;
import java.util.Comparator;
import java.util.HashSet;
import java.util.List;
import java.util.Locale;
import java.util.Set;

/**
 * Picks the catalog exercise that matches a machine name typed by the user.
 * Works offline: words are normalised (case, "ё"/"э"), Russian endings are cut off,
 * and words are compared exactly, by prefix or by letter trigrams (for typos).
 * Score = how much of the typed name and of the catalog phrase match each other (0..1).
 */
public final class Matcher {
    private Matcher() {}

    /** Minimum score for an automatic choice. */
    public static final double THRESHOLD = 0.7;

    public static final class Match {
        public final Catalog.Item item;
        public final double score;

        Match(Catalog.Item item, double score) {
            this.item = item;
            this.score = score;
        }
    }

    private static final Locale RU = new Locale("ru");
    private static final Set<String> STOP = new HashSet<>();
    private static final String[] ENDINGS = {
            "ями", "ами", "ого", "его", "ому", "ему", "ыми", "ими", "ией",
            "ой", "ей", "ий", "ый", "ая", "яя", "ое", "ее", "ые", "ие", "ов", "ев", "ам", "ям",
            "ах", "ях", "ом", "ем", "ую", "юю", "ия", "ья", "ью",
            "а", "я", "о", "е", "ы", "и", "у", "ю", "ь", "й"};

    static {
        for (String s : new String[]{"на", "в", "во", "с", "со", "для", "из", "за", "к", "ко", "по", "и",
                "от", "до", "под", "над", "а", "the", "on", "with", "of", "a", "машине", "упражнение", "кг"}) {
            STOP.add(s);
        }
    }

    static String stem(String w) {
        if (w.length() <= 3 || !isCyrillic(w)) {
            if (w.length() > 4 && w.endsWith("s")) return w.substring(0, w.length() - 1);
            return w;
        }
        for (String e : ENDINGS) {
            if (w.endsWith(e) && w.length() - e.length() >= 3) return w.substring(0, w.length() - e.length());
        }
        return w;
    }

    private static boolean isCyrillic(String w) {
        for (int i = 0; i < w.length(); i++) {
            char c = w.charAt(i);
            if (c >= 'а' && c <= 'я') return true;
        }
        return false;
    }

    static List<String> tokens(String text) {
        String t = text.toLowerCase(RU).replace('ё', 'е').replace('э', 'е');
        StringBuilder sb = new StringBuilder();
        for (int i = 0; i < t.length(); i++) {
            char c = t.charAt(i);
            sb.append(Character.isLetterOrDigit(c) ? c : ' ');
        }
        List<String> out = new ArrayList<>();
        for (String w : sb.toString().trim().split("\\s+")) {
            if (w.isEmpty() || STOP.contains(w)) continue;
            String s = stem(w);
            if (!STOP.contains(s)) out.add(s);
        }
        return out;
    }

    static double similarity(String a, String b) {
        if (a.equals(b)) return 1.0;
        String shorter = a.length() <= b.length() ? a : b;
        String longer = shorter == a ? b : a;
        if (shorter.length() >= 4 && longer.startsWith(shorter)) return 0.9;
        if (a.length() < 4 || b.length() < 4) return 0;
        Set<String> ta = trigrams(a), tb = trigrams(b);
        int common = 0;
        for (String g : ta) if (tb.contains(g)) common++;
        double dice = 2.0 * common / (ta.size() + tb.size());
        return dice >= 0.6 ? dice * 0.85 : 0;
    }

    private static Set<String> trigrams(String w) {
        String p = " " + w + " ";
        Set<String> out = new HashSet<>();
        for (int i = 0; i + 3 <= p.length(); i++) out.add(p.substring(i, i + 3));
        return out;
    }

    static double phraseScore(List<String> q, List<String> t) {
        if (q.isEmpty() || t.isEmpty()) return 0;
        double mq = 0, mt = 0;
        for (String a : q) {
            double best = 0;
            for (String b : t) best = Math.max(best, similarity(a, b));
            mq += best;
        }
        for (String b : t) {
            double best = 0;
            for (String a : q) best = Math.max(best, similarity(a, b));
            mt += best;
        }
        return (mq + mt) / (q.size() + t.size());
    }

    public static double score(String name, Catalog.Item item) {
        List<String> q = tokens(name);
        double best = phraseScore(q, tokens(item.title));
        for (String s : item.synonyms) best = Math.max(best, phraseScore(q, tokens(s)));
        return best;
    }

    /** All catalog items, best match first. */
    public static List<Match> rank(String name) {
        List<Match> out = new ArrayList<>();
        int order = 0;
        for (Catalog.Item it : Catalog.all()) {
            out.add(new Match(it, score(name == null ? "" : name, it) - order++ * 1e-9));
        }
        Collections.sort(out, new Comparator<Match>() {
            @Override
            public int compare(Match x, Match y) {
                return Double.compare(y.score, x.score);
            }
        });
        return out;
    }

    /** The automatic choice for this name, or null if nothing matches well enough. */
    public static Catalog.Item best(String name) {
        if (groupOf(name) != null) return null; // "грудь" is a muscle group, not one exercise
        List<Match> r = rank(name);
        return !r.isEmpty() && r.get(0).score >= THRESHOLD ? r.get(0).item : null;
    }

    private static final Set<String> GROUP_FILLER = new HashSet<>(java.util.Arrays.asList(
            "упражнен", "мышц", "трениров", "качат", "кача", "все", "вс"));

    /**
     * Muscle group key if the text only names a group ("грудь", "на спину", "упражнения на трицепс"),
     * otherwise null.
     */
    public static String groupOf(String text) {
        if (text == null) return null;
        String found = null;
        int words = 0;
        for (String t : tokens(text)) {
            if (GROUP_FILLER.contains(t)) continue;
            words++;
            String g = null;
            for (Catalog.Group gr : Catalog.GROUPS) {
                for (String w : gr.words) {
                    for (String wt : tokens(w)) {
                        if (similarity(t, wt) >= 0.9) g = gr.key;
                    }
                }
            }
            if (g == null || (found != null && !found.equals(g))) return null;
            found = g;
        }
        return words == 0 ? null : found;
    }

    /**
     * Reference exercises to suggest while the user types: the whole group for "грудь",
     * otherwise everything that matches reasonably well (best first).
     */
    public static List<Catalog.Item> suggest(String text, int limit) {
        List<Catalog.Item> out = new ArrayList<>();
        String g = groupOf(text);
        if (g != null) return Catalog.inGroup(g);
        if (text == null || tokens(text).isEmpty()) return out;
        for (Match m : rank(text)) {
            if (m.score < 0.4 || out.size() >= limit) break;
            out.add(m.item);
        }
        return out;
    }
}
