package app.gymlog;

import android.content.ContentValues;
import android.content.Context;
import android.database.Cursor;
import android.database.sqlite.SQLiteDatabase;
import android.database.sqlite.SQLiteOpenHelper;

import org.json.JSONArray;
import org.json.JSONException;
import org.json.JSONObject;

import java.text.Collator;
import java.util.ArrayList;
import java.util.Collections;
import java.util.Comparator;
import java.util.HashMap;
import java.util.HashSet;
import java.util.List;
import java.util.Locale;
import java.util.Map;
import java.util.Set;

/** SQLite storage: exercises, day entries and sets. */
public final class Db extends SQLiteOpenHelper {
    private static Db sInstance;

    public static synchronized Db get(Context ctx) {
        if (sInstance == null) sInstance = new Db(ctx.getApplicationContext());
        return sInstance;
    }

    /** Used by tests so every test starts with a fresh database. */
    public static synchronized void closeInstance() {
        if (sInstance != null) {
            sInstance.close();
            sInstance = null;
        }
    }

    private Db(Context ctx) {
        super(ctx, "gymlog.db", null, 2);
    }

    @Override
    public void onConfigure(SQLiteDatabase db) {
        db.setForeignKeyConstraintsEnabled(true);
    }

    @Override
    public void onCreate(SQLiteDatabase db) {
        db.execSQL("CREATE TABLE exercise (id INTEGER PRIMARY KEY AUTOINCREMENT,"
                + " name TEXT NOT NULL, norm TEXT NOT NULL UNIQUE,"
                + " catalog TEXT, catalog_manual INTEGER NOT NULL DEFAULT 0)");
        db.execSQL("CREATE TABLE entry (id INTEGER PRIMARY KEY AUTOINCREMENT,"
                + " day TEXT NOT NULL,"
                + " exercise_id INTEGER NOT NULL REFERENCES exercise(id) ON DELETE CASCADE,"
                + " pos INTEGER NOT NULL, UNIQUE(day, exercise_id))");
        db.execSQL("CREATE INDEX entry_ex_day ON entry(exercise_id, day)");
        db.execSQL("CREATE TABLE wset (id INTEGER PRIMARY KEY AUTOINCREMENT,"
                + " entry_id INTEGER NOT NULL REFERENCES entry(id) ON DELETE CASCADE,"
                + " pos INTEGER NOT NULL, reps INTEGER, weight REAL)");
        db.execSQL("CREATE INDEX wset_entry ON wset(entry_id, pos)");
    }

    @Override
    public void onUpgrade(SQLiteDatabase db, int oldVersion, int newVersion) {
        if (oldVersion < 2) {
            // v2: picture / technique from the built-in catalog, matched by name.
            db.execSQL("ALTER TABLE exercise ADD COLUMN catalog TEXT");
            db.execSQL("ALTER TABLE exercise ADD COLUMN catalog_manual INTEGER NOT NULL DEFAULT 0");
            Cursor c = db.rawQuery("SELECT id, name FROM exercise", null);
            try {
                while (c.moveToNext()) {
                    Catalog.Item it = Matcher.best(c.getString(1));
                    if (it == null) continue;
                    ContentValues v = new ContentValues();
                    v.put("catalog", it.key);
                    db.update("exercise", v, "id = ?", new String[]{Long.toString(c.getLong(0))});
                }
            } finally {
                c.close();
            }
        }
    }

    // ---------------------------------------------------------------- exercises

    public List<Model.Exercise> exercises() {
        List<Model.Exercise> out = new ArrayList<>();
        Cursor c = getReadableDatabase().rawQuery(
                "SELECT x.id, x.name, COUNT(e.id), MAX(e.day), x.catalog, x.catalog_manual FROM exercise x"
                        + " LEFT JOIN entry e ON e.exercise_id = x.id GROUP BY x.id", null);
        try {
            while (c.moveToNext()) {
                Model.Exercise ex = new Model.Exercise();
                ex.id = c.getLong(0);
                ex.name = c.getString(1);
                ex.sessions = c.getInt(2);
                ex.lastDay = c.isNull(3) ? null : c.getString(3);
                ex.catalog = c.isNull(4) ? null : c.getString(4);
                ex.catalogManual = c.getInt(5) != 0;
                out.add(ex);
            }
        } finally {
            c.close();
        }
        final Collator collator = Collator.getInstance(new Locale("ru"));
        Collections.sort(out, new Comparator<Model.Exercise>() {
            @Override
            public int compare(Model.Exercise a, Model.Exercise b) {
                // Most recently used first, never used last, then alphabetically.
                if (a.lastDay != null && b.lastDay != null) {
                    int d = b.lastDay.compareTo(a.lastDay);
                    if (d != 0) return d;
                } else if (a.lastDay != null) {
                    return -1;
                } else if (b.lastDay != null) {
                    return 1;
                }
                return collator.compare(a.name, b.name);
            }
        });
        return out;
    }

