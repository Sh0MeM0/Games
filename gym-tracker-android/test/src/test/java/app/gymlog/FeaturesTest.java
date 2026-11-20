package app.gymlog;

import static org.junit.Assert.assertEquals;
import static org.junit.Assert.assertNotNull;
import static org.junit.Assert.assertNull;
import static org.junit.Assert.assertTrue;
import static org.robolectric.Shadows.shadowOf;

import android.app.AlertDialog;
import android.content.Context;
import android.content.Intent;
import android.database.sqlite.SQLiteDatabase;
import android.view.View;
import android.view.ViewGroup;
import android.widget.Adapter;
import android.widget.Button;
import android.widget.EditText;
import android.widget.GridView;
import android.widget.LinearLayout;
import android.widget.TextView;

import org.junit.After;
import org.junit.Before;
import org.junit.Test;
import org.junit.runner.RunWith;
import org.robolectric.Robolectric;
import org.robolectric.RobolectricTestRunner;
import org.robolectric.RuntimeEnvironment;
import org.robolectric.shadows.ShadowAlertDialog;
import org.robolectric.shadows.ShadowLooper;

import java.io.File;

/** Pictures + reference, progress tab, database upgrade. */
@RunWith(RobolectricTestRunner.class)
public class FeaturesTest {
    private static final String DAY1 = "2026-11-10";
    private static final String DAY2 = "2026-11-12";
    private static final String DAY3 = "2026-11-14";
    private Db db;

    @Before
    public void setUp() {
        Db.closeInstance();
        db = Db.get(RuntimeEnvironment.application);
    }

    @After
    public void tearDown() {
        Db.closeInstance();
    }

    private static void idle() {
        ShadowLooper.runUiThreadTasksIncludingDelayedTasks();
    }

    private static TextView findText(View v, String text) {
        if (v instanceof TextView && text.contentEquals(((TextView) v).getText())) return (TextView) v;
        if (v instanceof ViewGroup) {
            ViewGroup g = (ViewGroup) v;
            for (int i = 0; i < g.getChildCount(); i++) {
                TextView t = findText(g.getChildAt(i), text);
                if (t != null) return t;
            }
        }
        return null;
    }

    private void sets(String day, long ex, Object... repsWeight) {
        long e = db.addEntry(day, ex, 0);
        for (int i = 0; i < repsWeight.length; i += 2) {
            db.updateSet(db.addSet(e), (Integer) repsWeight[i], (Double) repsWeight[i + 1]);
        }
    }

    @Test
    public void pictureIsChosenAutomaticallyAndKeptWhenChosenByHand() {
        long bench = db.findOrCreateExercise("Жим лёжа");
        assertEquals("bench_press", db.exercise(bench).catalog);
        long mine = db.findOrCreateExercise("Мой тренажёр");
        assertNull(db.exercise(mine).catalog);
        db.renameExercise(mine, "Приседания");
        assertEquals("squat", db.exercise(mine).catalog);
        db.setCatalog(mine, "leg_press");
        db.renameExercise(mine, "Жим лёжа узким хватом");
        assertEquals("leg_press", db.exercise(mine).catalog);
        assertTrue(db.exercise(mine).catalogManual);
        assertTrue(Ui.pictogram(RuntimeEnvironment.application, "bench_press") != 0);
        assertEquals(0, Ui.pictogram(RuntimeEnvironment.application, null));
    }

    @Test
    public void upgradeFromVersion1AddsPictures() {
        Db.closeInstance();
        Context ctx = RuntimeEnvironment.application;
        ctx.deleteDatabase("gymlog.db");
        File f = ctx.getDatabasePath("gymlog.db");
        f.getParentFile().mkdirs();
        SQLiteDatabase old = SQLiteDatabase.openOrCreateDatabase(f, null);
        old.execSQL("CREATE TABLE exercise (id INTEGER PRIMARY KEY AUTOINCREMENT, name TEXT NOT NULL, norm TEXT NOT NULL UNIQUE)");
        old.execSQL("CREATE TABLE entry (id INTEGER PRIMARY KEY AUTOINCREMENT, day TEXT NOT NULL,"
                + " exercise_id INTEGER NOT NULL REFERENCES exercise(id) ON DELETE CASCADE, pos INTEGER NOT NULL, UNIQUE(day, exercise_id))");
        old.execSQL("CREATE TABLE wset (id INTEGER PRIMARY KEY AUTOINCREMENT, entry_id INTEGER NOT NULL REFERENCES entry(id) ON DELETE CASCADE,"
                + " pos INTEGER NOT NULL, reps INTEGER, weight REAL)");
        old.execSQL("INSERT INTO exercise (name, norm) VALUES ('Тяга верхнего блока', 'тяга верхнего блока')");
        old.execSQL("INSERT INTO exercise (name, norm) VALUES ('Странный тренажёр', 'странный тренажер')");
        old.execSQL("INSERT INTO entry (day, exercise_id, pos) VALUES ('2026-11-01', 1, 0)");
        old.execSQL("INSERT INTO wset (entry_id, pos, reps, weight) VALUES (1, 0, 12, 40)");
        old.setVersion(1);
        old.close();

        db = Db.get(ctx);
        assertEquals("lat_pulldown", db.exerciseByName("Тяга верхнего блока").catalog);
        assertNull(db.exerciseByName("Странный тренажёр").catalog);
        assertEquals(Integer.valueOf(12), db.dayEntries("2026-11-01").get(0).sets.get(0).reps);
    }

