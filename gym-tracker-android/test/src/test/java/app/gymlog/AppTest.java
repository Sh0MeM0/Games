package app.gymlog;

import static org.junit.Assert.assertEquals;
import static org.junit.Assert.assertFalse;
import static org.junit.Assert.assertNotNull;
import static org.junit.Assert.assertNull;
import static org.junit.Assert.assertTrue;
import static org.robolectric.Shadows.shadowOf;

import android.app.AlertDialog;
import android.content.Intent;
import android.view.View;
import android.view.ViewGroup;
import android.widget.Button;
import android.widget.EditText;
import android.widget.ListView;
import android.widget.TextView;

import org.json.JSONObject;
import org.junit.After;
import org.junit.Before;
import org.junit.Test;
import org.junit.runner.RunWith;
import org.robolectric.Robolectric;
import org.robolectric.RobolectricTestRunner;
import org.robolectric.RuntimeEnvironment;
import org.robolectric.android.controller.ActivityController;
import org.robolectric.shadows.ShadowAlertDialog;
import org.robolectric.shadows.ShadowLooper;

import java.time.LocalDate;
import java.util.List;

/** Runs the real activities and database on the JVM (Robolectric 3.8, Android 8.1 / API 27). */
@RunWith(RobolectricTestRunner.class)
public class AppTest {
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

    private DayActivity openDay(String day) {
        Intent i = new Intent(RuntimeEnvironment.application, DayActivity.class)
                .putExtra(DayActivity.EXTRA_DAY, day);
        ActivityController<DayActivity> c = Robolectric.buildActivity(DayActivity.class, i).setup();
        idle();
        return c.get();
    }

    private static View findByTag(View root, Object tag) {
        return root.findViewWithTag(tag);
    }

    private static void type(EditText e, String s) {
        e.setText(s);
        idle();
    }

    // ---------------------------------------------------------------- database

    @Test
    public void exerciseNamesAreDeduplicatedCaseInsensitively() {
        long a = db.findOrCreateExercise("Жим лёжа");
        long b = db.findOrCreateExercise("  жим   ЛЕЖА ");
        assertEquals(a, b);
        assertEquals("Жим лёжа", db.exerciseName(a));
        long c = db.findOrCreateExercise("Тяга блока");
        assertTrue(c != a);
        assertFalse(db.renameExercise(c, "ЖИМ ЛЁЖА"));
        assertTrue(db.renameExercise(c, "Тяга верхнего блока"));
        assertEquals(2, db.exercises().size());
    }

    @Test
    public void previousSessionSkipsEmptyAndLaterDays() {
        long ex = db.findOrCreateExercise("Присед");
        long e1 = db.addEntry(DAY1, ex, 0);
        db.updateSet(db.addSet(e1), 10, 60.0);
        db.updateSet(db.addSet(e1), 8, 70.0);
        db.addEntry(DAY2, ex, 2); // only empty sets
        long e3 = db.addEntry(DAY3, ex, 0);
        db.updateSet(db.addSet(e3), 5, 80.0);

        Model.Session p = db.previousSession(ex, "2026-11-15");
        assertEquals(DAY3, p.day);
        p = db.previousSession(ex, DAY3);
        assertEquals(DAY1, p.day);
        assertEquals(2, p.sets.size());
        assertEquals(Integer.valueOf(8), p.sets.get(1).reps);
        assertEquals(70.0, p.sets.get(1).weight, 1e-9);
        assertNull(db.previousSession(ex, DAY1));
        assertEquals(2, db.history(ex).size());
    }

    @Test
    public void backupRoundTrip() throws Exception {
        long ex = db.findOrCreateExercise("Жим лёжа");
        long e1 = db.addEntry(DAY1, ex, 0);
        db.updateSet(db.addSet(e1), 10, 22.5);
        db.updateSet(db.addSet(e1), 12, null);
        JSONObject json = db.exportJson();
        int[] counts = Db.inspectBackup(new JSONObject(json.toString()));
        assertEquals(1, counts[0]);
        assertEquals(1, counts[1]);

        db.deleteExercise(ex);
        assertTrue(db.exercises().isEmpty());
        assertTrue(db.dayEntries(DAY1).isEmpty());

        db.importJson(new JSONObject(json.toString()));
        List<Model.Entry> entries = db.dayEntries(DAY1);
        assertEquals(1, entries.size());
        assertEquals("Жим лёжа", entries.get(0).name);
        assertEquals(2, entries.get(0).sets.size());
        assertEquals(22.5, entries.get(0).sets.get(0).weight, 1e-9);
        assertNull(entries.get(0).sets.get(1).weight);
        assertEquals(Integer.valueOf(12), entries.get(0).sets.get(1).reps);
    }

