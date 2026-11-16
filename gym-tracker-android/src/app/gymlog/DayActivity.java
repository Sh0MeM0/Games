package app.gymlog;

import android.app.ActionBar;
import android.app.Activity;
import android.app.AlertDialog;
import android.content.DialogInterface;
import android.content.Intent;
import android.os.Bundle;
import android.text.Editable;
import android.text.InputFilter;
import android.text.InputType;
import android.text.TextWatcher;
import android.text.method.DigitsKeyListener;
import android.view.Gravity;
import android.view.KeyEvent;
import android.view.Menu;
import android.view.MenuItem;
import android.view.View;
import android.view.ViewGroup;
import android.view.WindowManager;
import android.view.inputmethod.EditorInfo;
import android.view.inputmethod.InputMethodManager;
import android.widget.AdapterView;
import android.widget.ArrayAdapter;
import android.widget.Button;
import android.widget.EditText;
import android.widget.FrameLayout;
import android.widget.ImageButton;
import android.widget.LinearLayout;
import android.widget.ListView;
import android.widget.PopupMenu;
import android.widget.ScrollView;
import android.widget.TextView;
import android.widget.Toast;

import java.time.LocalDate;
import java.util.ArrayList;
import java.util.List;

/**
 * One training day: list of machines, and under each one its sets
 * (repetitions + weight) with a comparison to the previous time.
 */
public class DayActivity extends BaseActivity {
    public static final String EXTRA_DAY = "day";
    private static final int MENU_PREV = 1;
    private static final int MENU_NEXT = 2;

    String day;
    private ScrollView scroll;
    private LinearLayout list;
    private TextView emptyView;
    final List<EntryCard> cards = new ArrayList<>();

    @Override
    protected void onCreate(Bundle saved) {
        super.onCreate(saved);
        day = getIntent().getStringExtra(EXTRA_DAY);
        if (day == null) day = LocalDate.now().toString();
        ActionBar ab = getActionBar();
        if (ab != null) ab.setDisplayHomeAsUpEnabled(true);
        buildUi();
        reload();
    }

    @Override
    protected void onRestart() {
        super.onRestart();
        reload();
    }

    private void buildUi() {
        scroll = new ScrollView(this);
        scroll.setFillViewport(true);
        LinearLayout content = Ui.vertical(this);
        int p = Ui.dp(this, 12);
        content.setPadding(p, p, p, Ui.dp(this, 28));
        scroll.addView(content, new ScrollView.LayoutParams(ViewGroup.LayoutParams.MATCH_PARENT,
                ViewGroup.LayoutParams.WRAP_CONTENT));

        list = Ui.vertical(this);
        content.addView(list, Ui.matchWrap());

        emptyView = Ui.text(this, "В этот день ещё ничего не записано.\n\n"
                + "Нажмите «Добавить тренажёр», введите его название или выберите из сохранённых, "
                + "а затем запишите подходы: повторения и вес.", 15, R.color.text_secondary, false);
        emptyView.setLineSpacing(0, 1.15f);
        emptyView.setGravity(Gravity.CENTER);
        int ep = Ui.dp(this, 18);
        emptyView.setPadding(ep, Ui.dp(this, 36), ep, Ui.dp(this, 24));
        content.addView(emptyView, Ui.matchWrap());

        Button add = Ui.primaryButton(this, "+  Добавить тренажёр");
        add.setTag("addExercise");
        add.setOnClickListener(new View.OnClickListener() {
            @Override
            public void onClick(View v) {
                showAddDialog();
            }
        });
        LinearLayout.LayoutParams alp = Ui.matchWrap();
        alp.topMargin = Ui.dp(this, 4);
        content.addView(add, alp);

        setContentView(scroll);
    }

    void reload() {
        ActionBar ab = getActionBar();
        String sub = Fmt.capitalize(Fmt.weekday(day));
        if (day.equals(LocalDate.now().toString())) sub += " · сегодня";
        if (ab != null) {
            ab.setTitle(Fmt.dateLong(day));
            ab.setSubtitle(sub);
        } else {
            setTitle(Fmt.dateLong(day));
        }
        list.removeAllViews();
        cards.clear();
        for (Model.Entry e : Db.get(this).dayEntries(day)) {
            EntryCard card = new EntryCard(e);
            cards.add(card);
            LinearLayout.LayoutParams lp = Ui.matchWrap();
            lp.bottomMargin = Ui.dp(this, 12);
            list.addView(card.view, lp);
        }
        emptyView.setVisibility(cards.isEmpty() ? View.VISIBLE : View.GONE);
    }

