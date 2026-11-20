package app.gymlog;

import static org.junit.Assert.assertEquals;
import static org.junit.Assert.assertFalse;
import static org.junit.Assert.assertNotNull;
import static org.junit.Assert.assertTrue;
import static org.robolectric.Shadows.shadowOf;

import android.app.AlertDialog;
import android.content.Intent;
import android.view.View;
import android.widget.EditText;
import android.widget.ListView;
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

/** Version 1.2: collapsed cards, pinned add button, additional weight, search by muscle group. */
@RunWith(RobolectricTestRunner.class)
public class DayScreenTest {
    private static final String DAY1 = "2026-11-10";
    private static final String DAY2 = "2026-11-12";
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

    private DayActivity open(String day) {
        DayActivity d = Robolectric.buildActivity(DayActivity.class,
                new Intent(RuntimeEnvironment.application, DayActivity.class).putExtra(DayActivity.EXTRA_DAY, day))
                .setup().get();
        idle();
        return d;
    }

    private void sets(String day, long ex, Object... repsWeight) {
        long e = db.addEntry(day, ex, 0);
        for (int i = 0; i < repsWeight.length; i += 2) {
            db.updateSet(db.addSet(e), (Integer) repsWeight[i], (Double) repsWeight[i + 1]);
        }
    }

    private static void type(EditText e, String s) {
        e.setText(s);
        idle();
    }

    @Test
    public void cardsAreCollapsedAndOpenOnTap() {
        long bench = db.findOrCreateExercise("Жим лёжа");
        long row = db.findOrCreateExercise("Тяга верхнего блока");
        sets(DAY1, bench, 10, 50.0, 8, 55.0);
        sets(DAY1, row, 12, 40.0);
        DayActivity d = open(DAY1);
        assertEquals(2, d.cards.size());
        DayActivity.EntryCard c = d.cards.get(0);
        assertFalse(c.expandedNow);
        assertEquals(View.GONE, c.body.getVisibility());
        assertEquals(View.VISIBLE, c.summary.getVisibility());
        assertEquals("2 подхода:  10 × 50 кг  ·  8 × 55 кг", c.summary.getText().toString());
        c.view.findViewWithTag("cardHeader").performClick();
        assertTrue(c.expandedNow);
        assertEquals(View.VISIBLE, c.body.getVisibility());
        assertEquals(View.VISIBLE, c.infoLink.getVisibility());
        c.view.findViewWithTag("chevron").performClick();
        assertEquals(View.GONE, c.body.getVisibility());
        // the add button is outside the scrolling list, always reachable
        assertNotNull(d.bottomBar.findViewWithTag("addExercise"));
        assertEquals(View.VISIBLE, d.bottomBar.getVisibility());
    }

    @Test
    public void crunchesUseAdditionalWeight() {
        long ex = db.findOrCreateExercise("Скручивания");
        sets(DAY1, ex, 20, null);
        DayActivity d = open(DAY2);
        d.addExercise(ex);
        idle();
        DayActivity.EntryCard c = d.cards.get(0);
        assertTrue(c.entry.extra);
        assertEquals("Доп. вес, кг", ((TextView) c.view.findViewWithTag("weightColumn")).getText().toString());
        assertEquals("+0", c.rows.get(0).weight.getHint().toString());
        type(c.rows.get(0).reps, "20");
        type(c.rows.get(0).weight, "+10");
        assertEquals(10.0, c.rows.get(0).set.weight, 1e-9);
        assertEquals("UP", c.rows.get(0).indicatorBox.getTag());
        c.setExpanded(false);
        assertEquals("1 подход:  20 повт. +10 кг", c.summary.getText().toString());
        // Bench press is a normal weight exercise; the menu switch makes it "additional".
        long bench = db.findOrCreateExercise("Жим лёжа");
        d.addExercise(bench);
        idle();
        DayActivity.EntryCard b = d.cards.get(1);
        assertFalse(b.entry.extra);
        b.toggleWeightMode();
        idle();
        assertEquals(Integer.valueOf(Db.WEIGHT_EXTRA), db.exercise(bench).weightMode);
        assertTrue(d.cards.get(1).entry.extra);
    }

    @Test
    public void typingMuscleGroupListsReferenceExercisesWithVideo() {
        DayActivity d = open(DAY1);
        AlertDialog dlg = d.showAddDialog();
        idle();
        View root = dlg.getWindow().getDecorView();
        EditText input = (EditText) root.findViewWithTag("exerciseName");
        ListView lv = (ListView) root.findViewWithTag("addList");
        type(input, "грудь");
        DayActivity.AddAdapter ad = (DayActivity.AddAdapter) lv.getAdapter();
        int chest = Catalog.inGroup("chest").size();
        assertTrue(chest >= 12);
        assertEquals("Справочник: грудь (" + chest + ")", ad.getItem(0));
        int pecDeck = -1;
        for (int i = 1; i < ad.getCount(); i++) {
            Catalog.Item it = (Catalog.Item) ad.getItem(i);
            assertTrue(it.key, it.inGroup("chest"));
            if (it.key.equals("pec_deck")) pecDeck = i;
        }
        // video button next to the exercise
        View row = ad.getView(pecDeck, null, lv);
        row.findViewWithTag("video:pec_deck").performClick();
        Intent video = shadowOf(d).getNextStartedActivity();
        assertEquals(Intent.ACTION_VIEW, video.getAction());
        assertTrue(video.getDataString().startsWith("https://www.youtube.com/results?search_query="));
        // the "Add" button refuses a bare muscle group name
        dlg.getButton(AlertDialog.BUTTON_POSITIVE).performClick();
        idle();
        assertNotNull(input.getError());
        assertTrue(d.cards.isEmpty());
        // picking a reference exercise creates the machine with its picture
        lv.performItemClick(null, pecDeck, pecDeck);
        idle();
        assertEquals(1, d.cards.size());
        assertEquals("pec_deck", d.cards.get(0).entry.catalog);
        assertTrue(d.cards.get(0).expandedNow);
    }

    @Test
    public void groupChipsAndVariants() {
        DayActivity d = open(DAY1);
        AlertDialog dlg = d.showAddDialog();
        idle();
        View root = dlg.getWindow().getDecorView();
        root.findViewWithTag("chip:triceps").performClick();
        idle();
        EditText input = (EditText) root.findViewWithTag("exerciseName");
        assertEquals("Трицепс", input.getText().toString());
        DayActivity.AddAdapter ad = (DayActivity.AddAdapter) ((ListView) root.findViewWithTag("addList")).getAdapter();
        java.util.Set<String> keys = new java.util.HashSet<>();
        for (Object o : ad.rows) if (o instanceof Catalog.Item) keys.add(((Catalog.Item) o).key);
        assertTrue(keys.contains("triceps_pushdown"));
        assertTrue(keys.contains("reverse_pushdown"));
        assertTrue(keys.contains("overhead_cable_ext"));
        assertTrue(keys.contains("db_overhead_ext"));
        assertTrue(keys.contains("triceps_machine"));
        type(input, "шраги");
        keys.clear();
        for (Object o : ad.rows) if (o instanceof Catalog.Item) keys.add(((Catalog.Item) o).key);
        assertTrue(keys.contains("shrugs"));
        assertTrue(keys.contains("barbell_shrugs"));
        assertTrue(keys.contains("smith_shrugs"));
        ShadowAlertDialog.reset();
    }
}