    // ---------------------------------------------------------------- screens

    @Test
    public void calendarOpensTappedDay() {
        db.addEntry(LocalDate.now().withDayOfMonth(1).toString(), db.findOrCreateExercise("Жим"), 1);
        MainActivity main = Robolectric.buildActivity(MainActivity.class).setup().get();
        idle();
        View root = main.getWindow().getDecorView();
        String first = LocalDate.now().withDayOfMonth(1).toString();
        TextView cell = (TextView) findByTag(root, "day:" + first);
        assertNotNull(cell);
        assertEquals("1", cell.getText().toString());
        cell.performClick();
        Intent started = shadowOf(main).getNextStartedActivity();
        assertEquals(DayActivity.class.getName(), started.getComponent().getClassName());
        assertEquals(first, started.getStringExtra(DayActivity.EXTRA_DAY));
    }

    @Test
    public void fullWorkoutFlowWithArrows() {
        // Day 1: type a new machine name and record two sets.
        DayActivity d1 = openDay(DAY1);
        assertEquals("10 ноября 2026", d1.getActionBar().getTitle().toString());
        AlertDialog dlg = d1.showAddDialog();
        idle();
        EditText name = (EditText) findByTag(dlg.getWindow().getDecorView(), "exerciseName");
        type(name, "Жим лёжа");
        dlg.getButton(AlertDialog.BUTTON_POSITIVE).performClick();
        idle();
        assertEquals(1, d1.cards.size());
        DayActivity.EntryCard card = d1.cards.get(0);
        assertEquals(1, card.rows.size());
        type(card.rows.get(0).reps, "10");
        type(card.rows.get(0).weight, "50");
        ((Button) findByTag(card.view, "addSet")).performClick();
        idle();
        assertEquals(2, card.rows.size());
        type(card.rows.get(1).reps, "8");
        type(card.rows.get(1).weight, "55");
        assertEquals("NEW", card.headerIndicator.getTag());
        List<Model.Entry> saved = db.dayEntries(DAY1);
        assertEquals(Integer.valueOf(8), saved.get(0).sets.get(1).reps);
        assertEquals(55.0, saved.get(0).sets.get(1).weight, 1e-9);

        // Day 2: pick the machine from the saved list. Two empty sets appear with hints.
        DayActivity d2 = openDay(DAY2);
        dlg = d2.showAddDialog();
        idle();
        ListView lv = (ListView) findByTag(dlg.getWindow().getDecorView(), "addList");
        assertEquals("Ваши тренажёры", lv.getAdapter().getItem(0));
        assertEquals("Жим лёжа", ((Model.Exercise) lv.getAdapter().getItem(1)).name);
        lv.performItemClick(null, 1, 1);
        idle();
        card = d2.cards.get(0);
        assertEquals(2, card.rows.size());
        assertEquals("10", card.rows.get(0).reps.getHint().toString());
        assertEquals("50", card.rows.get(0).weight.getHint().toString());
        assertEquals("NONE", card.rows.get(0).indicatorBox.getTag());

        // Lower weight -> red arrow down, the note explains why.
        type(card.rows.get(0).reps, "10");
        type(card.rows.get(0).weight, "45");
        assertEquals("DOWN", card.rows.get(0).indicatorBox.getTag());
        View indicator = ((ViewGroup) card.rows.get(0).indicatorBox).getChildAt(0);
        indicator.performClick();
        idle();
        AlertDialog note = ShadowAlertDialog.getLatestAlertDialog();
        ShadowAlertDialog sh = shadowOf(note);
        assertEquals("Подход 1: хуже, чем в прошлый раз", sh.getTitle().toString());
        assertTrue(sh.getMessage().toString(),
                sh.getMessage().toString().contains("Вес меньше на 5 кг: 10 ноября 2026 вес был больше — 50 кг."));
        note.dismiss();

        // More reps with the same weight -> green arrow up. Header shows mixed result.
        type(card.rows.get(1).reps, "9");
        type(card.rows.get(1).weight, "55,0");
        assertEquals("UP", card.rows.get(1).indicatorBox.getTag());
        assertEquals("MIXED", card.headerIndicator.getTag());

        // Same as last time -> "=".
        type(card.rows.get(0).weight, "50");
        assertEquals("EQUAL", card.rows.get(0).indicatorBox.getTag());
        assertEquals("UP", card.headerIndicator.getTag());

        // Day 3 compares with day 2 (the latest earlier day), not day 1.
        DayActivity d3 = openDay(DAY3);
        d3.addExercise(db.exerciseByName("жим лёжа").id);
        idle();
        card = d3.cards.get(0);
        type(card.rows.get(0).reps, "10");
        type(card.rows.get(0).weight, "50");
        assertEquals("EQUAL", card.rows.get(0).indicatorBox.getTag());
        type(card.rows.get(1).reps, "9");
        type(card.rows.get(1).weight, "55");
        assertEquals("EQUAL", card.headerIndicator.getTag());

        // Delete an empty extra set.
        ((Button) findByTag(card.view, "addSet")).performClick();
        idle();
        assertEquals(3, card.rows.size());
        findByTag(card.rows.get(2).view, "deleteSet").performClick();
        idle();
        assertEquals(2, card.rows.size());
        assertEquals(2, db.dayEntries(DAY3).get(0).sets.size());
    }