    public Model.Exercise exerciseByName(String name) {
        return queryExercise("norm = ?", Fmt.normName(name));
    }

    public Model.Exercise exercise(long id) {
        return queryExercise("id = ?", Long.toString(id));
    }

    private Model.Exercise queryExercise(String where, String arg) {
        Cursor c = getReadableDatabase().rawQuery(
                "SELECT id, name, catalog, catalog_manual FROM exercise WHERE " + where, new String[]{arg});
        try {
            if (!c.moveToFirst()) return null;
            Model.Exercise ex = new Model.Exercise();
            ex.id = c.getLong(0);
            ex.name = c.getString(1);
            ex.catalog = c.isNull(2) ? null : c.getString(2);
            ex.catalogManual = c.getInt(3) != 0;
            return ex;
        } finally {
            c.close();
        }
    }

    /** Sets the picture by hand ({@code key} may be null = "no picture"). */
    public void setCatalog(long exerciseId, String key) {
        ContentValues v = new ContentValues();
        if (key == null) v.putNull("catalog"); else v.put("catalog", key);
        v.put("catalog_manual", 1);
        getWritableDatabase().update("exercise", v, "id = ?", new String[]{Long.toString(exerciseId)});
    }

    private static String autoCatalog(String name) {
        Catalog.Item it = Matcher.best(name);
        return it == null ? null : it.key;
    }

    public String exerciseName(long id) {
        Cursor c = getReadableDatabase().rawQuery(
                "SELECT name FROM exercise WHERE id = ?", new String[]{Long.toString(id)});
        try {
            return c.moveToFirst() ? c.getString(0) : null;
        } finally {
            c.close();
        }
    }

    /** Returns the id of the exercise with this name, creating it if needed. */
    public long findOrCreateExercise(String name) {
        String clean = Fmt.cleanName(name);
        if (clean.isEmpty()) throw new IllegalArgumentException("empty name");
        Model.Exercise ex = exerciseByName(clean);
        if (ex != null) return ex.id;
        ContentValues v = new ContentValues();
        v.put("name", clean);
        v.put("norm", Fmt.normName(clean));
        v.put("catalog", autoCatalog(clean));
        return getWritableDatabase().insertOrThrow("exercise", null, v);
    }

    /** @return false if another exercise already has this name. */
    public boolean renameExercise(long id, String newName) {
        String clean = Fmt.cleanName(newName);
        if (clean.isEmpty()) return false;
        Model.Exercise other = exerciseByName(clean);
        if (other != null && other.id != id) return false;
        Model.Exercise self = exercise(id);
        ContentValues v = new ContentValues();
        v.put("name", clean);
        v.put("norm", Fmt.normName(clean));
        if (self != null && !self.catalogManual) v.put("catalog", autoCatalog(clean));
        getWritableDatabase().update("exercise", v, "id = ?", new String[]{Long.toString(id)});
        return true;
    }

    public void deleteExercise(long id) {
        getWritableDatabase().delete("exercise", "id = ?", new String[]{Long.toString(id)});
    }

    // ---------------------------------------------------------------- entries & sets

    public boolean hasEntry(String day, long exerciseId) {
        Cursor c = getReadableDatabase().rawQuery(
                "SELECT 1 FROM entry WHERE day = ? AND exercise_id = ?",
                new String[]{day, Long.toString(exerciseId)});
        try {
            return c.moveToFirst();
        } finally {
            c.close();
        }
    }