    private void switchDay(String newDay) {
        day = newDay;
        setIntent(new Intent(getIntent()).putExtra(EXTRA_DAY, day));
        reload();
        scroll.scrollTo(0, 0);
    }

    @Override
    public boolean onCreateOptionsMenu(Menu menu) {
        super.onCreateOptionsMenu(menu);
        MenuItem prev = menu.add(0, MENU_PREV, 0, "Предыдущий день");
        prev.setIcon(tinted(R.drawable.ic_chevron_left));
        prev.setShowAsAction(MenuItem.SHOW_AS_ACTION_ALWAYS);
        MenuItem next = menu.add(0, MENU_NEXT, 1, "Следующий день");
        next.setIcon(tinted(R.drawable.ic_chevron_right));
        next.setShowAsAction(MenuItem.SHOW_AS_ACTION_ALWAYS);
        return true;
    }

    private android.graphics.drawable.Drawable tinted(int res) {
        android.graphics.drawable.Drawable d = getDrawable(res);
        if (d != null) {
            d = d.mutate();
            d.setTint(0xFFFFFFFF);
        }
        return d;
    }

    @Override
    public boolean onOptionsItemSelected(MenuItem item) {
        switch (item.getItemId()) {
            case android.R.id.home:
                finish();
                return true;
            case MENU_PREV:
                switchDay(Fmt.parseDay(day).minusDays(1).toString());
                return true;
            case MENU_NEXT:
                switchDay(Fmt.parseDay(day).plusDays(1).toString());
                return true;
            default:
                return super.onOptionsItemSelected(item);
        }
    }

    // ---------------------------------------------------------------- add exercise