    @Test
    public void exercisesScreenListsSavedMachines() {
        long ex = db.findOrCreateExercise("Жим ногами");
        db.addEntry(DAY1, ex, 1);
        db.findOrCreateExercise("Бабочка");
        ExercisesActivity a = Robolectric.buildActivity(ExercisesActivity.class).setup().get();
        idle();
        View root = a.getWindow().getDecorView();
        assertNotNull(findText(root, "Жим ногами"));
        assertNotNull(findText(root, "Бабочка"));
        assertNotNull(findText(root, "1 тренировка · последняя: 10 ноября 2026"));
    }

    @Test
    public void importBackupFromFile() throws Exception {
        long ex = db.findOrCreateExercise("Жим лёжа");
        long e1 = db.addEntry(DAY1, ex, 0);
        db.updateSet(db.addSet(e1), 10, 50.0);
        String json = db.exportJson().toString();
        db.deleteExercise(ex);

        MainActivity main = Robolectric.buildActivity(MainActivity.class).setup().get();
        android.net.Uri uri = android.net.Uri.parse("content://test/backup.json");
        shadowOf(RuntimeEnvironment.application.getContentResolver()).registerInputStream(uri,
                new java.io.ByteArrayInputStream(json.getBytes("UTF-8")));
        main.onActivityResult(MainActivity.REQ_IMPORT, android.app.Activity.RESULT_OK,
                new Intent().setData(uri));
        idle();
        AlertDialog confirm = ShadowAlertDialog.getLatestAlertDialog();
        assertTrue(shadowOf(confirm).getMessage().toString().contains("тренажёров — 1, дней с тренировками — 1"));
        confirm.getButton(AlertDialog.BUTTON_POSITIVE).performClick();
        idle();
        assertEquals(1, db.dayEntries(DAY1).size());
        assertEquals(Integer.valueOf(10), db.dayEntries(DAY1).get(0).sets.get(0).reps);
    }

    @Test
    public void historyDialogListsDays() {
        long ex = db.findOrCreateExercise("Жим лёжа");
        long e1 = db.addEntry(DAY1, ex, 0);
        db.updateSet(db.addSet(e1), 10, 50.0);
        long e2 = db.addEntry(DAY2, ex, 0);
        db.updateSet(db.addSet(e2), 12, 50.0);
        DayActivity d = openDay(DAY2);
        Ui.showHistory(d, ex, "Жим лёжа", DAY2);
        idle();
        AlertDialog h = ShadowAlertDialog.getLatestAlertDialog();
        assertEquals("История: Жим лёжа", shadowOf(h).getTitle().toString());
        CharSequence[] items = shadowOf(h).getItems();
        assertEquals(2, items.length);
        assertEquals("12 ноября 2026\n12 × 50 кг", items[0].toString());
        assertEquals("10 ноября 2026\n10 × 50 кг", items[1].toString());
    }

    @Test
    @org.robolectric.annotation.Config(qualifiers = "night")
    public void darkThemeScreensStart() {
        long ex = db.findOrCreateExercise("Жим лёжа");
        long e1 = db.addEntry(DAY1, ex, 0);
        db.updateSet(db.addSet(e1), 10, 50.0);
        Robolectric.buildActivity(MainActivity.class).setup();
        DayActivity d = openDay(DAY2);
        d.addExercise(ex);
        idle();
        type(d.cards.get(0).rows.get(0).reps, "11");
        type(d.cards.get(0).rows.get(0).weight, "50");
        assertEquals("UP", d.cards.get(0).rows.get(0).indicatorBox.getTag());
        Robolectric.buildActivity(ExercisesActivity.class).setup();
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
}
