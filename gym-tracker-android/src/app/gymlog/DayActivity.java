package app.gymlog;

import android.app.ActionBar;
import android.app.Activity;
import android.app.AlertDialog;
import android.content.ActivityNotFoundException;
import android.content.DialogInterface;
import android.content.Intent;
import android.content.res.ColorStateList;
import android.graphics.Rect;
import android.os.Bundle;
import android.text.Editable;
import android.text.InputFilter;
import android.text.InputType;
import android.text.TextUtils;
import android.text.TextWatcher;
import android.text.method.DigitsKeyListener;
import android.view.Gravity;
import android.view.KeyEvent;
import android.view.Menu;
import android.view.MenuItem;
import android.view.View;
import android.view.ViewGroup;
import android.view.ViewTreeObserver;
import android.view.inputmethod.EditorInfo;
import android.view.inputmethod.InputMethodManager;
import android.widget.AdapterView;
import android.widget.BaseAdapter;
import android.widget.Button;
import android.widget.EditText;
import android.widget.FrameLayout;
import android.widget.HorizontalScrollView;
import android.widget.ImageButton;
import android.widget.ImageView;
import android.widget.LinearLayout;
import android.widget.ListView;
import android.widget.PopupMenu;
import android.widget.ScrollView;
import android.widget.TextView;
import android.widget.Toast;

import java.time.LocalDate;
import java.util.ArrayList;
import java.util.HashSet;
import java.util.List;
import java.util.Set;

/**
 * One training day: the machines are shown collapsed (name + short summary); tapping one
 * opens its sets (repetitions + weight) with the comparison to the previous time.
 */
public class DayActivity extends BaseActivity {
    public static final String EXTRA_DAY = "day";
    private static final int MENU_PREV = 1;
    private static final int MENU_NEXT = 2;
    private static final int MENU_EXPAND_ALL = 3;
    private static final int MENU_COLLAPSE_ALL = 4;

    String day;
    private ScrollView scroll;
    private LinearLayout list;
    private TextView emptyView;
    View bottomBar;
    final List<EntryCard> cards = new ArrayList<>();
    /** Entries that are opened (expanded). Everything else is shown collapsed. */
    final Set<Long> expanded = new HashSet<>();

    @Override
    protected void onCreate(Bundle saved) {
        super.onCreate(saved);
        day = getIntent().getStringExtra(EXTRA_DAY);
        if (day == null) day = LocalDate.now().toString();
        if (saved != null && saved.getLongArray("expanded") != null) {
            for (long id : saved.getLongArray("expanded")) expanded.add(id);
        }
        ActionBar ab = getActionBar();
        if (ab != null) ab.setDisplayHomeAsUpEnabled(true);
        buildUi();
        reload();
    }

    @Override
    protected void onSaveInstanceState(Bundle out) {
        super.onSaveInstanceState(out);
        long[] ids = new long[expanded.size()];
        int i = 0;
        for (long id : expanded) ids[i++] = id;
        out.putLongArray("expanded", ids);
    }

    @Override
    protected void onRestart() {
        super.onRestart();
        reload();
    }