    AlertDialog showAddDialog() {
        final Db db = Db.get(this);
        final List<Model.Exercise> saved = new ArrayList<>();
        for (Model.Exercise ex : db.exercises()) {
            if (!db.hasEntry(day, ex.id)) saved.add(ex);
        }
        final List<Model.Exercise> shown = new ArrayList<>(saved);
        final List<String> names = new ArrayList<>();
        for (Model.Exercise ex : shown) names.add(ex.name);

        LinearLayout box = Ui.vertical(this);
        int p = Ui.dp(this, 20);
        box.setPadding(p, Ui.dp(this, 8), p, 0);

        final EditText input = new EditText(this);
        input.setTag("exerciseName");
        input.setHint("Название тренажёра");
        input.setSingleLine(true);
        input.setInputType(InputType.TYPE_CLASS_TEXT | InputType.TYPE_TEXT_FLAG_CAP_SENTENCES);
        input.setImeOptions(EditorInfo.IME_ACTION_DONE);
        input.setFilters(new InputFilter[]{new InputFilter.LengthFilter(60)});
        box.addView(input, Ui.matchWrap());

        final ListView lv = new ListView(this);
        lv.setTag("savedList");
        final ArrayAdapter<String> adapter = new ArrayAdapter<String>(this,
                android.R.layout.simple_list_item_1, names) {
            @Override
            public View getView(int position, View convert, ViewGroup parent) {
                LinearLayout row = Ui.horizontal(DayActivity.this);
                int rp = Ui.dp(DayActivity.this, 6);
                row.setPadding(0, rp, 0, rp);
                FrameLayout th = Ui.thumbnail(DayActivity.this, shown.get(position).catalog, 40);
                th.setClickable(false);
                th.setForeground(null);
                row.addView(th, Ui.lp(Ui.dp(DayActivity.this, 40), Ui.dp(DayActivity.this, 40)));
                TextView t = Ui.text(DayActivity.this, shown.get(position).name, 16, R.color.text, false);
                LinearLayout.LayoutParams tl = Ui.lpWeight(1);
                tl.leftMargin = Ui.dp(DayActivity.this, 12);
                row.addView(t, tl);
                return row;
            }
        };
        lv.setAdapter(adapter);
        if (!saved.isEmpty()) {
            TextView label = Ui.text(this, "или выберите из сохранённых:", 14,
                    R.color.text_secondary, false);
            LinearLayout.LayoutParams llp = Ui.matchWrap();
            llp.topMargin = Ui.dp(this, 14);
            box.addView(label, llp);
            int h = Math.min(saved.size() * Ui.dp(this, 48), Ui.dp(this, 264));
            box.addView(lv, new LinearLayout.LayoutParams(ViewGroup.LayoutParams.MATCH_PARENT, h));
        }

        final AlertDialog dialog = new AlertDialog.Builder(this)
                .setTitle("Добавить тренажёр")
                .setView(box)
                .setNegativeButton("Отмена", null)
                .setPositiveButton("Добавить", null)
                .create();

        input.addTextChangedListener(new TextWatcher() {
            @Override public void beforeTextChanged(CharSequence s, int a, int b, int c) {}
            @Override public void onTextChanged(CharSequence s, int a, int b, int c) {}
            @Override
            public void afterTextChanged(Editable s) {
                String q = Fmt.normName(s.toString());
                shown.clear();
                names.clear();
                for (Model.Exercise ex : saved) {
                    if (q.isEmpty() || Fmt.normName(ex.name).contains(q)) {
                        shown.add(ex);
                        names.add(ex.name);
                    }
                }
                adapter.notifyDataSetChanged();
            }
        });
        lv.setOnItemClickListener(new AdapterView.OnItemClickListener() {
            @Override
            public void onItemClick(AdapterView<?> parent, View view, int position, long id) {
                dialog.dismiss();
                addExercise(shown.get(position).id);
            }
        });
        final Runnable submit = new Runnable() {
            @Override
            public void run() {
                String name = Fmt.cleanName(input.getText().toString());
                if (name.isEmpty()) {
                    input.setError("Введите название");
                    return;
                }
                dialog.dismiss();
                addExercise(db.findOrCreateExercise(name));
            }
        };
        input.setOnEditorActionListener(new TextView.OnEditorActionListener() {
            @Override
            public boolean onEditorAction(TextView v, int actionId, KeyEvent event) {
                submit.run();
                return true;
            }
        });
        dialog.setOnShowListener(new DialogInterface.OnShowListener() {
            @Override
            public void onShow(DialogInterface d) {
                dialog.getButton(AlertDialog.BUTTON_POSITIVE).setOnClickListener(
                        new View.OnClickListener() {
                            @Override
                            public void onClick(View v) {
                                submit.run();
                            }
                        });
            }
        });
        if (saved.isEmpty() && dialog.getWindow() != null) {
            dialog.getWindow().setSoftInputMode(WindowManager.LayoutParams.SOFT_INPUT_STATE_ALWAYS_VISIBLE);
        }
        dialog.show();
        return dialog;
    }

    void addExercise(long exerciseId) {
        Db db = Db.get(this);
        if (db.hasEntry(day, exerciseId)) {
            Toast.makeText(this, "Этот тренажёр уже есть в этом дне", Toast.LENGTH_SHORT).show();
            return;
        }
        Model.Session prev = db.previousSession(exerciseId, day);
        int sets = prev == null ? 1 : Math.max(1, Math.min(prev.sets.size(), 12));
        db.addEntry(day, exerciseId, sets);
        reload();
        for (final EntryCard c : cards) {
            if (c.entry.exerciseId == exerciseId) {
                scroll.post(new Runnable() {
                    @Override
                    public void run() {
                        scroll.smoothScrollTo(0, Math.max(0, c.view.getTop() - Ui.dp(DayActivity.this, 8)));
                    }
                });
                c.focusFirstEmpty();
                break;
            }
        }
    }

    void showKeyboard(final View v) {
        v.requestFocus();
        v.post(new Runnable() {
            @Override
            public void run() {
                InputMethodManager imm = (InputMethodManager) getSystemService(INPUT_METHOD_SERVICE);
                if (imm != null) imm.showSoftInput(v, InputMethodManager.SHOW_IMPLICIT);
            }
        });
    }

    // ---------------------------------------------------------------- exercise card

    final class EntryCard {
        final Model.Entry entry;
        final Model.Session prev;
        final LinearLayout view;
        final FrameLayout headerIndicator;
        final LinearLayout setsBox;
        final List<SetRow> rows = new ArrayList<>();

