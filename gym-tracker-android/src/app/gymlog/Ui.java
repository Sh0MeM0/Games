package app.gymlog;

import android.app.Activity;
import android.app.AlertDialog;
import android.content.Context;
import android.content.Intent;
import android.content.res.ColorStateList;
import android.graphics.Typeface;
import android.graphics.drawable.Drawable;
import android.graphics.drawable.GradientDrawable;
import android.graphics.drawable.RippleDrawable;
import android.graphics.drawable.StateListDrawable;
import android.util.TypedValue;
import android.view.Gravity;
import android.view.View;
import android.view.ViewGroup;
import android.widget.Button;
import android.widget.FrameLayout;
import android.widget.ImageButton;
import android.widget.ImageView;
import android.widget.LinearLayout;
import android.widget.TextView;

import java.util.List;

/** Small helpers for building the UI in code. */
final class Ui {
    private Ui() {}

    static int dp(Context c, float v) {
        return Math.round(v * c.getResources().getDisplayMetrics().density);
    }

    static int color(Context c, int res) {
        return c.getColor(res);
    }

    static GradientDrawable rounded(int fill, float radiusPx) {
        GradientDrawable d = new GradientDrawable();
        d.setColor(fill);
        d.setCornerRadius(radiusPx);
        return d;
    }

    static GradientDrawable rounded(int fill, float radiusPx, int strokeColor, int strokePx) {
        GradientDrawable d = rounded(fill, radiusPx);
        d.setStroke(strokePx, strokeColor);
        return d;
    }

    static GradientDrawable circle(int fill) {
        GradientDrawable d = new GradientDrawable();
        d.setShape(GradientDrawable.OVAL);
        d.setColor(fill);
        return d;
    }

    static Drawable ripple(Context c, Drawable content, Drawable mask) {
        return new RippleDrawable(ColorStateList.valueOf(color(c, R.color.ripple)), content, mask);
    }

    static void selectableBorderless(View v) {
        TypedValue tv = new TypedValue();
        v.getContext().getTheme().resolveAttribute(
                android.R.attr.selectableItemBackgroundBorderless, tv, true);
        v.setBackgroundResource(tv.resourceId);
    }

    static void selectable(View v) {
        TypedValue tv = new TypedValue();
        v.getContext().getTheme().resolveAttribute(android.R.attr.selectableItemBackground, tv, true);
        v.setBackgroundResource(tv.resourceId);
    }

    static TextView text(Context c, CharSequence s, float sp, int colorRes, boolean bold) {
        TextView t = new TextView(c);
        t.setText(s);
        t.setTextSize(TypedValue.COMPLEX_UNIT_SP, sp);
        t.setTextColor(color(c, colorRes));
        if (bold) t.setTypeface(Typeface.create("sans-serif-medium", Typeface.NORMAL));
        return t;
    }

    static LinearLayout.LayoutParams lp(int w, int h) {
        return new LinearLayout.LayoutParams(w, h);
    }

    static LinearLayout.LayoutParams lpWeight(float weight) {
        return new LinearLayout.LayoutParams(0, ViewGroup.LayoutParams.WRAP_CONTENT, weight);
    }

    static LinearLayout.LayoutParams matchWrap() {
        return new LinearLayout.LayoutParams(ViewGroup.LayoutParams.MATCH_PARENT,
                ViewGroup.LayoutParams.WRAP_CONTENT);
    }

    static LinearLayout vertical(Context c) {
        LinearLayout l = new LinearLayout(c);
        l.setOrientation(LinearLayout.VERTICAL);
        return l;
    }

    static LinearLayout horizontal(Context c) {
        LinearLayout l = new LinearLayout(c);
        l.setOrientation(LinearLayout.HORIZONTAL);
        l.setGravity(Gravity.CENTER_VERTICAL);
        return l;
    }

    /** White rounded card. */
    static LinearLayout card(Context c) {
        LinearLayout l = vertical(c);
        l.setBackground(rounded(color(c, R.color.card), dp(c, 16)));
        l.setElevation(dp(c, 1.5f));
        int p = dp(c, 14);
        l.setPadding(p, p, p, p);
        return l;
    }

    static Button primaryButton(Context c, String label) {
        Button b = new Button(c);
        b.setText(label);
        b.setAllCaps(false);
        b.setTextSize(TypedValue.COMPLEX_UNIT_SP, 17);
        b.setTextColor(color(c, R.color.on_accent));
        b.setTypeface(Typeface.create("sans-serif-medium", Typeface.NORMAL));
        float r = dp(c, 14);
        b.setBackground(ripple(c, rounded(color(c, R.color.accent), r), rounded(0xFFFFFFFF, r)));
        b.setStateListAnimator(null);
        b.setMinHeight(dp(c, 54));
        return b;
    }

