package app.gymlog;

import java.util.ArrayList;
import java.util.List;

/** Plain data classes (no Android dependencies, so they can be unit-tested on the JVM). */
public final class Model {
    private Model() {}

    /** A saved machine / exercise ("тренажёр"). */
    public static final class Exercise {
        public long id;
        public String name;
        /** Number of days this exercise was recorded on. */
        public int sessions;
        /** ISO date (yyyy-MM-dd) of the latest day with this exercise, or null. */
        public String lastDay;
        /** Key of the matching {@link Catalog} item (picture + technique), or null. */
        public String catalog;
        /** True if the user chose the picture by hand (then it is not re-matched on rename). */
        public boolean catalogManual;
    }

    /** One set ("подход"): repetitions and weight. Empty fields are null. */
    public static final class WSet {
        public long id;
        public int pos;
        public Integer reps;
        public Double weight;

        public WSet() {}

        public WSet(Integer reps, Double weight) {
            this.reps = reps;
            this.weight = weight;
        }

        /** A set counts as done once the number of repetitions is entered. */
        public boolean filled() {
            return reps != null;
        }
    }

    /** An exercise performed on a particular day, with its sets. */
    public static final class Entry {
        public long id;
        public String day;
        public long exerciseId;
        public String name;
        public String catalog;
        public int pos;
        public final List<WSet> sets = new ArrayList<>();
    }

    /** A previous day's result for one exercise (only filled sets, in order). */
    public static final class Session {
        public String day;
        public long entryId;
        public long exerciseId;
        public String name;
        public final List<WSet> sets = new ArrayList<>();
    }
}