        EntryCard(Model.Entry e) {
            entry = e;
            prev = Db.get(DayActivity.this).previousSession(e.exerciseId, day);
            final Activity a = DayActivity.this;
            view = Ui.card(a);
            view.setTag("card:" + e.name);

            LinearLayout header = Ui.horizontal(a);
            // Picture from the reference: tap to pick another one.
            FrameLayout thumb = Ui.thumbnail(a, e.catalog, 52);
            thumb.setOnClickListener(new View.OnClickListener() {
                @Override
                public void onClick(View v) {
                    pickPicture();
                }
            });
            LinearLayout.LayoutParams thl = Ui.lp(Ui.dp(a, 52), Ui.dp(a, 52));
            thl.rightMargin = Ui.dp(a, 10);
            header.addView(thumb, thl);
            LinearLayout titles = Ui.vertical(a);
            TextView name = Ui.text(a, e.name, 19, R.color.text, true);
            name.setOnClickListener(new View.OnClickListener() {
                @Override
                public void onClick(View v) {
                    focusFirstEmpty();
                }
            });
            titles.addView(name, Ui.matchWrap());
            if (e.catalog != null) {
                TextView info = Ui.text(a, "Техника и описание ›", 13, R.color.accent, false);
                info.setTag("infoLink");
                info.setPadding(0, Ui.dp(a, 2), 0, Ui.dp(a, 2));
                info.setOnClickListener(new View.OnClickListener() {
                    @Override
                    public void onClick(View v) {
                        Ui.openInfo(DayActivity.this, entry.exerciseId);
                    }
                });
                titles.addView(info, Ui.matchWrap());
            }
            header.addView(titles, Ui.lpWeight(1));
            headerIndicator = new FrameLayout(a);
            headerIndicator.setTag("headerIndicator");
            header.addView(headerIndicator, Ui.lp(ViewGroup.LayoutParams.WRAP_CONTENT,
                    ViewGroup.LayoutParams.WRAP_CONTENT));
            final ImageButton more = Ui.iconButton(a, R.drawable.ic_more, R.color.text_secondary, "Ещё");
            more.setOnClickListener(new View.OnClickListener() {
                @Override
                public void onClick(View v) {
                    showMenu(more);
                }
            });
            header.addView(more, Ui.lp(Ui.dp(a, 40), Ui.dp(a, 40)));
            view.addView(header, Ui.matchWrap());

            String prevText = prev == null
                    ? "Первая тренировка на этом тренажёре"
                    : "Прошлый раз, " + Fmt.dateLong(prev.day) + ":\n" + Fmt.joinSets(prev.sets);
            TextView prevLine = Ui.text(a, prevText, 13, R.color.text_secondary, false);
            prevLine.setLineSpacing(0, 1.1f);
            LinearLayout.LayoutParams plp = Ui.matchWrap();
            plp.topMargin = Ui.dp(a, 2);
            plp.bottomMargin = Ui.dp(a, 10);
            view.addView(prevLine, plp);

            LinearLayout cols = Ui.horizontal(a);
            cols.addView(Ui.text(a, "", 12, R.color.text_secondary, false),
                    Ui.lp(Ui.dp(a, 38), ViewGroup.LayoutParams.WRAP_CONTENT));
            TextView c1 = Ui.text(a, "Повторения", 12, R.color.text_secondary, false);
            c1.setGravity(Gravity.CENTER);
            cols.addView(c1, Ui.lpWeight(1));
            TextView c2 = Ui.text(a, "Вес, кг", 12, R.color.text_secondary, false);
            c2.setGravity(Gravity.CENTER);
            cols.addView(c2, Ui.lpWeight(1));
            cols.addView(new View(a), Ui.lp(Ui.dp(a, 52 + 36), 1));
            view.addView(cols, Ui.matchWrap());

            setsBox = Ui.vertical(a);
            view.addView(setsBox, Ui.matchWrap());
            for (Model.WSet s : e.sets) addRow(s);

            Button addSet = Ui.softButton(a, "+  Подход");
            addSet.setTag("addSet");
            addSet.setOnClickListener(new View.OnClickListener() {
                @Override
                public void onClick(View v) {
                    Model.WSet s = new Model.WSet();
                    s.id = Db.get(DayActivity.this).addSet(entry.id);
                    entry.sets.add(s);
                    SetRow r = addRow(s);
                    refreshAll();
                    showKeyboard(r.reps);
                }
            });
            LinearLayout.LayoutParams blp = Ui.matchWrap();
            blp.topMargin = Ui.dp(a, 8);
            view.addView(addSet, blp);

            refreshAll();
        }