    /** Adds the exercise to the day with {@code setCount} empty sets. */
    public long addEntry(String day, long exerciseId, int setCount) {
        SQLiteDatabase db = getWritableDatabase();
        db.beginTransaction();
        try {
            int pos = 0;
            Cursor c = db.rawQuery("SELECT COALESCE(MAX(pos), -1) + 1 FROM entry WHERE day = ?",
                    new String[]{day});
            try {
                if (c.moveToFirst()) pos = c.getInt(0);
            } finally {
                c.close();
            }
            ContentValues v = new ContentValues();
            v.put("day", day);
            v.put("exercise_id", exerciseId);
            v.put("pos", pos);
            long entryId = db.insertOrThrow("entry", null, v);
            for (int i = 0; i < setCount; i++) {
                ContentValues s = new ContentValues();
                s.put("entry_id", entryId);
                s.put("pos", i);
                db.insertOrThrow("wset", null, s);
            }
            db.setTransactionSuccessful();
            return entryId;
        } finally {
            db.endTransaction();
        }
    }

    public void deleteEntry(long entryId) {
        getWritableDatabase().delete("entry", "id = ?", new String[]{Long.toString(entryId)});
    }

    public List<Model.Entry> dayEntries(String day) {
        List<Model.Entry> out = new ArrayList<>();
        Cursor c = getReadableDatabase().rawQuery(
                "SELECT e.id, e.exercise_id, x.name, e.pos, x.catalog FROM entry e"
                        + " JOIN exercise x ON x.id = e.exercise_id"
                        + " WHERE e.day = ? ORDER BY e.pos, e.id", new String[]{day});
        try {
            while (c.moveToNext()) {
                Model.Entry e = new Model.Entry();
                e.id = c.getLong(0);
                e.exerciseId = c.getLong(1);
                e.name = c.getString(2);
                e.pos = c.getInt(3);
                e.catalog = c.isNull(4) ? null : c.getString(4);
                e.day = day;
                out.add(e);
            }
        } finally {
            c.close();
        }
        for (Model.Entry e : out) e.sets.addAll(loadSets(e.id, false));
        return out;
    }

    private List<Model.WSet> loadSets(long entryId, boolean filledOnly) {
        List<Model.WSet> out = new ArrayList<>();
        Cursor c = getReadableDatabase().rawQuery(
                "SELECT id, pos, reps, weight FROM wset WHERE entry_id = ?"
                        + (filledOnly ? " AND reps IS NOT NULL" : "")
                        + " ORDER BY pos, id", new String[]{Long.toString(entryId)});
        try {
            while (c.moveToNext()) {
                Model.WSet s = new Model.WSet();
                s.id = c.getLong(0);
                s.pos = c.getInt(1);
                s.reps = c.isNull(2) ? null : c.getInt(2);
                s.weight = c.isNull(3) ? null : c.getDouble(3);
                out.add(s);
            }
        } finally {
            c.close();
        }
        return out;
    }

    public long addSet(long entryId) {
        SQLiteDatabase db = getWritableDatabase();
        int pos = 0;
        Cursor c = db.rawQuery("SELECT COALESCE(MAX(pos), -1) + 1 FROM wset WHERE entry_id = ?",
                new String[]{Long.toString(entryId)});
        try {
            if (c.moveToFirst()) pos = c.getInt(0);
        } finally {
            c.close();
        }
        ContentValues s = new ContentValues();
        s.put("entry_id", entryId);
        s.put("pos", pos);
        return db.insertOrThrow("wset", null, s);
    }

    public void updateSet(long setId, Integer reps, Double weight) {
        ContentValues v = new ContentValues();
        if (reps == null) v.putNull("reps"); else v.put("reps", reps);
        if (weight == null) v.putNull("weight"); else v.put("weight", weight);
        getWritableDatabase().update("wset", v, "id = ?", new String[]{Long.toString(setId)});
    }

    public void deleteSet(long setId) {
        getWritableDatabase().delete("wset", "id = ?", new String[]{Long.toString(setId)});
    }