    private void buildUi() {
        final LinearLayout root = Ui.vertical(this);

        scroll = new ScrollView(this);
        scroll.setFillViewport(true);
        LinearLayout content = Ui.vertical(this);
        int p = Ui.dp(this, 12);
        content.setPadding(p, p, p, p);
        scroll.addView(content, new ScrollView.LayoutParams(ViewGroup.LayoutParams.MATCH_PARENT,
                ViewGroup.LayoutParams.WRAP_CONTENT));
        root.addView(scroll, new LinearLayout.LayoutParams(ViewGroup.LayoutParams.MATCH_PARENT, 0, 1));

        list = Ui.vertical(this);
        content.addView(list, Ui.matchWrap());

        emptyView = Ui.text(this, "В этот день ещё ничего не записано.\n\n"
                + "Нажмите «Добавить тренажёр» внизу: введите название тренажёра или группу мышц "
                + "(например, «грудь») и выберите упражнение, а затем запишите подходы.",
                15, R.color.text_secondary, false);
        emptyView.setLineSpacing(0, 1.15f);
        emptyView.setGravity(Gravity.CENTER);
        int ep = Ui.dp(this, 18);
        emptyView.setPadding(ep, Ui.dp(this, 36), ep, Ui.dp(this, 24));
        content.addView(emptyView, Ui.matchWrap());

        // The "add" button is pinned to the bottom: no need to scroll down to it.
        LinearLayout bar = Ui.vertical(this);
        bar.setBackgroundColor(Ui.color(this, R.color.card));
        bar.setElevation(Ui.dp(this, 8));
        bar.setPadding(p, Ui.dp(this, 8), p, Ui.dp(this, 8));
        Button add = Ui.primaryButton(this, "+  Добавить тренажёр");
        add.setTag("addExercise");
        add.setOnClickListener(new View.OnClickListener() {
            @Override
            public void onClick(View v) {
                showAddDialog();
            }
        });
        bar.addView(add, Ui.matchWrap());
        root.addView(bar, Ui.matchWrap());
        bottomBar = bar;

        // Hide the bottom button while the keyboard is open: more room for the sets.
        root.getViewTreeObserver().addOnGlobalLayoutListener(new ViewTreeObserver.OnGlobalLayoutListener() {
            @Override
            public void onGlobalLayout() {
                Rect r = new Rect();
                root.getWindowVisibleDisplayFrame(r);
                int screen = root.getRootView().getHeight();
                // Unknown sizes (r is empty) -> keep the button visible.
                boolean keyboard = screen > 0 && r.height() > 0 && screen - r.bottom > screen * 0.15;
                int vis = keyboard ? View.GONE : View.VISIBLE;
                if (bottomBar.getVisibility() != vis) bottomBar.setVisibility(vis);
            }
        });
        setContentView(root);
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
            lp.bottomMargin = Ui.dp(this, 10);
            list.addView(card.view, lp);
        }
        emptyView.setVisibility(cards.isEmpty() ? View.VISIBLE : View.GONE);
    }

    private void switchDay(String newDay) {
        day = newDay;
        expanded.clear();
        setIntent(new Intent(getIntent()).putExtra(EXTRA_DAY, day));
        reload();
        scroll.scrollTo(0, 0);
    }

    @Override
    public boolean onCreateOptionsMenu(Menu menu) {
        MenuItem prev = menu.add(0, MENU_PREV, 0, "Предыдущий день");
        prev.setIcon(whiteIcon(R.drawable.ic_chevron_left));
        prev.setShowAsAction(MenuItem.SHOW_AS_ACTION_ALWAYS);
        MenuItem next = menu.add(0, MENU_NEXT, 1, "Следующий день");
        next.setIcon(whiteIcon(R.drawable.ic_chevron_right));
        next.setShowAsAction(MenuItem.SHOW_AS_ACTION_ALWAYS);
        menu.add(0, MENU_EXPAND_ALL, 2, "Развернуть все");
        menu.add(0, MENU_COLLAPSE_ALL, 3, "Свернуть все");
        return true;
    }

    @Override
    public boolean onOptionsItemSelected(MenuItem item) {
        switch (item.getItemId()) {
            case MENU_PREV:
                switchDay(Fmt.parseDay(day).minusDays(1).toString());
                return true;
            case MENU_NEXT:
                switchDay(Fmt.parseDay(day).plusDays(1).toString());
                return true;
            case MENU_EXPAND_ALL:
                for (EntryCard c : cards) c.setExpanded(true);
                return true;
            case MENU_COLLAPSE_ALL:
                for (EntryCard c : cards) c.setExpanded(false);
                return true;
            default:
                return super.onOptionsItemSelected(item);
        }
    }

    // ---------------------------------------------------------------- add exercise

    /** Called when something is picked in the "add" dialog. */
    interface Picker {
        void pick(Object o);
    }

    /**
     * Rows of the list in the "add" dialog: section titles (String), saved machines
     * (Model.Exercise) and rows of two photo tiles from the reference (Catalog.Item[]).
     */
    static final class AddAdapter extends BaseAdapter {
        final Activity a;
        final List<Object> rows = new ArrayList<>();
        Picker picker;

        AddAdapter(Activity a) {
            this.a = a;
        }

        /** All reference exercises currently shown, in order. */
        List<Catalog.Item> catalogItems() {
            List<Catalog.Item> out = new ArrayList<>();
            for (Object o : rows) {
                if (o instanceof Catalog.Item[]) {
                    for (Catalog.Item it : (Catalog.Item[]) o) out.add(it);
                }
            }
            return out;
        }

        /** Index of the row that shows this reference exercise, or -1. */
        int rowOf(String key) {
            for (int i = 0; i < rows.size(); i++) {
                Object o = rows.get(i);
                if (o instanceof Catalog.Item[]) {
                    for (Catalog.Item it : (Catalog.Item[]) o) if (it.key.equals(key)) return i;
                }
            }
            return -1;
        }

        @Override
        public int getCount() {
            return rows.size();
        }

        @Override
        public Object getItem(int i) {
            return rows.get(i);
        }

        @Override
        public long getItemId(int i) {
            return i;
        }

        @Override
        public boolean isEnabled(int i) {
            return rows.get(i) instanceof Model.Exercise;
        }

        @Override
        public boolean areAllItemsEnabled() {
            return false;
        }

        @Override
        public View getView(int i, View convert, ViewGroup parent) {
            Object o = rows.get(i);
            if (o instanceof String) {
                TextView h = Ui.text(a, (String) o, 13, R.color.text_secondary, true);
                h.setPadding(0, Ui.dp(a, 12), 0, Ui.dp(a, 4));
                return h;
            }
            if (o instanceof Model.Exercise) {
                Model.Exercise ex = (Model.Exercise) o;
                LinearLayout row = Ui.horizontal(a);
                int rp = Ui.dp(a, 6);
                row.setPadding(0, rp, 0, rp);
                FrameLayout th = Ui.thumbnail(a, ex.catalog, 48);
                th.setClickable(false);
                th.setForeground(null);
                row.addView(th, Ui.lp(Ui.dp(a, 56), Ui.dp(a, 44)));
                TextView t = Ui.text(a, ex.name, 16, R.color.text, false);
                LinearLayout.LayoutParams tl = Ui.lpWeight(1);
                tl.leftMargin = Ui.dp(a, 12);
                row.addView(t, tl);
                return row;
            }
            Catalog.Item[] pair = (Catalog.Item[]) o;
            LinearLayout row = Ui.horizontal(a);
            row.setGravity(Gravity.TOP);
            row.setPadding(0, Ui.dp(a, 4), 0, Ui.dp(a, 4));
            for (int k = 0; k < 2; k++) {
                LinearLayout.LayoutParams lp = Ui.lpWeight(1);
                if (k == 0) lp.rightMargin = Ui.dp(a, 4); else lp.leftMargin = Ui.dp(a, 4);
                if (k < pair.length) row.addView(tile(pair[k]), lp);
                else row.addView(new View(a), lp);
            }
            return row;
        }

        /** Photo + caption; tap = add, the red ▶ = video with the technique. */
        private View tile(final Catalog.Item it) {
            LinearLayout t = Ui.vertical(a);
            t.setTag("tile:" + it.key);
            float r = Ui.dp(a, 12);
            t.setBackground(Ui.ripple(a, Ui.rounded(Ui.color(a, R.color.input_bg), r), Ui.rounded(0xFFFFFFFF, r)));
            t.setClickable(true);
            t.setOnClickListener(new View.OnClickListener() {
                @Override
                public void onClick(View v) {
                    if (picker != null) picker.pick(it);
                }
            });
            FrameLayout photo = Ui.photoBox(a, it.key, 0, 48, r);
            photo.setClickable(false);
            photo.setForeground(null);
            ImageButton video = Ui.iconButton(a, R.drawable.ic_play, R.color.down, "Видео с техникой");
            video.setBackground(Ui.circle(0xE6FFFFFF));
            int vp = Ui.dp(a, 4);
            video.setPadding(vp, vp, vp, vp);
            video.setFocusable(false);
            video.setTag("video:" + it.key);
            video.setOnClickListener(new View.OnClickListener() {
                @Override
                public void onClick(View v) {
                    openVideo(a, it);
                }
            });
            FrameLayout.LayoutParams vl = new FrameLayout.LayoutParams(Ui.dp(a, 34), Ui.dp(a, 34),
                    Gravity.TOP | Gravity.END);
            vl.setMargins(0, Ui.dp(a, 4), Ui.dp(a, 4), 0);
            photo.addView(video, vl);
            t.addView(photo, new LinearLayout.LayoutParams(ViewGroup.LayoutParams.MATCH_PARENT, Ui.dp(a, 104)));
            TextView title = Ui.text(a, it.title, 13, R.color.text, true);
            title.setMaxLines(2);
            title.setEllipsize(TextUtils.TruncateAt.END);
            int p = Ui.dp(a, 6);
            title.setPadding(p, p, p, 0);
            t.addView(title, Ui.matchWrap());
            TextView eq = Ui.text(a, it.equipmentLabel()
                    + (it.bodyweight() ? " · доп. вес" : ""), 11, R.color.text_secondary, false);
            eq.setPadding(p, 0, p, p);
            t.addView(eq, Ui.matchWrap());
            return t;
        }
    }

    static void openVideo(Activity a, Catalog.Item it) {
        try {
            a.startActivity(new Intent(Intent.ACTION_VIEW, ExerciseInfoActivity.videoSearchUri(it)));
        } catch (ActivityNotFoundException e) {
            Toast.makeText(a, "Не найдено приложение для открытия ссылки", Toast.LENGTH_LONG).show();
        }
    }

    private TextView chip(String label, String tag) {
        TextView chip = Ui.text(this, label, 14, R.color.accent, true);
        chip.setTag(tag);
        int cp = Ui.dp(this, 12);
        chip.setPadding(cp, Ui.dp(this, 6), cp, Ui.dp(this, 6));
        chip.setBackground(Ui.ripple(this, Ui.rounded(Ui.color(this, R.color.accent_soft), Ui.dp(this, 16)),
                Ui.rounded(0xFFFFFFFF, Ui.dp(this, 16))));
        return chip;
    }

    private static void selectChip(Activity a, TextView chip, boolean selected) {
        chip.setBackground(Ui.ripple(a, Ui.rounded(Ui.color(a, selected ? R.color.accent : R.color.accent_soft),
                Ui.dp(a, 16)), Ui.rounded(0xFFFFFFFF, Ui.dp(a, 16))));
        chip.setTextColor(Ui.color(a, selected ? R.color.on_accent : R.color.accent));
    }

    private static HorizontalScrollView chipRow(Activity a, LinearLayout chips) {
        HorizontalScrollView sc = new HorizontalScrollView(a);
        sc.setHorizontalScrollBarEnabled(false);
        sc.addView(chips);
        return sc;
    }

    AlertDialog showAddDialog() {
        final Db db = Db.get(this);
        final List<Model.Exercise> all = db.exercises();
        final List<Model.Exercise> saved = new ArrayList<>();
        final Set<String> usedKeys = new HashSet<>();
        for (Model.Exercise ex : all) {
            if (ex.catalog != null) usedKeys.add(ex.catalog);
            if (!db.hasEntry(day, ex.id)) saved.add(ex);
        }

        LinearLayout box = Ui.vertical(this);
        int p = Ui.dp(this, 16);
        box.setPadding(p, Ui.dp(this, 8), p, 0);

        final EditText input = new EditText(this);
        input.setTag("exerciseName");
        input.setHint("Тренажёр или группа мышц: грудь, спина…");
        input.setSingleLine(true);
        input.setInputType(InputType.TYPE_CLASS_TEXT | InputType.TYPE_TEXT_FLAG_CAP_SENTENCES);
        input.setImeOptions(EditorInfo.IME_ACTION_DONE);
        input.setFilters(new InputFilter[]{new InputFilter.LengthFilter(60)});
        box.addView(input, Ui.matchWrap());

        // Muscle group chips: tap to see all exercises of the group as photos.
        LinearLayout groups = Ui.horizontal(this);
        for (final Catalog.Group g : Catalog.GROUPS) {
            TextView c = chip(g.title, "chip:" + g.key);
            c.setOnClickListener(new View.OnClickListener() {
                @Override
                public void onClick(View v) {
                    input.setText(g.title);
                    input.setSelection(input.getText().length());
                }
            });
            LinearLayout.LayoutParams cl = Ui.lp(ViewGroup.LayoutParams.WRAP_CONTENT, ViewGroup.LayoutParams.WRAP_CONTENT);
            cl.rightMargin = Ui.dp(this, 6);
            groups.addView(c, cl);
        }
        LinearLayout.LayoutParams gl = Ui.matchWrap();
        gl.topMargin = Ui.dp(this, 8);
        box.addView(chipRow(this, groups), gl);

        // Equipment filter: all / machine / cable / barbell / dumbbells / own weight.
        final String[] equip = {null};
        final List<TextView> equipChips = new ArrayList<>();
        LinearLayout eqRow = Ui.horizontal(this);
        final AddAdapter adapter = new AddAdapter(this);
        final Runnable[] filterRef = new Runnable[1];
        String[][] eqs = new String[Catalog.EQUIPMENT.length + 1][];
        eqs[0] = new String[]{null, "Всё оборудование"};
        System.arraycopy(Catalog.EQUIPMENT, 0, eqs, 1, Catalog.EQUIPMENT.length);
        for (final String[] e : eqs) {
            final TextView c = chip(e[1], "equip:" + (e[0] == null ? "all" : e[0]));
            equipChips.add(c);
            c.setOnClickListener(new View.OnClickListener() {
                @Override
                public void onClick(View v) {
                    equip[0] = e[0];
                    for (TextView other : equipChips) selectChip(DayActivity.this, other, other == c);
                    filterRef[0].run();
                }
            });
            LinearLayout.LayoutParams cl = Ui.lp(ViewGroup.LayoutParams.WRAP_CONTENT, ViewGroup.LayoutParams.WRAP_CONTENT);
            cl.rightMargin = Ui.dp(this, 6);
            eqRow.addView(c, cl);
        }
        selectChip(this, equipChips.get(0), true);
        LinearLayout.LayoutParams el = Ui.matchWrap();
        el.topMargin = Ui.dp(this, 6);
        box.addView(chipRow(this, eqRow), el);

        final ListView lv = new ListView(this);
        lv.setTag("addList");
        lv.setDivider(null);
        lv.setAdapter(adapter);
        box.addView(lv, new LinearLayout.LayoutParams(ViewGroup.LayoutParams.MATCH_PARENT, Ui.dp(this, 420)));

        final Runnable filter = new Runnable() {
            @Override
            public void run() {
                String text = input.getText().toString();
                String q = Fmt.normName(text);
                String group = Matcher.groupOf(text);
                adapter.rows.clear();
                List<Model.Exercise> mine = new ArrayList<>();
                for (Model.Exercise ex : saved) {
                    Catalog.Item it = Catalog.get(ex.catalog);
                    if (q.isEmpty() || Fmt.normName(ex.name).contains(q)
                            || (group != null && it != null && it.inGroup(group))) {
                        mine.add(ex);
                    }
                }
                if (!mine.isEmpty()) {
                    adapter.rows.add(q.isEmpty() ? "Ваши тренажёры" : "Ваши тренажёры — подходят");
                    adapter.rows.addAll(mine);
                }
                List<Catalog.Item> ref = new ArrayList<>();
                String title;
                if (q.isEmpty()) {
                    title = "Популярные упражнения";
                    for (Catalog.Item it : Catalog.all()) {
                        if (it.technique.length > 0 && !usedKeys.contains(it.key)) ref.add(it);
                    }
                } else {
                    Catalog.Group g = Catalog.group(group);
                    title = g != null ? "Справочник: " + g.title.toLowerCase(new java.util.Locale("ru")) : "Из справочника";
                    for (Catalog.Item it : Matcher.suggest(text, 40)) {
                        if (!usedKeys.contains(it.key)) ref.add(it);
                    }
                }
                if (equip[0] != null) {
                    List<Catalog.Item> f = new ArrayList<>();
                    for (Catalog.Item it : ref) if (equip[0].equals(it.equipment())) f.add(it);
                    ref = f;
                }
                if (q.isEmpty() && ref.size() > 24) ref = ref.subList(0, 24);
                if (!ref.isEmpty()) {
                    adapter.rows.add(title + " (" + ref.size() + ")");
                    for (int i = 0; i < ref.size(); i += 2) {
                        adapter.rows.add(i + 1 < ref.size()
                                ? new Catalog.Item[]{ref.get(i), ref.get(i + 1)} : new Catalog.Item[]{ref.get(i)});
                    }
                } else if (!q.isEmpty() && mine.isEmpty()) {
                    adapter.rows.add("В справочнике не нашлось — нажмите «Добавить», чтобы сохранить своё название");
                }
                adapter.notifyDataSetChanged();
            }
        };
        filterRef[0] = filter;
        filter.run();

        final AlertDialog dialog = new AlertDialog.Builder(this)
                .setTitle("Добавить тренажёр")
                .setView(box)
                .setNegativeButton("Отмена", null)
                .setPositiveButton("Добавить", null)
                .create();

        adapter.picker = new Picker() {
            @Override
            public void pick(Object o) {
                if (o instanceof Model.Exercise) {
                    dialog.dismiss();
                    addExercise(((Model.Exercise) o).id);
                } else if (o instanceof Catalog.Item) {
                    Catalog.Item it = (Catalog.Item) o;
                    long exId = db.findOrCreateExercise(it.title);
                    db.setCatalog(exId, it.key);
                    dialog.dismiss();
                    addExercise(exId);
                }
            }
        };
        input.addTextChangedListener(new TextWatcher() {
            @Override public void beforeTextChanged(CharSequence s, int a, int b, int c) {}
            @Override public void onTextChanged(CharSequence s, int a, int b, int c) {}
            @Override
            public void afterTextChanged(Editable s) {
                // search after a short pause in typing: the reference can hold 1500+ exercises
                input.removeCallbacks(filter);
                input.postDelayed(filter, 150);
            }
        });
        lv.setOnItemClickListener(new AdapterView.OnItemClickListener() {
            @Override
            public void onItemClick(AdapterView<?> parent, View view, int position, long id) {
                adapter.picker.pick(adapter.getItem(position));
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
                if (Matcher.groupOf(name) != null) {
                    input.setError("Это группа мышц — выберите упражнение из фото ниже");
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
        long entryId = db.addEntry(day, exerciseId, sets);
        expanded.add(entryId);
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
        final TextView summary;
        final TextView infoLink;
        final ImageView chevron;
        /** Everything under the header: previous result, sets, "+ Подход". */
        final LinearLayout body;
        final LinearLayout setsBox;
        final List<SetRow> rows = new ArrayList<>();
        boolean expandedNow;

        EntryCard(Model.Entry e) {
            entry = e;
            prev = Db.get(DayActivity.this).previousSession(e.exerciseId, day);
            final Activity a = DayActivity.this;
            view = Ui.card(a);
            int cp = Ui.dp(a, 10);
            view.setPadding(cp, cp, Ui.dp(a, 4), cp);
            view.setTag("card:" + e.name);

            LinearLayout header = Ui.horizontal(a);
            // Picture from the reference: tap to pick another one.
            FrameLayout thumb = Ui.thumbnail(a, e.catalog, 48);
            thumb.setOnClickListener(new View.OnClickListener() {
                @Override
                public void onClick(View v) {
                    pickPicture();
                }
            });
            LinearLayout.LayoutParams thl = Ui.lp(Ui.dp(a, 48), Ui.dp(a, 48));
            thl.rightMargin = Ui.dp(a, 10);
            header.addView(thumb, thl);

            LinearLayout titles = Ui.vertical(a);
            titles.setTag("cardHeader");
            TextView name = Ui.text(a, e.name, 18, R.color.text, true);
            titles.addView(name, Ui.matchWrap());
            summary = Ui.text(a, "", 13, R.color.text_secondary, false);
            summary.setTag("summary");
            summary.setMaxLines(2);
            summary.setEllipsize(TextUtils.TruncateAt.END);
            titles.addView(summary, Ui.matchWrap());
            infoLink = Ui.text(a, "Техника и описание ›", 13, R.color.accent, false);
            infoLink.setTag("infoLink");
            infoLink.setPadding(0, Ui.dp(a, 2), 0, Ui.dp(a, 2));
            infoLink.setOnClickListener(new View.OnClickListener() {
                @Override
                public void onClick(View v) {
                    Ui.openInfo(DayActivity.this, entry.exerciseId);
                }
            });
            titles.addView(infoLink, Ui.matchWrap());
            View.OnClickListener toggle = new View.OnClickListener() {
                @Override
                public void onClick(View v) {
                    setExpanded(!expandedNow);
                }
            };
            titles.setOnClickListener(toggle);
            header.addView(titles, Ui.lpWeight(1));

            headerIndicator = new FrameLayout(a);
            headerIndicator.setTag("headerIndicator");
            header.addView(headerIndicator, Ui.lp(ViewGroup.LayoutParams.WRAP_CONTENT,
                    ViewGroup.LayoutParams.WRAP_CONTENT));
            chevron = new ImageView(a);
            chevron.setImageResource(R.drawable.ic_expand);
            chevron.setImageTintList(ColorStateList.valueOf(Ui.color(a, R.color.text_secondary)));
            chevron.setContentDescription("Развернуть или свернуть");
            int chp = Ui.dp(a, 6);
            chevron.setPadding(chp, chp, chp, chp);
            Ui.selectableBorderless(chevron);
            chevron.setOnClickListener(toggle);
            chevron.setTag("chevron");
            header.addView(chevron, Ui.lp(Ui.dp(a, 36), Ui.dp(a, 36)));
            final ImageButton more = Ui.iconButton(a, R.drawable.ic_more, R.color.text_secondary, "Ещё");
            more.setOnClickListener(new View.OnClickListener() {
                @Override
                public void onClick(View v) {
                    showMenu(more);
                }
            });
            header.addView(more, Ui.lp(Ui.dp(a, 36), Ui.dp(a, 40)));
            view.addView(header, Ui.matchWrap());

            body = Ui.vertical(a);
            body.setTag("body");
            body.setPadding(0, 0, Ui.dp(a, 6), 0);
            view.addView(body, Ui.matchWrap());

            String prevText = prev == null
                    ? "Первая тренировка на этом тренажёре"
                    : "Прошлый раз, " + Fmt.dateLong(prev.day) + ":\n" + Fmt.joinSets(prev.sets, e.extra);
            TextView prevLine = Ui.text(a, prevText, 13, R.color.text_secondary, false);
            prevLine.setLineSpacing(0, 1.1f);
            LinearLayout.LayoutParams plp = Ui.matchWrap();
            plp.topMargin = Ui.dp(a, 8);
            plp.bottomMargin = Ui.dp(a, 10);
            body.addView(prevLine, plp);

            LinearLayout cols = Ui.horizontal(a);
            cols.addView(Ui.text(a, "", 12, R.color.text_secondary, false),
                    Ui.lp(Ui.dp(a, 38), ViewGroup.LayoutParams.WRAP_CONTENT));
            TextView c1 = Ui.text(a, "Повторения", 12, R.color.text_secondary, false);
            c1.setGravity(Gravity.CENTER);
            cols.addView(c1, Ui.lpWeight(1));
            TextView c2 = Ui.text(a, e.extra ? "Доп. вес, кг" : "Вес, кг", 12, R.color.text_secondary, false);
            c2.setTag("weightColumn");
            c2.setGravity(Gravity.CENTER);
            cols.addView(c2, Ui.lpWeight(1));
            cols.addView(new View(a), Ui.lp(Ui.dp(a, 52 + 36), 1));
            body.addView(cols, Ui.matchWrap());

            setsBox = Ui.vertical(a);
            body.addView(setsBox, Ui.matchWrap());
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
            body.addView(addSet, blp);

            applyExpanded(expanded.contains(e.id));
            refreshAll();
        }

        void setExpanded(boolean open) {
            if (open) expanded.add(entry.id); else expanded.remove(entry.id);
            applyExpanded(open);
        }

        private void applyExpanded(boolean open) {
            expandedNow = open;
            body.setVisibility(open ? View.VISIBLE : View.GONE);
            summary.setVisibility(open ? View.GONE : View.VISIBLE);
            infoLink.setVisibility(open && entry.catalog != null ? View.VISIBLE : View.GONE);
            chevron.setRotation(open ? 180 : 0);
            if (!open) {
                View f = getCurrentFocus();
                if (f != null && isDescendant(body, f)) {
                    f.clearFocus();
                    InputMethodManager imm = (InputMethodManager) getSystemService(INPUT_METHOD_SERVICE);
                    if (imm != null) imm.hideSoftInputFromWindow(f.getWindowToken(), 0);
                }
            }
        }

        private boolean isDescendant(View parent, View v) {
            while (v != null) {
                if (v == parent) return true;
                v = v.getParent() instanceof View ? (View) v.getParent() : null;
            }
            return false;
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
            Compare.Result r = Compare.compareExercise(entry.name, day, entry.sets, prev, entry.extra);
            headerIndicator.setTag(r.status.name());
            headerIndicator.addView(Ui.indicator(DayActivity.this, r));
            int filled = 0;
            for (Model.WSet s : entry.sets) if (s.filled()) filled++;
            summary.setText(filled == 0 ? "Подходы не записаны — нажмите, чтобы записать"
                    : Fmt.setsCount(filled) + ":  " + Fmt.joinSets(entry.sets, entry.extra));
        }

        void focusFirstEmpty() {
            if (!expandedNow) setExpanded(true);
            for (SetRow r : rows) {
                if (r.reps.getText().length() == 0) {
                    showKeyboard(r.reps);
                    return;
                }
                if (!entry.extra && r.weight.getText().length() == 0 && r.set.weight == null) {
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
            pm.getMenu().add(0, 5, 3, entry.extra ? "Вес: обычный (не дополнительный)"
                    : "Вес: дополнительный (свой вес + кг)");
            pm.getMenu().add(0, 2, 4, "Убрать из этого дня");
            pm.setOnMenuItemClickListener(new PopupMenu.OnMenuItemClickListener() {
                @Override
                public boolean onMenuItemClick(MenuItem item) {
                    switch (item.getItemId()) {
                        case 1: Ui.showHistory(DayActivity.this, entry.exerciseId, entry.name, day); break;
                        case 3: Ui.openInfo(DayActivity.this, entry.exerciseId); break;
                        case 4: pickPicture(); break;
                        case 5: toggleWeightMode(); break;
                        default: confirmRemove(); break;
                    }
                    return true;
                }
            });
            pm.show();
        }

        void toggleWeightMode() {
            Db.get(DayActivity.this).setWeightMode(entry.exerciseId, entry.extra ? Db.WEIGHT_NORMAL : Db.WEIGHT_EXTRA);
            Toast.makeText(DayActivity.this, entry.extra
                    ? "Теперь вес считается обычным"
                    : "Теперь вес — дополнительный: пишите только утяжеление (например, 10) или оставляйте пустым",
                    Toast.LENGTH_LONG).show();
            reload();
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
                            expanded.remove(entry.id);
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
                e.setKeyListener(DigitsKeyListener.getInstance("0123456789.,+"));
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
            boolean extra = card.entry.extra;
            // Hints show what was done last time in this set.
            reps.setHint(p != null ? String.valueOf(p.reps) : "повт.");
            if (p != null && p.weight != null && p.weight > 0) {
                weight.setHint((extra ? "+" : "") + Fmt.num(p.weight));
            } else {
                weight.setHint(extra ? "+0" : "кг");
            }
            indicatorBox.removeAllViews();
            Compare.Result r = Compare.compareSet(card.entry.name, day, index, set, card.prev, extra);
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
                    .setMessage(Fmt.setLong(set, card.entry.extra))
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