        SetRow addRow(Model.WSet s) {
            SetRow r = new SetRow(this, s);
            rows.add(r);
            setsBox.addView(r.view, Ui.matchWrap());
            return r;
        }

        void removeRow(SetRow r) {
            Db.get(DayActivity.this).deleteSet(r.set.id);
            entry.sets.remove(r.set);
            rows.remove(r);
            setsBox.removeView(r.view);
            refreshAll();
        }

        void refreshAll() {
            for (int i = 0; i < rows.size(); i++) rows.get(i).refresh(i);
            headerIndicator.removeAllViews();
            Compare.Result r = Compare.compareExercise(entry.name, day, entry.sets, prev);
            headerIndicator.setTag(r.status.name());
            headerIndicator.addView(Ui.indicator(DayActivity.this, r));
        }

        void focusFirstEmpty() {
            for (SetRow r : rows) {
                if (r.reps.getText().length() == 0) {
                    showKeyboard(r.reps);
                    return;
                }
                if (r.weight.getText().length() == 0 && r.set.weight == null) {
                    showKeyboard(r.weight);
                    return;
                }
            }
        }

        private void showMenu(View anchor) {
            PopupMenu pm = new PopupMenu(DayActivity.this, anchor);
            pm.getMenu().add(0, 1, 0, "История тренажёра");
            pm.getMenu().add(0, 3, 1, "Описание и техника");
            pm.getMenu().add(0, 4, 2, "Сменить картинку");
            pm.getMenu().add(0, 2, 3, "Убрать из этого дня");
            pm.setOnMenuItemClickListener(new PopupMenu.OnMenuItemClickListener() {
                @Override
                public boolean onMenuItemClick(MenuItem item) {
                    switch (item.getItemId()) {
                        case 1: Ui.showHistory(DayActivity.this, entry.exerciseId, entry.name, day); break;
                        case 3: Ui.openInfo(DayActivity.this, entry.exerciseId); break;
                        case 4: pickPicture(); break;
                        default: confirmRemove(); break;
                    }
                    return true;
                }
            });
            pm.show();
        }

        void pickPicture() {
            Ui.showPicker(DayActivity.this, entry.exerciseId, new Runnable() {
                @Override
                public void run() {
                    reload();
                }
            });
        }

        private void confirmRemove() {
            new AlertDialog.Builder(DayActivity.this)
                    .setTitle("Убрать «" + entry.name + "»?")
                    .setMessage("Подходы этого тренажёра за " + Fmt.dateLong(day)
                            + " будут удалены. Сам тренажёр останется в списке сохранённых.")
                    .setNegativeButton("Отмена", null)
                    .setPositiveButton("Убрать", new DialogInterface.OnClickListener() {
                        @Override
                        public void onClick(DialogInterface d, int which) {
                            Db.get(DayActivity.this).deleteEntry(entry.id);
                            reload();
                        }
                    })
                    .show();
        }
    }

    // ---------------------------------------------------------------- one set row

    final class SetRow {
        final EntryCard card;
        final Model.WSet set;
        final LinearLayout view;
        final TextView label;
        final EditText reps;
        final EditText weight;
        final FrameLayout indicatorBox;