    static Button softButton(Context c, String label) {
        Button b = new Button(c);
        b.setText(label);
        b.setAllCaps(false);
        b.setTextSize(TypedValue.COMPLEX_UNIT_SP, 16);
        b.setTextColor(color(c, R.color.accent));
        b.setTypeface(Typeface.create("sans-serif-medium", Typeface.NORMAL));
        float r = dp(c, 12);
        b.setBackground(ripple(c, rounded(color(c, R.color.accent_soft), r), rounded(0xFFFFFFFF, r)));
        b.setStateListAnimator(null);
        b.setMinHeight(dp(c, 46));
        return b;
    }

    static ImageButton iconButton(Context c, int iconRes, int tintRes, String description) {
        ImageButton b = new ImageButton(c);
        b.setImageResource(iconRes);
        b.setImageTintList(ColorStateList.valueOf(color(c, tintRes)));
        b.setContentDescription(description);
        selectableBorderless(b);
        int p = dp(c, 8);
        b.setPadding(p, p, p, p);
        return b;
    }

    /** Background for number inputs: highlighted border when focused. */
    static Drawable inputBackground(Context c) {
        float r = dp(c, 10);
        StateListDrawable s = new StateListDrawable();
        s.addState(new int[]{android.R.attr.state_focused},
                rounded(color(c, R.color.input_bg), r, color(c, R.color.accent), dp(c, 2)));
        s.addState(new int[]{},
                rounded(color(c, R.color.input_bg), r, color(c, R.color.divider), dp(c, 1)));
        return s;
    }

    // ---------------------------------------------------------------- comparison indicators

    static int statusIcon(Compare.Status s) {
        switch (s) {
            case UP: return R.drawable.ic_up;
            case DOWN: return R.drawable.ic_down;
            case EQUAL: return R.drawable.ic_equal;
            case MIXED: return R.drawable.ic_mixed;
            case NEW: return R.drawable.ic_new;
            default: return 0;
        }
    }

    static int statusColor(Compare.Status s) {
        switch (s) {
            case UP: return R.color.up;
            case DOWN: return R.color.down;
            case MIXED: return R.color.mixed;
            case NEW: return R.color.fresh;
            default: return R.color.equal;
        }
    }

    static String statusDescription(Compare.Status s) {
        switch (s) {
            case UP: return "Лучше, чем в прошлый раз";
            case DOWN: return "Хуже, чем в прошлый раз";
            case EQUAL: return "Без изменений";
            case MIXED: return "Есть и рост, и спад";
            case NEW: return "Сравнивать не с чем";
            default: return "";
        }
    }

    /**
     * Arrow / "=" icon plus a small "!" icon. Tapping either one opens the explanation.
     * Returns an empty view for {@link Compare.Status#NONE}.
     */
    static View indicator(final Activity a, final Compare.Result r) {
        LinearLayout box = horizontal(a);
        box.setGravity(Gravity.CENTER);
        box.setTag(r.status.name());
        if (r.status == Compare.Status.NONE) return box;
        int col = color(a, statusColor(r.status));

        ImageView arrow = new ImageView(a);
        arrow.setImageResource(statusIcon(r.status));
        arrow.setImageTintList(ColorStateList.valueOf(col));
        box.addView(arrow, lp(dp(a, 24), dp(a, 24)));

        ImageView info = new ImageView(a);
        info.setImageResource(R.drawable.ic_info);
        info.setImageTintList(ColorStateList.valueOf(col));
        info.setAlpha(0.85f);
        LinearLayout.LayoutParams ilp = lp(dp(a, 16), dp(a, 16));
        ilp.leftMargin = dp(a, 2);
        box.addView(info, ilp);

        int p = dp(a, 5);
        box.setPadding(p, p, p, p);
        selectableBorderless(box);
        box.setClickable(true);
        box.setContentDescription(statusDescription(r.status) + ". Нажмите, чтобы узнать почему");
        box.setOnClickListener(new View.OnClickListener() {
            @Override
            public void onClick(View v) {
                showResult(a, r);
            }
        });
        return box;
    }

    static AlertDialog showResult(Activity a, Compare.Result r) {
        Drawable icon = a.getDrawable(statusIcon(r.status));
        if (icon != null) {
            icon = icon.mutate();
            icon.setTint(color(a, statusColor(r.status)));
        }
        return new AlertDialog.Builder(a)
                .setIcon(icon)
                .setTitle(r.title)
                .setMessage(r.message)
                .setPositiveButton("Понятно", null)
                .show();
    }