    /** The latest day before {@code beforeDay} on which this exercise has filled sets. */
    public Model.Session previousSession(long exerciseId, String beforeDay) {
        Cursor c = getReadableDatabase().rawQuery(
                "SELECT e.id, e.day FROM entry e WHERE e.exercise_id = ? AND e.day < ?"
                        + " AND EXISTS (SELECT 1 FROM wset s WHERE s.entry_id = e.id"
                        + " AND s.reps IS NOT NULL) ORDER BY e.day DESC LIMIT 1",
                new String[]{Long.toString(exerciseId), beforeDay});
        Model.Session s = null;
        try {
            if (c.moveToFirst()) {
                s = new Model.Session();
                s.entryId = c.getLong(0);
                s.day = c.getString(1);
            }
        } finally {
            c.close();
        }
        if (s != null) s.sets.addAll(loadSets(s.entryId, true));
        return s;
    }

    /** All days with filled sets for this exercise, newest first. */
    public List<Model.Session> history(long exerciseId) {
        List<Model.Session> out = new ArrayList<>();
        Cursor c = getReadableDatabase().rawQuery(
                "SELECT e.id, e.day FROM entry e WHERE e.exercise_id = ?"
                        + " AND EXISTS (SELECT 1 FROM wset s WHERE s.entry_id = e.id"
                        + " AND s.reps IS NOT NULL) ORDER BY e.day DESC",
                new String[]{Long.toString(exerciseId)});
        try {
            while (c.moveToNext()) {
                Model.Session s = new Model.Session();
                s.entryId = c.getLong(0);
                s.day = c.getString(1);
                out.add(s);
            }
        } finally {
            c.close();
        }
        for (Model.Session s : out) s.sets.addAll(loadSets(s.entryId, true));
        return out;
    }

    /**
     * Every day-entry that has filled sets, with those sets: ordered by exercise, then day.
     * Used for the progress statistics.
     */
    public List<Model.Session> allSessions() {
        List<Model.Session> out = new ArrayList<>();
        Cursor c = getReadableDatabase().rawQuery(
                "SELECT e.id, e.day, e.exercise_id, x.name, s.id, s.pos, s.reps, s.weight FROM entry e"
                        + " JOIN exercise x ON x.id = e.exercise_id JOIN wset s ON s.entry_id = e.id"
                        + " WHERE s.reps IS NOT NULL ORDER BY e.exercise_id, e.day, s.pos, s.id", null);
        try {
            Model.Session cur = null;
            while (c.moveToNext()) {
                long entryId = c.getLong(0);
                if (cur == null || cur.entryId != entryId) {
                    cur = new Model.Session();
                    cur.entryId = entryId;
                    cur.day = c.getString(1);
                    cur.exerciseId = c.getLong(2);
                    cur.name = c.getString(3);
                    out.add(cur);
                }
                Model.WSet s = new Model.WSet();
                s.id = c.getLong(4);
                s.pos = c.getInt(5);
                s.reps = c.getInt(6);
                s.weight = c.isNull(7) ? null : c.getDouble(7);
                cur.sets.add(s);
            }
        } finally {
            c.close();
        }
        return out;
    }

    public Set<String> daysWithEntries(String fromDay, String toDay) {
        Set<String> out = new HashSet<>();
        Cursor c = getReadableDatabase().rawQuery(
                "SELECT DISTINCT day FROM entry WHERE day >= ? AND day <= ?",
                new String[]{fromDay, toDay});
        try {
            while (c.moveToNext()) out.add(c.getString(0));
        } finally {
            c.close();
        }
        return out;
    }

    public List<String> recentDays(int limit) {
        List<String> out = new ArrayList<>();
        Cursor c = getReadableDatabase().rawQuery(
                "SELECT DISTINCT day FROM entry ORDER BY day DESC LIMIT " + limit, null);
        try {
            while (c.moveToNext()) out.add(c.getString(0));
        } finally {
            c.close();
        }
        return out;
    }

    public List<String> exerciseNamesForDay(String day) {
        List<String> out = new ArrayList<>();
        Cursor c = getReadableDatabase().rawQuery(
                "SELECT x.name FROM entry e JOIN exercise x ON x.id = e.exercise_id"
                        + " WHERE e.day = ? ORDER BY e.pos, e.id", new String[]{day});
        try {
            while (c.moveToNext()) out.add(c.getString(0));
        } finally {
            c.close();
        }
        return out;
    }

    // ---------------------------------------------------------------- backup