        SetRow(EntryCard c, Model.WSet s) {
            card = c;
            set = s;
            Activity a = DayActivity.this;
            view = Ui.horizontal(a);
            view.setTag("setRow");
            int v = Ui.dp(a, 4);
            view.setPadding(0, v, 0, v);

            label = Ui.text(a, "", 14, R.color.accent, true);
            label.setGravity(Gravity.CENTER);
            label.setBackground(Ui.circle(Ui.color(a, R.color.accent_soft)));
            LinearLayout.LayoutParams llp = Ui.lp(Ui.dp(a, 30), Ui.dp(a, 30));
            llp.rightMargin = Ui.dp(a, 8);
            view.addView(label, llp);

            reps = numberInput(false);
            reps.setTag("reps");
            if (s.reps != null) reps.setText(String.valueOf(s.reps));
            LinearLayout.LayoutParams rlp = Ui.lpWeight(1);
            rlp.rightMargin = Ui.dp(a, 6);
            view.addView(reps, rlp);

            weight = numberInput(true);
            weight.setTag("weight");
            if (s.weight != null) weight.setText(Fmt.num(s.weight));
            view.addView(weight, Ui.lpWeight(1));

            indicatorBox = new FrameLayout(a);
            indicatorBox.setTag("indicator");
            view.addView(indicatorBox, Ui.lp(Ui.dp(a, 52), Ui.dp(a, 40)));

            ImageButton del = Ui.iconButton(a, R.drawable.ic_close, R.color.text_secondary, "Удалить подход");
            del.setTag("deleteSet");
            del.setOnClickListener(new View.OnClickListener() {
                @Override
                public void onClick(View v) {
                    confirmDelete();
                }
            });
            view.addView(del, Ui.lp(Ui.dp(a, 36), Ui.dp(a, 36)));

            TextWatcher w = new TextWatcher() {
                @Override public void beforeTextChanged(CharSequence x, int a1, int b, int c1) {}
                @Override public void onTextChanged(CharSequence x, int a1, int b, int c1) {}
                @Override
                public void afterTextChanged(Editable e) {
                    save();
                }
            };
            reps.addTextChangedListener(w);
            weight.addTextChangedListener(w);
        }

        private EditText numberInput(boolean decimal) {
            Activity a = DayActivity.this;
            EditText e = new EditText(a);
            e.setSingleLine(true);
            e.setGravity(Gravity.CENTER);
            e.setTextSize(18);
            e.setTextColor(Ui.color(a, R.color.text));
            e.setHintTextColor(Ui.color(a, R.color.hint));
            e.setBackground(Ui.inputBackground(a));
            int p = Ui.dp(a, 8);
            e.setPadding(p, p, p, p);
            e.setMinHeight(Ui.dp(a, 44));
            e.setSelectAllOnFocus(true);
            e.setImeOptions(EditorInfo.IME_ACTION_NEXT | EditorInfo.IME_FLAG_NO_EXTRACT_UI);
            if (decimal) {
                e.setKeyListener(DigitsKeyListener.getInstance("0123456789.,"));
                e.setRawInputType(InputType.TYPE_CLASS_NUMBER | InputType.TYPE_NUMBER_FLAG_DECIMAL);
                e.setFilters(new InputFilter[]{new InputFilter.LengthFilter(7)});
            } else {
                e.setInputType(InputType.TYPE_CLASS_NUMBER);
                e.setFilters(new InputFilter[]{new InputFilter.LengthFilter(4)});
            }
            return e;
        }

        void save() {
            set.reps = Fmt.parseReps(reps.getText().toString());
            set.weight = Fmt.parseWeight(weight.getText().toString());
            Db.get(DayActivity.this).updateSet(set.id, set.reps, set.weight);
            card.refreshAll();
        }

        void refresh(int index) {
            label.setText(String.valueOf(index + 1));
            Model.WSet p = card.prev != null && index < card.prev.sets.size()
                    ? card.prev.sets.get(index) : null;
            // Hints show what was done last time in this set.
            reps.setHint(p != null ? String.valueOf(p.reps) : "повт.");
            weight.setHint(p != null && p.weight != null && p.weight > 0 ? Fmt.num(p.weight) : "кг");
            indicatorBox.removeAllViews();
            Compare.Result r = Compare.compareSet(card.entry.name, day, index, set, card.prev);
            indicatorBox.setTag(r.status.name());
            indicatorBox.addView(Ui.indicator(DayActivity.this, r), new FrameLayout.LayoutParams(
                    ViewGroup.LayoutParams.WRAP_CONTENT, ViewGroup.LayoutParams.WRAP_CONTENT, Gravity.CENTER));
        }

        private void confirmDelete() {
            if (!set.filled() && set.weight == null) {
                card.removeRow(this);
                return;
            }
            new AlertDialog.Builder(DayActivity.this)
                    .setTitle("Удалить подход " + label.getText() + "?")
                    .setMessage(Fmt.setLong(set))
                    .setNegativeButton("Отмена", null)
                    .setPositiveButton("Удалить", new DialogInterface.OnClickListener() {
                        @Override
                        public void onClick(DialogInterface d, int which) {
                            card.removeRow(SetRow.this);
                        }
                    })
                    .show();
        }
    }
}