    // ---------------------------------------------------------------- shared dialogs

    static void showHistory(final Activity a, long exerciseId, String name, final String currentDay) {
        final List<Model.Session> h = Db.get(a).history(exerciseId);
        AlertDialog.Builder b = new AlertDialog.Builder(a).setTitle("История: " + name)
                .setPositiveButton("Закрыть", null);
        if (h.isEmpty()) {
            b.setMessage("Пока нет записанных подходов.");
        } else {
            String[] items = new String[h.size()];
            for (int i = 0; i < h.size(); i++) {
                Model.Session s = h.get(i);
                items[i] = Fmt.dateLong(s.day) + "\n" + Fmt.joinSets(s.sets);
            }
            b.setItems(items, new android.content.DialogInterface.OnClickListener() {
                @Override
                public void onClick(android.content.DialogInterface d, int which) {
                    String day = h.get(which).day;
                    if (!day.equals(currentDay)) openDay(a, day);
                }
            });
        }
        b.show();
    }

    static void openDay(Activity a, String day) {
        a.startActivity(new Intent(a, DayActivity.class).putExtra(DayActivity.EXTRA_DAY, day));
    }

    // ---------------------------------------------------------------- pictures & reference

    /** Drawable id of the pictogram for a catalog key, or 0. */
    static int pictogram(Context c, String key) {
        if (key == null || Catalog.get(key) == null) return 0;
        return c.getResources().getIdentifier("ex_" + key, "drawable", c.getPackageName());
    }

    /** Rounded square with the pictogram (or an "add picture" icon if there is none). */
    static FrameLayout thumbnail(Context c, String key, int sizeDp) {
        FrameLayout box = new FrameLayout(c);
        box.setBackground(rounded(color(c, R.color.pict_bg), dp(c, 12)));
        ImageView img = new ImageView(c);
        int res = pictogram(c, key);
        int pad;
        if (res != 0) {
            img.setImageResource(res);
            pad = dp(c, 2);
            box.setContentDescription("Картинка тренажёра. Нажмите, чтобы выбрать другую");
        } else {
            img.setImageResource(R.drawable.ic_photo_add);
            img.setImageTintList(ColorStateList.valueOf(color(c, R.color.accent)));
            pad = dp(c, sizeDp / 4f);
            box.setContentDescription("Выбрать картинку тренажёра");
        }
        img.setPadding(pad, pad, pad, pad);
        box.addView(img, new FrameLayout.LayoutParams(ViewGroup.LayoutParams.MATCH_PARENT,
                ViewGroup.LayoutParams.MATCH_PARENT));
        box.setForeground(ripple(c, null, rounded(0xFFFFFFFF, dp(c, 12))));
        box.setClickable(true);
        box.setTag("thumb:" + key);
        return box;
    }

    static void openInfo(Activity a, long exerciseId) {
        a.startActivity(new Intent(a, ExerciseInfoActivity.class)
                .putExtra(ExerciseInfoActivity.EXTRA_EXERCISE, exerciseId));
    }