    @Test
    public void tapPictureAndPickAnother() {
        long ex = db.findOrCreateExercise("Жим");
        db.addEntry(DAY1, ex, 1);
        DayActivity d = Robolectric.buildActivity(DayActivity.class,
                new Intent(RuntimeEnvironment.application, DayActivity.class).putExtra(DayActivity.EXTRA_DAY, DAY1))
                .setup().get();
        idle();
        assertNotNull(d.cards.get(0).view.findViewWithTag("thumb:bench_press"));
        assertNotNull(d.cards.get(0).view.findViewWithTag("infoLink"));
        d.cards.get(0).view.findViewWithTag("thumb:bench_press").performClick();
        idle();
        AlertDialog dlg = ShadowAlertDialog.getLatestAlertDialog();
        View root = dlg.getWindow().getDecorView();
        EditText search = (EditText) root.findViewWithTag("pickerSearch");
        GridView grid = (GridView) root.findViewWithTag("pickerGrid");
        Adapter ad = grid.getAdapter();
        assertNull(ad.getItem(0)); // "no picture"
        assertEquals("bench_press", ((Catalog.Item) ad.getItem(1)).key); // best match first
        search.setText("жим ногами");
        idle();
        assertEquals("leg_press", ((Catalog.Item) ad.getItem(1)).key);
        grid.performItemClick(null, 1, 1);
        idle();
        assertEquals("leg_press", db.exercise(ex).catalog);
        assertTrue(db.exercise(ex).catalogManual);
        assertNotNull(d.cards.get(0).view.findViewWithTag("thumb:leg_press"));
    }

    @Test
    public void infoScreenShowsTechniqueAndVideoSearch() throws Exception {
        long ex = db.findOrCreateExercise("Тяга верхнего блока");
        ExerciseInfoActivity a = Robolectric.buildActivity(ExerciseInfoActivity.class,
                new Intent(RuntimeEnvironment.application, ExerciseInfoActivity.class)
                        .putExtra(ExerciseInfoActivity.EXTRA_EXERCISE, ex)).setup().get();
        idle();
        View root = a.getWindow().getDecorView();
        assertNotNull(findText(root, "Техника"));
        assertNotNull(findText(root, "Частые ошибки"));
        ((Button) root.findViewWithTag("video")).performClick();
        Intent i = shadowOf(a).getNextStartedActivity();
        assertEquals(Intent.ACTION_VIEW, i.getAction());
        assertEquals("https://www.youtube.com/results?search_query="
                + java.net.URLEncoder.encode("техника Тяга верхнего блока широким хватом", "UTF-8"),
                i.getDataString());
    }

    @Test
    public void infoScreenWithoutPictureOffersToChoose() {
        long ex = db.findOrCreateExercise("Мой особый тренажёр");
        ExerciseInfoActivity a = Robolectric.buildActivity(ExerciseInfoActivity.class,
                new Intent(RuntimeEnvironment.application, ExerciseInfoActivity.class)
                        .putExtra(ExerciseInfoActivity.EXTRA_EXERCISE, ex)).setup().get();
        idle();
        assertNull(findText(a.getWindow().getDecorView(), "Техника"));
        assertNotNull(a.getWindow().getDecorView().findViewWithTag("thumb:null"));
    }

    @Test
    public void progressTabShowsMonthlyPercentages() {
        long ex = db.findOrCreateExercise("Жим лёжа");
        sets(DAY1, ex, 10, 50.0, 8, 50.0);
        sets(DAY2, ex, 10, 55.0, 8, 50.0);   // up, equal
        sets(DAY3, ex, 10, 55.0, 7, 50.0);   // equal, down
        MainActivity m = Robolectric.buildActivity(MainActivity.class).setup().get();
        idle();
        m.getWindow().getDecorView().findViewWithTag("tab:1").performClick();
        idle();
        assertEquals(1, m.tab);
        LinearLayout month = (LinearLayout) m.progressBox.findViewWithTag("month:2026-11");
        assertNotNull(month);
        assertNotNull(findText(month, "Ноябрь 2026"));
        assertNotNull(findText(month, "3 тренировки"));
        View percents = month.findViewWithTag("percents");
        assertNotNull(findText(percents, "25%"));
        assertNotNull(findText(percents, "50%"));
        assertNotNull(findText(month, "Сравнено подходов: 4 (лучше — 1, хуже — 1, так же — 2)"));
        assertNotNull(findText(month, "макс. вес 50 → 55 кг (+10%)"));
    }

    @Test
    public void progressTabEmptyState() {
        MainActivity m = Robolectric.buildActivity(MainActivity.class).setup().get();
        m.showTab(1);
        idle();
        assertEquals(1, m.progressBox.getChildCount());
    }
}