    public JSONObject exportJson() throws JSONException {
        JSONObject root = new JSONObject();
        root.put("app", "gymlog");
        root.put("version", 2);
        JSONArray exs = new JSONArray();
        SQLiteDatabase db = getReadableDatabase();
        Cursor c = db.rawQuery("SELECT id, name, catalog, catalog_manual FROM exercise ORDER BY id", null);
        try {
            while (c.moveToNext()) {
                JSONObject o = new JSONObject();
                o.put("id", c.getLong(0));
                o.put("name", c.getString(1));
                if (!c.isNull(2)) o.put("catalog", c.getString(2));
                o.put("catalogManual", c.getInt(3) != 0);
                exs.put(o);
            }
        } finally {
            c.close();
        }
        root.put("exercises", exs);
        JSONArray entries = new JSONArray();
        c = db.rawQuery("SELECT id, day, exercise_id, pos FROM entry ORDER BY day, pos, id", null);
        try {
            while (c.moveToNext()) {
                JSONObject o = new JSONObject();
                o.put("day", c.getString(1));
                o.put("exercise", c.getLong(2));
                o.put("pos", c.getInt(3));
                JSONArray sets = new JSONArray();
                for (Model.WSet s : loadSets(c.getLong(0), false)) {
                    JSONObject so = new JSONObject();
                    if (s.reps != null) so.put("reps", s.reps.intValue());
                    if (s.weight != null) so.put("weight", s.weight.doubleValue());
                    sets.put(so);
                }
                o.put("sets", sets);
                entries.put(o);
            }
        } finally {
            c.close();
        }
        root.put("entries", entries);
        return root;
    }

    /** Validates a backup and returns {exercises, days} counts. Throws if the file is not valid. */
    public static int[] inspectBackup(JSONObject root) throws JSONException {
        JSONArray exs = root.getJSONArray("exercises");
        JSONArray entries = root.getJSONArray("entries");
        Set<String> days = new HashSet<>();
        for (int i = 0; i < entries.length(); i++) {
            String day = entries.getJSONObject(i).getString("day");
            Fmt.parseDay(day); // throws DateTimeParseException on garbage
            days.add(day);
        }
        return new int[]{exs.length(), days.size()};
    }

    /** Replaces all data with the backup contents. */
    public void importJson(JSONObject root) throws JSONException {
        inspectBackup(root);
        SQLiteDatabase db = getWritableDatabase();
        db.beginTransaction();
        try {
            db.delete("wset", null, null);
            db.delete("entry", null, null);
            db.delete("exercise", null, null);
            Map<Long, Long> idMap = new HashMap<>();
            JSONArray exs = root.getJSONArray("exercises");
            for (int i = 0; i < exs.length(); i++) {
                JSONObject o = exs.getJSONObject(i);
                String name = Fmt.cleanName(o.getString("name"));
                if (name.isEmpty()) continue;
                long newId = findOrCreateExercise(name);
                idMap.put(o.getLong("id"), newId);
                if (o.optBoolean("catalogManual", false)) {
                    String key = o.isNull("catalog") ? null : o.optString("catalog", null);
                    setCatalog(newId, Catalog.get(key) == null ? null : key);
                }
            }
            JSONArray entries = root.getJSONArray("entries");
            for (int i = 0; i < entries.length(); i++) {
                JSONObject o = entries.getJSONObject(i);
                Long exId = idMap.get(o.getLong("exercise"));
                String day = o.getString("day");
                if (exId == null || hasEntry(day, exId)) continue;
                ContentValues v = new ContentValues();
                v.put("day", day);
                v.put("exercise_id", exId);
                v.put("pos", o.optInt("pos", i));
                long entryId = db.insertOrThrow("entry", null, v);
                JSONArray sets = o.optJSONArray("sets");
                if (sets == null) continue;
                for (int j = 0; j < sets.length(); j++) {
                    JSONObject so = sets.getJSONObject(j);
                    ContentValues s = new ContentValues();
                    s.put("entry_id", entryId);
                    s.put("pos", j);
                    if (so.has("reps") && !so.isNull("reps")) s.put("reps", so.getInt("reps"));
                    if (so.has("weight") && !so.isNull("weight")) s.put("weight", so.getDouble("weight"));
                    db.insertOrThrow("wset", null, s);
                }
            }
            db.setTransactionSuccessful();
        } finally {
            db.endTransaction();
        }
    }
}