    /**
     * Grid of all catalog pictures, best matches for the name first. Picking one saves it
     * for the exercise; then {@code onDone} runs.
     */
    static AlertDialog showPicker(final Activity a, final long exerciseId, final Runnable onDone) {
        final Db db = Db.get(a);
        final Model.Exercise ex = db.exercise(exerciseId);
        if (ex == null) return null;
        final List<Catalog.Item> shown = new java.util.ArrayList<>();
        final List<Catalog.Item> ranked = new java.util.ArrayList<>();
        for (Matcher.Match m : Matcher.rank(ex.name)) ranked.add(m.item);

        LinearLayout box = vertical(a);
        int p = dp(a, 16);
        box.setPadding(p, dp(a, 4), p, 0);
        final android.widget.EditText search = new android.widget.EditText(a);
        search.setTag("pickerSearch");
        search.setHint("Поиск: например, «жим» или «тяга»");
        search.setSingleLine(true);
        box.addView(search, matchWrap());

        final android.widget.GridView grid = new android.widget.GridView(a);
        grid.setTag("pickerGrid");
        grid.setNumColumns(3);
        grid.setVerticalSpacing(dp(a, 6));
        grid.setHorizontalSpacing(dp(a, 6));
        LinearLayout.LayoutParams glp = new LinearLayout.LayoutParams(ViewGroup.LayoutParams.MATCH_PARENT, dp(a, 400));
        glp.topMargin = dp(a, 8);
        box.addView(grid, glp);

        final android.widget.BaseAdapter adapter = new android.widget.BaseAdapter() {
            @Override
            public int getCount() {
                return shown.size() + 1;
            }

            @Override
            public Object getItem(int i) {
                return i == 0 ? null : shown.get(i - 1);
            }

            @Override
            public long getItemId(int i) {
                return i;
            }

            @Override
            public View getView(int i, View convert, ViewGroup parent) {
                Catalog.Item it = (Catalog.Item) getItem(i);
                LinearLayout cell = vertical(a);
                cell.setGravity(Gravity.CENTER_HORIZONTAL);
                int cp = dp(a, 4);
                cell.setPadding(cp, cp, cp, cp);
                boolean current = it == null ? ex.catalog == null : it.key.equals(ex.catalog);
                cell.setBackground(current
                        ? rounded(color(a, R.color.accent_soft), dp(a, 12), color(a, R.color.accent), dp(a, 2))
                        : rounded(0x00000000, dp(a, 12)));
                FrameLayout th = thumbnail(a, it == null ? null : it.key, 76);
                th.setClickable(false);
                th.setForeground(null);
                if (it == null) {
                    ((ImageView) th.getChildAt(0)).setImageResource(R.drawable.ic_close);
                    ((ImageView) th.getChildAt(0)).setImageTintList(
                            ColorStateList.valueOf(color(a, R.color.text_secondary)));
                }
                cell.addView(th, lp(dp(a, 76), dp(a, 76)));
                TextView t = text(a, it == null ? "Без картинки" : it.title, 12, R.color.text, false);
                t.setGravity(Gravity.CENTER);
                t.setMaxLines(3);
                t.setEllipsize(android.text.TextUtils.TruncateAt.END);
                LinearLayout.LayoutParams tl = matchWrap();
                tl.topMargin = dp(a, 3);
                cell.addView(t, tl);
                return cell;
            }
        };
        grid.setAdapter(adapter);

        final Runnable filter = new Runnable() {
            @Override
            public void run() {
                String q = search.getText().toString().trim();
                shown.clear();
                if (q.isEmpty()) {
                    shown.addAll(ranked);
                } else {
                    String nq = Fmt.normName(q);
                    for (Matcher.Match m : Matcher.rank(q)) {
                        if (m.score >= 0.3 || Fmt.normName(m.item.title).contains(nq)) shown.add(m.item);
                    }
                }
                adapter.notifyDataSetChanged();
            }
        };
        filter.run();
        search.addTextChangedListener(new android.text.TextWatcher() {
            @Override public void beforeTextChanged(CharSequence s, int st, int c, int af) {}
            @Override public void onTextChanged(CharSequence s, int st, int b, int c) {}
            @Override
            public void afterTextChanged(android.text.Editable s) {
                filter.run();
            }
        });

        final AlertDialog dialog = new AlertDialog.Builder(a)
                .setTitle("Картинка для «" + ex.name + "»")
                .setView(box)
                .setNegativeButton("Отмена", null)
                .create();
        grid.setOnItemClickListener(new android.widget.AdapterView.OnItemClickListener() {
            @Override
            public void onItemClick(android.widget.AdapterView<?> parent, View view, int pos, long id) {
                Catalog.Item it = (Catalog.Item) adapter.getItem(pos);
                db.setCatalog(exerciseId, it == null ? null : it.key);
                dialog.dismiss();
                if (onDone != null) onDone.run();
            }
        });
        dialog.show();
        return dialog;
    }

    /** Horizontal bar split into green / red / grey parts. */
    static LinearLayout ratioBar(Context c, Progress.Counts counts, int heightDp) {
        LinearLayout bar = horizontal(c);
        GradientDrawable bg = rounded(color(c, R.color.divider), dp(c, heightDp / 2f));
        bar.setBackground(bg);
        bar.setClipToOutline(true);
        int[][] parts = {{counts.up, R.color.up}, {counts.down, R.color.down}, {counts.equal, R.color.equal}};
        for (int[] part : parts) {
            if (part[0] <= 0) continue;
            View v = new View(c);
            v.setBackgroundColor(color(c, part[1]));
            bar.addView(v, new LinearLayout.LayoutParams(0, dp(c, heightDp), part[0]));
        }
        if (counts.total() == 0) {
            bar.addView(new View(c), new LinearLayout.LayoutParams(0, dp(c, heightDp), 1));
        }
        return bar;
    }
}
